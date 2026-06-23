<div align="center">

<img src="docs/logo.png" alt="ACT Discord Triggers logo" width="160" />

# ACT Discord Triggers

[![Build Plugin](https://github.com/jlagedo/ACT-Discord-Triggers/actions/workflows/ci.yml/badge.svg)](https://github.com/jlagedo/ACT-Discord-Triggers/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An [Advanced Combat Tracker](https://advancedcombattracker.com/) (ACT) plugin
that plays your triggers — text-to-speech and sound effects — through a Discord
bot in a voice channel, so your whole party hears them.

Works with ACT **Custom Triggers**, **Triggernometry**, and **Triggevent**.

</div>

> 🔀 **Maintained fork.** This is the actively maintained continuation of
> [Makar8000/ACT-Discord-Triggers](https://github.com/Makar8000/ACT-Discord-Triggers),
> revived after Discord's DAVE encryption rollout broke the original in early
> 2026. See [Acknowledgments](#acknowledgments) for full credit to the original
> author.

## Screenshots

<!-- Screenshots predate the Output-tab merge (Main + General → Output) and need regeneration. -->

| Output — mode & connection | Output — voice channel |
| :---: | :---: |
| ![Output settings tab — output mode](docs/screenshot-main.png) | ![Output settings tab — Discord connection](docs/screenshot-general.png) |
| **Text-to-Speech — engine & voice** | **Sound — effects & mastering** |
| ![Text-to-Speech settings tab](docs/screenshot-tts.png) | ![Sound settings tab](docs/screenshot-sound.png) |

## Features

- 🖥️ **Two output modes** — stream triggers to a **Discord voice channel** for
  the whole party, or play them **locally on this PC's speakers** with no bot or
  Discord login required. Switch on the **Output** tab.
- 🔊 **TTS in voice chat** — fire trigger text-to-speech straight into a Discord
  voice channel.
- 🧠 **Neural voices** — an optional **ONNX** engine plays natural-sounding
  neural voices (60+ across several languages), downloaded on demand. Or stick
  with the offline **Windows (SAPI)** system voices — no download, nothing extra
  to install.
- 🎵 **Sound-file triggers** — play sound effects through the bot in `.wav`,
  MP3, OGG, or FLAC (MP3/OGG/FLAC via Triggernometry or Triggevent).
- 🎚️ **Concurrent playback** — overlapping triggers mix together instead of
  queueing, so nothing gets dropped or delayed.
- 🎲 **Random sound effects** — optionally add a random audio effect (echo,
  reverb, pitch-shift, and more) to triggers as they play.
- 📈 **Auto-leveling** — perceptual **LUFS** loudness matching evens out the
  volume so every callout and sound plays at a consistent level.
- 🎛️ **Master limiter** — a look-ahead brickwall limiter rides overlapping
  triggers down to a true-peak-safe ceiling, so simultaneous sounds never clip.
- 📡 **Selectable audio quality** — choose the Discord voice bitrate (48 / 96 /
  128 kbps) to trade bandwidth for clarity.
- 🔐 **Discord DAVE support** — speaks Discord's end-to-end voice encryption,
  which is now required to join voice channels.

## Requirements

- Windows
- [Advanced Combat Tracker](https://advancedcombattracker.com/)
- A Discord bot token ([setup guide below](#setup))

> Node.js is **bundled** in the release archive — you do **not** need to install
> it separately.

## Installation

1. Download the latest `ACT_DiscordTriggers-*.zip` from the
   [Releases page](https://github.com/jlagedo/ACT-Discord-Triggers/releases).
2. Right-click the downloaded zip → **Properties** → tick **Unblock** → OK.
   (Windows flags `node.exe`/DLLs downloaded from the internet; skipping this
   can trip SmartScreen or antivirus.)
3. Extract it into your ACT plugins folder:
   `%AppData%\Advanced Combat Tracker\Plugins`. You should end up with a
   `Plugins\ACT_DiscordTriggers\` folder containing `ACT_DiscordTriggers.dll`,
   `node.exe`, `bundle.js`, and `node_modules\`. **Keep all of these files
   together in that folder.**
4. In ACT, open the **Plugins** tab → **Browse…**, select
   `ACT_DiscordTriggers.dll` inside that folder, and click **Add/Enable
   Plugin**.

## Setup

Follow the
[First-Time Setup Guide](https://github.com/jlagedo/ACT-Discord-Triggers/wiki/First-Time-Setup-Guide)
to create a Discord bot, invite it to your server, and connect it to the
plugin. From there:

- [Settings & Configuration](https://github.com/jlagedo/ACT-Discord-Triggers/wiki/Settings-&-Configuration)
  — every setting on the plugin tab, explained.
- [Trigger Software](https://github.com/jlagedo/ACT-Discord-Triggers/wiki/Trigger-Software)
  — use it with ACT Custom Triggers, Triggernometry, and Triggevent.
- [Common Questions & Issues](https://github.com/jlagedo/ACT-Discord-Triggers/wiki/Common-Questions-&-Issues)
  — troubleshooting and FAQ.

## Updating

Replace the whole plugin folder with the new release — don't just swap the
`.dll`. The plugin runs alongside the bundled `node.exe` / `bundle.js` /
`node_modules/`, and those are updated together.

---

## For developers

The plugin targets .NET Framework 4.8 (the only runtime ACT loads), but Discord
voice now runs in a separate Node.js process. This is what makes DAVE possible
without dropping ACT compatibility:

```
ACT (net48) ─loads─▶ ACT_DiscordTriggers.dll (net48)
                            │ spawns
                            ▼
                     node.exe + bundle.js (discord.js + @snazzah/davey)
                            ▲
                            │ named pipe (length-prefixed JSON frames)
                            ▼
                ACT_DiscordTriggers.dll IPC client
```

Discord enforced [DAVE](https://daveprotocol.com/) end-to-end encryption on
voice in early 2026, and [Discord.Net 3.19](https://github.com/discord-net/Discord.Net/releases/tag/3.19.1)
(the first version with DAVE) dropped .NET Framework support — so the old
in-process voice path became unfixable. Moving voice into a bundled Node bridge
keeps ACT on net48 while staying current with Discord. The full design rationale
and alternatives considered are documented in
[`CLAUDE.md`](CLAUDE.md).

### The audio chain

How a trigger becomes Opus packets — the complete signal flow from C# synthesis
through the bridge's DSP (r8brain resampling, LUFS auto-leveling, the float64
mix bus, the look-ahead master limiter) to the Discord voice socket, with
diagrams and a full numeric reference — is documented for contributors and
audio engineers in [`docs/AUDIO-CHAIN.md`](docs/AUDIO-CHAIN.md). The ranked
roadmap for taking the realtime path pro-grade lives in
[`docs/AUDIO-PIPELINE.md`](docs/AUDIO-PIPELINE.md).

### Building

One command from a clean clone produces `release/` with everything an end user
drops into ACT's plugins directory:

```
cd DiscordBridge-node && npm ci && cd ..
pwsh ./build.ps1
```

`build.ps1` builds the plugin (`dotnet build` — net48 reference assemblies
auto-restore via NuGet, Costura.Fody merges into a single DLL), type-checks and
bundles the bridge, copies `node.exe`, stages the external `node_modules/`, runs
a spawn self-test (asserts `BRIDGE_READY`), and assembles `release/`.

For bridge-only iteration, use the npm scripts in `DiscordBridge-node/`:
`npm run typecheck`, `npm run bundle`, `npm test`.

### Tests

```
dotnet test ACT_DiscordTriggers.Tests/ACT_DiscordTriggers.Tests.csproj   # C# (net48): protocol + IPC + integration
cd DiscordBridge-node && npm test         # JS (tsx + node:test)
```

Integration tests require the bridge to be built first (they spawn
`DiscordBridge-node/dist/node.exe` with `bundle.js`).

### Built with

- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework) (plugin + tests)
- [Node.js 22+](https://nodejs.org/) (bridge runtime)
- [discord.js](https://github.com/discordjs/discord.js) + [@discordjs/voice](https://github.com/discordjs/voice) (bridge)
- [@snazzah/davey](https://github.com/snazzah/davey) (DAVE E2EE)

## Acknowledgments

This project began as a fork of
[Makar8000/ACT-Discord-Triggers](https://github.com/Makar8000/ACT-Discord-Triggers)
by [@Makar8000](https://github.com/Makar8000), who created and maintained the
original plugin. The DAVE rewrite landed as
[PR #82](https://github.com/Makar8000/ACT-Discord-Triggers/pull/82) upstream,
after which maintenance was handed off to this fork. All credit for the original
design and years of upkeep goes to Makar8000 — thank you.

## License

[MIT](LICENSE) — Copyright (c) 2017 Marcus Terry, Copyright (c) 2026 João Amaro
Lagedo.
