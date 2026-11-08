// Owner: persistence. PERSIST-013 (live): never silently overwrite another commit.
namespace Causa.Acceptance.Live

open Xunit
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

type PERSIST013Tests(fixture: LiveFixture<PERSIST013Tests>) =
    /// Saves, a rebase, a lost answer reconciled, and a conflict reapplied; returns every commit made.
    let scenario (live: LiveRun) =
        let a = LiveSaveRig.Create(live, "p013a")
        let b = LiveSaveRig.Create(live, "p013b")
        let sA = saved a "PERSIST-013 investigation A" "First save."
        let sB = saved b "PERSIST-013 investigation B" "Unrelated save (A will rebase)."
        let sA2 = step a (observe a sA "A rebases over B.") SaveInput.SaveRequested
        a.Recorder.LoseNextMutationAnswer()
        let sA3 = step a (observe a sA2 "A's answer is lost.") SaveInput.SaveRequested
        let sA4 = a.Due sA3
        a, sA4

    interface IClassFixture<LiveFixture<PERSIST013Tests>>

    /// O1: no write removes, replaces or hides another writer's commit.
    [<Fact>]
    member _.``PERSIST-013-O1``() =
        let live = fixture.Run
        let a, s = scenario live
        let _, commits = compareFiles live live.BaseHead (head live)
        for c in live.CreatedCommits do
            Assert.Contains(c, commits)
        Assert.Equal("Saved", stateName s)

    /// O2: no forced write, ref reset or delete-and-recreate; every mutation names a head the tab read.
    [<Fact>]
    member _.``PERSIST-013-O2``() =
        let live = fixture.Run
        let a, _ = scenario live
        let read = a.Recorder.HeadsRead |> Set.ofList
        for m in a.Recorder.Mutations do
            let body = defaultArg m.Body ""
            let expected = Json.parse body |> Json.path [ "variables"; "input"; "expectedHeadOid" ] |> Json.text |> Option.get
            Assert.True(read.Contains expected, sprintf "expectedHeadOid %s was never read" expected)
            Assert.Empty(Json.parse body |> Json.path [ "variables"; "input"; "fileChanges"; "deletions" ] |> Json.items)
        Assert.True(a.Recorder.Requests |> List.forall (fun r -> r.Method = Get || r.Url = GitHubRequests.GraphQlUrl))

    /// O3: every other writer's commit stays reachable after saves, rebases, reconciliation and conflict resolution.
    [<Fact>]
    member _.``PERSIST-013-O3``() =
        let live = fixture.Run
        scenario live |> ignore
        // a conflict on the same investigation, resolved by reapply, then saved
        let x = LiveSaveRig.Create(live, "p013x")
        let sX = saved x "PERSIST-013 conflict" "Two tabs."
        let y = ProviderRig(live.Execute, live.WriteConnection, (Writable.address x.Writable).Workspace |> fun (Workspace w) -> w.Substring("workspaces/demo/causa-app-".Length))
        let vY = y.Load(target x sX) |> Result.defaultWith (failwithf "%A")
        let sY = y.Step (y.Observe (y.Machine.Initial y.Writable vY) "Tab Y") SaveInput.SaveRequested
        Assert.Equal("Saved", stateName sY)
        let conflicted = step x (observe x sX "Tab X") SaveInput.SaveRequested
        match conflicted.State with
        | SaveState.Conflict(Reviewable remote) ->
            let accepted, refused = Reapply.redecide x.Actor remote conflicted.Unsaved
            let view = Reapplied(remote, refused)
            let s' = step x { conflicted with State = SaveState.Conflict view } (SaveInput.ConflictResolvedReapplied accepted)
            x.Rebase s' |> ignore
            Assert.Equal("Saved", stateName (step x s' SaveInput.SaveRequested))
        | other -> failwithf "%A" other
        let _, commits = compareFiles live live.BaseHead (head live)
        for c in live.CreatedCommits do
            Assert.Contains(c, commits)
        Assert.Contains(sY.BaseHead, commits)

    /// O4 (PAT-02/04/05): after each scenario every writer's commit is reachable from the head.
    [<Fact>]
    member _.``PERSIST-013-O4``() =
        let live = fixture.Run
        scenario live |> ignore
        let _, commits = compareFiles live live.BaseHead (head live)
        Assert.NotEmpty(live.CreatedCommits)
        Assert.True(live.CreatedCommits |> List.forall (fun c -> List.contains c commits))
