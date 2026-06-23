using ACT_DiscordTriggers.Core.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;

namespace ACT_DiscordTriggers.Core.Ipc {
    public static class DiscordClient {
        private static PipeClient pipeClient;
        private static BridgeProcess bridge;
        private static string bridgeDir;
        private static readonly object lifecycleLock = new object();
        private static int connectInProgress;

        // TTS is synthesized in-process (System.Speech is a Windows/net48 capability
        // the bridge can't run) and sent as 48k/16/stereo PCM. Everything else —
        // effects, normalization, bitrate, presence — is decided by the bridge from
        // the config the plugin pushes. DiscordClient is just the router.
        private static readonly SpeechAudioFormatInfo formatInfo =
            new SpeechAudioFormatInfo(48000, AudioBitsPerSample.Sixteen, AudioChannel.Stereo);

        public delegate void BotLoaded();
        public static event BotLoaded BotReady;

        // Fired when the bridge connection is gone (CleanupAfterPipeBroken): bridge
        // process exit, broken pipe, or a failed login teardown. The UI uses this to
        // drop back to the disconnected state.
        public static event Action Disconnected;

        public delegate void BotMessage(string message, LogLevel level);
        public static event BotMessage Log;

        // The single funnel for every line this facade emits to the UI. Callers pass
        // the severity (default Info); the level rides the Log event to the ViewModel,
        // which colours the row and tags the file line.
        private static void Emit(string message, LogLevel level = LogLevel.Info) => Log?.Invoke(message, level);

        // Exception funnel: the UI gets a short, human line; the diagnostics file gets
        // the full exception (type + inner + stack) via a direct Append so the detail
        // survives even though the Log event only carries the short text. The VM's
        // funnel still echoes the short line to the file — that one-line echo is
        // intentional; don't "simplify" this into a second full-exception append.
        private static void EmitError(string userMessage, Exception ex) {
            DiagnosticsLog.Append(userMessage + " | " + ex, LogLevel.Error);
            Emit(userMessage + ": " + ex.Message, LogLevel.Error);
        }

        public static void SetBridgePath(string dir) {
            bridgeDir = dir;
        }

        // Spawn the bridge, handshake, and push the config, then tell the bridge to
        // log in to Discord. `config` is the plugin's whole settings POCO, serialized
        // verbatim; the bridge reads what it needs.
        public static Task ConnectAsync<TConfig>(TConfig config, Dictionary<string, string> ttsParams = null) =>
            StartAsync(config, ttsParams, login: true);

        // Local-output start: spawn the bridge, handshake, and push the config — but
        // skip the Discord login. The config carries outputMode="local", so the bridge
        // brings up the local sound device on SetConfig. Used when the plugin is in
        // local-output mode: the bridge comes online without a token/channel. Returns
        // whether the bridge confirmed the local device actually opened (the SetConfig
        // result's ok), so the caller only routes audio when there is something live
        // to play it.
        public static Task<bool> StartLocalAsync<TConfig>(TConfig config, Dictionary<string, string> ttsParams = null) =>
            StartAsync(config, ttsParams, login: false);

