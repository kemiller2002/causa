# Echelon tool usage

Owner: integration. This page covers how often and how long the Echelon development tools are used, and how much of
each Echelon runtime library the shipped application references. The recorder is F# only:
`tools/usage/usage.fsx`, with its pure core in `tools/usage/UsageCore.fs`.

## Running a tool through the recorder

```
dotnet fsi tools/usage/usage.fsx -- run <tool> -- <arguments...>
```

| `<tool>` | What runs |
| --- | --- |
| `ordo` | `ordo` on PATH (the State-Directed Engineering CLI `sde`) |
| `praxis` | `./praxis` at the repository root (the pinned Praxis), else `praxis` on PATH |
| `percepta`, `percepta-repo`, `dokimos`, `conditor` | the executable of that name on PATH |
| `requirements-compiler` | `dotnet $INDY_INIT_ROOT/tools/requirements-compiler/src/bin/Release/net10.0/requirements-compiler.dll` |
| `r9` | `dotnet fsi $INDY_INIT_ROOT/tools/r9/r9.fsx --` |

`INDY_INIT_ROOT` defaults to `~/Indy-init`. On the workstation, put the tools on PATH first:
`export PATH=$HOME/.comp/echelon-home/.local/bin:$HOME/.comp/conditor:$PATH`.

Examples:

```
dotnet fsi tools/usage/usage.fsx -- run ordo -- status
dotnet fsi tools/usage/usage.fsx -- run praxis -- validate
dotnet fsi tools/usage/usage.fsx -- run r9 -- check-sequencing --target . --scope-manifest "$SRC/docs/competition-readiness/scope-manifest.json"
```

The tool runs with the recorder's standard input, output and error inherited, so its output reaches you byte for
byte and interactive use still works. The recorder exits with the tool's exit code. A tool that cannot be started
exits 127 and is recorded as `not-started`. The recorder adds about two seconds of `dotnet fsi` start-up to each call.

**Recording never changes the result.** If the record cannot be written, the recorder prints
`tool-usage: warning: the invocation was not recorded: ...` on stderr and still exits with the tool's exit code.

## Where the records go

One JSON line per invocation is appended to `<dir>/<YYYY-MM-DD>.ndjson` (UTC date of the start):

- `<dir>` is `CAUSA_TOOL_USAGE_DIR` when it is set;
- otherwise it is `.ros/telemetry/tool-usage/` under the repository root, the nearest directory that holds `ros.json`.

Records made on a workstation may be committed. Praxis's `.gitignore` does not exclude `.ros/telemetry/`,
`./praxis validate` accepts files there, and `ros.json` lists `.ros/telemetry/**` under `ignoredPaths`, so a
record never counts as unattributed meaningful work. CI points `CAUSA_TOOL_USAGE_DIR` at the runner's temporary
directory and uploads the records as the `tool-usage-*` artifact. CI records are never committed.

Writes are append-only, and each one is atomic in the same directory, as Praxis does it. Under an exclusive lock (a
file in the system temporary directory, keyed by the record directory), the existing file plus the new line is
written to a temporary file in the same directory, which then atomically replaces the day file. An interrupted
writer leaves the previous complete file, and earlier lines are never changed.

## What is recorded (schema `causa.tool-usage/1`)

```json
{"schema":"causa.tool-usage/1","invocationId":"9ea6801aa9634d2d8f56eb17aaee1df1","tool":"ordo","toolVersion":"1.20.0","subcommand":"status","argNames":[],"argCount":1,"startedAt":"2026-11-08T12:51:15.241Z","endedAt":"2026-11-08T12:51:15.569Z","durationMs":327,"exitCode":0,"outcome":"succeeded","praxis":{"workItem":null,"executionId":null,"resolution":"ambiguous","candidates":14},"environment":"local"}
```

| Member | Meaning |
| --- | --- |
| `invocationId` | a random identifier for this invocation |
| `tool` | the tool id above |
| `toolVersion` | the first `x.y.z` the tool reports. It comes from `--version` (`version` for Dokimos), probed once per recorder process. For requirements-compiler it comes from the assembly's version. It is `null` when the tool has no version command (Conditor 0.10.21, r9) or did not start; it is never guessed |
| `subcommand` | the command words, recognised against the tool's known command vocabulary (`Tool.vocabulary`); `null` when there are none |
| `argNames` | the distinct flag names (`--input`, `-v`), in first-use order; `--name=value` keeps `--name` |
| `argCount` | how many arguments were passed |
| `startedAt`, `endedAt` | UTC, millisecond precision |
| `durationMs` | wall time of the tool process |
| `exitCode`, `outcome` | the tool's exit code, and `succeeded`, `failed` or `not-started` |
| `praxis` | the active Praxis work item and execution, and how they were found (below) |
| `environment` | `ci` when `CI` or `GITHUB_ACTIONS` is `true`, else `local` |

### Privacy rule

