// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-001 provider-neutral boundary: one typed port over the stateful fake and real GitHub.
namespace Causa.Acceptance.Live

open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF001Tests(fixture: LiveFixture<PF001Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf001/investigations/00000000-0000-0000-0000-000000000001"
    let fakeProbes () = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
    let liveProbes () =
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created

    interface IClassFixture<LiveFixture<PF001Tests>>

    /// PF-001-O1: one typed F# port (ProviderPort) is shared by the fixture adapter (stateful fake) and the GitHub
    /// adapter (real network): both are built by Provider.over and answer with the repository they are connected to.
    [<Fact>]
    member _.``PF-001-O1``() =
        let fake = StatefulFake()
        let overFake : ProviderPort = Provider.over fake.Execute fake.ReadConnection
        Assert.Equal("ok", Conformance.kind (overFake.ReadHead()))
        Assert.Equal(fake.Repository, overFake.Repository)
        let live = fixture.Run
        let overGitHub : ProviderPort = live.Reader
        Assert.Equal("ok", Conformance.kind (overGitHub.ReadHead()))
        Assert.Equal(live.ReadConnection.Repository, overGitHub.Repository)

    /// PF-001-O2: the same result, error and provenance semantics whichever provider answers: the probe transcript
    /// over the fake equals the one over real GitHub, probe by probe.
    [<Fact>]
    member _.``PF-001-O2``() =
        let fake = fakeProbes ()
        Assert.Empty(Conformance.failures fake)
        let real = liveProbes ()
        Assert.Empty(Conformance.failures real)
        Assert.Empty(Conformance.divergence fake real)

    /// PF-001-O3: provider selection alters neither meaning nor authorization: what the read credential, the write
    /// credential and a refused credential may do is the same on both providers.
    [<Fact>]
    member _.``PF-001-O3``() =
        let authz (probes: Conformance.Probe list) =
            probes
            |> List.filter (fun p -> p.Name.StartsWith "commit" || p.Name.Contains "credential")
            |> List.map (fun p -> p.Name, p.Actual)
        let onFake = authz (fakeProbes ())
        Assert.Contains(("commit with the read credential", "PermissionDenied"), onFake)
        Assert.Contains(("read head with a bad credential", "AuthenticationFailed"), onFake)
        Assert.Equal<(string * string) list>(onFake, authz (liveProbes ()))
