import esbuild from 'esbuild';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Banner runs before any bundled code. Teaches Node to resolve `external`
// requires (the ones we left out of the bundle) from the node_modules folder
// we copy next to node.exe at build time. process.execPath is dist\node.exe,
// so dist\node_modules ends up on NODE_PATH and Node's CJS resolver finds the
// externals there.
const banner = `
"use strict";
(() => {
    const path = require("node:path");
    const Module = require("node:module");
    const exeDir = path.dirname(process.execPath);
    const localModules = path.join(exeDir, "node_modules");
    process.env.NODE_PATH = process.env.NODE_PATH ? (localModules + path.delimiter + process.env.NODE_PATH) : localModules;
    Module.Module._initPaths();
})();
`;

await esbuild.build({
    entryPoints: [path.join(__dirname, 'src/bridge.ts')],
    bundle: true,
    platform: 'node',
    target: 'node22',
    format: 'cjs',
    // Force CJS-flavoured resolution of conditional exports. The TS source uses
    // ESM `import` syntax, which by default makes esbuild walk into each dep's
    // `import` condition (e.g. @discordjs/voice's `dist/index.mjs`). Those .mjs
    // files use `createRequire(import.meta.url)`, but in CJS output `import.meta`
    // is `{}`, so `createRequire(undefined)` throws ERR_INVALID_ARG_VALUE at
    // startup. The original JS source happened to use `require()`, which got the
    // `require` condition and dodged the trap. Keep the dodge under TS by asking
    // for it explicitly.
    conditions: ['node', 'require'],
    outfile: path.join(__dirname, 'dist/bundle.js'),
    banner: { js: banner },
    // Native modules and packages with __dirname / require.resolve runtime tricks
    // can't be statically bundled. Mark them external so node.exe loads them from
    // node_modules at runtime via Node's normal require resolution (see the
    // NODE_PATH banner above).
    external: [
        '@snazzah/davey',          // DAVE JS wrapper (loads its native .node sibling)
        'opusscript',              // ships pure-JS but uses path tricks
        'libsodium-wrappers',
        'libsodium-wrappers-sumo',
        // prism-media optionally requires these; we don't use any of them at runtime
        // (we feed raw PCM, never go through ffmpeg or the alt opus/sodium libs),
        // but esbuild can't tell that without help.
        'ffmpeg-static',
        '@discordjs/opus',
        'node-opus',
        'mediaplex',
        'sodium-native',
        'sodium',
        'tweetnacl',
        // discord.js optional perf shims
        'bufferutil',
        'utf-8-validate',
        'erlpack',
        'zlib-sync',
        // ONNX neural TTS native addon + its win-x64 binary package. Loaded lazily
        // via createRequire in tts.ts (so the bridge starts even when absent); listed
        // here so a stray static import would also stay external rather than break the
        // bundle. Staged into dist/node_modules by build.ps1 $externals.
        'sherpa-onnx-node',
        'sherpa-onnx-win-x64',
        // Local audio output (RtAudio/WASAPI). Native N-API addon, loaded lazily
        // via createRequire in local-output.ts so the bridge starts even when it's
        // absent; listed here so a stray static import also stays external. Staged
        // into dist/node_modules (with its `bindings` loader) by build.ps1 $externals.
        'audify',
        // High-quality resampler: ESM package whose .wasm is a SEPARATE file located
        // at runtime via `new URL("r8brain.wasm", import.meta.url)`. It must stay
        // external (not inlined) and loaded as on-disk ESM (dynamic import in
        // resample.ts) so import.meta.url resolves to the staged file. Staged into
        // dist/node_modules by build.ps1 $externals.
        'r8brain-wasm',
    ],
    minify: false,
    sourcemap: false,
    logLevel: 'info',
});
