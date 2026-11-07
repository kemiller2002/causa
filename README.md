# Causa T0

This repository is a greenfield pilot running Praxis 3.25.0, Echelon Foundry's
repository operating system. An older installation may also have `./ros`, a
compatibility alias of `./praxis`.

## Start here

1. Read [`AGENTS.md`](AGENTS.md) and [`BOOTSTRAP.md`](BOOTSTRAP.md).
2. Complete [`PROJECT-CHARTER.md`](PROJECT-CHARTER.md).
3. Establish the baseline in [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md).
4. Select the first bounded mission and its observable acceptance criteria.
5. Record durable evidence, decisions, and handoffs as the work proceeds.

## Local operating commands

```bash
./praxis work begin --id TASK-001 --occurred-at TIMESTAMP --type task
./praxis work context TASK-001
./praxis status
./praxis registry check
./praxis registry build
./praxis validate
```

`work context` reports legal actions and completion evidence. Validation errors include repair instructions; use `./praxis validate --json` for machine-readable output. Complete work with explicit evidence paths as described in `docs/work-protocol.md`.

The installed snapshot is self-contained. It does not read from the source Praxis
repository. `.ros/installation.json` records the package version and checksums
of installed files.

## Building, running and testing Causa

Causa is an F# .NET WebAssembly application (Limen 0.9.1, Forma 0.13.0) that keeps its investigations in a GitHub
repository. The .NET SDK is pinned by `global.json` (10.0.402) and the npm tree by `package-lock.json`.

```bash
npm ci                                   # the locked npm tree (Forma, Limen, Folio, Playwright)
npm run build                            # dotnet fsi build.fsx: writes the site to dist/ (no secrets needed)
node $INDY_INIT/verification/harness/serve.mjs dist 4620   # serve it at http://127.0.0.1:4620/
```

### Provider modes

- **Real GitHub:** `CAUSA_PROVIDER=github` (the default, and the mode the governed gate sets). The app talks only to
  `https://api.github.com`; you connect with owner, repository, branch and a fine-grained token. To run the live suites
  against the designated test repository:

  ```bash
  CAUSA_PROVIDER=github CAUSA_GITHUB_TOKEN=... CAUSA_WRITE_TOKEN=... CAUSA_TEST_OWNER=... CAUSA_TEST_REPO=... CAUSA_TEST_BRANCH=... dotnet test Causa.slnx --filter "FullyQualifiedName~Causa.Acceptance.Live.|FullyQualifiedName~Causa.Invariants.Live."
  ```

  Without the credentials the live tests fail as BLOCKED; they never pass.
- **Local fixture mode (offline, synthetic data, never evidence):** `CAUSA_PROVIDER=harness-double` serves the
  fixture-backed UI against the harness GitHub double (`harness/causa-test`, branch `main`, token `harness-token`):

  ```bash
  npm run build && node $INDY_INIT/verification/harness/github-double.mjs 4610 & node $INDY_INIT/verification/harness/serve.mjs dist 4620   # fixture-backed UI
  CAUSA_PROVIDER=harness-double CAUSA_DOUBLE_URL=http://127.0.0.1:4610 CAUSA_APP_URL=http://127.0.0.1:4620/ npx playwright test   # fixture mode journeys (run from $INDY_INIT/verification/journeys)
  ```

  The provider suites' offline layer uses the stateful GitHub fake built from the recorded answers, with no network.

### Offline checks (what Causa CI runs; no secrets)

```bash
dotnet build Causa.slnx -c Release
dotnet test Causa.slnx -c Release --filter "FullyQualifiedName!~Causa.Acceptance.Live.&FullyQualifiedName!~Causa.Invariants.Live."
npx playwright test --project=chromium && PLAYWRIGHT_BROWSERS_PATH=... npx playwright test --project=webkit
```

`$INDY_INIT` is a checkout of the governed Indy-init repository (the harness lives there, not in this repository).

## Pilot rule

The operating system is itself under evaluation. Do not infer that
Causa T0 is a validated discipline, method, or product merely because
the repository follows a rigorous process. Measure whether the process improves
decisions, traceability, handoffs, and rework relative to the declared baseline.
