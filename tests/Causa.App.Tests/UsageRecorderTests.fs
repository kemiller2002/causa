// Owner: integration. The Echelon tool-usage recorder (tools/usage, docs/tool-usage.md): pass-through of the real
// tool's output and exit code, the privacy rule (no argument value is ever recorded), and deterministic reports.
// The recorder's core is linked from tools/usage/UsageCore.fs, as DeployConnectionTests links build/DeployConnection.fs.
namespace Causa.App.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open Causa.Tools.Usage

module private Recorder =
    let repositoryRoot =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwithf "no Causa.slnx above %s" AppContext.BaseDirectory
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let script = Path.Combine(repositoryRoot, "tools", "usage", "usage.fsx")

    let dotnet =
        Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" |> Option.ofObj |> Option.filter File.Exists |> Option.defaultValue "dotnet"

    // Values that must never reach a record: a path, a token-shaped value and a positional value.
    let secretPath = "/srv/secret/customer-a.json"
    let secretToken = "ghp_" + String('Q', 36)
    let secretPositional = "acme-private-repository"

    /// A scratch directory holding a stand-in `ordo` that answers --version and otherwise writes one line to each
    /// stream and exits 7.
    let sandbox () =
        let dir = Path.Combine(Path.GetTempPath(), "causa-usage-" + Guid.NewGuid().ToString "N")
        let bin = Path.Combine(dir, "bin")
        Directory.CreateDirectory bin |> ignore
        let fake = Path.Combine(bin, "ordo")
        File.WriteAllText(
            fake,
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'sde 9.8.7'; exit 0; fi\nprintf 'out-line\\n'\nprintf 'err-line\\n' >&2\nexit 7\n"
        )
        File.SetUnixFileMode(fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        dir, bin

    type Run = { Exit: int; Out: string; Err: string }

    let run (dir: string) (bin: string) (recordDir: string) (args: string list) : Run =
        let info = ProcessStartInfo(dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir)
        for a in [ "fsi"; script; "--" ] @ args do
            info.ArgumentList.Add a
        let path = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
        info.Environment["PATH"] <- bin + string Path.PathSeparator + path
        info.Environment["CAUSA_TOOL_USAGE_DIR"] <- recordDir
        for name in [ "PRAXIS_EXECUTION_ID"; "PRAXIS_WORK_ITEM" ] do
            info.Environment.Remove name |> ignore
        use p =
            match Process.Start info with
            | null -> failwith "the recorder did not start"
            | p -> p
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        if not (p.WaitForExit 180000) then
            p.Kill true
            failwith "the recorder did not finish in three minutes"
        { Exit = p.ExitCode; Out = out.Result; Err = err.Result }

    let lines (recordDir: string) =
        if Directory.Exists recordDir then
            Directory.GetFiles(recordDir, "*.ndjson") |> Array.collect File.ReadAllLines |> Array.filter ((<>) "") |> List.ofArray
        else []

module private Fixture =
    let at (s: string) = DateTimeOffset.Parse(s, Globalization.CultureInfo.InvariantCulture)

    let record id tool sub (started: string) (ms: int64) exit workItem : UsageRecord =
        { InvocationId = id
          Tool = tool
          ToolVersion = Some "1.0.0"
          Subcommand = sub
          ArgNames = []
          ArgCount = 0
          StartedAt = at started
          EndedAt = (at started).AddMilliseconds(float ms)
          DurationMs = ms
          ExitCode = exit
          Outcome = Record.outcomeOf exit
          Praxis = { WorkItem = workItem; ExecutionId = None; Resolution = (if workItem.IsSome then FromEnvironment else NoneActive) }
          Environment = "local" }

    let records =
        [ record "a" "ordo" (Some "status") "2026-11-01T10:00:00Z" 100L 0 (Some "WI-1")
          record "b" "ordo" (Some "status") "2026-11-02T10:00:00Z" 300L 0 (Some "WI-1")
          record "c" "ordo" (Some "verify") "2026-11-02T11:00:00Z" 1000L 1 None
          record "d" "ordo" (Some "status") "2026-11-03T09:00:00Z" 200L 0 (Some "WI-2")
          record "e" "praxis" (Some "validate") "2026-11-03T12:00:00Z" 50L 0 (Some "WI-2")
          record "f" "praxis" None "2026-11-01T08:00:00Z" 70L 3 None ]

    /// The fixture as an ndjson file, with one unreadable line.
    let ndjson = (records |> List.map Record.toJson |> String.concat "\n") + "\nnot a record\n"

type UsageRecorderTests() =

    [<Fact>]
    member _.``run passes the tool's stdout, stderr and exit code through unchanged and records the invocation``() =
        let dir, bin = Recorder.sandbox ()
        let records = Path.Combine(dir, "records")
        let r =
            Recorder.run dir bin records [ "run"; "ordo"; "--"; "verify"; "--target"; Recorder.secretPath; "--token=" + Recorder.secretToken; Recorder.secretPositional ]
        Assert.Equal(7, r.Exit)
        Assert.Equal("out-line\n", r.Out)
        Assert.Equal("err-line\n", r.Err)
        let line = Assert.Single(Recorder.lines records)
        match Record.ofJson line with
        | Error e -> failwithf "the record does not parse: %s" e
        | Ok record ->
            Assert.Equal("ordo", record.Tool)
            Assert.Equal(Some "9.8.7", record.ToolVersion)
            Assert.Equal(Some "verify", record.Subcommand)
            Assert.Equal<string list>([ "--target"; "--token" ], record.ArgNames)
            Assert.Equal(5, record.ArgCount)
            Assert.Equal(7, record.ExitCode)
            Assert.Equal(Failed, record.Outcome)
            Assert.True(record.EndedAt >= record.StartedAt)
            Assert.Equal(TimeSpan.Zero, record.StartedAt.Offset)
        Directory.Delete(dir, true)

    [<Fact>]
    member _.``no argument value, positional value or token reaches the record``() =
        let dir, bin = Recorder.sandbox ()
        let records = Path.Combine(dir, "records")
        Recorder.run dir bin records [ "run"; "ordo"; "--"; "boundary"; "assess"; "--input"; Recorder.secretPath; "--token=" + Recorder.secretToken; Recorder.secretPositional ]
        |> ignore
        let text = String.Join("\n", Recorder.lines records)
        Assert.False(String.IsNullOrEmpty text, "no record was written")
        for secret in [ Recorder.secretPath; Recorder.secretToken; Recorder.secretPositional; "customer-a"; "QQQQ" ] do
            Assert.DoesNotContain(secret, text)
        Directory.Delete(dir, true)

    [<Fact>]
    member _.``a recording failure warns on stderr and never changes the tool's exit code``() =
        let dir, bin = Recorder.sandbox ()
        let blocker = Path.Combine(dir, "a-file")
        File.WriteAllText(blocker, "")
        // The record directory would have to be inside a regular file: the write must fail.
        let r = Recorder.run dir bin (Path.Combine(blocker, "records")) [ "run"; "ordo"; "--"; "status" ]
        Assert.Equal(7, r.Exit)
        Assert.Equal("out-line\n", r.Out)
        Assert.StartsWith("err-line\n", r.Err)
        Assert.Contains("tool-usage: warning: the invocation was not recorded", r.Err)
        Directory.Delete(dir, true)

    [<Fact>]
    member _.``argument shape keeps known subcommand words and flag names only``() =
        let shape tool args = Arguments.shape tool args
        let s1 = shape Praxis [ "work"; "context"; "WI-0001"; "--text" ]
        Assert.Equal(Some "work context", s1.Subcommand)
        Assert.Equal<string list>([ "--text" ], s1.ArgNames)
        Assert.Equal(4, s1.ArgCount)
        Assert.Equal(Some "validate", (shape Praxis [ "--root"; "/home/someone/private"; "validate" ]).Subcommand)
        Assert.Equal(Some "telemetry step start", (shape Praxis [ "telemetry"; "step"; "start"; "--step"; "S-1" ]).Subcommand)
        Assert.Equal(Some "boundary assess", (shape Ordo [ "boundary"; "assess"; "--input"; "x.json" ]).Subcommand)
        Assert.Equal(Some "check-sequencing", (shape R9 [ "check-sequencing"; "--target"; "." ]).Subcommand)
        Assert.Equal(None, (shape Ordo [ "customer-secret-word" ]).Subcommand)
        Assert.Equal(None, (shape RequirementsCompiler [ "--process-plan"; "plan.json" ]).Subcommand)
        let s2 = shape Praxis [ "step"; "record"; "--metric=tool.calls"; "--value"; "-5"; "-"; "--"; "--evidence"; "a"; "--evidence"; "b" ]
        Assert.Equal<string list>([ "--metric"; "--value"; "--evidence" ], s2.ArgNames)

    [<Fact>]
    member _.``a serialised record round-trips and carries only the schema's members``() =
        let r = { Fixture.records.Head with ArgNames = [ "--json" ]; ArgCount = 1 }
        let json = Record.toJson r
        Assert.Equal(Ok r, Record.ofJson json)
        use d = JsonDocument.Parse json
        let names = d.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> List.ofSeq
        Assert.Equal<string list>(
            [ "schema"; "invocationId"; "tool"; "toolVersion"; "subcommand"; "argNames"; "argCount"; "startedAt"; "endedAt"; "durationMs"; "exitCode"; "outcome"; "praxis"; "environment" ],
            names
        )
        Assert.Equal("2026-11-01.ndjson", Record.fileName r)
        Assert.Equal("a\nb\n", Record.appended "a" "b")
        Assert.Equal("a\nb\n", Record.appended "a\n" "b")

    [<Fact>]
    member _.``report aggregates counts, durations, failures and first and last use per tool and subcommand``() =
        let records, skipped = Record.parseLines Fixture.ndjson
        Assert.Equal<int list>([ 7 ], skipped)
        let report = Report.build ByTool None (List.length skipped) records
        Assert.Equal(6, report.Records)
        let group = Assert.Single report.Groups
        let ordo = group.Tools |> List.find (fun t -> t.Tool = "ordo")
        Assert.Equal(4, ordo.Stats.Invocations)
        Assert.Equal(1600L, ordo.Stats.TotalDurationMs)
        Assert.Equal(250.0, ordo.Stats.MedianDurationMs)
        Assert.Equal(1, ordo.Stats.Failures)
        Assert.Equal(Fixture.at "2026-11-01T10:00:00Z", ordo.Stats.FirstUse)
        Assert.Equal(Fixture.at "2026-11-03T09:00:00Z", ordo.Stats.LastUse)
        let status = ordo.Subcommands |> List.find (fun s -> s.Subcommand = "status")
        Assert.Equal(3, status.Stats.Invocations)
        Assert.Equal(200.0, status.Stats.MedianDurationMs)
        Assert.Equal(0, status.Stats.Failures)
        let praxis = group.Tools |> List.find (fun t -> t.Tool = "praxis")
        Assert.Equal<string list>([ "(none)"; "validate" ], praxis.Subcommands |> List.map (fun s -> s.Subcommand))
        Assert.Equal(1, praxis.Stats.Failures)

    [<Fact>]
    member _.``report is deterministic over a fixed fixture whatever the record order``() =
        let records, skipped = Record.parseLines Fixture.ndjson
        for grouping in [ ByTool; ByDay; ByWorkItem ] do
            let a = Report.build grouping None skipped.Length records
            let b = Report.build grouping None skipped.Length (List.rev records)
            Assert.Equal(Report.toJson a, Report.toJson b)
            Assert.Equal(Report.toText a, Report.toText b)
        let byDay = Report.build ByDay None 0 records
        Assert.Equal<string list>([ "2026-11-01"; "2026-11-02"; "2026-11-03" ], byDay.Groups |> List.map (fun g -> g.Key))
        let byItem = Report.build ByWorkItem None 0 records
        Assert.Equal<string list>([ "(no work item)"; "WI-1"; "WI-2" ], byItem.Groups |> List.map (fun g -> g.Key))
        let since = Report.build ByTool (Some(DateOnly(2026, 11, 2))) 0 records
        Assert.Equal(4, since.Records)

    [<Fact>]
    member _.``the Praxis context comes from Praxis's own selectors, else from the one active execution of this session``() =
        let files =
            Map.ofList
                [ ".ros/context/current.json",
                  """{"workItems":[{"id":"WI-1","state":"active","telemetryExecutionIds":["EXE-1"]},{"id":"WI-2","state":"active","telemetryExecutionIds":["EXE-2"]},{"id":"WI-3","state":"complete","telemetryExecutionIds":["EXE-3"]}]}"""
                  ".ros/telemetry/executions/EXE-1.json", """{"workItemId":"WI-1","status":"active","identity":{"sessionId":"s-1"}}"""
                  ".ros/telemetry/executions/EXE-2.json", """{"workItemId":"WI-2","status":"active","identity":{"sessionId":"s-2"}}"""
                  ".ros/telemetry/executions/EXE-3.json", """{"workItemId":"WI-3","status":"finalized","identity":{"sessionId":"s-1"}}""" ]
        let read p = Map.tryFind p files
        let env pairs name = pairs |> List.tryFind (fst >> (=) name) |> Option.map snd
        Assert.Equal(
            { WorkItem = Some "WI-2"; ExecutionId = Some "EXE-2"; Resolution = FromEnvironment },
            PraxisContext.resolve (env [ "PRAXIS_EXECUTION_ID", "EXE-2" ]) read
        )
        Assert.Equal(
            { WorkItem = Some "WI-1"; ExecutionId = Some "EXE-1"; Resolution = FromSession },
            PraxisContext.resolve (env [ "CLAUDE_CODE_SESSION_ID", "s-1" ]) read
        )
        Assert.Equal(PraxisContext.none, PraxisContext.resolve (env [ "CLAUDE_CODE_SESSION_ID", "s-9" ]) read)
        Assert.Equal(PraxisContext.none, PraxisContext.resolve (env [ "PRAXIS_EXECUTION_ID", "has spaces; rm" ]) (fun _ -> None))
        let shared p =
            match p with
            | ".ros/telemetry/executions/EXE-2.json" -> Some """{"status":"active","identity":{"sessionId":"s-1"}}"""
            | _ -> read p
        Assert.Equal(Ambiguous 2, (PraxisContext.resolve (env [ "CLAUDE_CODE_SESSION_ID", "s-1" ]) shared).Resolution)

    [<Fact>]
    member _.``the inventory is deterministic and states that Ordo has no runtime usage``() =
        let files =
            Map.ofList
                [ "src/Causa.Host/Causa.Host.csproj", """<Project><ItemGroup><ProjectReference Include="../Causa.App/Causa.App.fsproj" /></ItemGroup></Project>"""
                  "src/Causa.App/Causa.App.fsproj", "<Project />"
                  "src/Causa.App/Program.fs", "writer.WriteString(\"kind\", \"Http\")\n| Some \"Initialize\" -> ()"
                  "web/boundary.js", "import { BrowserKernel } from \"@echelon-foundry/limen\";"
                  "web/index.html", "<div class=\"ef-badge causa-x\" data-percepta-region=\"r1\"></div>"
                  "web/causa.css", ".causa-x { color: var(--ef-color-text); }" ]
        let view = { Files = files |> Map.toList |> List.map fst; Read = fun p -> Map.tryFind p files }
        let a = Inventory.build view
        Assert.Equal(a, Inventory.build { view with Files = List.rev view.Files })
        use d = JsonDocument.Parse a
        let libraries = d.RootElement.GetProperty("libraries").EnumerateArray() |> List.ofSeq
        let byId id = libraries |> List.find (fun l -> l.GetProperty("id").GetString() = id)
        Assert.Equal("none", byId("ordo").GetProperty("runtimeUsage").GetString())
        Assert.Equal("used", byId("limen").GetProperty("runtimeUsage").GetString())
        Assert.Equal(1, byId("forma").GetProperty("tokens").GetProperty("distinctUsed").GetInt32())
        Assert.Equal(1, byId("forma").GetProperty("classes").GetProperty("distinctUsed").GetInt32())
        Assert.Equal("used", byId("percepta").GetProperty("runtimeUsage").GetString())
