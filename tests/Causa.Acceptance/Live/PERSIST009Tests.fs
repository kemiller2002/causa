// Owner: persistence. PERSIST-009 (live): rebase automatically over unrelated changes.
namespace Causa.Acceptance.Live

open Xunit
open Causa.Persistence
open Causa.TestSupport
open LiveScenarios

module RebaseScenario =
    /// Tab A decides a change at the head; another investigation is then saved (an unrelated commit); A saves.
    let run (live: LiveRun) (name: string) =
        let a = LiveSaveRig.Create(live, name + "a")
        let other = LiveSaveRig.Create(live, name + "b")
        let s0 = create a "PERSIST-009 rebasing investigation" "Saved over an unrelated commit."
        let unrelated = saved other "PERSIST-009 unrelated investigation" "Moves the branch."
        let s = step a s0 SaveInput.SaveRequested
        a, s, unrelated.BaseHead, s0

type PERSIST009Tests(fixture: LiveFixture<PERSIST009Tests>) =
    interface IClassFixture<LiveFixture<PERSIST009Tests>>

    /// O1: after the stale head, the new head is read and the investigation's files compared.
    [<Fact>]
    member _.``PERSIST-009-O1``() =
        let a, _, unrelatedCommit, _ = RebaseScenario.run fixture.Run "p009o1"
        Assert.Contains(unrelatedCommit, a.Recorder.HeadsRead |> List.map CommitOid)
        Assert.Contains(a.Recorder.Requests, fun r -> r.Url.Contains "/compare/")

    /// O2: unchanged files mean an unrelated move.
    [<Fact>]
    member _.``PERSIST-009-O2``() =
        let _, s, _, _ = RebaseScenario.run fixture.Run "p009o2"
        Assert.Equal("Saved", stateName s)
        Assert.Equal(1, s.RebaseAttempts)

    /// O3: the same events, manifest and snapshot are resent with the new head as expectedHeadOid, without asking.
    [<Fact>]
    member _.``PERSIST-009-O3``() =
        let a, s, unrelatedCommit, s0 = RebaseScenario.run fixture.Run "p009o3"
        match a.Recorder.Mutations with
        | [ first; second ] ->
            let changes (r: HttpRequest) = Json.parse (defaultArg r.Body "") |> Json.path [ "variables"; "input"; "fileChanges" ] |> Option.map Canonical.serialize
            Assert.Equal(changes first, changes second)
            let (CommitOid u) = unrelatedCommit
            let (CommitOid b) = s0.BaseHead
            Assert.Contains("\"expectedHeadOid\":\"" + b + "\"", defaultArg first.Body "")
            Assert.Contains("\"expectedHeadOid\":\"" + u + "\"", defaultArg second.Body "")
        | ms -> failwithf "%d mutations" ms.Length
        Assert.Equal("Saved", stateName s)

    /// O4: while it rebases, the investigation stays Saving.
    [<Fact>]
    member _.``PERSIST-009-O4``() =
        let live = fixture.Run
        let a = LiveSaveRig.Create(live, "p009o4a")
        let other = LiveSaveRig.Create(live, "p009o4b")
        let s0 = create a "PERSIST-009-O4" "Stays Saving."
        saved other "PERSIST-009-O4 unrelated" "Moves the branch." |> ignore
        match stepRaw a s0 SaveInput.SaveRequested with
        | Ok(saving, [ Send mutation; _ ]) ->
            match a.Machine.Interpret (a.Context()) saving (a.Recorder.Execute mutation) with
            | NextRequest(rebasing, _) -> Assert.Equal("Saving", stateName rebasing)
            | other -> failwithf "%A" other
        | other -> failwithf "%A" other

    /// O5 (PAT-04): saves without user action; the unrelated commit stays on the branch.
    [<Fact>]
    member _.``PERSIST-009-O5``() =
        let live = fixture.Run
        let _, s, unrelatedCommit, _ = RebaseScenario.run live "p009o5"
        Assert.Equal("Saved", stateName s)
        let _, commits = compareFiles live live.BaseHead s.BaseHead
        Assert.Contains(unrelatedCommit, commits)
