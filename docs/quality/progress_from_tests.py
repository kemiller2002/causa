# Derives implementation progress events (indy-init.progress-event/1) from test results that actually ran.
#
# A work item is "started" when work from its slice branch (slice/SL-<id>, or slice/SL-<id>-*) is in the measured commit.
# A requirement is "implementation-completed" when every one of its obligations that can be tested locally
# (all categories except live-provider, independent-acceptance, process-record and owner-decision) has at least
# one passing test and no failing test, and it has at least one such obligation. Obligations are matched to tests
# by the obligation identity in the test name (e.g. "UI-029-O1") or by the plan's test-to-obligation mapping
# (.echelon/obligations.json). This is implementer-run evidence: it never records verification or integration,
# which only the governed Verification gate may do.
#
# Usage: python3 -I docs/quality/progress_from_tests.py <repo-root> <measured-commit> <trx-dir> <playwright-json>...
#   prints the new events (JSON array) on stdout and a summary on stderr.
import glob, json, os, re, subprocess, sys
import xml.etree.ElementTree as ET
from functools import reduce

UNTESTABLE = {"live-provider", "independent-acceptance", "process-record", "owner-decision"}
OBLIGATION = re.compile(r"\b([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{3}-O\d+)\b")
TRX = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def trx_results(directory):
    """Latest outcome per .NET test name; later files (by mtime) win."""
    files = sorted(glob.glob(os.path.join(directory, "*.trx")), key=os.path.getmtime)
    rows = [(u.get("testName"), u.get("outcome") == "Passed")
            for f in files for u in ET.parse(f).getroot().iterfind(".//t:UnitTestResult", TRX)]
    return reduce(lambda acc, r: {**acc, f"dotnet:{r[0]}": r[1]}, rows, {})


def playwright_results(path):
    """Outcome per Playwright test title (passed only when every attempt's final status is 'passed')."""
    def walk(suite, prefix):
        here = [(f"playwright:{prefix}{spec['title']}", all(t.get("status") == "expected" for t in spec.get("tests", [])))
                for spec in suite.get("specs", [])]
        return here + [r for s in suite.get("suites", []) for r in walk(s, prefix)]
    report = json.load(open(path))
    return dict(r for s in report.get("suites", []) for r in walk(s, ""))


def obligations_of_test(name, mapping):
    named = set(OBLIGATION.findall(name))
    mapped = set(mapping.get(name, [])) | set(mapping.get(name.replace("dotnet:", ""), []))
    return named | mapped


def merged(repo, commit, work_item, bootstrap="c7068166e78fe9c21e6aed5e392242f779b27e7f"):
    """True when a commit that belongs to this work item's slice branch (slice/SL-<id> or slice/SL-<id>-*), and to no
    other slice branch, is in `commit`. Shared history merged in from other slices does not count."""
    git = lambda *a: subprocess.run(["git", "-C", repo, *a], capture_output=True, text=True).stdout.split()
    every = git("for-each-ref", "--format=%(refname:short)", "refs/remotes/origin/slice/")
    mine = [b for b in every if b == f"origin/slice/SL-{work_item}" or b.startswith(f"origin/slice/SL-{work_item}-")]
    others = [b for b in every if b not in mine]
    own = git("rev-list", *mine, f"^{bootstrap}", "--not", *others) if mine else []
    in_commit = lambda c: subprocess.run(["git", "-C", repo, "merge-base", "--is-ancestor", c, commit]).returncode == 0
    return any(in_commit(c) for c in own) if own else any(in_commit(b) for b in mine)


def main(repo, commit, trx_dir, *pw_files):
    plan = json.load(open(os.path.join(repo, ".echelon/r9-work-plan.json")))["workItems"]
    obl = json.load(open(os.path.join(repo, ".echelon/obligations.json")))
    existing = json.load(open(os.path.join(repo, "evidence/progress/events.json")))
    mapping = {t["id"]: t.get("obligations", []) for t in obl["tests"]}
    categories = {o["id"]: set(o["categories"]) for o in obl["obligations"]}
    by_requirement = reduce(lambda acc, o: {**acc, o["requirement"]: acc.get(o["requirement"], []) + [o["id"]]}, obl["obligations"], {})
    results = reduce(lambda a, b: {**a, **b}, [trx_results(trx_dir)] + [playwright_results(p) for p in pw_files], {})
    coverage = reduce(lambda acc, kv: {**acc, **{o: acc.get(o, []) + [kv[1]] for o in obligations_of_test(kv[0], mapping)}}, results.items(), {})

    started_before = {e["workItem"] for e in existing if e["kind"] == "implementation-started"}
    completed_before = {r for e in existing if e["kind"] == "implementation-completed" for r in e["requirements"]}
    on_main = [w for w in plan if merged(repo, commit, w["id"])]
    new_started = [w for w in on_main if w["id"] not in started_before]
    started_reqs = {r for w in plan if w["id"] in started_before | {x["id"] for x in new_started} for r in w["requirements"]}

    def evidenced(req):
        testable = [o for o in by_requirement.get(req, []) if not (categories.get(o, set()) & UNTESTABLE)]
        return bool(testable) and all(coverage.get(o) and all(coverage[o]) for o in testable)

    done = sorted(r for r in started_reqs - completed_before if evidenced(r))
    at = __import__("datetime").datetime.now(__import__("datetime").timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    events = ([{"kind": "implementation-started", "workItem": w["id"], "requirements": w["requirements"], "at": at} for w in new_started]
              + ([{"kind": "implementation-completed", "requirements": done, "commit": subprocess.run(["git", "-C", repo, "rev-parse", commit], capture_output=True, text=True).stdout.strip(), "at": at}] if done else []))
    print(json.dumps(events, indent=2))
    print(f"tests: {len(results)} ({sum(results.values())} passed); work items newly started: {[w['id'] for w in new_started]}; "
          f"requirements newly implementation-completed: {len(done)}", file=sys.stderr)


if __name__ == "__main__":
    main(*sys.argv[1:])
