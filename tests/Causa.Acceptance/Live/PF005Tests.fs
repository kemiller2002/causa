// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-005 provider isolation: UI and domain checks never need GitHub; real-provider checks never fall back to fixtures.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF005Tests(fixture: LiveFixture<PF005Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf005/investigations/00000000-0000-0000-0000-000000000005"

    /// The UI and domain test projects (fixture and deterministic levels).
    let offlineTestDirs = [ "tests/Causa.Domain.Tests", "*.fs"; "tests/Causa.App.Tests", "*.fs" ]

    /// What a test would use to reach GitHub, the network or a credential.
    let liveMarkers = [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN"; "LiveEnv"; "LiveFixture"; "LiveRun"; "LiveHttp"; "api.github.com"; "HttpClient" ]

    let withoutCredentials (f: unit -> 'a) : 'a =
        let saved = [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] |> List.map (fun v -> v, Environment.GetEnvironmentVariable v)
        try
            for (v, _) in saved do
                Environment.SetEnvironmentVariable(v, null)
            f ()
        finally
            for (v, value) in saved do
                Environment.SetEnvironmentVariable(v, value)

    interface IClassFixture<LiveFixture<PF005Tests>>

    /// PF-005-O1: UI and domain tests do not depend on GitHub, Actions, the network or credentials: the domain assembly
    /// references neither persistence nor HTTP, no domain or App test source names a live marker, no browser UI spec
    /// requires a credential or reaches GitHub unanswered, and the
    /// provider-conformance fixture passes with both secrets unset.
    [<Fact>]
    member _.``PF-005-O1``() =
        let domainRefs = typeof<Causa.Domain.Investigation>.Assembly.GetReferencedAssemblies() |> Array.map (fun a -> string a.Name)
        for forbidden in [ "Causa.Persistence"; "System.Net.Http"; "Causa.TestSupport" ] do
            Assert.DoesNotContain(forbidden, domainRefs)
        for (dir, pattern) in offlineTestDirs do
            let full = Repo.path dir
            if IO.Directory.Exists full then
                for file in IO.Directory.EnumerateFiles(full, pattern, IO.SearchOption.AllDirectories) do
                    if not (file.Contains "/obj/" || file.Contains "/bin/") then
                        let text = IO.File.ReadAllText file
                        for marker in liveMarkers do
                            Assert.False(text.Contains marker, sprintf "%s depends on %s" file marker)
        let browser = OfflineSpec.scan "tests/browser/acceptance"
        Assert.True(List.isEmpty browser, sprintf "UI tests depend on live GitHub or a credential: %A" browser)
        withoutCredentials (fun () -> Assert.Empty(Conformance.failures (Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore)))

    /// PF-005-O2: a real-provider test never falls back to fixtures: without its secret it is BLOCKED (never a fake
    /// pass), and with it every answer comes from api.github.com over the network and the run's evidence says
    /// live-github.
    [<Fact>]
    member _.``PF-005-O2``() =
        withoutCredentials (fun () ->
            let e = Assert.Throws<InvalidOperationException>(fun () -> new LiveRun("PF005Probe") |> ignore)
            Assert.StartsWith("BLOCKED: no credential", e.Message))
        let live = fixture.Run
        Assert.Equal("live-github", (Conformance.overLive live).Mode)
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
        Assert.NotEmpty(live.Log)
        Assert.All(live.Log, fun o -> Assert.StartsWith(GitHubRequests.ApiBase + "/", o.Url))
        Assert.Contains(live.Log, fun o -> o.Status.IsSome)
        let evidence = IO.File.ReadAllText(live.WriteEvidence())
        Assert.Contains("\"providerMode\":\"live-github\"", evidence)
