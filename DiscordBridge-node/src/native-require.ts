// Lazy loader for the bridge's native/external addons (sherpa-onnx, audify).
//
// Base createRequire on __filename, not import.meta.url: esbuild emits CJS where
// import.meta is `{}` (so createRequire(import.meta.url) would throw at startup),
// while __filename is a real CJS global in the bundle. These addons are in the
// esbuild `external` list and resolve from dist/node_modules at runtime — loading
// them lazily keeps the bridge starting (and printing BRIDGE_READY) even when an
// addon is missing/broken; only the feature that needs it fails then.

import { createRequire } from 'node:module';

let requireNative: NodeRequire | null = null;

export function requireExternal<T>(name: string): T {
    if (!requireNative) requireNative = createRequire(__filename);
    return requireNative(name) as T;
}
