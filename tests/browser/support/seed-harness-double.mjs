// Owner: integration. Seeds the governed harness GitHub double (Indy-init verification/harness/github-double.mjs) with
// causa-data's active catalog (catalog/ and the demonstration-v1 workspace) in harness/causa-test@main, so the browser
// specs that open catalog cases have them. The double is synthetic and never real-provider evidence.
//   node tests/browser/support/seed-harness-double.mjs <causa-data checkout> <double base URL>
import fs from 'node:fs';
import path from 'node:path';

const [root, double] = process.argv.slice(2);
if (!root || !double) {
  console.error('usage: seed-harness-double.mjs <causa-data checkout> <double base URL>');
  process.exit(2);
}
const base = `${double.replace(/\/$/, '')}/repos/harness/causa-test/contents/`;
const headers = { authorization: 'Bearer harness-token', 'content-type': 'application/json' };
const ws = 'workspaces/demo/demonstration-v1/investigations';
const files = [
  'catalog/active.json',
  'catalog/demonstration-v1/manifest.json',
  ...fs.readdirSync(path.join(root, ws)).flatMap((d) => ['events.ndjson', 'manifest.json', 'snapshot.json'].map((f) => `${ws}/${d}/${f}`)),
];
const failures = [];
for (const f of files) {
  const body = JSON.stringify({ message: `seed ${f}`, content: fs.readFileSync(path.join(root, f)).toString('base64'), branch: 'main' });
  const r = await fetch(base + f, { method: 'PUT', headers, body });
  if (!r.ok) failures.push(`${f}: ${r.status} ${await r.text()}`);
}
if (failures.length) {
  console.error(`seeding failed for ${failures.length} of ${files.length} files:\n${failures.join('\n')}`);
  process.exit(1);
}
console.log(`seeded ${files.length} files into harness/causa-test@main at ${double}`);
