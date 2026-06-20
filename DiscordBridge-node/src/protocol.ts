// Wire protocol mirror of ACT_DiscordTriggers/Protocol/Protocol.cs. Keep both sides in sync.
// PROTOCOL_VERSION here must match ProtocolConstants.Version on the C# side.
//
// The contract has three channels:
//
//   Commands   .NET -> bridge, request/response. Each carries a reqId; the reply
//              is always the single `Result` envelope (below), correlated by reqId.
//   Config     .NET -> bridge, one `SetConfig` op carrying the whole plugin config
//              object. The bridge stores it and applies it (presence, fx, normalize,
//              bitrate). Adding a config field is additive and does NOT bump the
//              protocol version — the bridge ignores unknown fields and defaults
//              missing ones.
//   Notifications  bridge -> .NET push (BotReady / Log / Disconnected), no reqId.
//
// Two frame shapes share the outer 4-byte LE length prefix; the first byte of the
// payload tells them apart:
//
//   0x7B ('{')  -> UTF-8 JSON, dispatched by op string. Used for everything
//                  except SpeakPcm.
//   0x01        -> Binary SpeakPcm, plugin -> bridge only:
//                    [0x01]
//                    [reqId u32 LE]
//                    [sampleRate u32 LE]
//                    [bits u8]
//                    [channels u8]
//                    [raw PCM bytes...]   // remainder of payload
//                  Header is 11 bytes. Response is the JSON `Result` envelope with
//                  the matching reqId.
//
// SpeakFile is a normal JSON op carrying a file path; the bridge opens and streams
// the file itself (decoded + resampled to 48 kHz / 16-bit / stereo). Whether a
// random sound effect is applied is decided by the bridge from the current config
// (randomFx + fxChance) — it is NOT a per-clip flag on the wire.
//
// SpeakText is the ONNX-neural counterpart: a JSON op carrying only the text. The
// bridge synthesizes with the voice it learned from the last SetConfig (ttsParams),
// resamples to 48 kHz / 16-bit / stereo, then rejoins the same fx/normalize path.

export const PROTOCOL_VERSION = 6 as const;
export const MAX_FRAME_BYTES = 64 * 1024 * 1024;

export const FRAME_JSON_MARKER = 0x7B; // '{'
export const FRAME_BINARY_SPEAK_PCM = 0x01;
// [marker u8][reqId u32][sampleRate u32][bits u8][channels u8] = 11 bytes
export const BINARY_SPEAK_PCM_HEADER_BYTES = 11;

export const Op = {
    Hello: 'Hello',
    SetConfig: 'SetConfig',
    Connect: 'Connect',
    Shutdown: 'Shutdown',
    IsConnected: 'IsConnected',
    GetServers: 'GetServers',
    GetChannels: 'GetChannels',
    JoinChannel: 'JoinChannel',
    LeaveChannel: 'LeaveChannel',
    SpeakFile: 'SpeakFile',
    SpeakPcm: 'SpeakPcm',
    SpeakText: 'SpeakText',
    Result: 'Result',
    BotReady: 'BotReady',
    Log: 'Log',
    Disconnected: 'Disconnected',
} as const;

export type OpName = typeof Op[keyof typeof Op];

export type LogLevel = 'Info' | 'Warn' | 'Error';

export type ReqId = number | null;

export interface BaseRequest { op: OpName; reqId: ReqId }

// The configuration object the plugin pushes via SetConfig. The plugin sends its
// whole settings POCO; this interface is the subset the bridge actually reads
// (extra fields — token persistence flags, TTS voice/volume/speed, autoConnect —
// are ignored). The bridge owns all interpretation:
//   - randomFx + fxChance: rolled per clip, then a random effect is picked.
//   - normalize + normalizeTarget: targetdB is a positive magnitude; the bridge
//     negates it to a dBFS RMS target (e.g. 20 -> -20 dBFS).
//   - audioQualityIndex: 0/1/2 mapped to an Opus bitrate by the bridge.
export interface BridgeConfigView {
    botToken: string;
    botStatus: string;
    randomFx: boolean;
    fxChance: number;          // 0..100 (%)
    normalize: boolean;
    normalizeTarget: number;   // positive dB magnitude
    audioQualityIndex: number; // 0=Low, 1=Medium, 2=High
}

// Bridge-side defaults, used until the first SetConfig lands (pre-connect clips)
// and to fill any field missing from an incoming config. The interpreted values
// (randomFx/fxChance/normalize/normalizeTarget/audioQualityIndex) MUST match the
// PluginSettings defaults on the C# side. botToken/botStatus are deliberately
// empty: there is no usable default token, and an empty botStatus makes
// _applyStatus fall back to its own 'Playing with ACT Triggers' label.
export const DEFAULT_CONFIG_VIEW: BridgeConfigView = {
    botToken: '',
    botStatus: '',
    randomFx: false,
    fxChance: 25,
    normalize: true,
    normalizeTarget: 20,
    audioQualityIndex: 1,
};

export interface HelloRequest        extends BaseRequest { op: 'Hello'; protocolVersion: number }
export interface SetConfigRequest    extends BaseRequest { op: 'SetConfig'; config: BridgeConfigView; ttsParams?: Record<string, string> }
export interface ConnectRequest      extends BaseRequest { op: 'Connect' }
export interface IsConnectedRequest  extends BaseRequest { op: 'IsConnected' }
export interface GetServersRequest   extends BaseRequest { op: 'GetServers' }
export interface GetChannelsRequest  extends BaseRequest { op: 'GetChannels'; server: string }
export interface JoinChannelRequest  extends BaseRequest { op: 'JoinChannel'; server: string; channel: string }
export interface LeaveChannelRequest extends BaseRequest { op: 'LeaveChannel' }
export interface SpeakFileRequest    extends BaseRequest { op: 'SpeakFile'; path: string }
export interface SpeakTextRequest    extends BaseRequest { op: 'SpeakText'; text: string }
export interface ShutdownRequest     extends BaseRequest { op: 'Shutdown' }

export type Request =
    | HelloRequest | SetConfigRequest | ConnectRequest
    | IsConnectedRequest | GetServersRequest | GetChannelsRequest
    | JoinChannelRequest | LeaveChannelRequest | SpeakFileRequest | SpeakTextRequest | ShutdownRequest;

// Single response envelope for every command/config op. C# correlates responses by
// reqId alone, so one op string ('Result') suffices; the optional `data` carries
// op-specific payload for the few queries that return values.
export interface HelloData     { bridgeVersion: string }
export interface ConnectedData { connected: boolean }
export interface ServersData   { servers: string[] }
export interface ChannelsData  { channels: string[] }
export type ResultData = HelloData | ConnectedData | ServersData | ChannelsData;

export interface ResultFrame {
    op: 'Result';
    reqId: ReqId;
    ok: boolean;
    error: string;
    data?: ResultData;
}

export interface BotReadyNotification     { op: 'BotReady' }
export interface LogNotification          { op: 'Log'; level: LogLevel; message: string }
export interface DisconnectedNotification { op: 'Disconnected'; reason: string }

export type Notification = BotReadyNotification | LogNotification | DisconnectedNotification;

export type OutboundFrame = ResultFrame | Notification;
