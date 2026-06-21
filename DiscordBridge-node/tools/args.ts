// Shared CLI helper for the tts:* dev probes — each parses the same
// `--name value` flags off process.argv.

export function arg(name: string, fallback: string): string {
    const i = process.argv.indexOf(`--${name}`);
    return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1]! : fallback;
}
