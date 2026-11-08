// Owner: persistence. PERSIST-006 (live): one atomic commit per save, with an expected head.
namespace Causa.Acceptance.Live

open Xunit
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

type PERSIST006Tests(fixture: LiveFixture<PERSIST006Tests>) =
    interface IClassFixture<LiveFixture<PERSIST006Tests>>

    /// O1: every save publishes exactly one commit through one createCommitOnBranch.
    [<Fact>]
    member _.``PERSIST-006-O1``() =
        let rig = LiveSaveRig.Create(fixture.Run, "p006o1")
        saved rig "PERSIST-006-O1" "One commit." |> ignore
        Assert.Equal(1, rig.Recorder.Mutations.Length)
        Assert.Contains("createCommitOnBranch", defaultArg rig.Recorder.Mutations.Head.Body "")

    /// O2: the commit holds the events append, manifest and snapshot together.
    [<Fact>]
    member _.``PERSIST-006-O2``() =
        let live = fixture.Run
        let before = head live
        let rig = LiveSaveRig.Create(live, "p006o2")
        let s = saved rig "PERSIST-006-O2" "Three files in one commit."
        let files, commits = compareFiles live before s.BaseHead
        let (RepoPath p) = Writes.sessionPrefix s
        Assert.Equal<string list>(Writes.fileNames |> List.map (fun f -> p + "/" + f) |> List.sort, files |> List.sort)
        Assert.Equal<CommitOid list>([ s.BaseHead ], commits)

    /// O3: expectedHeadOid is the head the change was decided at.
    [<Fact>]
    member _.``PERSIST-006-O3``() =
        let live = fixture.Run
        let decidedAt = head live
        let rig = LiveSaveRig.Create(live, "p006o3")
        saved rig "PERSIST-006-O3" "Expected head." |> ignore
        let (CommitOid h) = decidedAt
        Assert.Contains("\"expectedHeadOid\":\"" + h + "\"", defaultArg rig.Recorder.Mutations.Head.Body "")

    /// O4: no Contents API write, no forced ref update, no write without an expected head.
    [<Fact>]
    member _.``PERSIST-006-O4``() =
        let rig = LiveSaveRig.Create(fixture.Run, "p006o4")
        saved rig "PERSIST-006-O4" "No other write." |> ignore
        for r in rig.Recorder.Requests do
            Assert.True(r.Method = Get || r.Url = GitHubRequests.GraphQlUrl, sprintf "%A %s" r.Method r.Url)
            if GitHubRequests.isMutation r then Assert.Contains("expectedHeadOid", defaultArg r.Body "")

    /// O5: the commit changes files only under the investigation's prefix (checked before sending, then observed).
    [<Fact>]
    member _.``PERSIST-006-O5``() =
        let live = fixture.Run
        let before = head live
        let rig = LiveSaveRig.Create(live, "p006o5")
        let s = saved rig "PERSIST-006-O5" "Only under the prefix."
        let (RepoPath p) = Writes.sessionPrefix s
        let files, _ = compareFiles live before s.BaseHead
        Assert.True(files |> List.forall (fun f -> f.StartsWith(p + "/")))
        Assert.True(Result.isError (GitHubRequests.commit (RequestId 1L) live.WriteConnection before (RepoPath p) "x" "" [ PutFile(RepoPath "README.md", [| 1uy |]) ]))

    /// O6: at most one mutation in flight per tab; a further request waits in memory.
    [<Fact>]
    member _.``PERSIST-006-O6``() =
        let rig = LiveSaveRig.Create(fixture.Run, "p006o6")
        let s = create rig "PERSIST-006-O6" "Waits while another save is in flight."
        let busy = { rig.Context() with MutationInFlightElsewhere = true }
        match rig.Machine.Step busy s SaveInput.SaveRequested with
        | Ok(s', effects) ->
            Assert.Equal("Unsaved", stateName s')
            Assert.Empty(effects |> List.filter (function Send _ -> true | _ -> false))
        | Error f -> failwithf "%A" f
        Assert.Empty(rig.Recorder.Mutations)
        Assert.Equal("Saved", stateName (step rig s SaveInput.SaveRequested))

    /// O7 (PAT-02): every mutation sent is one conditional createCommitOnBranch under the prefix.
    [<Fact>]
    member _.``PERSIST-006-O7``() =
        let rig = LiveSaveRig.Create(fixture.Run, "p006o7")
        let s = saved rig "PERSIST-006-O7" "Every mutation verified."
        rig.Step (observe rig s "second") SaveInput.SaveRequested |> ignore
        let (RepoPath p) = Writes.sessionPrefix s
        Assert.Equal(2, rig.Recorder.Mutations.Length)
        for m in rig.Recorder.Mutations do
            let body = defaultArg m.Body ""
            Assert.Contains("expectedHeadOid", body)
            Assert.Contains(p + "/events.ndjson", body)
            Assert.Contains("causa-write-marker", body)
