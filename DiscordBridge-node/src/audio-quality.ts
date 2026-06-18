// Opus encoder bitrate config. Pushed by the plugin via SetAudioQuality and
// applied to the live encoder with prism's setBitrate (see discord-host.ts).
//
// Only bitrate is exposed: it's the single Opus knob reachable through the
// supported discord.js/prism public API (resource.encoder.setBitrate). Signal
// type and complexity are NOT exposed by prism and are deliberately out of
// scope (they'd require reaching into opusscript internals).
//
// prism's setBitrate clamps to [16000, 128000]; the plugin's UI tiers (Low
// 48000 / Medium 96000 / High 128000) all sit inside that range. The C#
// DiscordClient mirrors DEFAULT_AUDIO_BITRATE — keep the two sides in step.

export const DEFAULT_AUDIO_BITRATE = 96000;

// prism setBitrate clamp bounds; we clamp here too so a bad push can't reach
// the encoder with an out-of-range value.
export const MIN_AUDIO_BITRATE = 16000;
export const MAX_AUDIO_BITRATE = 128000;

export function clampBitrate(bitrate: number): number {
    if (!Number.isFinite(bitrate)) return DEFAULT_AUDIO_BITRATE;
    return Math.min(MAX_AUDIO_BITRATE, Math.max(MIN_AUDIO_BITRATE, Math.round(bitrate)));
}
