// Regenerates the embedded ONNX voice catalog:
//   ACT_DiscordTriggers.Core/Tts/onnx-voices.json
//
// Piper rows are pulled from the upstream rhasspy/piper-voices `voices.json`
// manifest (filtered to our shipped locales + medium/high tiers) so the list
// never drifts from upstream. Kokoro rows are hand-seeded below from the
// hexgrad/Kokoro-82M grades + the sherpa-onnx kokoro-multi-lang speaker map.
//
// Run:  node tools/gen-onnx-catalog/gen.mjs
// No dependencies — Node 18+ (built-in fetch). Network access required.

import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const SCHEMA_VERSION = 1;
const VOICES_JSON_URL =
  "https://huggingface.co/rhasspy/piper-voices/resolve/main/voices.json";

// Locales we ship (Piper `language.code` form). Japanese is deliberately excluded.
const LOCALES = new Set([
  "en_US", "en_GB", "fr_FR", "de_DE", "es_ES", "es_MX", "pt_BR", "ru_RU",
]);
// Friendly group label per locale, shown as the picker's section header
// (language name + hyphenated lowercase locale, e.g. "Portuguese (pt-br)").
const LOCALE_NAMES = {
  en_US: "English (en-us)",
  en_GB: "English (en-gb)",
  fr_FR: "French (fr-fr)",
  de_DE: "German (de-de)",
  es_ES: "Spanish (es-es)",
  es_MX: "Spanish (es-mx)",
  pt_BR: "Portuguese (pt-br)",
  ru_RU: "Russian (ru-ru)",
};
const localeName = (locale) =>
  LOCALE_NAMES[locale] || `${locale} (${locale.replace("_", "-").toLowerCase()})`;
// Only ship the 22.05 kHz tiers; x_low/low are 16 kHz and dropped.
const QUALITIES = new Set(["medium", "high"]);

// A few sensible defaults flagged per language so undecided users have a pick.
const RECOMMENDED_PIPER = new Set([
  "en_US-amy-medium", "en_US-ryan-high", "en_GB-alan-medium",
  "fr_FR-tom-medium", "de_DE-thorsten-medium", "es_ES-davefx-medium",
  "es_MX-ald-medium", "pt_BR-faber-medium", "ru_RU-ruslan-medium",
]);

// Kokoro: one downloadable pack (kokoro-multi-lang-v1_0) serves every speaker.
// sid is the speaker index in that model; grade is the hexgrad/Kokoro-82M
// VOICES.md letter grade. Seeded to the top English voices (a male set is kept
// even at C+ so the list isn't female-only) plus the three pt-BR speakers.
const KOKORO_PACK = "kokoro-multi-lang-v1_0";
const KOKORO_PACK_MB = 333;
// espeak-ng voice id per Kokoro locale, baked into each row so the bridge never
// computes it at runtime: an unknown espeak voice hard-exits the whole sherpa
// process (no catchable error). "" = use the model's lexicon (American English).
// There is no plain "en-gb"; British is "en-gb-x-rp".
const KOKORO_LANG = { en_US: "", en_GB: "en-gb-x-rp", pt_BR: "pt-br" };
const KOKORO = [
  // en_US (American)
  { sid: 3, name: "Heart", locale: "en_US", grade: "A", gender: "female", recommended: true },
  { sid: 2, name: "Bella", locale: "en_US", grade: "A-", gender: "female" },
  { sid: 6, name: "Nicole", locale: "en_US", grade: "B-", gender: "female" },
  { sid: 1, name: "Aoede", locale: "en_US", grade: "C+", gender: "female" },
  { sid: 5, name: "Kore", locale: "en_US", grade: "C+", gender: "female" },
  { sid: 9, name: "Sarah", locale: "en_US", grade: "C+", gender: "female" },
  { sid: 14, name: "Fenrir", locale: "en_US", grade: "C+", gender: "male" },
  { sid: 16, name: "Michael", locale: "en_US", grade: "C+", gender: "male" },
  { sid: 18, name: "Puck", locale: "en_US", grade: "C+", gender: "male" },
  // en_GB (British)
  { sid: 21, name: "Emma", locale: "en_GB", grade: "B-", gender: "female", recommended: true },
  { sid: 22, name: "Isabella", locale: "en_GB", grade: "C", gender: "female" },
  { sid: 26, name: "George", locale: "en_GB", grade: "C", gender: "male" },
  { sid: 25, name: "Fable", locale: "en_GB", grade: "C", gender: "male" },
  // pt_BR
  { sid: 42, name: "Dora", locale: "pt_BR", grade: "C", gender: "female", recommended: true },
  { sid: 43, name: "Alex", locale: "pt_BR", grade: "C", gender: "male" },
  { sid: 44, name: "Santa", locale: "pt_BR", grade: "C", gender: "male" },
];

