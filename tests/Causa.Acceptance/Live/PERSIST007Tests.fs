// Owner: persistence. PERSIST-007 (live): a save is confirmed only after GitHub confirms it.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

type PERSIST007Tests(fixture: LiveFixture<PERSIST007Tests>) =
    interface IClassFixture<LiveFixture<PERSIST007Tests>>

    /// O1: confirmed only when GitHub returned the commit and a re-read at it returned the written bytes.
    [<Fact>]
    member _.``PERSIST-007-O1``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p007o1")
        let s = saved rig "PERSIST-007-O1" "Read back before Saved."
        match s.State with
        | SaveState.Saved(Confirmed c) ->
            Assert.Equal(head live, c.CommitOid)
            let requests = rig.Recorder.Requests
            let iMutation = requests |> List.findIndex GitHubRequests.isMutation
            Assert.True(requests |> List.skip (iMutation + 1) |> List.exists (fun r -> (defaultArg r.Body "").Contains(let (CommitOid o) = c.CommitOid in o)))
        | other -> failwithf "%A" other

    /// O2: only a confirmed save moves to Saved and advances the base head.
    [<Fact>]
    member _.``PERSIST-007-O2``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p007o2")
        let s0 = create rig "PERSIST-007-O2" "Base advances only on confirmation."
        match stepRaw rig s0 SaveInput.SaveRequested with
        | Ok(saving, effects) ->
            Assert.Equal("Saving", stateName saving)
            Assert.Equal(s0.BaseHead, saving.BaseHead)
            let s = settle rig saving effects
            Assert.Equal("Saved", stateName s)
            Assert.Equal(head live, s.BaseHead)
        | Error f -> failwithf "%A" f

    /// O3: changes made while the save was in flight stay unsaved.
    [<Fact>]
    member _.``PERSIST-007-O3``() =
        let rig = LiveSaveRig.Create(fixture.Run, "p007o3")
        let s0 = create rig "PERSIST-007-O3" "In-flight edits stay unsaved."
        match stepRaw rig s0 SaveInput.SaveRequested with
        | Ok(saving, effects) ->
            let later = observe rig saving "Made during the live save."
            let s = settle rig later effects
            Assert.Equal("Unsaved", stateName s)
            Assert.Equal(1, s.Unsaved.Length)
        | Error f -> failwithf "%A" f

    /// O4 (PAT-02/10): Saved appears only after the read-back; a fresh tab sees exactly the confirmed commit.
    [<Fact>]
    member _.``PERSIST-007-O4``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p007o4")
        let s = saved rig "PERSIST-007-O4" "Saved after read-back."
        let v = (freshTab live rig).Load(target rig s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(s.BaseHead, v.ReadAt)

    /// A file over 1 MB is read back by its Git blob id (GH-LARGE-FILE): the save still confirms.
    [<Fact>]
    member _.ReadBackOfFileOver1MB() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p007big")
        let mutable s = create rig "PERSIST-007 large file" "Events over one megabyte."
        let paragraph = String.replicate 950 "Observed vibration rises with load. "
        for i in 1..34 do
            s <- observe rig s (sprintf "%03d %s" i paragraph)
        let s' = step rig s SaveInput.SaveRequested
        Assert.True((stateName s' = "Saved"), sprintf "%A" s'.State)
        let (RepoPath p) = Writes.sessionPrefix s'
        let request = GitHubRequests.readFile (RequestId 1L) live.ReadConnection s'.BaseHead (RepoPath(p + "/events.ndjson"))
        match live.Reader.ReadFile s'.BaseHead (RepoPath(p + "/events.ndjson")) with
        | Ok bytes -> Assert.True(bytes.Length > 1_000_000, sprintf "%d bytes" bytes.Length)
        | Error e -> failwithf "%A (%s)" e request.Url
