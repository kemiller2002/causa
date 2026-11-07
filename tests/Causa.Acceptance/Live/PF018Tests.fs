// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-018 traceability: every fixture and test maps to a stable requirement and source commit; P0 obligations are
// preserved.
namespace Causa.Acceptance.Live

open System
open System.Text.RegularExpressions
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF018Tests(fixture: LiveFixture<PF018Tests>) =
    /// The requirements of SL-T18.
    let sliceRequirements =
        set ([ "AR-010"; "AR-014"; "PERSIST-008"; "PERSIST-025" ] @ [ for n in [ 1 .. 9 ] @ [ 11 .. 20 ] -> sprintf "PF-%03d" n ])

    let obligationName = Regex(@"^(?<req>[A-Z]+(?:-[A-Z]+)?-\d{3})-O\d+$")

    interface IClassFixture<LiveFixture<PF018Tests>>

    /// PF-018-O1: each fixture and test maps to a stable requirement and the authoritative source commit: every
    /// acceptance test is named for one plan obligation of its class's requirement, every recorded answer names where it
    /// was observed, and the live run's evidence names the commit its fixtures were read at, whose catalog bytes match
    /// the pinned digests.
    [<Fact>]
    member _.``PF-018-O1``() =
        let obligations = Plan.obligations () |> List.map (fun o -> o.Id) |> set
        for (id, m) in Plan.identities typeof<PF018Tests>.Assembly do
            let name = obligationName.Match m.Name
            Assert.True(name.Success, sprintf "%s is not named for an obligation" id)
            Assert.Contains(m.Name, obligations)
            Assert.Equal(name.Groups["req"].Value.Replace("-", "") + "Tests", (m.DeclaringType |> Option.ofObj |> Option.map (fun t -> t.Name) |> Option.defaultValue ""))
        Assert.All(Recordings.all, fun r -> Assert.False(String.IsNullOrWhiteSpace r.ObservedIn, r.Form))
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let case = PinnedCatalog.cases.Head
        Assert.Equal(case.EventsSha256, PinnedCatalog.ok "read case" (PinnedCatalog.readFiles live.Reader live.BaseHead case) |> fun f -> PinnedCatalog.sha256 f.Events)
        let (CommitOid b) = live.BaseHead
        Assert.Contains(sprintf "\"baseHead\":\"%s\"" b, IO.File.ReadAllText(live.WriteEvidence()))
        Assert.Equal(live.BaseHead, head)

    /// PF-018-O2: obligations are preserved: every P0 obligation of this slice is in the plan with its criterion, is
    /// bound to at least one test, and every bound acceptance test exists in this assembly; a missing or weakened one
    /// fails here. The live suite that produces their evidence then runs on real GitHub.
    [<Fact>]
    member _.``PF-018-O2``() =
        let p0 = Plan.obligations () |> List.filter (fun o -> sliceRequirements.Contains o.Requirement && o.Priority = "P0")
        Assert.NotEmpty(p0)
        let bound = Plan.tests ()
        let present = Plan.identities typeof<PF018Tests>.Assembly |> List.map fst |> set
        let missing =
            [ for o in p0 do
                if String.IsNullOrWhiteSpace o.Criterion then yield o.Id, "no criterion"
                let tests = bound |> List.filter (fun t -> List.contains o.Id t.Obligations)
                if List.isEmpty tests then yield o.Id, "no bound test"
                for t in tests do
                    if t.Id.StartsWith "dotnet:Causa.Acceptance." && not (present.Contains t.Id) then yield o.Id, "missing " + t.Id ]
        Assert.True(List.isEmpty missing, sprintf "P0 obligations not preserved: %A" missing)
        Assert.Equal("ok", Conformance.kind (fixture.Run.Reader.ReadHead()))