const titleCase = (s) =>
  s.replace(/_/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());

function onnxSizeMB(files) {
  const onnx = Object.entries(files).find(([p]) => p.endsWith(".onnx"));
  if (!onnx) return 0;
  return Math.round(onnx[1].size_bytes / 1e6);
}

async function buildPiper() {
  const res = await fetch(VOICES_JSON_URL);
  if (!res.ok) throw new Error(`voices.json fetch failed: ${res.status}`);
  const manifest = await res.json();
  const rows = [];
  for (const v of Object.values(manifest)) {
    const locale = v.language.code;
    if (!LOCALES.has(locale) || !QUALITIES.has(v.quality)) continue;
    const id = `vits-piper-${v.key}`; // matches the k2-fsa tts-models release name
    rows.push({
      id,
      family: "piper",
      locale,
      localeName: localeName(locale),
      // Multi-speaker models expose only their default speaker (sid 0).
      displayName: titleCase(v.name),
      quality: v.quality,
      sid: 0,
      // Piper models carry their own espeak voice; no per-call lang override.
      lang: "",
      downloadId: id,
      sizeMB: onnxSizeMB(v.files),
      recommended: RECOMMENDED_PIPER.has(v.key),
    });
  }
  return rows;
}

function buildKokoro() {
  return KOKORO.map((k) => ({
    id: `kokoro-${k.sid}`,
    family: "kokoro",
    locale: k.locale,
    localeName: localeName(k.locale),
    displayName: `${k.name} (${k.gender})`,
    quality: k.grade,
    sid: k.sid,
    lang: KOKORO_LANG[k.locale] ?? "",
    downloadId: KOKORO_PACK,
    sizeMB: KOKORO_PACK_MB,
    recommended: !!k.recommended,
  }));
}

const localeOrder = [...LOCALES];
function sortKey(v) {
  return [
    v.family === "piper" ? 0 : 1,
    localeOrder.indexOf(v.locale),
    v.recommended ? 0 : 1,
    v.displayName.toLowerCase(),
  ];
}

const piper = await buildPiper();
const kokoro = buildKokoro();
const voices = [...piper, ...kokoro].sort((a, b) => {
  const ka = sortKey(a), kb = sortKey(b);
  for (let i = 0; i < ka.length; i++) {
    if (ka[i] < kb[i]) return -1;
    if (ka[i] > kb[i]) return 1;
  }
  return 0;
});

const out = {
  schemaVersion: SCHEMA_VERSION,
  source:
    "rhasspy/piper-voices voices.json (Piper) + hexgrad/Kokoro-82M VOICES.md (Kokoro grades)",
  voices,
};

const dest = join(
  dirname(fileURLToPath(import.meta.url)),
  "..", "..",
  "ACT_DiscordTriggers.Core", "Tts", "onnx-voices.json",
);
writeFileSync(dest, JSON.stringify(out, null, 2) + "\n", "utf8");
console.log(
  `Wrote ${voices.length} voices (${piper.length} Piper, ${kokoro.length} Kokoro) -> ${dest}`,
);
