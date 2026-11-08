// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-019 local-first workflow: smallest deterministic checks first, the complete live suite before release, and no
// required test is ever skipped.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF019Tests(fixture: LiveFixture<PF019Tests>) =
    interface IClassFixture<LiveFixture<PF019Tests>>

    /// PF-019-O1: the deterministic suites come first and the live suite depends on them: in the plan SU-DOTNET-LIVE
    /// depends on the deterministic and invariant suites and they depend on nothing live; the gate batches the live
    /// suites after them; and the complete live suite runs on real GitHub.
    [<Fact>]
    member _.``PF-019-O1``() =
        let suites = Plan.suites () |> List.map (fun s -> s.Id, s) |> Map.ofList
        let liveSuite = suites["SU-DOTNET-LIVE"]
        Assert.Contains("SU-DOTNET-DETERMINISTIC", liveSuite.DependsOn)
        Assert.Contains("SU-DOTNET-INVARIANTS", liveSuite.DependsOn)
        for id in [ "SU-OWN-DOTNET"; "SU-DOTNET-DETERMINISTIC"; "SU-DOTNET-INVARIANTS" ] do
            Assert.DoesNotContain("SU-DOTNET-LIVE", suites[id].DependsOn)
            Assert.DoesNotContain(".Live.", suites[id].Command.Replace("!~Causa.Invariants.Live.", ""))
        let gate = Repo.read ".github/workflows/verification-gate.yml"
        Assert.Contains("suites=\"$suites,SU-DOTNET-LIVE", gate)
        Assert.Equal("ok", Conformance.kind (fixture.Run.Reader.ReadHead()))

    /// PF-019-O2: no test required by an impacted risk or dependency is skipped: no acceptance test carries a Skip, the
    /// CI fails a run that skips any test, and every live test of this slice is bound in the plan to the live suite.
    [<Fact>]
    member _.``PF-019-O2``() =
        let skipped = Plan.identities typeof<PF019Tests>.Assembly |> List.choose (fun (id, m) -> Plan.skipOf m |> Option.map (fun r -> id, r))
        Assert.True(List.isEmpty skipped, sprintf "skipped tests: %A" skipped)
        Assert.Contains("Skipped:", Repo.read ".github/workflows/ci.yml")
        let bound = Plan.tests () |> List.filter (fun t -> t.Id.StartsWith "dotnet:Causa.Acceptance.Live.PF")
        Assert.NotEmpty(bound)
        Assert.All(bound, fun t -> Assert.Equal("SU-DOTNET-LIVE", t.Suite))
        Assert.Equal("ok", Conformance.kind (fixture.Run.Reader.ReadHead()))
