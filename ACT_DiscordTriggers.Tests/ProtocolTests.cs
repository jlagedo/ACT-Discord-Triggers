using ACT_DiscordTriggers.Settings;
using ACT_DiscordTriggers.Protocol;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
    public class ProtocolTests {
        private static readonly JsonSerializerOptions opts = new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        [Fact]
        public void HelloRequest_serializes_with_op_and_protocolVersion() {
            var req = new HelloRequest { ReqId = 1, ProtocolVersion = ProtocolConstants.Version };
            string json = JsonSerializer.Serialize(req, opts);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Hello", doc.RootElement.GetProperty("op").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("reqId").GetInt32());
            Assert.Equal(ProtocolConstants.Version, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        }

        [Fact]
        public void BridgeResponse_defaults_op_to_Result() {
            var resp = new BridgeResponse { ReqId = 3, Ok = true };
            string json = JsonSerializer.Serialize(resp, opts);
            Assert.Contains("\"op\":\"Result\"", json);
            Assert.Contains("\"reqId\":3", json);
        }

        [Fact]
        public void BridgeResponse_with_HelloData_round_trips() {
            var resp = new BridgeResponse<HelloData> {
                ReqId = 2, Ok = true, Data = new HelloData { BridgeVersion = "1.0.0" },
            };
            string json = JsonSerializer.Serialize(resp, opts);
            var back = JsonSerializer.Deserialize<BridgeResponse<HelloData>>(json);
            Assert.True(back.Ok);
            Assert.Equal(2, back.ReqId);
            Assert.Equal("Result", back.Op);
            Assert.Equal("1.0.0", back.Data.BridgeVersion);
        }

        [Fact]
        public void BridgeResponse_with_ConnectedData_round_trips() {
            var resp = new BridgeResponse<ConnectedData> {
                ReqId = 4, Ok = true, Data = new ConnectedData { Connected = true },
            };
            var back = JsonSerializer.Deserialize<BridgeResponse<ConnectedData>>(
                JsonSerializer.Serialize(resp, opts));
            Assert.True(back.Data.Connected);
        }

        [Fact]
        public void BridgeResponse_with_error_carries_error_string() {
            var resp = new BridgeResponse { ReqId = 7, Ok = false, Error = "channel not found" };
            var back = JsonSerializer.Deserialize<BridgeResponse>(JsonSerializer.Serialize(resp, opts));
            Assert.False(back.Ok);
            Assert.Equal("channel not found", back.Error);
        }

        [Fact]
        public void ChannelsData_with_empty_array_serializes_as_empty_array() {
            var resp = new BridgeResponse<ChannelsData> {
                ReqId = 5, Ok = true, Data = new ChannelsData { Channels = new string[0] },
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(resp, opts));
            var channels = doc.RootElement.GetProperty("data").GetProperty("channels");
            Assert.Equal(JsonValueKind.Array, channels.ValueKind);
            Assert.Equal(0, channels.GetArrayLength());
        }

        [Fact]
        public void Notification_omits_reqId_when_null() {
            var note = new BotReadyNotification();
            string json = JsonSerializer.Serialize(note, opts);
            Assert.DoesNotContain("reqId", json);
            Assert.Contains("\"op\":\"BotReady\"", json);
        }

        [Fact]
        public void LogNotification_carries_message_and_level() {
            var note = new LogNotification { Message = "boom", Level = "Error" };
            string json = JsonSerializer.Serialize(note, opts);
            var back = JsonSerializer.Deserialize<LogNotification>(json);
            Assert.Equal("boom", back.Message);
            Assert.Equal("Error", back.Level);
            Assert.Equal("Log", back.Op);
        }

        [Fact]
        public void SpeakFileRequest_serializes_with_path_and_no_effect_flag() {
            var req = new SpeakFileRequest { ReqId = 9, Path = @"C:\sounds\alert.wav" };
            string json = JsonSerializer.Serialize(req, opts);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("SpeakFile", doc.RootElement.GetProperty("op").GetString());
            Assert.Equal(9, doc.RootElement.GetProperty("reqId").GetInt32());
            Assert.Equal(@"C:\sounds\alert.wav", doc.RootElement.GetProperty("path").GetString());
            // The per-clip effect flag is gone — the bridge decides from config.
            Assert.False(doc.RootElement.TryGetProperty("randomEffect", out _));
        }

        // The whole plugin settings POCO is the config payload. Serialize via the
        // runtime type, mirroring PipeClient.SendFrameAsync, and confirm the bridge
        // sees the camelCase field names it reads (including the bot token).
        [Fact]
        public void SetConfigRequest_serializes_whole_settings_with_camelCase_fields() {
            var settings = new PluginSettings {
                BotToken = "abc", BotStatus = "hi", RandomFx = true, FxChance = 50,
                Normalize = false, NormalizeTarget = 18, AudioQualityIndex = 2,
            };
            var req = new SetConfigRequest<PluginSettings> { ReqId = 1, Config = settings };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(req, req.GetType(), opts));
            var root = doc.RootElement;
            Assert.Equal("SetConfig", root.GetProperty("op").GetString());
            var cfg = root.GetProperty("config");
            Assert.Equal("abc", cfg.GetProperty("botToken").GetString());
            Assert.Equal("hi", cfg.GetProperty("botStatus").GetString());
            Assert.True(cfg.GetProperty("randomFx").GetBoolean());
            Assert.Equal(50, cfg.GetProperty("fxChance").GetInt32());
            Assert.False(cfg.GetProperty("normalize").GetBoolean());
            Assert.Equal(18, cfg.GetProperty("normalizeTarget").GetInt32());
            Assert.Equal(2, cfg.GetProperty("audioQualityIndex").GetInt32());
        }

        [Fact]
        public void All_op_constants_are_distinct() {
            var ops = typeof(Op).GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetValue(null))
                .ToList();
            Assert.NotEmpty(ops);
            Assert.Equal(ops.Count, ops.Distinct().Count());
        }

        [Fact]
        public void Result_is_the_only_response_op() {
            var ops = typeof(Op).GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetValue(null));
            Assert.Contains(Op.Result, ops);
            foreach (var op in ops) {
                if (op != Op.Result) {
                    Assert.False(op.EndsWith("Result"), $"unexpected *Result op: {op}");
                }
            }
        }

        [Fact]
        public void Removed_ops_are_gone() {
            var ops = new System.Collections.Generic.HashSet<string>(
                typeof(Op).GetFields()
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                    .Select(f => (string)f.GetValue(null)));
            foreach (var removed in new[] { "SetGame", "SetNormalization", "SetAudioQuality", "Init", "Deinit" }) {
                Assert.DoesNotContain(removed, ops);
            }
        }

        [Fact]
        public void ProtocolVersion_is_positive() {
            Assert.True(ProtocolConstants.Version > 0);
        }

        [Fact]
        public void ProtocolVersion_matches_bridge() {
            // Tripwire: bump both this and PROTOCOL_VERSION in protocol.ts together.
            Assert.Equal(5, ProtocolConstants.Version);
        }
    }
}
