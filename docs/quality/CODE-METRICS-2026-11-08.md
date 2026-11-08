# Code-quality metrics baseline: main at c8831ba (2026-11-08)

A one-time snapshot of causa's code quality after the Indy Init competition (7 November 2026) and the merge of PR #6. It is a baseline for deciding what to do with the code next. It is not a governed verdict: nothing here was judged by the Verification gate.

- **Commit measured:** `c8831ba` (main, "Merge pull request #6 from kemiller2002/slice/SL-T20").
- **Collected:** 2026-11-08, by a Claude Code agent session at the owner's request.
- **Reproduce:**
  - `python3 -I docs/quality/code_metrics.py .` gives size, test ratio, F# function length and complexity, functional-style counts and the largest files, as JSON.
  - Run `jscpd` 4.0.5 at `--min-tokens 50` on `src`, on `tests/browser`, and on `tests/Causa.Acceptance tests/Causa.Contract.Tests tests/Causa.Invariants` to get duplication.

## Summary

The F# application code is small and almost free of duplication. It is strongly functional, and its tests outweigh it. The two weak spots are a handful of very large decision functions, and heavily copy-pasted browser test helpers. Line coverage has not been measured.

| Metric | causa | Common benchmark | Reading |
|---|---|---|---|
| Size | 8,826 F# code lines in 58 files (`src/`); 155 JS/TS lines (`web/`, `src/kernel`) | — | small, focused |
| Test-to-code ratio | **1.41 : 1**: 9,109 F# and 3,566 browser-spec test lines | 1 : 1 or higher for well-tested code | good |
| Duplication, application (`src/`) | **0.11%**: 2 clones, 10 lines | SonarQube default gate ≤ 3% | excellent |
| Duplication, F# tests (Acceptance, Contract, Invariants) | 1.78%: 16 clones | ≤ 3% | good |
| Duplication, browser specs (`tests/browser`) | **29.05%**: 24 clones, 1,446 lines | ≤ 3% (tests are often given more room) | poor |
| Function length, median / 90th percentile / max | 8 / 29 / 285 lines | most guides: under 30–50 | good; 26 functions over 60 lines |
| Cyclomatic complexity (approximate), median / 90th percentile / max | 5 / 17 / 114 | McCabe / NIST SP 500-235: ≤ 10 per function | median fine; 156 of 815 functions (19%) over 10, 63 (8%) over 20 |
| Largest files (code lines) | `Ui/UiAdapter.fs` 938, `Persistence/Reader.fs` 886, `Ui/Presentation.fs` 474 | under 500–1,000 | at the upper edge |
| Comment lines to code lines | 1,142 : 8,826 (13%) | 10–30% | fine |
| Release build warnings | 0, reported by the build agent on 2026-11-07 for the PR #6 merge candidate | 0 | good |
| Line coverage | **not measured** | 70–80% is a common target | unknown |

The tests passing at the PR #6 merge candidate, as reported by the build agent on 2026-11-07 (implementer-run, non-live):

| Suite | Tests |
|---|---|
| App | 52 |
| Domain | 14 |
| Contract | 31 |
| Persistence | 71 |
| Invariants | 7 |
| Acceptance | 71 |
| **Total** | **246** |

## Functional style (`src/`, F#)

| Construct | Count |
|---|---|
| `for` / `while` loops | 0 |
| `mutable` | 9 |
| `<-` assignments (mostly browser and JSON interop) | 30 |
| Raised exceptions (`failwith`, `raise`, `invalidOp`, `invalidArg`) | 6 |
| `Ok` / `Error` / `Result` | 494 |
| `Some` / `None` / `Option` | 1,238 |
| Pipelines (`\|>`) | 833 |
| `match` expressions | 549 |

Errors travel as values. State changes are expressed as decide and evolve functions over immutable records, with no imperative loops.

## Complexity hotspots

| Function | File:line | Lines | Complexity |
|---|---|---|---|
| `openInvestigation` | `src/Causa.Domain/Decide.fs:71` | 285 | ~114 |
| `step` / `keepKeptChanges` | `src/Causa.Persistence/Session.fs:122` | 109 | ~98 |
| `applyCore` | `src/Causa.Domain/Evolve.fs:60` | 270 | ~75 |
| `dispatch` | `src/Causa.App/Ui/UiAdapter.fs:694` | 75 | ~75 |
| `verify` | `src/Causa.Persistence/Reader.fs:526` | 101 | ~73 |
| `interpret` | `src/Causa.Persistence/Session.fs:256` | 75 | ~70 |
| `revisionRules` | `src/Causa.Persistence/Reader.fs:449` | 75 | ~69 |

Most of these are command or event dispatchers that pattern-match over every case of a union type, such as `CoreCommand` and the domain events. The F# compiler checks those matches for exhaustiveness, so the defect risk is lower than the raw number suggests, much as with a large C# `switch` over an enum. They would still read and test better split into one handler per case. `Decide.openInvestigation` and `Evolve.applyCore` are the natural first candidates.

## Method and limits

- **Lines of code:** non-blank lines with comments stripped, both `//` and `(* *)` for F#, and `//` and `/* */` for JS/TS. Lines that are only comments are counted separately. `bin/`, `obj/` and `node_modules/` are excluded.
- **Complexity is approximate.** No standard F# complexity tool was available: lizard and similar tools do not parse F#. The script counts decision points (match arms, `if`/`elif`, `when`, `&&`, `||`, `while`, `for`, `try`/`with`) per `let`/`member` binding. A binding ends at the next binding at the same or a lower indent, or at a `type`/`module` boundary. Treat the numbers as directional and the ranking as more reliable than the absolute values.
- **Duplication:** measured by jscpd 4.0.5 with a 50-token minimum.
- **Coverage** was not collected. Running the .NET suites with coverlet (or `dotnet-coverage`) would fill that row.

## Suggested next steps

1. Measure line and branch coverage for the six .NET suites.
2. Extract the shared browser-spec setup (connect, seed, navigate) in `tests/browser` into a helper module, to bring duplication under 5%.
3. Split `Decide.openInvestigation` and `Evolve.applyCore` into per-command and per-event functions, keeping behaviour identical under the existing Domain and Invariants tests.
4. Consider splitting `UiAdapter.fs` and `Reader.fs` by responsibility.
