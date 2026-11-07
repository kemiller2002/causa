// Owner: coordinator's demo verification. Writes the markdown summary: one row per flow with PASS or FAIL and the
// first line of the failure (or the flow's own note), plus the site and the time.
import fs from 'node:fs';
import path from 'node:path';

export default class SummaryReporter {
  constructor(options = {}) {
    this.outputFile = options.outputFile || 'demo-summary.md';
    this.rows = [];
  }
  onTestEnd(test, result) {
    const failed = result.status !== 'passed';
    const error = (result.errors[0]?.message || '').replace(/\u001b\[[0-9;]*m/g, '').split('\n').find((l) => l.trim()) || result.status;
    const notes = test.annotations.filter((a) => a.type === 'note').map((a) => a.description);
    this.rows.push({ flow: test.title, status: failed ? 'FAIL' : 'PASS', detail: failed ? error.trim() : notes.join('; ') });
  }
  onEnd() {
    const site = process.env.DEMO_URL || '(DEMO_URL not set)';
    const double = process.env.DEMO_GITHUB_DOUBLE ? `\n\n**GitHub was the local harness double (${process.env.DEMO_GITHUB_DOUBLE}): not demo evidence.**` : '';
    const esc = (s) => String(s).replace(/\|/g, '\\|').replace(/\n/g, ' ');
    const lines = [
      `# Demo flow verification`,
      ``,
      `Site: ${site}  `,
      `Run: ${new Date().toISOString()}${double}`,
      ``,
      `| Flow | Result | Detail |`,
      `|---|---|---|`,
      ...this.rows.map((r) => `| ${esc(r.flow)} | ${r.status} | ${esc(r.detail)} |`),
      ``,
      `${this.rows.filter((r) => r.status === 'PASS').length} of ${this.rows.length} flows passed.`,
      ``,
    ];
    fs.mkdirSync(path.dirname(this.outputFile), { recursive: true });
    fs.writeFileSync(this.outputFile, lines.join('\n'));
  }
}
