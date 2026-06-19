using System.Text.Json.Serialization;

namespace ACT_DiscordTriggers.Core.Protocol {

    public static class ProtocolConstants {
        public const int Version = 5;
    }

    // Three channels share one op set:
    //   Commands  (.NET -> bridge, request/response; reply is always Result)
    //   Config    (.NET -> bridge, the single SetConfig op carrying the whole
    //              settings POCO; adding a config field is additive and does NOT
    //              bump Version)
    //   Notifications (bridge -> .NET push: BotReady / Log / Disconnected)
    public static class Op {
        public const string Hello = "Hello";
        public const string SetConfig = "SetConfig";
        public const string Connect = "Connect";
        public const string Shutdown = "Shutdown";
        public const string IsConnected = "IsConnected";
        public const string GetServers = "GetServers";
        public const string GetChannels = "GetChannels";
        public const string JoinChannel = "JoinChannel";
        public const string LeaveChannel = "LeaveChannel";
        public const string SpeakFile = "SpeakFile";
        public const string SpeakPcm = "SpeakPcm";

        // The single response envelope op. Every command/config reply is "Result",
        // correlated by reqId (PipeClient matches on reqId, ignoring the op).
        public const string Result = "Result";

        // Notifications (server-pushed; no reqId).
        public const string BotReady = "BotReady";
        public const string Log = "Log";
        public const string Disconnected = "Disconnected";
    }

    // Marker interface for request DTOs. Lets PipeClient.SendAsync set ReqId
    // without reflection.
    public interface IBridgeRequest {
        int? ReqId { get; set; }
    }

    // ---- Requests ----

    public class HelloRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Hello;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    }

    // Config push. Carries the whole plugin settings object; the bridge reads the
    // fields it needs and ignores the rest. Kept generic so the protocol types stay
    // decoupled from the concrete PluginSettings POCO — PipeClient.SendFrameAsync
    // serializes by runtime type, so the closed generic serializes the full POCO.
    public class SetConfigRequest<TConfig> : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.SetConfig;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("config")] public TConfig Config { get; set; }
    }

    public class ConnectRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Connect;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
    }

    public class ShutdownRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Shutdown;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
    }

    public class IsConnectedRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.IsConnected;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
    }

    public class GetServersRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.GetServers;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
    }

    public class GetChannelsRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.GetChannels;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("server")] public string Server { get; set; } = "";
    }

    public class JoinChannelRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.JoinChannel;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("server")] public string Server { get; set; } = "";
        [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    }

    public class LeaveChannelRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.LeaveChannel;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
    }

    public class SpeakFileRequest : IBridgeRequest {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.SpeakFile;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = "";
    }

    // SpeakPcm is sent as a length-prefixed BINARY frame, not JSON.
    // See PipeClient.SendSpeakPcmAsync / pipe-server.ts _handleBinarySpeakPcm.
    // Layout (after the outer 4-byte LE length): [0x01][reqId u32 LE][sampleRate u32 LE][bits u8][channels u8][raw PCM...]
    // Header is 11 bytes. Whether a random effect is applied is decided by the
    // bridge from the current config, not per clip. Response is the JSON Result
    // envelope: { op:"Result", reqId, ok, error }.

    // ---- Response envelope ----

    // Every command/config reply. C# correlates by reqId, so the op is always
    // "Result". Queries that return data use BridgeResponse<TData> below.
    public class BridgeResponse {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Result;
        [JsonPropertyName("reqId")] public int? ReqId { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; } = "";
    }

    public class BridgeResponse<TData> : BridgeResponse {
        [JsonPropertyName("data")] public TData Data { get; set; }
    }

    public class HelloData {
        [JsonPropertyName("bridgeVersion")] public string BridgeVersion { get; set; } = "";
    }

    public class ConnectedData {
        [JsonPropertyName("connected")] public bool Connected { get; set; }
    }

    public class ServersData {
        [JsonPropertyName("servers")] public string[] Servers { get; set; } = new string[0];
    }

    public class ChannelsData {
        [JsonPropertyName("channels")] public string[] Channels { get; set; } = new string[0];
    }

    // ---- Notifications ----

    public class LogNotification {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Log;
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("level")] public string Level { get; set; } = "Info";
    }

    public class BotReadyNotification {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.BotReady;
    }

    public class DisconnectedNotification {
        [JsonPropertyName("op")] public string Op { get; set; } = Protocol.Op.Disconnected;
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
}
