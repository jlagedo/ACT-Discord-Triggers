# Audio decode test fixtures

Small (≤ 5 s) audio clips used by the bridge's decode tests (see
`docs/AUDIO-PIPELINE.md`, **Phase 1 — Unified decode via `audio-decode`**). They
exercise the formats Triggernometry can hand to `PlaySoundMethod` (WAV / MP3 /
OGG / FLAC) plus a couple of edge cases (8-bit PCM, a truncated/corrupt file for
the graceful `{ok:false,error}` path).

Everything here is **public domain or CC0** — safe to commit and redistribute.
Total ≈ 0.8 MiB.

## Files

| File | Codec / container | Rate | Ch | Bits | Dur | Source | License |
|------|-------------------|------|----|------|-----|--------|---------|
| `mp3-44100-mono.mp3` | MP3 | 44.1k | 1 | — | 4.99s | exaile `click.mp3` | Public domain |
| `mp3-44100-stereo.mp3` | MP3 | 44.1k | 2 | — | 5.03s | exaile `left_channel.mp3` (trimmed) | Public domain |
| `vorbis-44100-mono.ogg` | OGG/Vorbis | 44.1k | 1 | — | 4.99s | exaile `gapless_noise1.ogg` | Public domain |
| `vorbis-48000-stereo-bell.ogg` | OGG/Vorbis | 48k | 2 | — | 1.30s | CC0-Public-Domain-Sounds `bell_01` | CC0 |
| `vorbis-48000-stereo-gong.ogg` | OGG/Vorbis | 48k | 2 | — | 1.42s | CC0-Public-Domain-Sounds `gong_01` | CC0 |
| `vorbis-48000-stereo-explosion.ogg` | OGG/Vorbis | 48k | 2 | — | 0.56s | CC0-Public-Domain-Sounds `explosion` | CC0 |
| `vorbis-48000-stereo-glass.ogg` | OGG/Vorbis | 48k | 2 | — | 0.39s | CC0-Public-Domain-Sounds `glass_01` | CC0 |
| `vorbis-48000-stereo-door.ogg` | OGG/Vorbis | 48k | 2 | — | 0.45s | CC0-Public-Domain-Sounds `door_open` | CC0 |
| `flac-44100-mono.flac` | FLAC | 44.1k | 1 | 16 | 5.00s | exaile `gapless_3.flac` (trimmed) | Public domain |
| `wav-44100-mono-s16.wav` | WAV / PCM s16le | 44.1k | 1 | 16 | 3.00s | exaile `noise_tone.wav` (trimmed, →16-bit) | Public domain |
| `wav-44100-mono-u8.wav` | WAV / PCM u8 | 44.1k | 1 | 8 | 3.07s | exaile `noise_tone.wav` (trimmed) | Public domain |
| `corrupt-truncated.ogg` | OGG/Vorbis (truncated) | — | — | — | n/a | exaile `gapless_noise1.ogg` (first 2 KiB only) | Public domain |

### Coverage notes

- **44.1k is deliberately over-represented.** The doc flags 44.1k MP3 as the
  specific QA target for the linear-interpolation resampler's weakest (non-integer
  ratio) case. `mp3-44100-mono.mp3` / `mp3-44100-stereo.mp3` are the files to listen to.
- **48k stereo** clips (the CC0 SFX) pass straight through with no resample, and
  cover the multi-channel path.
- **WAV** ships in both 8-bit unsigned (`u8`, the unusual case) and 16-bit signed
  (`s16le`, the common case) so the WAV-unification path is tested at two bit depths.
- **`corrupt-truncated.ogg`** is the first 2 KiB of a valid PD OGG — enough to
  look like an OGG by extension/magic but truncated mid-stream so it cannot be
  decoded (ffmpeg: "End of file", 0 samples). Use it to assert the decoder
  returns `{ok:false,error}` rather than throwing/crashing.
- Not yet covered: Opus and M4A/AAC (the doc lists m4a/aac as "maybe"). Add clean
  fixtures here if those codecs get wired into `audio-decode`.

## Provenance

- **exaile-test-files** — <https://github.com/exaile/exaile-test-files>. Repo
  states all files are "contributed to the public domain, or were derived from
  sources in the public domain."
- **CC0-Public-Domain-Sounds** — <https://github.com/lavenderdotpet/CC0-Public-Domain-Sounds>
  (`100-CC0-SFX/`). Released under Creative Commons CC0 1.0 (public-domain dedication).

Files marked *(trimmed)* were shortened to ≤ 5 s from their public-domain
originals with `ffmpeg -t`; the 16-bit WAV was additionally converted from the
8-bit PCM source (`-c:a pcm_s16le`). No license obligations attach to either
public-domain or CC0 material.
