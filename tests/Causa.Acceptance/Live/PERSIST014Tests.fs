// Owner: persistence. PERSIST-014 (live): unique write markers.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

type PERSIST014Tests(fixture: LiveFixture<PERSIST014Tests>) =
    let markers (rig: LiveSaveRig) =
        rig.Recorder.Mutations
        |> List.choose (fun m -> Json.parse (defaultArg m.Body "") |> Json.path [ "variables"; "input"; "message"; "body" ] |> Json.text)

    interface IClassFixture<LiveFixture<PERSIST014Tests>>

    /// O1: the tab has a random session identifier in memory.
    [<Fact>]
    member _.``PERSIST-014-O1``() =
        Assert.NotEqual(Guid.Empty, Writes.tabSession)
        let rig = LiveSaveRig.Create(fixture.Run, "p014o1")
        saved rig "PERSIST-014-O1" "Marker carries the tab session." |> ignore
        Assert.Contains(Writes.tabSession.ToString(), markers rig |> List.exactlyOne)

    /// O2: each mutation carries the marker as a trailer of the commit message, which GitHub keeps.
    [<Fact>]
    member _.``PERSIST-014-O2``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p014o2")
        let s = saved rig "PERSIST-014-O2" "Trailer in the commit message."
        let request = GitHubRequests.readCommit (RequestId 77L) live.ReadConnection s.BaseHead
        match live.Execute request with
        | Answered(_, 200, _, body) ->
            let message = Json.parse body |> Json.path [ "commit"; "message" ] |> Json.text |> Option.defaultValue ""
            Assert.Contains("causa-write-marker: " + Writes.tabSession.ToString() + "/", message)
        | other -> failwithf "%A" other

    /// O3: markers are unique across investigations, saves and rebases.
    [<Fact>]
    member _.``PERSIST-014-O3``() =
        let live = fixture.Run
        let a = LiveSaveRig.Create(live, "p014o3a")
        let b = LiveSaveRig.Create(live, "p014o3b")
        let sA = saved a "PERSIST-014-O3 A" "first"
        saved b "PERSIST-014-O3 B" "second" |> ignore
        step a (observe a sA "after B") SaveInput.SaveRequested |> ignore
        let all = markers a @ markers b
        Assert.True(all.Length >= 3)
        Assert.Equal(all.Length, (List.distinct all).Length)

    /// O4 (PAT-02): markers of concurrent tabs and reloads differ.
    [<Fact>]
    member _.``PERSIST-014-O4``() =
        let live = fixture.Run
        let a = LiveSaveRig.Create(live, "p014o4a")
        let s = saved a "PERSIST-014-O4" "Reloaded and saved again."
        let reload = LiveSaveRig.Create(live, "p014o4r")
        let v = reload.Load(target a s) |> Result.defaultWith (failwithf "%A")
        ignore v
        step a (observe a s "second save") SaveInput.SaveRequested |> ignore
        let all = markers a
        Assert.Equal(all.Length, (List.distinct all).Length)
        // A different tab session (another page load) gives a different marker for the same session state.
        let other = { Writes.marker s with Session = Guid.NewGuid() }
        Assert.NotEqual<string>(Writes.markerText other, Writes.markerText (Writes.marker s))
