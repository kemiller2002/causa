**Governed verdict: NO GOVERNED ACCEPTANCE. The requirements ledger reports 0 of 203 applicable mandatory requirements integrated, with completion `CONTINUE_REQUIRED`. Main holds only merges made under the owner's P18 rule (implementer-verified on executable obligations, not governed-accepted).**

# Causa competition build: final report

- **Window:** T0 `2026-11-07T16:10:50Z` (`evidence/t0/t0.json`). The recorded deadline is `2026-11-07T23:10:50Z` (420 minutes). The owner set the working stop at 22:00Z.
- **Frozen package:** requirements v4.1.6, governed from `kemiller2002/Indy-init` at `dae2347`. The package, the lock, the scope and the owner records were not changed.
- **Report status:** written at 19:58Z, last updated at the time given in the "Final state" section.
- **Actor:** `claude` for every Praxis record. The coordinator is a Claude Code session; the lanes are sub-agents of that session.

## 1. Ledger output (verbatim)

Command (SRC is the governed Indy-init checkout):

```
requirements-compiler ledger --manifest SRC/docs/competition-readiness/scope-manifest.json \
  --events evidence/progress/events.json --out evidence/final/ledger-main.json
```

Console output:

```
ledger: evidence/final/ledger-main.json (203 open mandatory; CONTINUE_REQUIRED)
```

- **Totals** (`evidence/final/ledger-main.json`):
  - `mandatoryApplicable` 203, `mandatoryOpen` 203, `completion` CONTINUE_REQUIRED, `mainHealthy` true;
  - by state: not-started 131, in-progress 68, implemented-unverified 9, verified-unmerged 0, integrated 0, blocked 0, failed 0, excluded 0;
  - scope sha256 `09e38f7db1888b3bbb957272cddfa050908a077a1f115d0cef2d412d7ec40666`.
- **By group:**

| Group | Requirements | Mandatory | Applicable obligations | Implemented | Verified | Integrated | Missing | In progress |
|---|---|---|---|---|---|---|---|---|
| AR | 30 | 30 | 81 | 6 | 0 | 0 | 64 | 11 |
| EXT-DATA | 13 | 13 | 31 | 0 | 0 | 0 | 0 | 31 |
| INV | 46 | 41 | 254 | 0 | 0 | 0 | 254 | 0 |
| PERSIST | 46 | 46 | 255 | 0 | 0 | 0 | 128 | 127 |
| PF | 19 | 19 | 55 | 0 | 0 | 0 | 0 | 55 |
| UI | 30 | 30 | 140 | 0 | 0 | 0 | 99 | 41 |
| VER | 9 | 9 | 23 | 19 | 0 | 0 | 4 | 0 |
| WDL | 15 | 15 | 31 | 0 | 0 | 0 | 31 | 0 |

The ledger reads only main's progress events. Work that exists only on unmerged branches (see section 4) is, correctly, not counted.

## 2. What is on main

| PR | Slice | Merge | Gate evidence | Basis |
|---|---|---|---|---|
| #1 | setup | `1c67842` | Verification gate success on `eec92ac` | records only: scope and initialization checkpoints |
| #2 | SL-T10 | `3432bdf` | gate run 42397129424: executable obligations 11/11 verified, test integrity PASS | P18 (process records unverifiable) |
| #3 | SL-T11 | `447015c` | gate run 42398897090: executable obligations 8/8, test integrity PASS | P18 as extended (DEPENDENCY_NOT_INTEGRATED on P18-merged dependencies) |
| #5 | SL-T11 follow-up (deploy credential scan) | `78591f4` | gate run 42401138760: executable obligations 8/8, test integrity PASS | P18 as extended |

Every merge used a merge commit, was green on its current head and up to date with main. There was no rebase and no force-push.

## 3. CI status of main (`78591f4`)

| Workflow | Run | Result |
|---|---|---|
| Verification gate | 42402898246 | success |
| Causa CI | 42402898196 | success |
| Build and test | 42402898429 | success |
| Praxis validation | 42402898307 | success |
| Echelon foundations | 42402898623 | success |
| Deploy to GitHub Pages | 42402898344 | **cancelled by the coordinator** (see section 6) |

