# Causa

Causa is a workspace for causal investigations: you record what happened, the evidence, the hypotheses that could
explain it and how they relate, and Causa shows the causal analysis. It is an F# .NET WebAssembly application built
with Limen 0.9.1 (the browser kernel) and Forma 0.13.0 (the design tokens). Each investigation is stored as a history of
events in a GitHub repository that you own; Causa has no server of its own.

## Try it now

Open **https://causa.echelonfoundry.com/**. It is a preview built from `slice/SL-T20` (frozen at `f28fd9e`) and is
already connected to `kemiller2002/causa-data`.

`main` holds only the governed slices T10 and T11 (the shell and the build). The full application is on
`slice/SL-T20`; see draft PR #6.

What to tap:

- **A catalog case:** open *Apollo 11* or *Challenger* from the list, then the **Causal Analysis** section.
- **Your own investigation:** **New investigation**, fill in the problem statement, **Start investigation**, **Save**.
  Then record an observation and a hypothesis, **Save** again and reload the page: the investigation is still there.

## Run it locally

Prerequisites: the .NET SDK 10.0.402 (pinned by `global.json`) and Node 22.

```bash
git checkout slice/SL-T20
npm ci                 # the locked npm tree (Forma, Limen, Folio, Playwright)
npm run build          # dotnet fsi build.fsx: writes the site to dist/ (no secrets needed)
python3 -m http.server 4620 --bind 127.0.0.1 --directory dist   # or: node $INDY_INIT/verification/harness/serve.mjs dist 4620
```

Then open http://127.0.0.1:4620/. `dist/index.html` is the app, so serve `dist/` as the site root. A local build has no
connection built in, so the Connect form is shown (see below).

## Connect to your own data

1. Create a GitHub repository you own. To get the catalog cases, copy `catalog/` and `templates/` from
   `kemiller2002/causa-data` into it. An empty repository works too; it then holds only your own working copies.
2. Create a fine-grained personal access token with **Contents: Read and write** on that repository only.
3. In Causa, on the investigation list, enter the repository owner, repository, branch and token, then
   **Connect data repository**.

The token is kept only in the tab's memory. It is never saved or shown, so you enter it again in a new tab.

## Tests

`$INDY_INIT` is a checkout of the governed Indy-init repository (the harness lives there, not in this repository).

### Offline checks (what Causa CI runs; no secrets)

```bash
dotnet build Causa.slnx -c Release
dotnet test Causa.slnx -c Release --filter "FullyQualifiedName!~Causa.Acceptance.Live.&FullyQualifiedName!~Causa.Invariants.Live."
npx playwright test -c playwright.config.ts --project=chromium   # browser tests against the built dist/ (http://127.0.0.1:4321)
npx playwright install webkit && npx playwright test -c playwright.config.ts --project=webkit
```

### Live suites (real GitHub)

`CAUSA_PROVIDER=github` is the default, and the mode the governed gate sets. The app talks only to
`https://api.github.com`. To run the live suites against the designated test repository:

```bash
CAUSA_PROVIDER=github CAUSA_GITHUB_TOKEN=... CAUSA_WRITE_TOKEN=... CAUSA_TEST_OWNER=... CAUSA_TEST_REPO=... CAUSA_TEST_BRANCH=... dotnet test Causa.slnx --filter "FullyQualifiedName~Causa.Acceptance.Live.|FullyQualifiedName~Causa.Invariants.Live."
```

Without the credentials the live tests fail as BLOCKED; they never pass.

### Local fixture mode (harness double: offline, synthetic data, never evidence)

`CAUSA_PROVIDER=harness-double` runs the UI against the harness GitHub double (`harness/causa-test`, branch `main`,
token `harness-token`):

```bash
npm run build && node $INDY_INIT/verification/harness/github-double.mjs 4610 & node $INDY_INIT/verification/harness/serve.mjs dist 4620   # fixture-backed UI
CAUSA_PROVIDER=harness-double CAUSA_DOUBLE_URL=http://127.0.0.1:4610 CAUSA_APP_URL=http://127.0.0.1:4620/ npx playwright test   # fixture mode journeys (run from $INDY_INIT/verification/journeys)
```

The provider suites' offline layer uses the stateful GitHub fake built from the recorded answers, with no network.

## Repository operating system (Praxis)

This repository is a greenfield pilot running Praxis 3.25.0, Echelon Foundry's repository operating system. An older
installation may also have `./ros`, a compatibility alias of `./praxis`.

### Start here

1. Read [`AGENTS.md`](AGENTS.md) and [`BOOTSTRAP.md`](BOOTSTRAP.md).
2. Complete [`PROJECT-CHARTER.md`](PROJECT-CHARTER.md).
3. Establish the baseline in [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md).
4. Select the first bounded mission and its observable acceptance criteria.
5. Record durable evidence, decisions, and handoffs as the work proceeds.

### Local operating commands

```bash
./praxis work begin --id TASK-001 --occurred-at TIMESTAMP --type task
./praxis work context TASK-001
./praxis status
./praxis registry check
./praxis registry build
./praxis validate
```

`work context` reports legal actions and completion evidence. Validation errors include repair instructions; use
`./praxis validate --json` for machine-readable output. Complete work with explicit evidence paths as described in
`docs/work-protocol.md`.

The installed snapshot is self-contained. It does not read from the source Praxis repository.
`.ros/installation.json` records the package version and checksums of installed files.

### Pilot rule

The operating system is itself under evaluation. Do not infer that Causa T0 is a validated discipline, method, or
product merely because the repository follows a rigorous process. Measure whether the process improves decisions,
traceability, handoffs, and rework relative to the declared baseline.