A record holds **only the subcommand and the argument names**. It never holds an argument value, a positional
argument, file contents, a token, or the value of an environment variable. These can contain secrets.

- Argument values are dropped, whether they follow a flag or come after `=`.
- A word counts as part of the subcommand only when it is in that tool's command vocabulary. So
  `praxis work context WI-0001` records `work context`, and an unknown first word records no subcommand. A path, ID or
  name in a positional argument can therefore never be recorded.
- The version is reduced to its `x.y.z` match. Nothing else of the tool's output is kept.
- Only identifier-shaped work-item and execution IDs are read (below), and they are Praxis's own record identifiers.

The tests in `tests/Causa.App.Tests/UsageRecorderTests.fs` check this rule with a token-shaped value, a path and a
positional value.

### The Praxis work item and execution

Praxis does not export its current execution to child processes. The recorder resolves it the way Praxis selects an
execution:

1. `PRAXIS_EXECUTION_ID` and `PRAXIS_WORK_ITEM`, the selectors Praxis itself reads. The `resolution` is
   `environment`.
2. Otherwise, the active work items in `.ros/context/current.json`, their `telemetryExecutionIds`, and each
   execution's `.ros/telemetry/executions/<id>.json`. The recorder keeps the active executions whose
   `identity.sessionId` is this process's session: `PRAXIS_TELEMETRY_SESSION_ID`, `ROS_TELEMETRY_SESSION_ID`,
   `CLAUDE_CODE_SESSION_ID` or `CODEX_THREAD_ID`. With exactly one, the `resolution` is `session`. With several, it is
   `ambiguous`: the recorder chooses none and records the number of `candidates`. With none, it is `none`.

### Why the usage is not also a Praxis metric

Praxis 3.25.0 accepts only metric IDs in its normalized registry (`telemetry/metrics.json`).
`./praxis telemetry record --metric tool.ordo.invocations ...` fails with
`unknown normalized metric 'tool.ordo.invocations'; preserve it in raw telemetry until it is registered`.
`./praxis step record` also needs an execution that the calling session owns. The registry has generic `tool.calls`
and `tool.duration_ms`, but they have no tool dimension, so recording Ordo calls there would mix them with every
other tool call of the execution. Usage is therefore recorded only in the ndjson. When Praxis registers per-tool
metrics, this recorder can add them.

## Report

```
dotnet fsi tools/usage/usage.fsx -- report [--since YYYY-MM-DD] [--by tool|day|work-item] [--json]
```

For each tool, and for each of its subcommands, the report gives the invocation count, the total and median duration,
the failure count (any non-zero exit or `not-started`) and the first and last use. `--by day` and `--by work-item`
repeat this per day or per Praxis work item. The output is sorted and does not depend on the order of the records.
Lines that are not records are skipped and counted.

## Runtime library inventory

```
dotnet fsi tools/usage/usage.fsx -- inventory [--check]
```

This writes `evidence/tool-usage/inventory.json`, a deterministic static report of how much of each Echelon runtime
library the shipped application references. "Shipped" means `web/**` and the projects that `src/Causa.Host` builds,
transitively. The Conditor scaffold (`src/engine`, `src/kernel`, `App.slnx`) is reported separately where relevant.

- **Limen:** the imports in shipped scripts and the names they import. It also lists the modules of the package's
  `dist` that the page loads, including its `generated/` codecs, and the effect and message kinds of the engine
  boundary (`src/Causa.App/Program.fs`).
- **Forma:** the `--ef-*` tokens and `ef-*` classes used, the number the design-system package defines, and any used
  name the package does not define. It also lists any token the application declares and the references to
  forma-workflow.
- **Folio:** the print components (`ef-print-*`) used in shipped code and in the scaffold.
- **Percepta:** the screen contracts, regions and capabilities, and the `data-percepta-*` hooks in shipped code.
- **Aegis:** the projects that reference the NuGet package, whether they ship, and the Aegis API names they use.
- **Ordo:** stated explicitly as having no runtime usage. Ordo is a development process tool, and the shipped
  application has no Ordo package, import or project reference.

Package facts are read from `node_modules/@echelon-foundry` after `npm ci`. Without it, those members say
`unavailable` instead of inventing a number. `--check` fails when the committed file is out of date.

## Call sites that record

| Call site | Recorded |
| --- | --- |
| `.github/workflows/praxis-validation.yml`: `./praxis registry check`, `./praxis validate` | yes, uploaded as the `tool-usage-praxis-validation` artifact |
| `./praxis` (launcher) | no: Praxis owns the file (`tool-owned` in `.echelon/ros.json`) and replaces it on upgrade. Run it through the recorder instead |
| `package.json` scripts, `build.fsx`, `ci.yml`, `build-and-test.yml` | no Ordo, Praxis or Conditor call to route |
| `echelon-foundations.yml` | no: its Praxis calls run inside the reusable workflow `kemiller2002/praxis/.github/workflows/foundations-verify.yml` |
