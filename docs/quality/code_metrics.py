# Code-quality metrics for causa (docs/quality/CODE-METRICS-2026-11-08.md).
# Usage: python3 -I docs/quality/code_metrics.py <repo-root>  -> JSON on stdout.
# F# complexity is an approximation (decision points per let/member binding); see the report for limits.
import re, os, json, statistics as st, subprocess, sys
from pathlib import Path
from functools import reduce
ROOT = Path(sys.argv[1])
SKIP = ('node_modules', '/bin/', '/obj/')
def files(globs, under):
    return [p for g in globs for p in (ROOT/under).rglob(g) if not any(s in str(p) for s in SKIP)]
def sloc_fs(text):
    text = re.sub(r'\(\*.*?\*\)', '', text, flags=re.S)
    lines = [l.strip() for l in text.splitlines()]
    code = [l for l in lines if l and not l.startswith('//')]
    comments = [l for l in lines if l.startswith('//')]
    return len(code), len(comments)
def sloc_js(text):
    text2 = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    lines = [l.strip() for l in text2.splitlines()]
    return len([l for l in lines if l and not l.startswith('//')]), len([l for l in lines if l.startswith('//')]) + text.count('/*')
def tally(paths, f):
    rs = [f(p.read_text(errors='ignore')) for p in paths]
    return dict(files=len(paths), code=sum(r[0] for r in rs), comments=sum(r[1] for r in rs))
fs_src = files(['*.fs'], 'src'); fs_test = files(['*.fs'], 'tests'); js_test = files(['*.js','*.mjs'], 'tests/browser')
js_web = files(['*.js'], 'web') + files(['*.ts'], 'src/kernel')
size = dict(fs_src=tally(fs_src, sloc_fs), fs_test=tally(fs_test, sloc_fs), js_browser_tests=tally(js_test, sloc_js), js_web=tally(js_web, sloc_js))
# F# function-level approximation: top-level and nested `let`/`member` bindings, decision points until next binding at same or lower indent
DEC = re.compile(r'(^\s*\|(?!\|)\s*[^|>])|\bif\b|\belif\b|\bwhen\b|&&|\|\||\bwhile\b|\bfor\b|\btry\b|\bwith\b(?=\s*$)')
BIND = re.compile(r'^(\s*)(let|member|override|and)\s+(rec\s+|private\s+|inline\s+|mutable\s+)*(\(?[\w.\']+)')
def fs_functions(path):
    lines = path.read_text(errors='ignore').splitlines()
    starts = [(i, len(m.group(1)), m.group(4)) for i, l in enumerate(lines) if (m := BIND.match(l)) and ('=' in l or l.rstrip().endswith('=') or True)]
    def body(k):
        i, ind, name = starts[k]
        BOUND = re.compile(r'^(\s*)(type|module|exception|\[<|namespace)\b'); ends = [j for j, jind, _ in starts[k+1:] if jind <= ind] + [j for j in range(i+1, len(lines)) if (b := BOUND.match(lines[j])) and len(b.group(1)) <= ind]; nxt = min(ends) if ends else len(lines)
        blk = [l for l in lines[i:nxt] if l.strip() and not l.strip().startswith('//')]
        return dict(file=str(path.relative_to(ROOT)), name=name, line=i+1, length=len(blk), ccn=1 + sum(len(DEC.findall(l)) for l in blk), indent=ind)
    return [body(k) for k in range(len(starts))]
fns = [f for p in fs_src for f in fs_functions(p) if f['length'] >= 3]
top = [f for f in fns if f['indent'] <= 4]
def dist(xs, key): 
    v = sorted(x[key] for x in xs); 
    return dict(n=len(v), median=st.median(v), p90=v[int(len(v)*0.9)-1], max=v[-1]) if v else {}
style_counts = lambda paths: reduce(lambda a, t: {k: a[k] + len(re.findall(rx, t)) for k, rx in PAT.items()}, (p.read_text(errors='ignore') for p in paths), {k: 0 for k in PAT})
PAT = {'mutable': r'\bmutable\b', 'assign_<-': r'<-', 'for/while loops': r'^\s*(for|while)\b', 'exceptions_raised': r'\b(failwith|failwithf|raise|invalidOp|invalidArg)\b', 'Result_usage': r'\b(Ok|Error|Result)\b', 'Option_usage': r'\b(Some|None|Option)\b', 'pipelines_|>': r'\|>', 'match_expressions': r'\bmatch\b'}
out = dict(size=size, test_to_code=round((size['fs_test']['code']+size['js_browser_tests']['code'])/max(1, size['fs_src']['code']+size['js_web']['code']), 2),
  fs_functions_all=dict(length=dist(fns,'length'), ccn=dist(fns,'ccn')), fs_top_level=dict(length=dist(top,'length'), ccn=dist(top,'ccn')),
  over_ccn10=len([f for f in fns if f['ccn']>10]), over_ccn20=len([f for f in fns if f['ccn']>20]), over_len60=len([f for f in fns if f['length']>60]),
  worst=sorted(fns, key=lambda f: -f['ccn'])[:8], style_src=style_counts(fs_src),
  largest_files=sorted(((str(p.relative_to(ROOT)), sloc_fs(p.read_text(errors='ignore'))[0]) for p in fs_src), key=lambda x: -x[1])[:6])
print(json.dumps(out, indent=1))