        // Shared bridge bring-up for both entry points. `login` gates the final
        // Connect (Discord login) step; everything before it — spawn, Hello handshake,
        // SetConfig — is identical and required either way. Returns true when the
        // requested bring-up succeeded: a completed Discord login (login=true), or a
        // confirmed local device (login=false, from the SetConfig result's ok).
        private static async Task<bool> StartAsync<TConfig>(TConfig config, Dictionary<string, string> ttsParams, bool login) {
            // Race guard: a fast double-click on Connect would otherwise spawn two
            // node.exe processes since the long async work below runs unlocked.
            if (Interlocked.CompareExchange(ref connectInProgress, 1, 0) != 0) {
                Emit("Connection already in progress.");
                return false;
            }
            try {
                lock (lifecycleLock) {
                    if (pipeClient != null) {
                        Emit("Already connected.");
                        return false;
                    }
                }
                if (string.IsNullOrEmpty(bridgeDir)) {
                    Emit("Bridge directory not configured. Internal error.", LogLevel.Error);
                    return false;
                }

                BridgeProcess localBridge = new BridgeProcess();
                localBridge.OnStderr += msg => Emit("[bridge] " + msg);
                localBridge.OnExited += code => {
                    // A clean exit (code 0) is routine teardown; any other code is a crash.
                    Emit($"Bridge process exited (code {code}).", code == 0 ? LogLevel.Info : LogLevel.Error);
                    CleanupAfterPipeBroken();
                };

                NamedPipeClientStream pipe;
                try {
                    pipe = await localBridge.StartAndConnectAsync(bridgeDir).ConfigureAwait(false);
                } catch (Exception ex) {
                    EmitError("Failed to start bridge", ex);
                    try { localBridge.Dispose(); } catch { }
                    return false;
                }

                PipeClient localClient = new PipeClient(pipe);
                localClient.OnLog += (msg, lvl) => Emit(msg, LogLevels.ParseWire(lvl));
                localClient.OnBotReady += () => BotReady?.Invoke();
                localClient.OnDisconnected += reason => Emit("Discord disconnected: " + reason, LogLevel.Warn);
                localClient.OnPipeBroken += reason => {
                    Emit("Bridge connection lost: " + reason, LogLevel.Warn);
                    CleanupAfterPipeBroken();
                };
                localClient.Start();

                lock (lifecycleLock) {
                    bridge = localBridge;
                    pipeClient = localClient;
                }

                BridgeResponse<HelloData> hello;
                try {
                    hello = await localClient.SendAsync<BridgeResponse<HelloData>>(
                        new HelloRequest { ProtocolVersion = ProtocolConstants.Version },
                        TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                } catch (Exception ex) {
                    EmitError("Bridge handshake error", ex);
                    CleanupAfterPipeBroken();
                    return false;
                }
                if (!hello.Ok) {
                    Emit("Bridge handshake failed: " + hello.Error, LogLevel.Error);
                    CleanupAfterPipeBroken();
                    return false;
                }

                // Push config (incl. the bot token, and outputMode) before logging in.
                // In local-output mode this SetConfig is what arms the local sound
                // device on the bridge — there is no login step that follows. The
                // bridge fails the result (ok=false) if the device couldn't open, so
                // localOk tells us whether local output is actually live.
                bool localOk = false;
                try {
                    var setCfg = await localClient.SendAsync<BridgeResponse>(
                        new SetConfigRequest<TConfig> { Config = config, TtsParams = ttsParams },
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    localOk = setCfg.Ok;
                    if (!setCfg.Ok) Emit("SetConfig: " + setCfg.Error, LogLevel.Error);
                } catch (Exception ex) {
                    EmitError("SetConfig failed", ex);
                }

                // Local-output mode stops here: no Discord login follows. Report
                // whether the device came up so the caller only routes audio when live.
                if (!login) return localOk;

                try {
                    var connect = await localClient.SendAsync<BridgeResponse>(
                        new ConnectRequest(), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (!connect.Ok) {
                        // Login failed (e.g. bad token). Tear the bridge down so the
                        // user can retry with a new token — otherwise the stale
                        // pipeClient makes the next Connect bail with "Already connected."
                        Emit("Discord login failed: " + connect.Error, LogLevel.Error);
                        CleanupAfterPipeBroken();
                        return false;
                    }
                } catch (Exception ex) {
                    EmitError("Discord login error", ex);
                    CleanupAfterPipeBroken();
                    return false;
                }
                return true;
            } catch (Exception ex) {
                EmitError("Connect error", ex);
                return false;
            } finally {
                Interlocked.Exchange(ref connectInProgress, 0);
            }
        }

        // Push the latest config to the bridge. Called from the UI whenever any
        // setting changes. No-op until the bridge pipe exists — ConnectAsync
        // pushes the config on connect anyway.
        public static async Task SetConfigAsync<TConfig>(TConfig config, Dictionary<string, string> ttsParams = null) {
            var pc = pipeClient;
            if (pc == null) return;
            try {
                await pc.SendAsync<BridgeResponse>(
                    new SetConfigRequest<TConfig> { Config = config, TtsParams = ttsParams },
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                EmitError("SetConfig failed", ex);
            }
        }

        public static async Task DeinitAsync() {
            PipeClient localClient;
            BridgeProcess localBridge;
            lock (lifecycleLock) {
                localClient = pipeClient;
                localBridge = bridge;
                pipeClient = null;
                bridge = null;
            }
            if (localClient == null && localBridge == null) return;

            try {
                if (localClient != null) {
                    try {
                        await localClient.SendAsync<BridgeResponse>(
                            new ShutdownRequest(), TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    } catch { }
                }
                if (localBridge != null) {
                    await localBridge.WaitForExitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    if (!localBridge.HasExited) {
                        localBridge.Kill();
                    }
                }
            } finally {
                try { localClient?.Dispose(); } catch { }
                try { localBridge?.Dispose(); } catch { }
            }
        }

        private static void CleanupAfterPipeBroken() {
            PipeClient localClient;
            BridgeProcess localBridge;
            lock (lifecycleLock) {
                localClient = pipeClient;
                localBridge = bridge;
                pipeClient = null;
                bridge = null;
            }
            try { localClient?.Dispose(); } catch { }
            try { localBridge?.Dispose(); } catch { }
            // Only signal a transition: if there was nothing to tear down this was a
            // no-op (e.g. a failed login that never connected), so don't fire.
            if (localClient != null || localBridge != null) {
                try { Disconnected?.Invoke(); } catch { }
            }
        }

        public static async Task<bool> IsConnectedAsync() {
            var pc = pipeClient;
            if (pc == null) return false;
            try {
                var resp = await pc.SendAsync<BridgeResponse<ConnectedData>>(
                    new IsConnectedRequest(), TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                return resp.Data?.Connected ?? false;
            } catch {
                return false;
            }
        }

        public static async Task<string[]> GetServersAsync() {
            var pc = pipeClient;
            if (pc == null) return new string[0];
            try {
                var resp = await pc.SendAsync<BridgeResponse<ServersData>>(
                    new GetServersRequest()).ConfigureAwait(false);
                return resp.Data?.Servers ?? new string[0];
            } catch (Exception ex) {
                EmitError("GetServersAsync failed", ex);
                return new string[0];
            }
        }

        public static async Task<string[]> GetChannelsAsync(string server) {
            var pc = pipeClient;
            if (pc == null) return new string[0];
            try {
                var resp = await pc.SendAsync<BridgeResponse<ChannelsData>>(
                    new GetChannelsRequest { Server = server }).ConfigureAwait(false);
                return resp.Data?.Channels ?? new string[0];
            } catch (Exception ex) {
                EmitError("GetChannelsAsync failed", ex);
                return new string[0];
            }
        }

        public static async Task<bool> JoinChannel(string server, string channel) {
            var pc = pipeClient;
            if (pc == null) {
                Emit("Cannot join channel: bridge not connected.", LogLevel.Warn);
                return false;
            }
            try {
                var resp = await pc.SendAsync<BridgeResponse>(
                    new JoinChannelRequest { Server = server, Channel = channel },
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Emit("JoinChannel failed: " + resp.Error, LogLevel.Error);
                }
                return resp.Ok;
            } catch (Exception ex) {
                EmitError("JoinChannel error", ex);
                return false;
            }
        }

        public static async Task LeaveChannelAsync() {
            var pc = pipeClient;
            if (pc == null) return;
            try {
                await pc.SendAsync<BridgeResponse>(
                    new LeaveChannelRequest(), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            } catch (Exception ex) {
                EmitError("LeaveChannelAsync failed", ex);
            }
        }

        // Names of the SAPI voices installed on this machine. Centralized here so all
        // System.Speech usage (and its COM/disposal quirks) lives in one place.
        public static string[] GetInstalledVoices() {
            try {
                using (var tts = new SpeechSynthesizer())
                    return tts.GetInstalledVoices().Select(v => v.VoiceInfo.Name).ToArray();
            } catch {
                return new string[0];
            }
        }

        // SAPI TTS: synthesize in-process (System.Speech is a Windows capability the
        // bridge can't run), then send the PCM. The CPU-bound synth AND the IPC run on a
        // thread-pool thread (Task.Run) so the ACT callout thread that invokes this is
        // never blocked — the caller awaits the returned Task instead.
        public static Task SpeakAsync(string text, string voice, int vol, int speed) {
            return Task.Run(async () => {
                var swSynth = Stopwatch.StartNew();
                byte[] pcm;
                try {
                    using (var tts = new SpeechSynthesizer())
                    using (var ms = new MemoryStream()) {
                        tts.SelectVoice(voice);
                        tts.Volume = vol * 5;
                        tts.Rate = speed - 10;
                        tts.SetOutputToAudioStream(ms, formatInfo);
                        tts.Speak(text);
                        pcm = ms.ToArray();
                    }
                } catch (Exception ex) {
                    EmitError("TTS synthesis failed", ex);
                    return;
                }
                swSynth.Stop();
                var swIpc = Stopwatch.StartNew();
                await SendSpeakPcmAsync(pcm).ConfigureAwait(false);
                swIpc.Stop();
                Emit($"Speak timing: synth={swSynth.ElapsedMilliseconds}ms ipc={swIpc.ElapsedMilliseconds}ms bytes={pcm.Length}");
            });
        }

        // Play a sound file through the bridge. Awaitable; the bridge decodes + enqueues
        // and replies, so the caller never blocks on playback.
        public static Task SpeakFileAsync(string path) =>
            SendSpeakRequestAsync(
                new SpeakFileRequest { Path = path },
                notConnected: "Cannot play file: bridge not connected.",
                rejectedPrefix: "Bridge file rejected: ",
                opName: "SpeakFile");

        // ONNX TTS dispatch: send only the text. The bridge synthesizes with the voice
        // it learned from the last SetConfig (ttsParams) and replies (on acceptance) via
        // the Result envelope. Awaitable, so the ACT callout thread isn't blocked.
        public static Task SpeakOnnxAsync(string text) =>
            SendSpeakRequestAsync(
                new SpeakTextRequest { Text = text },
                notConnected: "Cannot speak (ONNX): bridge not connected.",
                rejectedPrefix: "Bridge TTS rejected: ",
                opName: "SpeakText");

        // Shared dispatch for the fire-and-await playback commands (SpeakFile /
        // SpeakText): null-guard the pipe, send, then log either the bridge's
        // rejection or the IPC timing. opName labels the timing/error lines.
        private static async Task SendSpeakRequestAsync(
            IBridgeRequest req, string notConnected, string rejectedPrefix, string opName) {
            var pc = pipeClient;
            if (pc == null) {
                Emit(notConnected, LogLevel.Warn);
                return;
            }
            var sw = Stopwatch.StartNew();
            try {
                var resp = await pc.SendAsync<BridgeResponse>(req, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                sw.Stop();
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Emit(rejectedPrefix + resp.Error, LogLevel.Error);
                } else {
                    Emit($"{opName} timing: ipc={sw.ElapsedMilliseconds}ms");
                }
            } catch (Exception ex) {
                EmitError($"{opName} error", ex);
            }
        }

        private static async Task SendSpeakPcmAsync(byte[] pcm) {
            var pc = pipeClient;
            if (pc == null) {
                Emit("Cannot send audio: bridge not connected.", LogLevel.Warn);
                return;
            }
            try {
                var resp = await pc.SendSpeakPcmAsync(pcm, 48000, 16, 2, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Emit("Bridge audio rejected: " + resp.Error, LogLevel.Error);
                }
            } catch (Exception ex) {
                EmitError("SpeakPcm error", ex);
            }
        }
    }
}
