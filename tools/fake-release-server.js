#!/usr/bin/env node
// Local stand-in for the GitHub Releases API, so the auto-updater can be exercised
// end-to-end without publishing a real release. Serves a `releases/latest` JSON whose
// asset points back at a locally-built zip, then serves that zip with a Content-Length.
//
// Usage:
//   node tools/fake-release-server.js --zip ACT_DiscordTriggers.zip --tag v2.2.0 [--port 8099] [--notes "..."]
//
// Then point the plugin at it (and pretend you're on an older version), e.g. before
// launching ACT, or in a sim:
//   set ACT_DT_UPDATE_FEED=http://localhost:8099
//   set ACT_DT_UPDATE_FAKE_CURRENT=2.0.0
//   set ACT_DT_UPDATE_DRYRUN=1        (optional: rehearse the swap without touching files)
//
// The updater requests {FEED}/repos/<owner>/<repo>/releases/latest; this server answers any
// path ending in /releases/latest, and serves the zip from /download/<name>.

const http = require('http');
const fs = require('fs');
const path = require('path');

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const zipPath = path.resolve(arg('zip', 'ACT_DiscordTriggers.zip'));
const tag = arg('tag', 'v99.0.0');
const port = parseInt(arg('port', '8099'), 10);
const notes = arg('notes', `Test release ${tag}.\n\n- Built locally by fake-release-server.js\n- Exercises the auto-update path end to end.`);

if (!fs.existsSync(zipPath)) {
  console.error(`[fake-release] zip not found: ${zipPath}\n  Build one first:  pwsh ./build.ps1 -Zip`);
  process.exit(1);
}

const assetName = `ACT_DiscordTriggers-${tag}.zip`;

const server = http.createServer((req, res) => {
  const url = req.url.split('?')[0];
  console.log(`[fake-release] ${req.method} ${url}`);

  if (url.endsWith('/releases/latest')) {
    const base = `http://${req.headers.host}`;
    const body = JSON.stringify({
      tag_name: tag,
      name: `Release ${tag}`,
      body: notes,
      html_url: `${base}/releases/${tag}`,
      prerelease: tag.includes('-'),
      assets: [
        { name: assetName, browser_download_url: `${base}/download/${assetName}` },
      ],
    });
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(body);
    return;
  }

  if (url.startsWith('/download/')) {
    const stat = fs.statSync(zipPath);
    res.writeHead(200, { 'content-type': 'application/zip', 'content-length': stat.size });
    fs.createReadStream(zipPath).pipe(res);
    return;
  }

  res.writeHead(404, { 'content-type': 'text/plain' });
  res.end('not found');
});

server.listen(port, () => {
  console.log(`[fake-release] serving ${path.basename(zipPath)} as ${tag} on http://localhost:${port}`);
  console.log(`[fake-release]   latest:   http://localhost:${port}/repos/owner/repo/releases/latest`);
  console.log(`[fake-release]   asset:    http://localhost:${port}/download/${assetName}`);
  console.log(`[fake-release] point the plugin at it:  set ACT_DT_UPDATE_FEED=http://localhost:${port}`);
});
