using ACT_DiscordTriggers.Core.Protocol;
using System;
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

        public delegate void BotMessage(string message);
        public static event BotMessage Log;

        public static void SetBridgePath(string dir) {
            bridgeDir = dir;
        }

        // Spawn the bridge, handshake, push the config (which carries the token and
        // every tunable), then tell the bridge to log in. `config` is the plugin's
        // whole settings POCO; it is serialized verbatim and the bridge reads what
        // it needs.
        public static async Task ConnectAsync<TConfig>(TConfig config) {
            // Race guard: a fast double-click on Connect would otherwise spawn two
            // node.exe processes since the long async work below runs unlocked.
            if (Interlocked.CompareExchange(ref connectInProgress, 1, 0) != 0) {
                Log?.Invoke("Connection already in progress.");
                return;
            }
            try {
                lock (lifecycleLock) {
                    if (pipeClient != null) {
                        Log?.Invoke("Already connected.");
                        return;
                    }
                }
                if (string.IsNullOrEmpty(bridgeDir)) {
                    Log?.Invoke("Bridge directory not configured. Internal error.");
                    return;
                }

                BridgeProcess localBridge = new BridgeProcess();
                localBridge.OnStderr += msg => Log?.Invoke("[bridge] " + msg);
                localBridge.OnExited += code => {
                    Log?.Invoke($"Bridge process exited (code {code}).");
                    CleanupAfterPipeBroken();
                };

                NamedPipeClientStream pipe;
                try {
                    pipe = await localBridge.StartAndConnectAsync(bridgeDir).ConfigureAwait(false);
                } catch (Exception ex) {
                    Log?.Invoke("Failed to start bridge: " + ex.Message);
                    try { localBridge.Dispose(); } catch { }
                    return;
                }

                PipeClient localClient = new PipeClient(pipe);
                localClient.OnLog += (msg, lvl) => Log?.Invoke(msg);
                localClient.OnBotReady += () => BotReady?.Invoke();
                localClient.OnDisconnected += reason => Log?.Invoke("Discord disconnected: " + reason);
                localClient.OnPipeBroken += reason => {
                    Log?.Invoke("Bridge connection lost: " + reason);
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
                    Log?.Invoke("Bridge handshake error: " + ex.Message);
                    CleanupAfterPipeBroken();
                    return;
                }
                if (!hello.Ok) {
                    Log?.Invoke("Bridge handshake failed: " + hello.Error);
                    CleanupAfterPipeBroken();
                    return;
                }

                // Push config (incl. the bot token) before asking the bridge to log in.
                try {
                    await localClient.SendAsync<BridgeResponse>(
                        new SetConfigRequest<TConfig> { Config = config },
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                } catch (Exception ex) {
                    Log?.Invoke("SetConfig failed: " + ex.Message);
                }

                try {
                    var connect = await localClient.SendAsync<BridgeResponse>(
                        new ConnectRequest(), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (!connect.Ok) {
                        // Login failed (e.g. bad token). Tear the bridge down so the
                        // user can retry with a new token — otherwise the stale
                        // pipeClient makes the next Connect bail with "Already connected."
                        Log?.Invoke("Discord login failed: " + connect.Error);
                        CleanupAfterPipeBroken();
                    }
                } catch (Exception ex) {
                    Log?.Invoke("Discord login error: " + ex.Message);
                    CleanupAfterPipeBroken();
                }
            } catch (Exception ex) {
                Log?.Invoke("Connect error: " + ex.Message);
            } finally {
                Interlocked.Exchange(ref connectInProgress, 0);
            }
        }

        // Push the latest config to the bridge. Called from the UI whenever any
        // setting changes. No-op until the bridge pipe exists — ConnectAsync
        // pushes the config on connect anyway.
        public static async Task SetConfigAsync<TConfig>(TConfig config) {
            var pc = pipeClient;
            if (pc == null) return;
            try {
                await pc.SendAsync<BridgeResponse>(
                    new SetConfigRequest<TConfig> { Config = config },
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                Log?.Invoke("SetConfig failed: " + ex.Message);
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
                Log?.Invoke("GetServersAsync failed: " + ex.Message);
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
                Log?.Invoke("GetChannelsAsync failed: " + ex.Message);
                return new string[0];
            }
        }

        public static async Task<bool> JoinChannel(string server, string channel) {
            var pc = pipeClient;
            if (pc == null) {
                Log?.Invoke("Cannot join channel: bridge not connected.");
                return false;
            }
            try {
                var resp = await pc.SendAsync<BridgeResponse>(
                    new JoinChannelRequest { Server = server, Channel = channel },
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Log?.Invoke("JoinChannel failed: " + resp.Error);
                }
                return resp.Ok;
            } catch (Exception ex) {
                Log?.Invoke("JoinChannel error: " + ex.Message);
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
                Log?.Invoke("LeaveChannelAsync failed: " + ex.Message);
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

        public static void Speak(string text, string voice, int vol, int speed) {
            // Called from ACT's PlayTtsMethod hook on a background thread, not the UI.
            // Synthesis itself is sync; downstream IPC blocks the trigger thread by design.
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
                Log?.Invoke("TTS synthesis failed: " + ex.Message);
                return;
            }
            swSynth.Stop();
            var swIpc = Stopwatch.StartNew();
            SendSpeakPcm(pcm);
            swIpc.Stop();
            Log?.Invoke($"Speak timing: synth={swSynth.ElapsedMilliseconds}ms ipc={swIpc.ElapsedMilliseconds}ms bytes={pcm.Length}");
        }

        public static void SpeakFile(string path) {
            // Called from ACT's PlaySoundMethod hook (signature: void(string,int)) on
            // a background thread. The single sync-over-async boundary lives here.
            try {
                Task.Run(() => SpeakFileAsync(path)).GetAwaiter().GetResult();
            } catch (Exception ex) {
                Log?.Invoke("SpeakFile error: " + ex.Message);
            }
        }

        private static async Task SpeakFileAsync(string path) {
            var pc = pipeClient;
            if (pc == null) {
                Log?.Invoke("Cannot play file: bridge not connected.");
                return;
            }
            var sw = Stopwatch.StartNew();
            try {
                var resp = await pc.SendAsync<BridgeResponse>(
                    new SpeakFileRequest { Path = path },
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                sw.Stop();
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Log?.Invoke("Bridge file rejected: " + resp.Error);
                } else {
                    Log?.Invoke($"SpeakFile timing: ipc={sw.ElapsedMilliseconds}ms");
                }
            } catch (Exception ex) {
                Log?.Invoke("SpeakFile error: " + ex.Message);
            }
        }

        private static void SendSpeakPcm(byte[] pcm) {
            var pc = pipeClient;
            if (pc == null) {
                Log?.Invoke("Cannot send audio: bridge not connected.");
                return;
            }
            try {
                var resp = Task.Run(() => pc.SendSpeakPcmAsync(pcm, 48000, 16, 2, TimeSpan.FromSeconds(30)))
                    .GetAwaiter().GetResult();
                if (!resp.Ok && !string.IsNullOrEmpty(resp.Error)) {
                    Log?.Invoke("Bridge audio rejected: " + resp.Error);
                }
            } catch (Exception ex) {
                Log?.Invoke("SpeakPcm error: " + ex.Message);
            }
        }
    }
}
