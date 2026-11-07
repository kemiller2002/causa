// Owner: domain (v4 T12, invariant tests first). INV-02 negative cases (Rehearsal 7 third review E1 and E2).
namespace Causa.Invariants.Negative

open System
open Xunit
open Causa.Persistence
open Causa.Invariants.Harness
open Causa.Invariants.Harness.Fixtures

module INV02 =
    let private fail (problems: string list) =
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))

    let private stepConflict (machine: SaveMachine) ctx view input =
        match machine.StepConflict ctx view input with
        | Ok(v, _) -> v
        | Error fault -> failwithf "SM-CONFLICT refused %A in %A: %A" input view fault

    /// E1: a late read of head h1 after a re-read at h2 is dropped, and the other writer's change (h2) survives reapply.
    [<Fact>]
    let LateConflictReReadKeepsOtherWriter () =
        let machine = Machine.require ()
        let h1, h2 = commit 0x1a1, commit 0x1a2
        let firstRead = stepConflict machine (context 10L t0) RemoteUnavailable ConflictInput.ReadAgain
        let r1 = match firstRead with ReadingRemote id -> id | other -> failwithf "ReadAgain reached %A" other
        let atH1 = stepConflict machine (context 11L t0) firstRead (ConflictInput.RemoteVerified(verified h1))
        let reRead = stepConflict machine (context 20L t0) atH1 ConflictInput.ReadAgain
        let r2 = match reRead with ReadingRemote id -> id | other -> failwithf "ReadAgain reached %A" other
        // The late answer to r1 still carries head h1.
        let late = Answered(r1, 200, [ "content-type", "application/json" ], "{\"sha\":\"" + (match h1 with CommitOid sha -> sha) + "\"}")
        let afterLate =
            match machine.InterpretConflict (context 21L t0) reRead late with
            | ConflictEvent(ConflictInput.SupersededReply id) when id = r1 ->
                match machine.StepConflict (context 21L t0) reRead (ConflictInput.SupersededReply id) with
                | Ok(v, _) -> v
                | Error _ -> reRead
            | other -> failwithf "the late read of h1 was interpreted as %A, not SupersededReply" other
        let atH2 = stepConflict machine (context 22L t0) afterLate (ConflictInput.RemoteVerified(verified h2))
        let reapplying = stepConflict machine (context 23L t0) atH2 ConflictInput.ReapplyChosen
        let reapplied = stepConflict machine (context 24L t0) reapplying (ConflictInput.ReapplyDone([ command 1 ], []))
        // SM-SAVE leaves Conflict with the reapplied commands on the verified GitHub version (T-S37).
        let inConflict = { Machine.saving machine (commit 0xb1) with State = SaveState.Conflict reapplied; Outstanding = None }
        let resolved = Machine.step machine (context 25L t0) inConflict (SaveInput.ConflictResolvedReapplied [ command 1 ])
        let lateAgain =
            match machine.Interpret (context 26L t0) resolved late with
            | MachineEvent e ->
                match machine.Step (context 26L t0) resolved e with
                | Ok(s, _) -> s
                | Error _ -> resolved
            | NextRequest(s, _) -> s
        fail
            [ if r1 = r2 then "the re-read reused the first read's request identity"
              if afterLate <> reRead then sprintf "the late read of h1 changed the conflict view: %A" afterLate
              match reapplied with
              | Reapplied(remote, _) when remote.ReadAt = h2 -> ()
              | other -> sprintf "reapply did not rest on h2: %A" other
              if resolved.State <> SaveState.Unsaved then sprintf "reapply resolved to %s, expected Unsaved" (stateName resolved.State)
              if resolved.BaseHead <> h2 then sprintf "the base head after reapply is %A: the other writer's change at h2 was lost" resolved.BaseHead
              if not (List.contains (command 1) resolved.Unsaved) then "the reapplied command is not an unsaved change"
              if lateAgain <> resolved then "the late read of h1 changed the session after reapply" ]

    /// E2: a refresh requested at base b1 that completes after a save moved the tab to b2 is dropped.
    [<Fact>]
    let StaleRefreshDropped () =
        let machine = Machine.require ()
        let b1, b2 = commit 0x2b1, commit 0x2b2
        let refreshing =
            match machine.StepLoad (context 30L t0) (Loaded(verified b1)) LoadInput.RefreshRequested with
            | Ok(s, _) -> s
            | Error fault -> failwithf "RefreshRequested refused in Loaded: %A" fault
        let refresh = match refreshing with Loading(id, _) -> id | other -> failwithf "refresh reached %A" other
        // The save, started at b1, is confirmed at b2 by an equal read-back.
        let saving = Machine.saving machine b1
        let awaitingReadBack = { saving with Outstanding = Some(RequestId 31L, ReadBackAt b2) }
        let saved = Machine.step machine (context 32L t0) awaitingReadBack (SaveInput.ReadBackMatched(confirmed b2))
        // The refresh answers late, with head b1.
        let late = Answered(refresh, 200, [ "content-type", "application/json" ], "{\"sha\":\"" + (match b1 with CommitOid sha -> sha) + "\"}")
        let sessionAfter =
            match machine.Interpret (context 33L t0) saved late with
            | MachineEvent(SaveInput.SupersededReply id) when id = refresh ->
                match machine.Step (context 33L t0) saved (SaveInput.SupersededReply id) with
                | Ok(s, _) -> s
                | Error _ -> saved
            | other -> failwithf "the late refresh was interpreted by the session as %A, not SupersededReply" other
        let loadedAtB2 = Loaded(verified b2)
        let loadAfter =
            match machine.InterpretLoad (context 34L t0) loadedAtB2 late with
            | LoadEvent(LoadInput.SupersededReply id) when id = refresh ->
                match machine.StepLoad (context 34L t0) loadedAtB2 (LoadInput.SupersededReply id) with
                | Ok(s, _) -> s
                | Error fault -> failwithf "T-L10: Loaded refused a superseded reply: %A" fault
            | other -> failwithf "the late refresh was interpreted by the load as %A, not SupersededReply" other
        fail
            [ match saved.State with
              | SaveState.Saved(Confirmed c) when c.CommitOid = b2 && saved.BaseHead = b2 -> ()
              | other -> sprintf "the save did not reach Saved at b2: %s" (stateName other)
              if sessionAfter <> saved then sprintf "the late refresh changed the session: %s at %A" (stateName sessionAfter.State) sessionAfter.BaseHead
              if loadAfter <> loadedAtB2 then sprintf "the late refresh changed the loaded version: %A" loadAfter ]
