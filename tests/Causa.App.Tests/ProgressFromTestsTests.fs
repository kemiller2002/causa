// Owner: integration. The progress deriver (tools/progress/progress-from-tests.fsx): its derivation rules, its
// determinism and its equivalence with the Python script it replaced (docs/quality/progress_from_tests.py, PR #11).
// The deriver's core is linked from tools/progress/ProgressCore.fs, as UsageRecorderTests links tools/usage.
//
// Fixtures/progress-from-tests holds a small repository (work plan, obligations, existing events), two .trx files, two
// Playwright reports and expected.stdout/expected.stderr: the bytes the Python script printed for exactly these inputs,
// with its clock fixed to 2026-11-08T12:00:00Z, in the git repository FixtureRepository.build creates.
namespace Causa.App.Tests

open System
open System.Diagnostics
open System.IO
open Xunit
open Causa.Tools.Progress
open Causa.Tools.PythonCompat

module private ProgressFixture =
    let repositoryRoot =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwithf "no Causa.slnx above %s" AppContext.BaseDirectory
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let dir = Path.Combine(repositoryRoot, "tests", "Causa.App.Tests", "Fixtures", "progress-from-tests")
    let file (relative: string) = Path.Combine(dir, relative)
    let text (relative: string) = File.ReadAllText(file relative)
    let script = Path.Combine(repositoryRoot, "tools", "progress", "progress-from-tests.fsx")

    let at = "2026-11-08T12:00:00Z"
    /// The commit FixtureRepository.build produces (fixed author, committer, dates and content).
    let commit = "ec88e860056d4b6c5a6e2292241c3dbc09815126"
    let reports = [ "playwright/first.json"; "playwright/rerun.json" ]

    let ok (r: Result<'a, string>) : 'a =
        match r with
        | Ok v -> v
        | Error e -> failwithf "unexpected error: %s" e

    /// The fixture's inputs; the .trx files in modification-time order (2-older before 1-newer).
    let inputs () : Inputs =
        let trx = [ "trx/2-older.trx"; "trx/1-newer.trx" ] |> List.map (text >> Results.trx >> ok)
        let browser = reports |> List.map (text >> Results.playwright >> ok)
        { Plan = text "repo/.echelon/r9-work-plan.json" |> Inputs.plan |> ok
          Obligations = text "repo/.echelon/obligations.json" |> Inputs.obligations |> ok
          Existing = text "repo/evidence/progress/events.json" |> Inputs.events |> ok
          Results = Results.merge (trx @ browser) }

    /// Slice A's and slice E's own commits are in the measured commit; B's is not and D has no branch.
    let onMain = set [ "A"; "E" ]

    let render (i: Inputs) (onMain: Set<string>) =
        let d = Derive.progress i onMain
        Output.events (Derive.events at (fun () -> commit) d) + "\n", Output.summary i.Results d + "\n"

module private FixtureRepository =
    let dotnet =
        Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" |> Option.ofObj |> Option.filter File.Exists |> Option.defaultValue "dotnet"

    type Run = { Exit: int; Out: string; Err: string }

    let run (file: string) (cwd: string) (env: (string * string) list) (args: string list) : Run =
        let info = ProcessStartInfo(file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd)
        for a in args do
            info.ArgumentList.Add a
        for k, v in env do
            info.Environment[k] <- v
        use p =
            match Process.Start info with
            | null -> failwithf "%s did not start" file
            | p -> p
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        { Exit = p.ExitCode; Out = out.Result; Err = err.Result }

    let git (repo: string) (args: string list) =
        let stamp = "2026-01-01T00:00:00Z"
        let env = [ "GIT_AUTHOR_DATE", stamp; "GIT_COMMITTER_DATE", stamp; "GIT_CONFIG_NOSYSTEM", "1"; "GIT_CONFIG_GLOBAL", "/dev/null" ]
        let config = [ "-c"; "user.name=fixture"; "-c"; "user.email=fixture@example.invalid"; "-c"; "commit.gpgsign=false"; "-c"; "init.defaultBranch=main" ]
        let r = run "git" repo env (config @ args)
        if r.Exit <> 0 then failwithf "git %s failed: %s" (String.concat " " args) r.Err

    let rec copy (source: string) (target: string) =
        Directory.CreateDirectory target |> ignore
        for f in Directory.GetFiles source do
            File.Copy(f, Path.Combine(target, Path.GetFileName f |> string))
        for d in Directory.GetDirectories source do
            copy d (Path.Combine(target, Path.GetFileName d |> string))

    /// A scratch directory with `repo` (the fixture repository with slice branches A, A-browser, B and E-ui; main
    /// merges A and E) and `trx` (the .trx files with 1-newer modified after 2-older).
    let build () : string =
        let work = Path.Combine(Path.GetTempPath(), "causa-progress-" + Guid.NewGuid().ToString "N")
        let repo = Path.Combine(work, "repo")
        copy (ProgressFixture.file "repo") repo
        copy (ProgressFixture.file "trx") (Path.Combine(work, "trx"))
        File.SetLastWriteTimeUtc(Path.Combine(work, "trx", "1-newer.trx"), DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))
        File.SetLastWriteTimeUtc(Path.Combine(work, "trx", "2-older.trx"), DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        git repo [ "init"; "-q" ]
        git repo [ "add"; "-A" ]
        git repo [ "commit"; "-q"; "-m"; "base" ]
        for branch, slice in [ "work-a", "SL-A"; "work-a-browser", "SL-A-browser"; "work-b", "SL-B"; "work-e", "SL-E-ui" ] do
            git repo [ "checkout"; "-q"; "-b"; branch; "main" ]
            git repo [ "commit"; "-q"; "--allow-empty"; "-m"; branch ]
            git repo [ "update-ref"; "refs/remotes/origin/slice/" + slice; "HEAD" ]
        git repo [ "checkout"; "-q"; "main" ]
        git repo [ "merge"; "-q"; "--no-ff"; "-m"; "merge A"; "work-a" ]
        git repo [ "merge"; "-q"; "--no-ff"; "-m"; "merge E"; "work-e" ]
        work

    let derive (work: string) =
        run dotnet work [ "CAUSA_PROGRESS_AT", ProgressFixture.at ] ([ "fsi"; ProgressFixture.script; "--"; "repo"; "main"; "trx" ] @ (ProgressFixture.reports |> List.map ProgressFixture.file))

module ProgressFromTestsTests =
    let private item id requirements = { Id = id; Requirements = requirements }
    let private obligation id requirement categories = { Id = id; Requirement = requirement; Categories = set categories }

    let private inputs plan obligations existing results =
        { Plan = plan
          Obligations = { TestObligations = Map.empty; Obligations = obligations }
          Existing = existing
          Results = Map.ofList results }

    let private completed i onMain = (Derive.progress i (set onMain)).NewlyCompleted

    // ---- Equivalence with the Python script ---------------------------------------------------------------------

    [<Fact>]
    let ``Equivalence: the core reproduces the Python script's stdout and stderr byte for byte`` () =
        let out, err = ProgressFixture.render (ProgressFixture.inputs ()) ProgressFixture.onMain
        Assert.Equal(ProgressFixture.text "expected.stdout", out)
        Assert.Equal(ProgressFixture.text "expected.stderr", err)

    [<Fact>]
    let ``Equivalence: the script, run against a real git repository, prints the Python script's bytes`` () =
        let work = FixtureRepository.build ()
        try
            let first = FixtureRepository.derive work
            Assert.True((first.Exit = 0), first.Err)
            Assert.Equal(ProgressFixture.text "expected.stdout", first.Out)
            Assert.Equal(ProgressFixture.text "expected.stderr", first.Err)
            // Determinism end to end: a second run over the same inputs prints the same bytes.
            let second = FixtureRepository.derive work
            Assert.Equal(first, second)
        finally
            Directory.Delete(work, true)

    // ---- Determinism ---------------------------------------------------------------------------------------------

    [<Fact>]
    let ``Determinism: the same inputs give the same bytes, whatever order distinct test results arrive in`` () =
        let i = ProgressFixture.inputs ()
        let shuffled = { i with Results = i.Results |> Map.toList |> List.rev |> Map.ofList }
        Assert.Equal(ProgressFixture.render i ProgressFixture.onMain, ProgressFixture.render i ProgressFixture.onMain)
        Assert.Equal(ProgressFixture.render i ProgressFixture.onMain, ProgressFixture.render shuffled ProgressFixture.onMain)

    [<Fact>]
    let ``Determinism: completed requirements are sorted ordinally, not in plan order`` () =
        let requirements = [ "R-010"; "R-002"; "Q-001" ]
        let i =
            inputs
                [ item "W" requirements ]
                [ for r in requirements -> obligation (r + "-O1") r [] ]
                []
                [ for r in requirements -> "dotnet:" + r + "-O1", true ]
        Assert.Equal<string list>([ "Q-001"; "R-002"; "R-010" ], completed i [ "W" ])

    [<Fact>]
    let ``Determinism: the timestamp is UTC seconds with Z and an override must have exactly that form`` () =
        Assert.Equal("2026-11-08T12:00:00Z", Output.timestamp (DateTimeOffset(2026, 11, 8, 13, 0, 0, TimeSpan.FromHours 1.0)))
        Assert.Equal(Ok "2026-11-08T12:00:00Z", Output.parseTimestamp "2026-11-08T12:00:00Z")
        Assert.True(Result.isError (Output.parseTimestamp "2026-11-08 12:00"))

    // ---- Derivation rules ----------------------------------------------------------------------------------------

    [<Fact>]
    let ``Rule: a work item is started only when its slice work is on main, and only once`` () =
        let i = inputs [ item "A" [ "R-001" ]; item "B" [ "R-002" ]; item "C" [ "R-003" ] ] [] [ WasStarted "C" ] []
        let d = Derive.progress i (set [ "A"; "C" ])
        Assert.Equal<string list>([ "A" ], d.NewlyStarted |> List.map (fun w -> w.Id))

    [<Fact>]
    let ``Rule: only requirements of started work items can complete, and never twice`` () =
        let i =
            inputs
                [ item "A" [ "R-001"; "R-002" ]; item "B" [ "R-003" ]; item "C" [ "R-004" ] ]
                [ for r in [ "R-001"; "R-002"; "R-003"; "R-004" ] -> obligation (r + "-O1") r [] ]
                [ WasStarted "C"; WasCompleted [ "R-002" ]; OtherEvent "verification-recorded" ]
                [ for r in [ "R-001"; "R-002"; "R-003"; "R-004" ] -> "dotnet:T " + r + "-O1", true ]
        Assert.Equal<string list>([ "R-001"; "R-004" ], completed i [ "A" ])

    [<Fact>]
    let ``Rule: every locally testable obligation needs a passing test and no failing test`` () =
        let plan = [ item "A" [ "R-001" ] ]
        let obligations = [ obligation "R-001-O1" "R-001" []; obligation "R-001-O2" "R-001" [ "security" ] ]
        let evidenced results = completed (inputs plan obligations [] results) [ "A" ]
        Assert.Equal<string list>([ "R-001" ], evidenced [ "dotnet:x R-001-O1", true; "dotnet:y R-001-O2", true ])
        Assert.Empty(evidenced [ "dotnet:x R-001-O1", true ])
        Assert.Empty(evidenced [ "dotnet:x R-001-O1", true; "dotnet:y R-001-O2", true; "playwright:z R-001-O2", false ])

    [<Fact>]
    let ``Rule: live-provider, independent-acceptance, process-record and owner-decision obligations are not required`` () =
        let plan = [ item "A" [ "R-001"; "R-002" ] ]
        let obligations =
            [ yield obligation "R-001-O1" "R-001" []
              for c in [ "live-provider"; "independent-acceptance"; "process-record"; "owner-decision" ] ->
                  obligation ("R-001-" + c) "R-001" [ "functional"; c ]
              yield obligation "R-002-O1" "R-002" [ "owner-decision" ] ]
        let i = inputs plan obligations [] [ "dotnet:R-001-O1", true; "dotnet:R-002-O1", true ]
        // R-002 has no locally testable obligation at all, so a passing test cannot complete it.
        Assert.Equal<string list>([ "R-001" ], completed i [ "A" ])

    [<Fact>]
    let ``Rule: no completion event when nothing completes, and the commit is then never resolved`` () =
        let i = inputs [ item "A" [ "R-001" ] ] [ obligation "R-001-O1" "R-001" [] ] [] []
        let events = Derive.progress i (set [ "A" ]) |> Derive.events "t" (fun () -> failwith "the commit was resolved")
        Assert.Equal<ProgressEvent list>([ ImplementationStarted(item "A" [ "R-001" ], "t") ], events)
        Assert.Equal("[]", Output.events [])

    [<Fact>]
    let ``Rule: a test evidences the obligations its name carries and those the plan maps to it`` () =
        let mapping = Map.ofList [ "dotnet:Suite.Mapped", [ "R-002-O1" ]; "Suite.Bare", [ "R-003-O1" ] ]
        let of' = Derive.obligationsOfTest mapping >> Set.toList
        Assert.Equal<string list>([ "EXT-DATA-011-O2"; "UI-029-O1" ], of' "playwright:UI-029-O1 and EXT-DATA-011-O2")
        Assert.Equal<string list>([ "R-002-O1" ], of' "dotnet:Suite.Mapped")
        Assert.Equal<string list>([ "R-003-O1" ], of' "dotnet:Suite.Bare")
        Assert.Empty(of' "dotnet:Suite.x_R-001-O1")
        Assert.Empty(of' "dotnet:R-01-O1 r-001-O1 R-001-O")

    [<Fact>]
    let ``Rule: a later result for the same test wins`` () =
        let older = Results.trx (ProgressFixture.text "trx/2-older.trx") |> ProgressFixture.ok
        let newer = Results.trx (ProgressFixture.text "trx/1-newer.trx") |> ProgressFixture.ok
        Assert.Equal(Some true, Results.merge [ older; newer ] |> Map.tryFind "dotnet:Suite.R-001-O1 works")
        Assert.Equal(Some false, Results.merge [ newer; older ] |> Map.tryFind "dotnet:Suite.R-001-O1 works")

    [<Fact>]
    let ``Rule: .trx results are read from the TeamTest namespace only, and a nameless result is dotnet:None`` () =
        let r = Results.trx (ProgressFixture.text "trx/2-older.trx") |> ProgressFixture.ok |> Map.ofList
        Assert.Equal(Some true, Map.tryFind "dotnet:None" r)
        Assert.Equal(Some false, Map.tryFind "dotnet:Suite.Nested R-006-O1" r)
        Assert.Equal(Some true, Map.tryFind "dotnet:Suite.R-003-O1 saves" r)
        Assert.False(r.ContainsKey "dotnet:Suite.R-003-O1 other namespace is ignored")

    [<Fact>]
    let ``Rule: a Playwright spec passes only when every test is expected, nested suites included`` () =
        let r = Results.playwright (ProgressFixture.text "playwright/first.json") |> ProgressFixture.ok |> Map.ofList
        Assert.Equal(Some true, Map.tryFind "playwright:renders the start page" r)
        Assert.Equal(Some false, Map.tryFind "playwright:R-003-O1 graph renders" r)
        Assert.Equal(Some true, Map.tryFind "playwright:spec with no tests" r)
        Assert.Equal(Some false, Map.tryFind "playwright:R-012-O1 skipped" r)

    [<Fact>]
    let ``Rule: a slice branch belongs to SL-<id> and SL-<id>-*, never to a longer id`` () =
        let mine, others =
            Branches.split "T1" [ "origin/slice/SL-T1"; "origin/slice/SL-T10"; "origin/slice/SL-T1-browser"; "origin/slice/SL-T2" ]
        Assert.Equal<string list>([ "origin/slice/SL-T1"; "origin/slice/SL-T1-browser" ], mine)
        Assert.Equal<string list>([ "origin/slice/SL-T10"; "origin/slice/SL-T2" ], others)

    [<Fact>]
    let ``Rule: malformed inputs are errors, not exceptions`` () =
        Assert.True(Result.isError (Inputs.plan "{}"))
        Assert.True(Result.isError (Inputs.events "[{\"kind\": \"implementation-started\"}]"))
        Assert.True(Result.isError (Inputs.obligations "not json"))
        Assert.True(Result.isError (Results.trx "<unclosed"))

    [<Fact>]
    let ``Output: JSON is rendered as Python's json.dumps(indent=2) with ASCII escapes`` () =
        Assert.Equal("\"R\\u00e9 \\u007f \\ud83d\\ude00 \\\" \\\\ \\n \\u0001 ~\"", Json.quote "R\u00e9 \u007f \U0001F600 \" \\ \n \u0001 ~")
        Assert.Equal("{\n  \"a\": [],\n  \"b\": [\n    \"c\"\n  ]\n}", Json.dumps 2 (JObject [ "a", JArray []; "b", JArray [ JString "c" ] ]))
