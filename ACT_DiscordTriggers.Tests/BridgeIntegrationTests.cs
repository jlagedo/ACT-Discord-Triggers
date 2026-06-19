using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Protocol;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
    public class BridgeIntegrationTests {
        private static string FindBridgeDir() {
            string testDir = Path.GetDirectoryName(typeof(BridgeIntegrationTests).Assembly.Location);
            string solDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
            string dir = Path.Combine(solDir, "DiscordBridge-node", "dist");
            if (File.Exists(Path.Combine(dir, "node.exe")) && File.Exists(Path.Combine(dir, "bundle.js"))) {
                return dir;
            }
            throw new FileNotFoundException(
                "Bridge not built. Run `pwsh DiscordBridge-node\\build.ps1` first. Looked in: " + dir);
        }

        [Fact]
        public async Task Bridge_handshake_succeeds_against_real_exe() {
            string dir = FindBridgeDir();
            using var bp = new BridgeProcess();
            var stderr = new System.Collections.Generic.List<string>();
            bp.OnStderr += s => { lock (stderr) stderr.Add(s); };

            var pipe = await bp.StartAndConnectAsync(dir, TimeSpan.FromSeconds(15));
            using var pc = new PipeClient(pipe);
            pc.Start();

            var hello = await pc.SendAsync<BridgeResponse<HelloData>>(
                new HelloRequest { ProtocolVersion = ProtocolConstants.Version },
                TimeSpan.FromSeconds(5));

            Assert.True(hello.Ok, $"Hello failed: {hello.Error}; stderr:\n{string.Join("\n", stderr)}");
            Assert.False(string.IsNullOrEmpty(hello.Data.BridgeVersion));

            try {
                await pc.SendAsync<BridgeResponse>(new ShutdownRequest(), TimeSpan.FromSeconds(2));
            } catch { /* bridge exits during the call, response may not arrive */ }

            await bp.WaitForExitAsync(TimeSpan.FromSeconds(5));
            Assert.True(bp.HasExited, "Bridge did not exit after Shutdown.");
        }

        [Fact]
        public async Task Bridge_IsConnected_returns_false_before_Connect() {
            string dir = FindBridgeDir();
            using var bp = new BridgeProcess();
            var pipe = await bp.StartAndConnectAsync(dir, TimeSpan.FromSeconds(15));
            using var pc = new PipeClient(pipe);
            pc.Start();

            await pc.SendAsync<BridgeResponse<HelloData>>(
                new HelloRequest { ProtocolVersion = ProtocolConstants.Version },
                TimeSpan.FromSeconds(5));

            var ic = await pc.SendAsync<BridgeResponse<ConnectedData>>(
                new IsConnectedRequest(), TimeSpan.FromSeconds(3));
            Assert.False(ic.Data.Connected);

            var servers = await pc.SendAsync<BridgeResponse<ServersData>>(
                new GetServersRequest(), TimeSpan.FromSeconds(3));
            Assert.NotNull(servers.Data.Servers);
            Assert.Empty(servers.Data.Servers);

            try {
                await pc.SendAsync<BridgeResponse>(new ShutdownRequest(), TimeSpan.FromSeconds(2));
            } catch { }
            await bp.WaitForExitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task Bridge_handshake_rejects_wrong_protocol_version() {
            string dir = FindBridgeDir();
            using var bp = new BridgeProcess();
            var pipe = await bp.StartAndConnectAsync(dir, TimeSpan.FromSeconds(15));
            using var pc = new PipeClient(pipe);
            pc.Start();

            var hello = await pc.SendAsync<BridgeResponse<HelloData>>(
                new HelloRequest { ProtocolVersion = 9999 },
                TimeSpan.FromSeconds(5));

            Assert.False(hello.Ok);
            Assert.Contains("mismatch", hello.Error, StringComparison.OrdinalIgnoreCase);

            try {
                await pc.SendAsync<BridgeResponse>(new ShutdownRequest(), TimeSpan.FromSeconds(2));
            } catch { }
            await bp.WaitForExitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task BridgeProcess_with_missing_dir_throws_FileNotFoundException() {
            using var bp = new BridgeProcess();
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                bp.StartAndConnectAsync(@"C:\definitely-not-here\bridge"));
        }

        private static async Task<(BridgeProcess bp, PipeClient pc)> StartBridgeAndHelloAsync() {
            var bp = new BridgeProcess();
            var pipe = await bp.StartAndConnectAsync(FindBridgeDir(), TimeSpan.FromSeconds(15));
            var pc = new PipeClient(pipe);
            pc.Start();
            await pc.SendAsync<BridgeResponse<HelloData>>(
                new HelloRequest { ProtocolVersion = ProtocolConstants.Version },
                TimeSpan.FromSeconds(5));
            return (bp, pc);
        }

        private static async Task ShutdownBridgeAsync(BridgeProcess bp, PipeClient pc) {
            try { await pc.SendAsync<BridgeResponse>(new ShutdownRequest(), TimeSpan.FromSeconds(2)); } catch { }
            await bp.WaitForExitAsync(TimeSpan.FromSeconds(5));
            try { pc.Dispose(); } catch { }
            try { bp.Dispose(); } catch { }
        }

        [Fact]
        public async Task GetChannels_for_unknown_server_returns_empty_array() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                var resp = await pc.SendAsync<BridgeResponse<ChannelsData>>(
                    new GetChannelsRequest { Server = "no-such-server" },
                    TimeSpan.FromSeconds(3));
                Assert.NotNull(resp.Data.Channels);
                Assert.Empty(resp.Data.Channels);
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task LeaveChannel_before_join_does_not_throw() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                var resp = await pc.SendAsync<BridgeResponse>(
                    new LeaveChannelRequest(), TimeSpan.FromSeconds(3));
                Assert.True(resp.Ok);
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task SpeakPcm_without_join_returns_not_connected_error() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                byte[] pcm = new byte[1024];
                var resp = await pc.SendSpeakPcmAsync(pcm, 48000, 16, 2, TimeSpan.FromSeconds(3));
                Assert.False(resp.Ok);
                Assert.Contains("Not connected", resp.Error, StringComparison.OrdinalIgnoreCase);
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task SpeakFile_without_join_returns_not_connected_error() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                var resp = await pc.SendAsync<BridgeResponse>(
                    new SpeakFileRequest { Path = @"C:\does-not-matter.wav" },
                    TimeSpan.FromSeconds(3));
                Assert.False(resp.Ok);
                Assert.Contains("Not connected", resp.Error, StringComparison.OrdinalIgnoreCase);
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task Connect_with_invalid_token_keeps_bridge_responsive_and_disconnected() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                // Push a config carrying a bad token, then ask the bridge to log in.
                // Discord.js's eagerness on token validation varies by version; what
                // matters is the bridge survives and IsConnected stays false. We don't
                // assert on the Connect response directly because login may fail
                // asynchronously rather than throwing during the call.
                await pc.SendAsync<BridgeResponse>(
                    new SetConfigRequest<PluginSettings> {
                        Config = new PluginSettings { BotToken = "obviously-not-a-real-bot-token" },
                    },
                    TimeSpan.FromSeconds(5));
                try {
                    await pc.SendAsync<BridgeResponse>(
                        new ConnectRequest(), TimeSpan.FromSeconds(10));
                } catch (TimeoutException) { /* allowed: REST validation can be slow */ }

                var ic = await pc.SendAsync<BridgeResponse<ConnectedData>>(
                    new IsConnectedRequest(), TimeSpan.FromSeconds(3));
                Assert.False(ic.Data.Connected);
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task Concurrent_bridge_requests_get_correct_responses() {
            var (bp, pc) = await StartBridgeAndHelloAsync();
            try {
                const int N = 10;
                var tasks = new Task<BridgeResponse<ConnectedData>>[N];
                for (int i = 0; i < N; i++) {
                    tasks[i] = pc.SendAsync<BridgeResponse<ConnectedData>>(
                        new IsConnectedRequest(), TimeSpan.FromSeconds(5));
                }
                var results = await Task.WhenAll(tasks);
                foreach (var r in results) {
                    Assert.False(r.Data.Connected);
                }
            } finally {
                await ShutdownBridgeAsync(bp, pc);
            }
        }

        [Fact]
        public async Task Killing_bridge_breaks_pipe_and_fails_pending_requests() {
            string dir = FindBridgeDir();
            using var bp = new BridgeProcess();
            var pipe = await bp.StartAndConnectAsync(dir, TimeSpan.FromSeconds(15));
            using var pc = new PipeClient(pipe);
            pc.Start();

            await pc.SendAsync<BridgeResponse<HelloData>>(
                new HelloRequest { ProtocolVersion = ProtocolConstants.Version },
                TimeSpan.FromSeconds(5));

            var brokenSignal = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pc.OnPipeBroken += r => brokenSignal.TrySetResult(r);

            bp.Kill();
            string reason = await brokenSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(reason);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                pc.SendAsync<BridgeResponse<ConnectedData>>(
                    new IsConnectedRequest(), TimeSpan.FromSeconds(2)));
        }
    }
}
