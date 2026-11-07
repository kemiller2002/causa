// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-003 explicit provider selection: a local fixture mode and the real GitHub mode, never confused.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type PF003Tests(fixture: LiveFixture<PF003Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf003/investigations/00000000-0000-0000-0000-000000000003"
    let docs = [ "README.md"; "DEPLOYMENT.md" ]

    let withoutCredentials (f: unit -> 'a) : 'a =
        let saved = [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] |> List.map (fun v -> v, Environment.GetEnvironmentVariable v)
        try
            for (v, _) in saved do
                Environment.SetEnvironmentVariable(v, null)
            f ()
        finally
            for (v, value) in saved do
                Environment.SetEnvironmentVariable(v, value)

    interface IClassFixture<LiveFixture<PF003Tests>>

    /// PF-003-O1: an explicit fixture mode and an explicit GitHub mode, each with a documented, repeatable command:
    /// the two adapters name their mode, the GitHub one talks to api.github.com, and the run documentation states the
    /// command for each mode (CAUSA_PROVIDER=fixture and CAUSA_PROVIDER=github, the selection the governed gate sets).
    [<Fact>]
    member _.``PF-003-O1``() =
        Assert.Equal("fake", (Conformance.overFake (StatefulFake()) prefix).Mode)
        let documented = docs |> List.filter Repo.exists |> List.map Repo.read |> String.concat "\n"
        for mode in [ "CAUSA_PROVIDER=fixture"; "CAUSA_PROVIDER=github" ] do
            Assert.True(documented.Contains mode, sprintf "no documented command selects %s in %A" mode docs)
        let live = fixture.Run
        Assert.Equal("live-github", (Conformance.overLive live).Mode)
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
        Assert.Contains(live.Log, fun o -> o.Url.StartsWith(GitHubRequests.ApiBase + "/") && o.Status.IsSome)

    /// PF-003-O2: fixture mode needs no network, credential, database or external service: with both secrets unset the
    /// whole provider-conformance script runs over the fake, answered in memory.
    [<Fact>]
    member _.``PF-003-O2``() =
        withoutCredentials (fun () ->
            Assert.True(LiveEnv.connection LiveEnv.Read |> Result.isError)
            let fake = StatefulFake()
            let probes = Conformance.run (Conformance.overFake fake prefix) ignore
            Assert.Empty(Conformance.failures probes)
            Assert.NotEmpty(fake.State.Received))

    /// PF-003-O3: fixture mode cannot silently activate in a production deployment: the shipped assemblies (the
    /// application and its host) do not reference the fixture adapters at all, and the production executor reaches
    /// real GitHub.
    [<Fact>]
    member _.``PF-003-O3``() =
        let fixtures = typeof<StatefulFake>.Assembly.GetName().Name
        for shipped in [ typeof<Model>.Assembly; typeof<ProviderPort>.Assembly ] do
            let refs = shipped.GetReferencedAssemblies() |> Array.map (fun a -> a.Name)
            Assert.DoesNotContain(fixtures, refs)
        let live = fixture.Run
        live.Reader.ReadHead() |> PinnedCatalog.ok "production read" |> ignore
        Assert.All(live.Log, fun o -> Assert.True(LiveHttp.allowedHost o.Url || o.Status.IsNone, o.Url))
        Assert.Contains(live.Log, fun o -> o.Status = Some 200)

    /// PF-003-O4: the data mode is conspicuous and fixture data never passes as live: every fixture value carries a
    /// "FIXTURE:" label and no version, while a value read from real GitHub carries the commit it was read at and no
    /// fixture label.
    [<Fact>]
    member _.``PF-003-O4``() =
        let label = FixtureLabel.create "populated investigation"
        Assert.StartsWith("FIXTURE: ", FixtureLabel.value label)
        let fixtureValue = Visible.fixture label "Burned-out ceiling lightbulb"
        Assert.Equal(Some label, Visible.fixtureLabel fixtureValue)
        let understood = Understood.fixture label 3
        Assert.Equal(None, Understood.version understood)
        Assert.Equal(Some label, Understood.fixtureLabel understood)
        let live = fixture.Run
        let (CommitOid head) = live.Reader.ReadHead() |> PinnedCatalog.ok "live read"
        let stored = Visible.stored { Path = PinnedCatalog.activePath; Commit = Some head } "catalog"
        Assert.Equal(None, Visible.fixtureLabel stored)
