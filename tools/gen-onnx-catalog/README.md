# gen-onnx-catalog

Regenerates the embedded ONNX voice catalog read by `OnnxCatalog`:

```
ACT_DiscordTriggers.Core/Tts/onnx-voices.json
```

Run from the repo root (Node 18+, network access required — no dependencies):

```
node tools/gen-onnx-catalog/gen.mjs
```

- **Piper** rows are pulled from the upstream `rhasspy/piper-voices` `voices.json`
  manifest, filtered to the shipped locales (en_US, en_GB, fr_FR, de_DE, es_ES,
  es_MX, pt_BR, ru_RU) and the `medium`/`high` tiers.
- **Kokoro** rows are hand-seeded in the script from the `hexgrad/Kokoro-82M`
  letter grades and the sherpa-onnx `kokoro-multi-lang-v1_0` speaker→sid map.
- **Baked loudness** (`rmsDbfs`/`peakDbfs`) is merged in from `rms-baked.json`,
  measured offline by `DiscordBridge-node/tools/tts-rms.ts` (`npm run tts:rms`).
  A voice present there gets fixed-gain leveling at runtime (no whole-buffer RMS
  scan, which is what lets the synth path stream); a voice absent keeps the `0`
  sentinel and the bridge levels it by a runtime measure. To bake more voices:
  install them, run `npm run tts:rms -- --json rms.json`, fold the new entries
  into `rms-baked.json`, and rerun `gen.mjs`.

This is a dev-time tool — it is not part of the build or the shipped release. The
generated JSON is committed and embedded into the plugin DLL, so end users never
fetch anything to see the catalog. To curate the list (add/drop voices, change
which are flagged `recommended`), edit the seed/filters in `gen.mjs` and rerun.
