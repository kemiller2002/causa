// Owner: persistence. PERSIST-021 (live): reopen from GitHub.
namespace Causa.Acceptance.Live

open Xunit
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

type PERSIST021Tests(fixture: LiveFixture<PERSIST021Tests>) =
    interface IClassFixture<LiveFixture<PERSIST021Tests>>

    /// O1: a new tab loads the latest confirmed commit and verifies it.
    [<Fact>]
    member _.``PERSIST-021-O1``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p021o1")
        let s = saved rig "PERSIST-021-O1" "Reopened in a new tab."
        let v = (freshTab live rig).Load(target rig s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(s.BaseHead, v.ReadAt)
        Assert.Equal("PERSIST-021-O1", v.Investigation.Title.Value)

    /// O2: it shows Saved at that commit.
    [<Fact>]
    member _.``PERSIST-021-O2``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p021o2")
        let s = saved rig "PERSIST-021-O2" "Saved at the commit."
        let tab = freshTab live rig
        let v = tab.Load(target rig s) |> Result.defaultWith (failwithf "%A")
        let session = tab.Machine.Initial tab.Writable v
        Assert.Equal("Saved", stateName session)
        Assert.Equal(v.ReadAt, session.BaseHead)

    /// O3: every confirmed save and nothing unconfirmed.
    [<Fact>]
    member _.``PERSIST-021-O3``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p021o3")
        let s = saved rig "PERSIST-021-O3" "Confirmed only."
        let s2 = step rig (observe rig s "Confirmed second observation") SaveInput.SaveRequested
        let _unsaved = observe rig s2 "Never saved observation"
        let v = (freshTab live rig).Load(target rig s) |> Result.defaultWith (failwithf "%A")
        let texts = v.Investigation.Observations |> Map.toList |> List.map (fun (_, o) -> o.Original.Value)
        Assert.Contains("Confirmed second observation", texts)
        Assert.DoesNotContain("Never saved observation", texts)

    /// O4 (PAT-10): reopening after saves from several tabs.
    [<Fact>]
    member _.``PERSIST-021-O4``() =
        let live = fixture.Run
        let a = LiveSaveRig.Create(live, "p021o4")
        let s = saved a "PERSIST-021-O4" "Several tabs."
        let b = freshTab live a
        let vB = b.Load(target a s) |> Result.defaultWith (failwithf "%A")
        let sB = b.Step (b.Observe (b.Machine.Initial b.Writable vB) "From tab B") SaveInput.SaveRequested
        Assert.Equal("Saved", stateName sB)
        let c = freshTab live a
        let v = c.Load(target a s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(sB.BaseHead, v.ReadAt)
        Assert.True(v.Investigation.Observations |> Map.exists (fun _ o -> o.Original.Value = "From tab B"))
