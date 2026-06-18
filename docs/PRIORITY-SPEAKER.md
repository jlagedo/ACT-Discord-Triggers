# Why We Can't Add "Priority Speaker" (Cleanly)

> **TL;DR** — Discord's **Priority Speaker** is a *user-client* feature, not a *bot* capability. The flag exists in the protocol and is documented, but Discord has never confirmed it works for bot accounts, no major library exposes it for bots, and the one person who tested the raw flag found that Discord patched the unprivileged path. We can't ship it in a way that reliably works, so we don't pretend to. This document explains the whole story so you don't have to take our word for it.

If you opened an issue asking for Priority Speaker, this is the long answer. You are not the first — people have been asking for exactly this since **2018** — and the reason it's never landed isn't laziness or oversight. It's a genuine, well-documented dead end. Here's everything.

---

## What you're actually asking for

You want ACT callouts to **cut through** — when the bot speaks, everyone else in the voice channel gets quieter so the callout is unmistakable. In Discord terms, that's the **Priority Speaker** feature:

> *"While users with this permission are talking in a voice channel, the volume of other users will be lowered."*
> — [Discord, Permissions on Discord](https://discord.com/community/permissions-on-discord-discord)

Discord describes it as *"giving someone the ability to speak at volume 11, when everyone else is turned down to volume 2"* ([Discord Help Center](https://support.discord.com/hc/en-us/articles/360011876531-Setting-up-Priority-Speaker)). It's a great fit for raid callouts — which is exactly who Discord built it for.

So why can't a bot just *use* it?

---

## The short technical reason

The Discord voice protocol carries a **speaking bitmask** in its [Opcode 5 "Speaking" payload](https://discord.com/developers/docs/topics/voice-connections):

| Flag | Value | Meaning |
|------|-------|---------|
| Microphone | `1 << 0` (1) | Normal voice transmission |
| Soundshare | `1 << 1` (2) | Context audio for video |
| **Priority** | `1 << 2` (4) | **Priority speaker — lowers others' audio** |

The flag is real and officially documented. But two things make it unusable for us:

1. **The library we depend on (`@discordjs/voice`) doesn't expose it.** Its `setSpeaking()` is hardcoded to send `1` (Microphone) and nothing else — there is no parameter to request Priority.
2. **Even if we sent it, Discord has never confirmed the Priority flag does anything for a *bot* account.** The ducking effect is enforced by each *listener's* Discord client, and historically only honored from a *human* using push-to-talk with the role permission.

That's the whole problem in two sentences. The rest of this document is the receipts.

---

## Receipt #1 — the library hardcodes it away

ACT-Discord-Triggers sends voice through a Node bridge built on `@discordjs/voice`. Here is the actual code that sends the speaking flag (`@discordjs/voice`, `Networking.ts`):

```ts
public setSpeaking(speaking: boolean) {            // <- boolean, not flags
  const state = this.state;
  if (state.code !== Ready) return;
  if (state.connectionData.speaking === speaking) return;
  state.connectionData.speaking = speaking;
  state.ws.sendPacket({
    op: VoiceOpcodes.Speaking,
    d: {
      speaking: (speaking ? 1 : 0) as VoiceSpeakingFlags,  // <- HARDCODED to Microphone(1)
      delay: 0,
      ssrc: state.connectionData.ssrc,
    },
  });
}
```

The type system *knows* Priority exists — `discord-api-types` defines `VoiceSpeakingFlags { Microphone = 1, Soundshare = 2, Priority = 4 }` — but the value is pinned to `1`. There is no public API to OR in the `4` bit. `setSpeaking()` is also called automatically by the audio player on every packet; it's plumbing for the green "speaking" indicator, not a feature surface.

**It used to be possible.** discord.js **v12** (the monolithic pre-2021 version) had a `Speaking` BitField with `PRIORITY_SPEAKING` and let you call `connection.setSpeaking(Speaking.FLAGS.PRIORITY_SPEAKING | Speaking.FLAGS.SPEAKING)`. When voice was extracted into the separate `@discordjs/voice` package for **discord.js v13** (released **August 6, 2021**), `setSpeaking` was rewritten as boolean-only and the flags capability was **silently dropped**. It has never returned, and there is no open issue tracking its restoration.

---

## Receipt #2 — the feature is user-only by design

Priority Speaker shipped on **August 2, 2018** ([Discord's launch tweet](https://twitter.com/discord/status/1025166779281076224): *"Raid leaders across the world cheer!"*). From day one it has carried constraints that make no sense for a bot:

- **Push-to-talk only.** Discord's own docs: *"Priority Speaker only works when using Push to Talk as the input mode. Priority Speaker does not work when using Voice Activity."* A bot streaming Opus has no push-to-talk keybind.
- **Desktop client only.** *"Priority Speaker is currently only supported for the desktop app."*
- **Enforced by listeners.** The volume ducking happens locally in each *listening* user's Discord client when it receives a priority-flagged packet from a permitted user — it's not something the speaker's connection makes happen on its own.

The feature was built for a human raid leader holding a key. Discord froze it there and never extended it — there are years of unanswered community requests asking merely for *"Priority Speaker on Voice Activation"* for **humans**, let alone bots.

---

## Receipt #3 — every library treats it as "unconfirmed for bots"

This isn't a discord.js quirk. Look at how every major library handles the flag:

| Library | Exposes the Priority *speaking flag*? |
|---|---|
| **discord.js v12** | ✅ Yes — then **removed** in v13 (Aug 2021) |
| **`@discordjs/voice`** (what we use) | ❌ No — hardcoded to `1` |
| **JDA** (Java) | ⚠️ Yes — but stamped *"@incubating: Discord has not officially confirmed that this feature will be available to bots"* |
| **Discord.Net** (C#) | ⚠️ Exposes the enum, **no public setter** |
| **discord.py** (Python) | ⚠️ Exposes the *permission*, not a speaking-flag setter |

The most authoritative statement in the entire ecosystem comes from JDA's own source, attached to its `setSpeakingMode` method:

> **`@incubating Discord has not officially confirmed that this feature will be available to bots.`**

JDA implemented it, then explicitly declined to promise it works — and left it in that limbo for **~8 years**. That's not an accident. It's the collective verdict: the flag is sendable, but nobody can confirm it produces the ducking effect for a bot account.

---

## Receipt #4 — the one person who actually tested the raw flag

After exhaustive searching, exactly **one** public, primary-source statement exists of a human sending the actual Priority flag and reporting a result — the README of the [`kaitodsc/Priority-Plugins-PATCH`](https://github.com/kaitodsc/Priority-Plugins-PATCH) project:

> *"...one work by sending opcode 5 speaking payload, and the other one by sending `1<<2`, according to the Api doc of discord, **it actually gives you priority, but it's patched now**."*

Decoded: they sent the real Priority flag, it *did* produce ducking — and then **Discord patched it**. Crucially, this was a *client mod on a user account* trying to get priority **without** the role permission, and Discord closed that path. It tells us nothing reassuring about a permissioned bot; it tells us Discord actively polices this flag.

And the most damning evidence of all: a developer who built an entire bot literally named ["Priority-Speaker-Sam"](https://github.com/Midshadow77/Priority-Speaker-Sam) **did not use the Priority flag at all.** When a "priority speaker" talks, that bot **server-mutes everyone else** and unmutes them afterward:

```js
connection.on('speaking', this.speakingCallback);
// ...when a priority user speaks:
member.setMute(!CLIENT.prioritySpeakers.has(member.id));  // hard-mute everyone else
// ...when they stop:
member.setMute(false);
```

Someone who wanted *exactly this feature*, badly enough to build a whole bot for it, reached for `setMute()` instead of `speaking: 4`. That's your empirical answer: the real flag doesn't work for bots, so the people who need the effect fake it another way.

---

## The lineage — you are not alone, and not new

This exact request, in this exact (FFXIV / ACT) context, has been filed repeatedly:

- **2018** — [ACT.Hojoring #114](https://github.com/anoyetta/ACT.Hojoring/issues/114): *"people can talk over the discord bot... callouts get missed. If the Yukkuri bot used Priority Speaker, everyone would always hear the callouts."*
- **2019** — [Makar8000/ACT-Discord-Triggers #54](https://github.com/Makar8000/ACT-Discord-Triggers/issues/54): same request, closed with *"Discord.Net does not support priority speaker mode yet."* (This is the upstream of this very project.)
- **2025** — [discord.js-selfbot-v13 #1637](https://github.com/aiko-chan-ai/discord.js-selfbot-v13/issues/1637): achieved only via a **selfbot** (an automated *user* account, which violates Discord's ToS) + push-to-talk.

Seven years, the same wall, every time.

---

## So how *do* music bots "duck" the volume?

You've probably seen bots like **[Muse](https://github.com/museofficial/muse)** lower their music when people talk (`/config set-reduce-vol-when-voice`). That is **not** Priority Speaker — it's the **opposite** mechanism. Those bots *listen* for who's speaking (via the voice receiver's speaking events) and turn down **their own** output, then restore it. It needs no permission and no flag, and it works for every listener — but it makes the bot quieter, not the humans.

The only way to make *humans* quieter from a bot is the "Priority-Speaker-Sam" route: **server-mute everyone else** while the bot speaks. That genuinely works, but:

- It requires the bot's role to have **Mute Members** permission.
- It's a **hard mute**, not Discord's smooth volume duck — far more disruptive.
- A crash mid-callout could leave people muted (needs a fail-safe unmute).
- It mutes *everyone*, with no per-listener control.

We consider that too heavy-handed and too failure-prone to ship as a default "make callouts louder" feature. If you genuinely want it, that's the only path that works for a real bot — but it's a deliberate, eyes-open tradeoff, not a clean equivalent of Priority Speaker.

---

## The bottom line

| Question | Answer |
|---|---|
| Does the Priority flag exist? | **Yes** — documented in Discord's voice API (`1 << 2`). |
| Can `@discordjs/voice` send it? | **No** — hardcoded to `1`; removed from discord.js in v13 (Aug 2021). |
| Does it work for *bot accounts*? | **Unconfirmed in 7+ years.** JDA explicitly won't promise it; the only tested path was an unprivileged client trick that Discord patched. |
| Is there a clean alternative? | **No.** Auto-ducking lowers the *bot*, not humans; server-muting works but is a blunt, permission-heavy, crash-risky hammer. |
| Will we add it? | **Not as "Priority Speaker."** We won't ship a feature that the platform doesn't reliably support for bots. |

This isn't us refusing a five-minute change. It's a real limitation of Discord's platform that has defeated every library and every person who's tried for the better part of a decade. If Discord ever ships official bot support for priority speaking — or `@discordjs/voice` restores the flag *and* it's confirmed to work for bots — we'll revisit immediately. Until then, the honest answer is: **we can't do this cleanly, so we won't pretend to.**

---

### Sources

- Discord — [Voice Connections (Speaking flags)](https://discord.com/developers/docs/topics/voice-connections)
- Discord — [Setting up Priority Speaker (Help Center)](https://support.discord.com/hc/en-us/articles/360011876531-Setting-up-Priority-Speaker)
- discord.js — [`Networking.ts` `setSpeaking`](https://github.com/discordjs/discord.js/blob/main/packages/voice/src/networking/Networking.ts) · [v13.0.0 release, Aug 6 2021](https://github.com/discordjs/discord.js/releases/tag/13.0.0)
- JDA — [`SpeakingMode.java`](https://github.com/discord-jda/JDA/blob/master/src/main/java/net/dv8tion/jda/api/audio/SpeakingMode.java) · [`AudioManager.java` (the "@incubating / not confirmed for bots" note)](https://github.com/discord-jda/JDA/blob/master/src/main/java/net/dv8tion/jda/api/managers/AudioManager.java)
- [kaitodsc/Priority-Plugins-PATCH](https://github.com/kaitodsc/Priority-Plugins-PATCH) — the only public test of the raw flag ("patched now")
- [Midshadow77/Priority-Speaker-Sam](https://github.com/Midshadow77/Priority-Speaker-Sam) — a "priority speaker" bot that server-mutes instead of using the flag
- [ACT.Hojoring #114](https://github.com/anoyetta/ACT.Hojoring/issues/114) · [Makar8000/ACT-Discord-Triggers #54](https://github.com/Makar8000/ACT-Discord-Triggers/issues/54) · [discord.js-selfbot-v13 #1637](https://github.com/aiko-chan-ai/discord.js-selfbot-v13/issues/1637)
- [Muse](https://github.com/museofficial/muse) — `set-reduce-vol-when-voice` (the auto-duck alternative)
