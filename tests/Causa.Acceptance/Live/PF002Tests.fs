// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-002 one domain authority: the fixture adapter supplies bytes only; the application's one load path validates and
// reconstructs fixture and real-provider data alike.
namespace Causa.Acceptance.Live

open System
open System.Text.RegularExpressions
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF002Tests(fixture: LiveFixture<PF002Tests>) =
    /// The fixture adapters: the stateful fake and the recorded answers, and the UI fixture backend.
    let adapterSources =
        [ "tests/Causa.TestSupport/StatefulFake.fs"; "tests/Causa.TestSupport/Recordings.fs"; "tests/Causa.TestSupport/UiFixtures/FixtureBackend.fs" ]

    /// Domain authority a fixture adapter must not reimplement: the kernel's decide/evolve/replay/project, policy,
    /// lifecycle transitions, causal inference and confidence.
    let parallelDomain =
        Regex(@"\b(Decide|Evolve|Replay|Project|PolicySet|DomainKernel|Lifecycle|Confidence|confidence|infer\w*)\b")

    let liveCase () =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let case = PinnedCatalog.cases |> List.find (fun c -> c.Key = "burned-out-lightbulb")
        live, case, PinnedCatalog.ok ("read " + case.Key) (PinnedCatalog.readFiles live.Reader head case)

    interface IClassFixture<LiveFixture<PF002Tests>>

    /// PF-002-O1: the fixture adapter supplies contract inputs only. Its source names no domain policy, transition,
    /// reconstruction, inference or confidence, and over a case read from real GitHub it hands the application exactly
    /// the stored bytes, so the application, not the fixture, decides what they mean.
    [<Fact>]
    member _.``PF-002-O1``() =
        for source in adapterSources do
            let text = Repo.read source
            let code = text.Split('\n') |> Array.filter (fun l -> not (l.TrimStart().StartsWith "//")) |> String.concat "\n"
            let hits = parallelDomain.Matches code |> Seq.map (fun m -> m.Value) |> Seq.distinct |> List.ofSeq
            Assert.True(List.isEmpty hits, sprintf "%s implements domain authority: %A" source hits)
        let _, case, files = liveCase ()
        let fake = StatefulFake()
        let at = PinnedCatalog.seed fake case files
        let fromFake = PinnedCatalog.ok "fake read" (PinnedCatalog.readFiles (Provider.over fake.Execute fake.ReadConnection) at case)
        Assert.Equal<byte[]>(files.Manifest, fromFake.Manifest)
        Assert.Equal<byte[]>(files.Events, fromFake.Events)
        Assert.Equal<byte[]>(files.Snapshot, fromFake.Snapshot)

    /// PF-002-O2: the F# engine validates and reconstructs both fixture and real-provider data: the same case opened
    /// through SaveMachines.machine from the fake and from real GitHub verifies to the same investigation at the
    /// catalog's expected version.
    [<Fact>]
    member _.``PF-002-O2``() =
        AppLoad.requireCodec ()
        let live, case, files = liveCase ()
        let fake = StatefulFake()
        PinnedCatalog.seed fake case files |> ignore
        let fromFixture = AppLoad.load fake.Execute fake.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified "fixture load"
        let fromGitHub = AppLoad.load live.Execute live.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified "live load"
        Assert.Equal(fromGitHub.Investigation, fromFixture.Investigation)
        Assert.Equal(Causa.Domain.AggregateVersion(int64 case.ExpectedVersion), fromGitHub.Investigation.Version)
