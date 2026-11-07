// Owner: domain (v4 T12, invariant tests first). This file is the first of the domain's invariant tests in compile
// order, so it also holds the harness they share: fixtures of the frozen IF41-SAVE types, the locator of the
// persistence role's SaveMachine, the INV-01 random driver and oracle, and the reply-delivery loops of INV-02.
// The machines are the persistence role's (T19); while Causa.Persistence exports no SaveMachine value the tests fail
// with a BLOCKED message, never vacuously.
namespace Causa.Invariants.Harness

open System
open System.Reflection
open Causa.Domain
open Causa.Persistence

module Fixtures =
    let investigation = InvestigationId(Guid "9a1f0c2d-3b4e-4f50-8a61-72b3c4d5e6f7")
    let address = WorkspaceAddress.workingCopy [] "t12-invariants" |> Option.get
    let writable = Writable.ofAddress address |> Option.get
    let reference : InvestigationRef = { Address = address; Id = investigation }
    let commit (n: int) = CommitOid(sprintf "%040x" n)
    let t0 = DateTimeOffset(2026, 11, 7, 17, 0, 0, TimeSpan.Zero)

    let files (tag: string) : FileSet =
        { Manifest = Text.Encoding.UTF8.GetBytes("{\"manifest\":\"" + tag + "\"}\n")
          Events = Text.Encoding.UTF8.GetBytes("{\"events\":\"" + tag + "\"}\n")
          Snapshot = Text.Encoding.UTF8.GetBytes("{\"snapshot\":\"" + tag + "\"}\n") }

    let private text s = NonEmptyText.create s |> Option.get

    /// The kernel's state after InvestigationStarted at version 1.
    let startedInvestigation =
        Investigation.started
            investigation
            (AggregateVersion 1L)
            { Actor = Actor.Human(text "t12-fixture"); RecordedAt = t0 }
            { Title = text "Fixture investigation"; ObservationId = Guid "0f0e0d0c-0b0a-4908-8706-050403020100"; Original = text "Observed." }

    let verified (at: CommitOid) : VerifiedInvestigation =
        { Investigation = startedInvestigation
          History = []
          ReadAt = at
          Address = address
          Bytes = files ("verified-" + string at) }

    let confirmed (at: CommitOid) : Confirmation.T =
        Confirmation.ofReadBack investigation at t0 (files "written") (files "written") |> Option.get

    let command (n: int) : CommandEnvelope =
        { CommandId = Guid(n, 0s, 0s, Array.zeroCreate 8)
          Investigation = investigation
          ExpectedVersion = Some(AggregateVersion 1L)
          Command = Core ActivateInvestigation }

    let context (nextId: int64) (now: DateTimeOffset) : RequestContext =
        { Connection =
            { Repository = { Owner = "causa-test-owner"; Name = "causa-test-repo"; Branch = "main" }
              Credential = Credential.create "not-a-real-token" }
          NextId = RequestId nextId
          MutationInFlightElsewhere = false
          Now = now }

    let stateName (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

    let typeName (t: Type | null) =
        match t with
        | null -> "<none>"
        | t ->
            match t.FullName with
            | null -> t.Name
            | name -> name

    let requestOf (outcome: HttpOutcome) =
        match outcome with
        | Answered(id, _, _, _)
        | NotAnswered(id, _) -> id

module Machine =
    let require () : SaveMachine =
        let exported =
            typeof<SaveState>.Assembly.GetExportedTypes()
            |> Seq.collect (fun t -> t.GetProperties(BindingFlags.Public ||| BindingFlags.Static))
            |> Seq.filter (fun p -> p.PropertyType = typeof<SaveMachine> && p.GetIndexParameters().Length = 0)
            |> Seq.sortBy (fun p -> Fixtures.typeName p.DeclaringType + "." + p.Name)
            |> List.ofSeq
        match exported with
        | p :: _ ->
            match p.GetValue null with
            | :? SaveMachine as machine -> machine
            | _ -> failwithf "BLOCKED: %s.%s is null" (Fixtures.typeName p.DeclaringType) p.Name
        | [] ->
            failwith
                "BLOCKED: Causa.Persistence exports no SaveMachine value yet (IF41-SAVE is implemented by the persistence role, T19). Expected a public module value of type Causa.Persistence.SaveMachine."

    let step (machine: SaveMachine) ctx (session: InvestigationSession) input =
        match machine.Step ctx session input with
        | Ok(next, _) -> next
        | Error fault -> failwithf "setup step %A refused in %s: %A" input (Fixtures.stateName session.State) fault

    /// A session in Saving through the machine itself: verified load, one edit, Save.
    let saving (machine: SaveMachine) (baseHead: CommitOid) =
        let s0 = machine.Initial Fixtures.writable (Fixtures.verified baseHead)
        let s1 = step machine (Fixtures.context 10L Fixtures.t0) s0 (SaveInput.Edit(Fixtures.command 1))
        let s2 = step machine (Fixtures.context 11L Fixtures.t0) s1 SaveInput.SaveRequested
        match s2.State, s2.Outstanding with
        | SaveState.Saving, Some _ -> s2
        | state, outstanding -> failwithf "Save did not reach Saving with a request outstanding: %s, %A" (Fixtures.stateName state) outstanding

/// INV-01: an investigation is Saved only after a re-read at the commit returned exactly the written bytes, or a
/// marker reconciliation found a commit holding exactly those bytes. The driver delivers random edits, saves and fake
/// replies (forged commit oids, tampered read-backs, replies in the wrong state); every reply's input is produced by
/// `compare` (production: Confirmation.ofReadBack). The oracle compares the bytes itself, so it does not trust
/// `compare`: a comparator that compares the written bytes with themselves is caught.
module Inv01 =
    open Fixtures

    type Compare = InvestigationId -> CommitOid -> DateTimeOffset -> FileSet -> FileSet -> Confirmation.T option

    let production : Compare = Confirmation.ofReadBack

    /// The mutant of INV01.ReadBackSkipped: the read-back comparison removed (the written bytes compared with themselves).
    let readBackSkipped : Compare = fun inv at time written _ -> Confirmation.ofReadBack inv at time written written

    type ReplyKind =
        | ReadBack
        | Marker

    type Reply = { Kind: ReplyKind; Commit: CommitOid; Written: FileSet; ReadBack: FileSet }

    type Report =
        { Violations: string list
          ConfirmedSaves: int
          MismatchesDelivered: int
          Steps: int }

    let private tamper (rng: Random) (fs: FileSet) =
        let changed = Text.Encoding.UTF8.GetBytes(sprintf "{\"tampered\":%d}\n" (rng.Next()))
        match rng.Next 3 with
        | 0 -> { fs with Manifest = changed }
        | 1 -> { fs with Events = changed }
        | _ -> { fs with Snapshot = changed }

    let private reply (rng: Random) kind =
        let written = files (sprintf "w%d" (rng.Next 1000))
        let readBack = if rng.Next 2 = 0 then written else tamper rng written
        // Forged commit oids: arbitrary, sometimes the base head or a short non-oid.
        let at =
            match rng.Next 4 with
            | 0 -> commit 0xb1
            | 1 -> CommitOid "forged"
            | _ -> commit (rng.Next(0x100, 0xfffff))
        { Kind = kind; Commit = at; Written = written; ReadBack = readBack }

    let private replyInput (compare: Compare) (r: Reply) =
        match r.Kind, compare investigation r.Commit t0 r.Written r.ReadBack with
        | ReadBack, Some c -> SaveInput.ReadBackMatched c
        | ReadBack, None -> SaveInput.ReadBackMismatched r.Commit
        | Marker, Some c -> SaveInput.ReconcileMarkerBytesMatch c
        | Marker, None -> SaveInput.ReconcileMarkerBytesDiffer r.Commit

    let private pick (rng: Random) (xs: 'a list) = xs.[rng.Next xs.Length]

    /// The oracle: may this step have entered (or stayed in) Saved?
    let judge (before: InvestigationSession) (input: SaveInput) (r: Reply option) (after: InvestigationSession) =
        match after.State with
        | SaveState.Saved basis ->
            match before.State, input, basis, r with
            | SaveState.Saved earlier, _, _, _ -> if earlier = basis then [] else [ "Saved to Saved changed its basis" ]
            | (SaveState.Saving | SaveState.UnknownOutcome _),
              (SaveInput.ReadBackMatched c | SaveInput.ReconcileMarkerBytesMatch c),
              Confirmed c',
              Some r ->
                [ if r.Written <> r.ReadBack then
                      sprintf "Saved at %A although the bytes read back differ from the bytes written" r.Commit
                  if c' <> c then "Saved rests on another confirmation than the one delivered"
                  if c.CommitOid <> r.Commit || after.BaseHead <> r.Commit then
                      sprintf "Saved at base %A, not the read-back commit %A" after.BaseHead r.Commit ]
            | SaveState.Conflict(Discarded v), SaveInput.ConflictResolvedDiscarded _, VerifiedAt v', _ ->
                if v'.ReadAt = v.ReadAt && after.BaseHead = v.ReadAt then [] else [ "Saved after discard is not the verified read" ]
            | _ -> [ sprintf "Saved reached from %s by %A without a matching read-back" (stateName before.State) input ]
        | _ -> []

    let private conflictInputs (rng: Random) =
        [ ConflictInput.RemoteVerified(verified (commit (rng.Next(0x100, 0xfff))))
          ConflictInput.ReviewChosen
          ConflictInput.ReapplyChosen
          ConflictInput.ReapplyDone([ command 1 ], [])
          ConflictInput.DiscardChosen
          ConflictInput.DiscardConfirmed
          ConflictInput.DiscardCancelled
          ConflictInput.ReadAgain
          ConflictInput.RemoteReadFailed LostAfterSending ]

    /// Runs `sequences` random sequences of `length` steps and judges every step.
    let check (machine: SaveMachine) (compare: Compare) (sequences: int) (length: int) : Report =
        let mutable violations = []
        let mutable confirmedSaves = 0
        let mutable mismatches = 0
        let mutable steps = 0
        for seed in 1..sequences do
            let rng = Random seed
            let mutable session = machine.Initial writable (verified (commit 0xb1))
            let mutable nextId = 1000L
            for i in 1..length do
                let ctx = context nextId (t0.AddSeconds(float (i * 7)))
                nextId <- nextId + 1L
                // Interpret's NextRequest would have left the session awaiting this read (same state).
                let deliverReply (current: InvestigationSession) readId kind awaiting =
                    let r = reply rng kind
                    let before = { current with Outstanding = Some(RequestId readId, awaiting r.Commit) }
                    Some r, before, replyInput compare r
                let readId = nextId
                nextId <- nextId + 1L
                let r, before, input =
                    match session.State with
                    | SaveState.Saving when rng.Next 10 < 6 -> deliverReply session readId ReadBack ReadBackAt
                    | SaveState.UnknownOutcome _ when rng.Next 10 < 6 -> deliverReply session readId Marker ReconcileFilesAt
                    | SaveState.Saving ->
                        None,
                        session,
                        pick rng
                            [ SaveInput.AnswerLost
                              SaveInput.ProviderRefused(ValidationFailed [ "rejected" ])
                              SaveInput.StaleHeadUnrelated(commit (rng.Next(0x100, 0xfff)))
                              SaveInput.FailedBeforeSending
                              SaveInput.ReadBackReadFailed
                              SaveInput.Edit(command (rng.Next 50)) ]
                    | SaveState.UnknownOutcome _ ->
                        None,
                        session,
                        pick rng
                            [ SaveInput.ReconcileReadFailed
                              SaveInput.ReconcileNothingWrittenHeadUnchanged
                              SaveInput.ReconcileReadsSpent
                              SaveInput.CheckAgain
                              SaveInput.WentOnline ]
                    | SaveState.Conflict(Discarded v) when rng.Next 2 = 0 -> None, session, SaveInput.ConflictResolvedDiscarded v
                    | SaveState.Conflict(Reapplied(_, _)) when rng.Next 2 = 0 -> None, session, SaveInput.ConflictResolvedReapplied [ command 1 ]
                    | SaveState.Conflict view ->
                        // Drive SM-CONFLICT as the engine does, then keep the session's view current.
                        match machine.StepConflict ctx view (pick rng (conflictInputs rng)) with
                        | Ok(view', _) -> session <- { session with State = SaveState.Conflict view' }
                        | Error _ -> ()
                        None, session, SaveInput.Edit(command (rng.Next 50))
                    | _ when rng.Next 10 = 0 ->
                        // A forged reply arriving outside Saving and UnknownOutcome.
                        let r = reply rng (if rng.Next 2 = 0 then ReadBack else Marker)
                        Some r, session, replyInput compare r
                    | _ ->
                        None,
                        session,
                        pick rng
                            [ SaveInput.Edit(command (rng.Next 50))
                              SaveInput.SaveRequested
                              SaveInput.SaveRequested
                              SaveInput.SaveRequested
                              SaveInput.WentOffline
                              SaveInput.WentOnline
                              SaveInput.QueuedSaveStarted ]
                steps <- steps + 1
                match r with
                | Some r when r.Written <> r.ReadBack ->
                    match before.State with
                    | SaveState.Saving
                    | SaveState.UnknownOutcome _ -> mismatches <- mismatches + 1
                    | _ -> ()
                | _ -> ()
                match machine.Step ctx before input with
                | Ok(after, _) ->
                    let found = judge before input r after
                    if not found.IsEmpty then
                        violations <- violations @ (found |> List.map (sprintf "seed %d step %d: %s" seed i))
                    match before.State, after.State, input with
                    | (SaveState.Saving | SaveState.UnknownOutcome _), SaveState.Saved _, (SaveInput.ReadBackMatched _ | SaveInput.ReconcileMarkerBytesMatch _) ->
                        confirmedSaves <- confirmedSaves + 1
                    | _ -> ()
                    session <- after
                | Error _ -> session <- session
        { Violations = violations; ConfirmedSaves = confirmedSaves; MismatchesDelivered = mismatches; Steps = steps }

/// INV-02 delivery: replies are interpreted and stepped as the engine does; a refused step changes nothing.
module Delivery =
    let session (machine: SaveMachine) (start: InvestigationSession) (current: Set<RequestId>) (outcomes: HttpOutcome list) =
        let mutable s = start
        let mutable nextId = 700L
        for o in outcomes do
            let ctx = Fixtures.context nextId Fixtures.t0
            if current.Contains(Fixtures.requestOf o) then nextId <- nextId + 1L
            match machine.Interpret ctx s o with
            | MachineEvent e ->
                match machine.Step ctx s e with
                | Ok(next, _) -> s <- next
                | Error _ -> ()
            | NextRequest(next, _) -> s <- next
        s

    let load (machine: SaveMachine) (start: LoadState) (current: Set<RequestId>) (outcomes: HttpOutcome list) =
        let mutable s = start
        let mutable nextId = 700L
        for o in outcomes do
            let ctx = Fixtures.context nextId Fixtures.t0
            if current.Contains(Fixtures.requestOf o) then nextId <- nextId + 1L
            match machine.InterpretLoad ctx s o with
            | LoadEvent e ->
                match machine.StepLoad ctx s e with
                | Ok(next, _) -> s <- next
                | Error _ -> ()
            | LoadNextRequest(next, _) -> s <- next
        s

    let conflict (machine: SaveMachine) (start: ConflictView) (current: Set<RequestId>) (outcomes: HttpOutcome list) =
        let mutable s = start
        let mutable nextId = 700L
        for o in outcomes do
            let ctx = Fixtures.context nextId Fixtures.t0
            if current.Contains(Fixtures.requestOf o) then nextId <- nextId + 1L
            match machine.InterpretConflict ctx s o with
            | ConflictEvent e ->
                match machine.StepConflict ctx s e with
                | Ok(next, _) -> s <- next
                | Error _ -> ()
            | ConflictNextRequest(next, _) -> s <- next
        s

    /// Random replies to requests that are not current: forged heads, refusals, lost answers.
    let staleReplies (rng: Random) (avoid: Set<RequestId>) (count: int) =
        [ for _ in 1..count do
              let id =
                  Seq.initInfinite (fun _ -> RequestId(int64 (rng.Next(1, 400))))
                  |> Seq.find (fun id -> not (avoid.Contains id))
              match rng.Next 4 with
              | 0 -> NotAnswered(id, (if rng.Next 2 = 0 then LostAfterSending else TimedOut))
              | 1 -> Answered(id, 409, [], "{\"message\":\"stale\"}")
              | 2 -> Answered(id, 500, [], "")
              | _ ->
                  let sha = sprintf "%040x" (rng.Next())
                  Answered(id, 200, [ "content-type", "application/json" ], "{\"sha\":\"" + sha + "\",\"commit\":{\"sha\":\"" + sha + "\"}}") ]

    let shuffleWith (rng: Random) (current: HttpOutcome) (stale: HttpOutcome list) =
        let arr = List.toArray stale
        rng.Shuffle arr
        let at = rng.Next(arr.Length + 1)
        [ yield! arr.[.. at - 1]; current; yield! arr.[at..] ]

namespace Causa.Invariants.Mutation

open System
open Xunit
open Causa.Invariants.Harness

module INV01 =
    /// INV-01 mutation: with the read-back comparison removed (the written bytes compared with themselves) the named
    /// test Causa.Invariants.Property.INV01.NoSavedWithoutMatchingReadBack fails. The production comparator is the
    /// positive control: the same check over the same sequences finds nothing.
    [<Fact>]
    let ReadBackSkipped () =
        let machine = Machine.require ()
        let real = Inv01.check machine Inv01.production 150 40
        let mutant = Inv01.check machine Inv01.readBackSkipped 150 40
        let problems =
            [ if real.MismatchesDelivered = 0 then "no mismatched read-back was delivered in Saving or UnknownOutcome: the mutant cannot be exercised"
              if not real.Violations.IsEmpty then
                  sprintf "positive control: the production comparator already violates INV-01: %s" (String.Join("; ", real.Violations |> List.truncate 5))
              if mutant.Violations.IsEmpty then
                  "the mutant (read-back compared with itself) survived: Causa.Invariants.Property.INV01.NoSavedWithoutMatchingReadBack did not fail" ]
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))