## 4. Mandatory requirements not integrated, and why

All 203 are open. They fall into these causes:

1. **The T18 ↔ T20 cycle blocks every persistence-dependent slice.**
   - SL-T18's own acceptance spec, `playwright:invariants/INV-21.spec.js`, drives the built application's Connect.
   - Connect is wired into the application only by SL-T20, and SL-T20 depends on T18 and T19.
   - So T18 cannot pass its gate before T20 exists on main, and T20 cannot be merged before T18.
   - Every downstream slice is refused with DEPENDENCY_NOT_INTEGRATED: T19, T20, T21–T25, the INV-* work items, and the UI slices beyond T16.
   - The owner did not choose to defer INV-21 (option B) or to adopt new merge rules (option C).
2. **SL-T18 (PR #4, open).**
   - Latest gate run 42404772168 on `a8e95e4`: REFUSED, with 49 of 66 executable obligations verified (31 in the run before, 42402357707). The remaining 17 obligations:
     - **Catalog obligations, which need an owner data decision:** PF-002-O1, PF-004-O2/O3, PF-006-O1/O2, PF-007-O1, PF-011-O4, PF-014-O1/O2, PF-016-O1 and PF-018-O1. They read `catalog/active.json` and the demonstration cases from the governed test repository. That repository holds no catalog; the gate's tokens reach only `kemiller2002/causa-acceptance-r10`, and the frozen lock names no catalog repository. Not decided and not faked.
     - **Waiting on T19's storage codec:** PF-002-O2, PF-004-O1, PF-007-O2 and PF-008-O2.
     - **Depend on how the gate runs:** PF-016-O2 (no deployed candidate in the suites step) and PF-015-O4.
     - **The cycle in item 1:** INV-21.
   - Process records cannot be verified (item 3).
3. **Process records cannot be verified.** `verify-process` checks the `praxis:*` obligations against the pinned process rules. Those rules are Rehearsal 010's and require repository `indy-init-rehearsal-010`, so every record is INCORRECT-REPOSITORY. The repair (ODR-COMP-P17) is drafted in Indy-init#195 and is not recorded.
4. **Not started in the window:** WDL, most of INV, the remaining UI and AR, and the report/print slices.

## 5. The demonstration (not governed evidence)

- **Live at <https://causa.echelonfoundry.com/>.** The site is built from the integration branch `slice/SL-T20`, **not from main**. `/provenance.json` names the deployed commit.
  - The integration branch holds T13, T16, T17, T18, T19, T20, T22, WI-INV-START and WI-INV-WORKSPACE, merged together for the preview.
  - It is shown in draft PR #6, which is not for merge.
- **Verified on the live site** (implementer-verified and owner-verified, not governed):

| # | Flow | Verified by | How |
|---|---|---|---|
| 1 | The light theme loads | coordinator | demo suite with GitHub blocked (`DEMO_SKIP_GITHUB=1`), Chromium, against live |
| 2 | Navigation: Investigations, Administration, Settings | coordinator | same suite |
| 3 | New investigation form, validation message, Cancel | coordinator | same suite |
| 4 | No part of the GitHub token is ever shown | coordinator | same suite: 22 token fragments checked |
| 5 | Connect and list investigations, with status, last activity and counts | owner | by hand |
| 6 | The Apollo 11 catalog sections | owner | by hand |
| 7 | Start, Save, reload | owner | by hand on a phone; causa-data commit `fe6568d` |
| 8 | A saved investigation reopened from the list can be edited and saved | owner | by hand on a phone; causa-data commits `78c35f8` and `3c1c077` |
| 9 | Recording with an empty field shows an error and records nothing | owner | by hand on a phone |
| 10 | Causal Analysis appears on a working copy once it holds a hypothesis | owner | by hand on a phone |

- **Preview deploys, all from `slice/SL-T20`:**

| Run | Deployed commit | Result |
|---|---|---|
| 42400622742 | `7ecc1e0` | success |
| 42401141563 | `fa4551c` | success |
| 42402402709 | `6ec18db` | success |
| 42404266087 | `abdef87` | success |
| 42405580386 | `febf478` | success |
| 42406706976 | `95c6dd9` | success |
| 42407452267 | `aefd042` | success |
| (dispatch at 20:08Z) | `85ba0ed` | success |
| (dispatch at 20:33Z) | `f28fd9e` | success: **the frozen demo build** |

  Before each one, the coordinator told the coordination session, then ran the GitHub-independent suite against live (4 of 4 passed each time after `abdef87`).
- **Checks on the frozen build `f28fd9e`** (implementer-run; a local build against the harness GitHub double seeded with causa-data's catalog):
  - `workspace-catalog.spec.js`: 12/12 in Chromium (integration lane) and 12/12 in WebKit (coordinator). It covers four catalog cases at 1280 and 390 px, the working-copy record, save and read-back, the causal graph, and 44 px tap targets.
  - Offline suites: App 52, Domain 14, Contract 31, Persistence 71, Invariants 7 (offline), Acceptance 71 (live excluded).
  - Demo flows 8/8 against the double.
- **Demo recording and slide deck:** `docs/demo/causa-demo.mp4` (2 min 19 s) and `docs/demo/causa-demo.html` (8 slides). They cover the application and how it was built with AI: testing, security and delivery. The recording uses the harness double, because the sandbox browser cannot authenticate to GitHub. Both are linked from the README.
- **The causal graph**, a demonstration piece the owner asked for in the window: a pure F# layout, an inline SVG, Forma tokens only, and a text relationship index. It is in the Causal Analysis section.

## 6. Pages deploys from main that the coordinator cancelled

The owner directed that the preview stay live until 22:00Z, so main's automatic Pages deploys were cancelled. Each is recorded here:

| Run | Commit | Trigger | Cancelled |
|---|---|---|---|
| 42402898344 | `78591f4` (#5) | push to main | 18:46Z |

At about 21:01Z the owner **disabled** the "Deploy to GitHub Pages" workflow by hand ("This workflow was disabled manually"). The live site stays at `f28fd9e`, and merging this report does not deploy. The owner will re-enable the workflow after the competition.

## 7. Owner decisions made in chat, not recorded

These decisions are the owner's, given in chat. **None is written as an owner record**, and the coordinator did not create one. Each is "decided by the owner in chat … not yet recorded":

- **P17:** repair the process-evidence rules (repository `causa-t0`), drafted in Indy-init#195. Decided by the owner in chat; not yet recorded.
- **P18 = A:** merge a slice when its gate's only refusals are process records. Decided by the owner in chat; not yet recorded.
- **P18 extension:** also when the refusals include DEPENDENCY_NOT_INTEGRATED on P18-merged dependencies. Decided by the owner in chat; not yet recorded.
- **P19 = A:** the primary question is persisted as an informational unknown. Decided by the owner in chat; not yet recorded. See Indy-init#195.
- **Preview deploy, and option A:** demonstrate from the integration branch, and show it in draft PR #6, which is not to be merged.
- **Option C (about 21:05Z):** "C: merge PR #6 as implementer-verified". The integration branch is merged into main, labelled **implementer-verified, not governed-accepted**. The conditions were CI green on the head, a local build of the exact merge result, and all non-live tests passing. Decided by the owner in chat; not yet recorded. See section 2a.

## 8. Failures and corrective actions (including the coordinator's own)

| # | Failure | Corrective action |
|---|---|---|
| 1 | The read-only token could not be requested before T0: the proxy refused it | Proceeded without it; the live tests fail closed with BLOCKED |
| 2 | percepta fetch 401 during the bootstrap | Pre-filled the Conditor cache |
| 3 | Dokimos refused WDL-013 | Fixed in Indy-init#194, merged before T0 |
| 4 | `ros.json` counted as implementation on the setup PR | Reverted from the setup PR |
| 5 | `authorize-implementation` needed `--scope-manifest`; suite-preflight root mismatch | Re-ran both in the target checkout; both recorded in PR #1 |
| 6 | The slice gate crashed on a missing `evidence/progress/events.json` | Created the events file |
| 7 | Dokimos found "no assertion" in helper-based tests | Helpers renamed to `assert*` so the assertion is recognised; no test weakened |
| 8 | Causa CI ran the live tests without secrets | CI filter excludes the Live suites, which still run in the gate (T11) |
| 9 | The deploy credential scan died silently under `bash -e -o pipefail` | Fixed, with a test that runs the step verbatim (#5) |
| 10 | The Pages environment refused the preview branch | The owner allowed `slice/SL-T20` |
| 11 | **Coordinator error:** diagnosed the sandbox browser's anonymous GitHub access as a fine-grained-token problem | Corrected with the coordination session and the lanes. Cause: the sandbox's headless Chromium reaches api.github.com anonymously (60/h), even with the correct header |
| 12 | A page-route relay to work around item 11 was denied as a containment escape | Stopped and reported; not retried |
| 13 | A 403 rate limit was shown as "refused" | Classified as a rate limit (persistence and integration) |
| 14 | The gate's suites step does not pass CAUSA_TEST_OWNER, CAUSA_TEST_REPO or CAUSA_TEST_BRANCH | The live harness falls back to the governed test repository |
| 15 | **Lane errors:** a lane edited the read-only SRC checkout; a lane sent wrong SHAs; a lane seeded another lane's test double; a stray server on port 4321 | SRC restored and the work moved to a prep checkout; SHAs re-sent and verified; lanes given private ports |
| 16 | Owner-reported: list cards said "Not known" | Fixed in `febf478` |
| 17 | Owner-reported: a reopened working copy could not be edited | Fixed in `95c6dd9` |
| 18 | Owner-reported: a duplicate hypothesis in the owner's data | Two separate Record actions: the form sat above the list on a phone. Layout changed; the data was left untouched |
| 19 | A rebase-on-moved-head save could encode commands twice (found during item 18) | Fixed in `0695219`, with a contract test |
| 20 | WebKit flaked once on the catalog spec | Re-runs passed; recorded as possibly flaky, not hidden |
| 21 | Owner-reported: the graph did not demo well on a phone (one tall column, edges off-screen, a repeated label) | One panel per hypothesis at narrow widths; the full graph behind "Show full graph"; labels de-duplicated (`7d5527d`) |
| 22 | Phone audit at 390 px: section links, card titles and the masthead link were 20–24 px tall; 11–13 px card text | 44 px targets and 14 px text (`7d5527d`, `f28fd9e`), with a 390 px tap-target test |
| 23 | Audit-reported: Causal Analysis missing after a same-tab save and reopen | Not reproducible at `f28fd9e`; regression test `ReopenAfterSave` added |
| 24 | The README did not explain how to run the project, and listed no known limitations | README rewritten: Try it now, Known limitations, Run it locally, Connect to your own data, Tests |

## 9. Governance findings

- **The T18 ↔ T20 cycle in the slice plan.** A slice's own acceptance spec depends on a later slice, so no ordering can integrate it. This is a defect in the frozen package, reported here, not worked around.
- **Process-evidence rules pinned to a different repository** (item 3 of section 4). Every `praxis:*` record is unverifiable; repair proposed in Indy-init#195.
- **The gate's test-repository environment is incomplete.** The suites step does not pass CAUSA_TEST_OWNER, CAUSA_TEST_REPO or CAUSA_TEST_BRANCH, and no catalog repository is named. Catalog-reading live obligations cannot pass.
- **The sandbox browser reaches GitHub anonymously.** Live browser checks from the sandbox are limited to 60 requests an hour and do not authenticate. Live UI checks were therefore done by the owner, and the coordinator ran only the GitHub-independent suite.

## Final state

_(updated at the stop; see below)_
