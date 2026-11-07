// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-011 four independent verification levels: domain, UI, provider conformance and live integration, each with its
// own command, outcome and evidence; a higher level never hides a lower one.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF011Tests(fixture: LiveFixture<PF011Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf011/investigations/00000000-0000-0000-0000-000000000011"

    /// The suite commands the plan's suite-preflight recorded for this checkout (evidence/t0/suite-preflight.json).
    let suites () =
        Json.parse (Repo.read "evidence/t0/suite-preflight.json")
        |> Json.field "suites"
        |> Json.items
        |> List.choose (fun s ->
            match Some s |> Json.field "id" |> Json.text, Some s |> Json.field "command" |> Json.text with
            | Some id, Some command -> Some(id, command, Some s |> Json.field "executable" |> Json.bool = Some true)
            | _ -> None)

    let command id =
        match suites () |> List.tryFind (fun (i, _, _) -> i = id) with
        | Some(_, c, executable) ->
            Assert.True(executable, sprintf "%s cannot execute" id)
            c
        | None -> failwithf "suite-preflight records no %s" id

    /// The four levels and the suite that is each level's independent command.
    let levels =
        [ "domain", "SU-OWN-DOTNET"; "ui", "SU-BROWSER-CHROMIUM"; "provider-conformance", "SU-DOTNET-DETERMINISTIC"; "live-integration", "SU-DOTNET-LIVE" ]

    let fsproj path = Repo.read path

    interface IClassFixture<LiveFixture<PF011Tests>>

    /// PF-011-O1: the domain level is deterministic F# validation, reconstruction and transition tests with no external
    /// dependency: the domain test project references only the domain, the domain references no persistence or HTTP,
    /// and its suite command selects Causa.Domain.Tests.
    [<Fact>]
    member _.``PF-011-O1``() =
        let project = fsproj "tests/Causa.Domain.Tests/Causa.Domain.Tests.fsproj"
        Assert.Contains("Causa.Domain.fsproj", project)
        for forbidden in [ "Causa.Persistence"; "Causa.TestSupport"; "Causa.App" ] do
            Assert.DoesNotContain(forbidden, project)
        let refs = typeof<Causa.Domain.Investigation>.Assembly.GetReferencedAssemblies() |> Array.map (fun a -> string a.Name)
        Assert.DoesNotContain("System.Net.Http", refs)
        Assert.Contains("Causa.Domain.Tests.", command "SU-OWN-DOTNET")

    /// PF-011-O2: the UI level runs browser tests against local fixtures with no external dependency: the browser
    /// suite serves the build on 127.0.0.1 and no browser acceptance spec names a credential.
    [<Fact>]
    member _.``PF-011-O2``() =
        let config = Repo.read "playwright.config.ts"
        Assert.Contains("127.0.0.1", config)
        Assert.Contains("--project=chromium", command "SU-BROWSER-CHROMIUM")
        let dir = Repo.path "tests/browser/acceptance"
        if IO.Directory.Exists dir then
            for spec in IO.Directory.EnumerateFiles(dir, "*.js", IO.SearchOption.AllDirectories) do
                let text = IO.File.ReadAllText spec
                for marker in [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] do
                    Assert.False(text.Contains marker, sprintf "%s needs %s" spec marker)

    /// PF-011-O3: provider conformance runs one shared contract script against the fixture adapter and the GitHub
    /// adapter, with the sanitized recorded answers: every recorded form classifies into its matrix class, the fake
    /// passes the script, and real GitHub passes it with no divergence.
    [<Fact>]
    member _.``PF-011-O3``() =
        for r in Recordings.all do
            let cls = fst (Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 1L) r))
            Assert.True((cls = r.ExpectedClass), sprintf "%s classified as %s" r.Form (MatrixClass.id cls))
        let fake = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
        Assert.Empty(Conformance.failures fake)
        let live = fixture.Run
        let real = Conformance.run (Conformance.overLive live) live.Created
        Assert.Empty(Conformance.failures real)
        Assert.Empty(Conformance.divergence fake real)

    /// PF-011-O4: live integration makes real GitHub requests against the authorized data repository and checks
    /// catalog discovery, data transfer, reconstruction, expected digests and errors.
    [<Fact>]
    member _.``PF-011-O4``() =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        for case in PinnedCatalog.cases do
            let files = PinnedCatalog.ok ("read " + case.Key) (PinnedCatalog.readFiles live.Reader head case)
            Assert.Equal(case.EventsSha256, PinnedCatalog.sha256 files.Events)
        Assert.Equal("NotFound", Conformance.kind (live.Reader.ReadFile head (RepoPath "catalog/no-such-catalog.json")))
        match AppList.list live.Execute live.ReadConnection ActiveCatalog with
        | Ok listing -> Assert.Equal(PinnedCatalog.cases.Length, listing.Investigations.Length)
        | Error f -> failwithf "catalog discovery failed: %A" f
        AppLoad.requireCodec ()
        for case in PinnedCatalog.cases do
            let v = AppLoad.load live.Execute live.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified case.Key
            Assert.Equal(Causa.Domain.AggregateVersion(int64 case.ExpectedVersion), v.Investigation.Version)

    /// PF-011-O5: each level has an independently executable command, a CI outcome and evidence: the four suite
    /// commands are distinct and executable, the governed gate runs them as separate suites, and the live level
    /// writes its own evidence file.
    [<Fact>]
    member _.``PF-011-O5``() =
        let commands = levels |> List.map (snd >> command)
        Assert.Equal(levels.Length, commands |> List.distinct |> List.length)
        let gate = Repo.read ".github/workflows/verification-gate.yml"
        for (_, suite) in levels do
            Assert.Contains(suite, gate)
        let live = fixture.Run
        live.Reader.ReadHead() |> PinnedCatalog.ok "live read" |> ignore
        let evidence = live.WriteEvidence()
        Assert.True(IO.File.Exists evidence)
        Assert.Contains("\"providerMode\":\"live-github\"", IO.File.ReadAllText evidence)

    /// PF-011-O6: a higher-level pass never conceals a lower-level failure: the gate always runs the lower suites with
    /// the live ones, a live report whose probes fail is not live verification, a fake-only pass never is, and a live
    /// run that passes does not turn a failing fake transcript into a pass.
    [<Fact>]
    member _.``PF-011-O6``() =
        let gate = Repo.read ".github/workflows/verification-gate.yml"
        let devSuites = gate.Split('\n') |> Array.filter (fun l -> l.Contains "suites=\"--suites")
        Assert.All(devSuites, fun l -> Assert.Contains("SU-DOTNET-DETERMINISTIC", l))
        Assert.Contains("suites=\"$suites,SU-DOTNET-LIVE", gate)
        let failing : Conformance.Probe = { Name = "lower level"; Expected = "ok"; Actual = "StaleHead" }
        let report mode probes commits : Conformance.Report = { Mode = mode; Probes = probes; CreatedCommits = commits }
        Assert.False((report "live-github" [ failing ] [ CommitOid "a" ]).IsLiveVerification)
        let fake = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
        Assert.False((report "fake" fake []).IsLiveVerification)
        let live = fixture.Run
        let real = Conformance.run (Conformance.overLive live) live.Created
        let liveReport = report "live-github" real live.CreatedCommits
        Assert.True(liveReport.IsLiveVerification)
        Assert.False((report "live-github" (failing :: real) live.CreatedCommits).Passed)
