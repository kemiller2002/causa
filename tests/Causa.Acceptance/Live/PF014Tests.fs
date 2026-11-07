// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-014 live integration acceptance: the real provider against the approved repository and the pinned catalog.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF014Tests(fixture: LiveFixture<PF014Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf014/investigations/00000000-0000-0000-0000-000000000014"
    let json (bytes: byte[]) = Json.parse (Encoding.UTF8.GetString bytes)
    let env name = match Environment.GetEnvironmentVariable name with null | "" -> None | v -> Some v

    interface IClassFixture<LiveFixture<PF014Tests>>

    /// PF-014-O1: a real GitHub provider test runs against the approved repository and the pinned catalog:
    /// catalog/active.json names the pinned catalog, and its manifest lists exactly the pinned cases.
    [<Fact>]
    member _.``PF-014-O1``() =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let active = PinnedCatalog.ok "read active catalog" (live.Reader.ReadFile head (RepoPath PinnedCatalog.activePath)) |> json
        let (Workspace ws) = PinnedCatalog.workspace
        Assert.Equal(Some PinnedCatalog.catalogId, active |> Json.field "catalogId" |> Json.text)
        Assert.Equal(Some PinnedCatalog.catalogVersion, active |> Json.field "catalogVersion" |> Json.text)
        Assert.Equal(Some ws, active |> Json.field "workspace" |> Json.text)
        let manifest = PinnedCatalog.ok "read manifest" (live.Reader.ReadFile head (RepoPath PinnedCatalog.manifestPath)) |> json
        let ids = manifest |> Json.field "cases" |> Json.items |> List.choose (Some >> Json.field "investigationId" >> Json.text) |> set
        Assert.Equal<Set<string>>(PinnedCatalog.cases |> List.map (fun c -> c.Id.ToString()) |> set, ids)

    /// PF-014-O2: actual network communication, correct request-to-response association, the four catalog
    /// investigations reconstructed at their expected versions and digests, and the deployed application's behaviour.
    [<Fact>]
    member _.``PF-014-O2``() =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        Assert.Contains(live.Log, fun o -> o.Url.StartsWith(GitHubRequests.ApiBase + "/") && o.Status = Some 200)
        for n in [ 41L; 42L ] do
            match live.Execute(GitHubRequests.readHead (RequestId n) live.ReadConnection) with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> Assert.Equal(RequestId n, id)
        for case in PinnedCatalog.cases do
            let files = PinnedCatalog.ok ("read " + case.Key) (PinnedCatalog.readFiles live.Reader head case)
            Assert.Equal(case.EventsSha256, PinnedCatalog.sha256 files.Events)
        AppLoad.requireCodec ()
        for case in PinnedCatalog.cases do
            let v = AppLoad.load live.Execute live.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified case.Key
            Assert.Equal(Causa.Domain.AggregateVersion(int64 case.ExpectedVersion), v.Investigation.Version)
            Assert.Equal(case.EventsSha256, PinnedCatalog.sha256 v.Bytes.Events)
        let app = env "CAUSA_APP_URL" |> Option.defaultWith (fun () -> raise (InvalidOperationException "BLOCKED: no deployed application: CAUSA_APP_URL is not set"))
        use client = new Net.Http.HttpClient()
        use answer = client.GetAsync(app).GetAwaiter().GetResult()
        Assert.True(answer.IsSuccessStatusCode, sprintf "the deployed application at %s answered %d" app (int answer.StatusCode))

    /// PF-014-O3: the run records repository ref, source SHA, run ID and provider mode in its evidence.
    [<Fact>]
    member _.``PF-014-O3``() =
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created |> ignore
        let evidence = IO.File.ReadAllText(live.WriteEvidence()) |> Json.parse
        let field name = evidence |> Json.field name |> Json.text
        Assert.Equal(Some "live-github", field "providerMode")
        Assert.Equal(Some(live.WriteConnection.Repository.Owner + "/" + live.WriteConnection.Repository.Name), field "repository")
        Assert.Equal(Some live.Branch, field "branch")
        let (CommitOid b) = live.BaseHead
        Assert.Equal(Some b, field "baseHead")
        for (name, variable) in [ "sourceSha", "GITHUB_SHA"; "runId", "GITHUB_RUN_ID" ] do
            match env variable with
            | None -> raise (InvalidOperationException(sprintf "BLOCKED: not a CI run: %s is not set, so the evidence cannot name the %s" variable name))
            | Some v -> Assert.Equal(Some v, field name)

    /// PF-014-O4: live success is never claimed from mocks: a fake run, whatever it calls itself, creates no commit
    /// real GitHub knows, while every commit the live run reports exists on real GitHub.
    [<Fact>]
    member _.``PF-014-O4``() =
        let fakeCommits = ResizeArray<CommitOid>()
        let fake = StatefulFake()
        let fakeProbes = Conformance.run (Conformance.overFake fake prefix) fakeCommits.Add
        Assert.False(({ Mode = "fake"; Probes = fakeProbes; CreatedCommits = List.ofSeq fakeCommits } : Conformance.Report).IsLiveVerification)
        let live = fixture.Run
        let real = Conformance.run (Conformance.overLive live) live.Created
        let report : Conformance.Report = { Mode = "live-github"; Probes = real; CreatedCommits = live.CreatedCommits }
        Assert.True(report.IsLiveVerification)
        for c in live.CreatedCommits do
            Assert.Equal("ok", Conformance.kind (live.Reader.Compare live.BaseHead c 1))
        for c in fakeCommits do
            Assert.NotEqual<string>("ok", Conformance.kind (live.Reader.Compare live.BaseHead c 1))
