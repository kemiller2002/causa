// Owner: persistence. PERSIST-007 (deterministic, stateful fake): a save is confirmed only after GitHub confirms it.
// Also the shared scenario helpers of the persistence acceptance tests.
namespace Causa.Acceptance.Deterministic

open System
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module SaveScenarios =
    let stateName (s: InvestigationSession) = SaveSession.stateName s.State

    let rig (run: string) = SaveRig(StatefulFake(), run)

    /// A new investigation saved once (Saved), with the rig resting its session on what GitHub holds.
    let savedInvestigation (r: SaveRig) =
        let s = r.Step (r.Create "Pump trips at noon" "The cooling pump trips at noon every day.") SaveInput.SaveRequested
        if stateName s <> "Saved" then failwithf "the first save did not confirm: %A" s.State
        r.Rebase s |> ignore
        s

    let filesAtHead (r: SaveRig) (s: InvestigationSession) =
        let (RepoPath p) = Writes.sessionPrefix s
        let head = r.Fake.Head()
        [ "manifest.json"; "events.ndjson"; "snapshot.json" ] |> List.map (fun f -> r.Fake.FileAt(head, RepoPath(p + "/" + f)))

    let eventsText (r: SaveRig) (s: InvestigationSession) =
        match filesAtHead r s with
        | [ _; Some e; _ ] -> Encoding.UTF8.GetString e
        | _ -> ""

    /// A filesAt answer whose bytes differ from what the save wrote (a forged read-back).
    let forgedReadBack (id: RequestId) (commit: CommitOid) =
        let (CommitOid c) = commit
        Answered(id, 200, [], sprintf """{"data":{"repository":{"c":{"oid":"%s","committedDate":"2026-11-07T18:00:01Z"},"m":{"text":"{}\n","isTruncated":false},"e":{"text":"{}\n","isTruncated":false},"s":{"text":"{}\n","isTruncated":false}}}}""" c)

module PERSIST007Tests =
    open SaveScenarios

    /// O1: confirmed only when GitHub returned the new commit and a re-read at it returns the written bytes; a read-back
    /// of other bytes is Save failed, never Saved.
    [<Fact>]
    let ``PERSIST-007-O1`` () =
        let r = rig "persist007-o1"
        let s0 = r.Create "Title" "Problem statement"
        match r.StepRaw s0 SaveInput.SaveRequested with
        | Ok(saving, [ Send mutation; _ ]) ->
            let ctx = { r.Context() with NextId = RequestId 5000L }
            match r.Machine.Interpret ctx saving (r.Fake.Execute mutation) with
            | NextRequest(awaitingReadBack, readBack) ->
                let commit = match awaitingReadBack.Outstanding with Some(_, ReadBackAt c) -> c | other -> failwithf "%A" other
                Assert.Equal(commit, r.Fake.Head())
                match r.Machine.Interpret ctx awaitingReadBack (forgedReadBack readBack.Id commit) with
                | MachineEvent(SaveInput.ReadBackMismatched c) ->
                    let failed, _ = r.Machine.Step ctx awaitingReadBack (SaveInput.ReadBackMismatched c) |> Result.defaultWith (failwithf "%A")
                    Assert.Equal(SaveState.SaveFailed(BytesDiffer commit), failed.State)
                | other -> failwithf "a forged read-back was %A" other
                // the true read-back confirms
                match r.Machine.Interpret ctx awaitingReadBack (r.Fake.Execute readBack) with
                | MachineEvent(SaveInput.ReadBackMatched c) -> Assert.Equal(commit, c.CommitOid)
                | other -> failwithf "%A" other
            | other -> failwithf "%A" other
        | other -> failwithf "%A" other

    /// O2: only a confirmed save moves to Saved and advances the base head.
    [<Fact>]
    let ``PERSIST-007-O2`` () =
        let r = rig "persist007-o2"
        let s0 = r.Create "Title" "Problem statement"
        let head0 = r.Fake.Head()
        match r.StepRaw s0 SaveInput.SaveRequested with
        | Ok(saving, _) ->
            Assert.Equal("Saving", stateName saving)
            Assert.Equal(head0, saving.BaseHead)
        | Error f -> failwithf "%A" f
        let saved = r.Step s0 SaveInput.SaveRequested
        Assert.Equal("Saved", stateName saved)
        Assert.Equal(r.Fake.Head(), saved.BaseHead)
        Assert.NotEqual(head0, saved.BaseHead)

    /// O3: a change made while the save was in flight stays unsaved after the confirmation.
    [<Fact>]
    let ``PERSIST-007-O3`` () =
        let r = rig "persist007-o3"
        let s0 = r.Create "Title" "Problem statement"
        match r.StepRaw s0 SaveInput.SaveRequested with
        | Ok(saving, effects) ->
            let later = r.Command s0.Id (RecordObservation { ObservationId = Guid.NewGuid(); Original = NonEmptyText.create "Made during the save" |> Option.get; ObservedAt = None; Source = None })
            let edited = r.Step saving (SaveInput.Edit later)
            let settled = r.Settle edited effects
            Assert.Equal("Unsaved", stateName settled)
            Assert.Equal<CommandEnvelope list>([ later ], settled.Unsaved)
            Assert.DoesNotContain("Made during the save", eventsText r settled)
        | Error f -> failwithf "%A" f

    /// O4 (PAT-02/10/12): Saved appears only after the read-back; it is never reached otherwise.
    [<Fact>]
    let ``PERSIST-007-O4`` () =
        let r = rig "persist007-o4"
        let s0 = r.Create "Title" "Problem statement"
        let received () = r.Fake.State.Received
        let saved = r.Step s0 SaveInput.SaveRequested
        Assert.Equal("Saved", stateName saved)
        // the last request before Saved is the read-back GraphQL query, after the mutation
        let kinds = received () |> List.map fst
        Assert.Equal<string list>([ "Mutation"; "Post" ], kinds |> List.filter (fun m -> m <> "Get"))
        match saved.State with
        | SaveState.Saved(Confirmed c) -> Assert.Equal(r.Fake.Head(), c.CommitOid)
        | other -> failwithf "%A" other
