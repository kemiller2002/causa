// Owner: persistence. SM-SAVE (state-machine-contracts.json) and the assembled IF41-SAVE machine.
namespace Causa.Persistence

open System
open Causa.Domain

[<RequireQualifiedAccess>]
module SaveSession =

    /// PERSIST-010, PERSIST-016 bounds (github-error-matrix.json `bounds`).
    [<Literal>]
    let MutationsPerSave = 4

    [<Literal>]
    let AutomaticRebases = 3

    [<Literal>]
    let AutomaticReconciliationReads = 3

    [<Literal>]
    let ReadsPerMinute = 4

    let stateName =
        function
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

    let inputName =
        function
        | SaveInput.Edit _ -> "Edit"
        | SaveInput.SaveRequested -> "SaveRequested"
        | SaveInput.MutationSent _ -> "MutationSent"
        | SaveInput.ReadBackMatched _ -> "ReadBackMatched"
        | SaveInput.ReadBackMismatched _ -> "ReadBackMismatched"
        | SaveInput.StaleHeadUnrelated _ -> "StaleHeadUnrelated"
        | SaveInput.StaleHeadSameInvestigation _ -> "StaleHeadSameInvestigation"
        | SaveInput.ProviderRefused _ -> "ProviderRefused"
        | SaveInput.CredentialRefused -> "CredentialRefused"
        | SaveInput.AnswerLost -> "AnswerLost"
        | SaveInput.FailedBeforeSending -> "FailedBeforeSending"
        | SaveInput.ReconcileMarkerBytesMatch _ -> "ReconcileMarkerBytesMatch"
        | SaveInput.ReconcileMarkerBytesDiffer _ -> "ReconcileMarkerBytesDiffer"
        | SaveInput.ReconcileNothingWrittenHeadUnchanged -> "ReconcileNothingWrittenHeadUnchanged"
        | SaveInput.ReconcileNothingWrittenHeadMoved _ -> "ReconcileNothingWrittenHeadMoved"
        | SaveInput.ReconcileHistoryRewritten -> "ReconcileHistoryRewritten"
        | SaveInput.ReconcileReadFailed -> "ReconcileReadFailed"
        | SaveInput.ReconcileReadsSpent -> "ReconcileReadsSpent"
        | SaveInput.CheckAgain -> "CheckAgain"
        | SaveInput.WentOffline -> "WentOffline"
        | SaveInput.WentOnline -> "WentOnline"
        | SaveInput.ConflictResolvedReapplied _ -> "ConflictResolvedReapplied"
        | SaveInput.ConflictResolvedDiscarded _ -> "ConflictResolvedDiscarded"
        | SaveInput.SupersededReply _ -> "SupersededReply"
        | SaveInput.QueuedSaveStarted -> "QueuedSaveStarted"
        | SaveInput.RebaseReadFailed -> "RebaseReadFailed"
        | SaveInput.ReadBackReadFailed -> "ReadBackReadFailed"

    let private fault (s: InvestigationSession) (input: SaveInput) (reason: string) : SaveFault =
        { Machine = "SM-SAVE"; State = stateName s.State; Input = inputName input; Reason = reason }

    /// Every unsaved change, including those made during the save in flight, kept visible (never dropped).
    let private allChanges (s: InvestigationSession) = s.Unsaved @ s.AfterInFlight

    let private settle (s: InvestigationSession) (state: SaveState) =
        { s with State = state; Unsaved = allChanges s; AfterInFlight = []; Outstanding = None }

    let private ref' (s: InvestigationSession) = Writes.investigationRef s

    let private schedule (s: InvestigationSession) (ms: int) = ScheduleReconcileRead(ref' s, ms)

    /// Reads still automatic: 3 minus those made (PERSIST-016).
    let private readsLeft (s: InvestigationSession) = max 0 (AutomaticReconciliationReads - s.ReconcileReads)

    let private readsSpent (s: InvestigationSession) =
        readsLeft s = 0
        || (match s.State with
            | SaveState.UnknownOutcome 0 -> true
            | _ -> false)

    /// Send the session's one conditional commit at `expected` (T-S04, T-S14, T-S24, T-S26, T-S35, T-S54).
    let private send (encode: SessionEncoder) (ctx: RequestContext) (s: InvestigationSession) (expected: CommitOid) (input: SaveInput) =
        let s1 = { s with Mutations = s.Mutations + 1; BaseHead = expected }
        match Writes.commitRequest ctx encode s1 expected with
        | Error reason -> Error(fault s input ("nothing sent: " + reason))
        | Ok request ->
            Ok(
                { s1 with State = SaveState.Saving; Outstanding = Some(ctx.NextId, MutationAnswer) },
                [ Send request; Announce(Polite, "Saving to GitHub.") ]
            )

    /// A save request starts with fresh bounds; every unsaved change is part of it.
    let private startSave encode ctx (s: InvestigationSession) input =
        send encode ctx { s with Unsaved = allChanges s; AfterInFlight = []; Mutations = 0; RebaseAttempts = 0; ReconcileReads = 0 } s.BaseHead input

    /// One reconciliation read now, recorded in the 60 s window (T-S31, T-S32, ReconcileDue).
    let private reconcileRead (ctx: RequestContext) (s: InvestigationSession) =
        { s with Outstanding = Some(ctx.NextId, ReconcileHead); ReconcileReadsSentAt = ctx.Now :: s.ReconcileReadsSentAt },
        [ Send(GitHubRequests.readHead ctx.NextId ctx.Connection) ]

    let private windowFull (ctx: RequestContext) (s: InvestigationSession) =
        List.length (Writes.readsInWindow ctx.Now s.ReconcileReadsSentAt) >= ReadsPerMinute

    /// The confirmed save: Saved when nothing changed during it, else Unsaved with the later changes (T-S11/12, T-S21/22).
    let private confirmed (s: InvestigationSession) (c: Confirmation.T) =
        let s1 = { s with BaseHead = c.CommitOid; Outstanding = None; Unsaved = s.AfterInFlight; AfterInFlight = [] }
        if List.isEmpty s.AfterInFlight then
            { s1 with State = SaveState.Saved(Confirmed c) }, [ Announce(Polite, "Saved to GitHub.") ]
        else
            { s1 with State = SaveState.Unsaved }, [ Announce(Polite, "Saved to GitHub; changes made during the save are not saved yet.") ]

    let private startConflict (ctx: RequestContext) (s: InvestigationSession) =
        settle s (SaveState.Conflict(ReadingRemote ctx.NextId)),
        [ Send(ConflictMachine.remoteRead ctx (Some(Writes.investigationRef s))); Announce(Assertive, "This investigation changed on GitHub; your changes are kept. Review the GitHub version.") ]

    let private failed s failure text = settle s (SaveState.SaveFailed failure), [ Announce(Assertive, text) ]

    let step (encode: SessionEncoder) (ctx: RequestContext) (s: InvestigationSession) (input: SaveInput) : Result<InvestigationSession * SaveEffect list, SaveFault> =
        let ok (s', effects) = Ok(s', effects)
        let illegal () = Error(fault s input "illegal (state, event) pair: fails closed, nothing changes")
        let notCurrent () = Error(fault s input "the answer is not current: nothing changes")
        let current = s.Outstanding.IsSome
        let isOutstanding id = match s.Outstanding with Some(o, _) -> o = id | None -> false
        let readBackCurrent = match s.Outstanding with Some(_, ReadBackAt _) -> true | _ -> false
        let keepKeptChanges = "Your changes are kept in this tab."
        match s.State, input with
        // ---- Saved
        | SaveState.Saved _, SaveInput.Edit c -> ok ({ s with State = SaveState.Unsaved; Unsaved = s.Unsaved @ [ c ] }, [])
        | SaveState.Saved _, (SaveInput.WentOffline | SaveInput.WentOnline) -> ok (s, [])
        // ---- Unsaved
        | SaveState.Unsaved, SaveInput.Edit c -> ok ({ s with Unsaved = s.Unsaved @ [ c ] }, [])
        | SaveState.Unsaved, SaveInput.SaveRequested ->
            if ctx.MutationInFlightElsewhere then
                ok (s, [ Announce(Polite, "Waiting for another save in this tab to finish; nothing is sent yet.") ])
            else startSave encode ctx s input
        | SaveState.Unsaved, SaveInput.WentOffline -> ok ({ s with State = SaveState.Offline }, [ Announce(Assertive, "Offline: your changes exist only in this tab.") ])
        | SaveState.Unsaved, SaveInput.WentOnline -> ok (s, [])
        | SaveState.Unsaved, SaveInput.QueuedSaveStarted -> startSave encode ctx s input
        // ---- Saving
        | SaveState.Saving, SaveInput.Edit c -> ok ({ s with AfterInFlight = s.AfterInFlight @ [ c ] }, [])
        | SaveState.Saving, SaveInput.MutationSent id ->
            match s.Outstanding with
            | Some(o, MutationAnswer) when o = id -> ok (s, [])
            | _ -> notCurrent ()
        | SaveState.Saving, SaveInput.ReadBackMatched c when readBackCurrent && c.InvestigationId = s.Id -> ok (confirmed s c)
        | SaveState.Saving, SaveInput.ReadBackMismatched oid when readBackCurrent ->
            ok (failed s (BytesDiffer oid) ("GitHub holds different bytes than this save wrote; nothing is reported as saved. " + keepKeptChanges))
        | SaveState.Saving, SaveInput.StaleHeadUnrelated head when current ->
            if s.RebaseAttempts < AutomaticRebases && s.Mutations < MutationsPerSave then
                send encode ctx { s with RebaseAttempts = s.RebaseAttempts + 1 } head input
            else ok (failed s RepositoryKeptChanging ("The repository kept changing; the save stopped. " + keepKeptChanges))
        | SaveState.Saving, SaveInput.StaleHeadSameInvestigation _ when current -> ok (startConflict ctx s)
        | SaveState.Saving, SaveInput.ProviderRefused e when current -> ok (failed s (Refused e) (Classify.message e))
        | SaveState.Saving, SaveInput.CredentialRefused when current -> ok (failed s CredentialNotAccepted (Classify.message AuthenticationFailed))
        | SaveState.Saving, SaveInput.AnswerLost when current ->
            ok ({ s with State = SaveState.UnknownOutcome(readsLeft s); Outstanding = None },
                [ schedule s 2000; Announce(Polite, "The save may or may not have reached GitHub; checking.") ])
        | SaveState.Saving, SaveInput.FailedBeforeSending when current ->
            ok (settle s SaveState.Offline, [ Announce(Assertive, "Offline: your changes exist only in this tab and cannot be saved until the connection returns.") ])
        | SaveState.Saving, (SaveInput.WentOffline | SaveInput.WentOnline) -> ok (s, [])
        | SaveState.Saving, SaveInput.SupersededReply id when not (isOutstanding id) -> ok (s, [])
        | SaveState.Saving, SaveInput.RebaseReadFailed when current ->
            ok ({ s with State = SaveState.UnknownOutcome(readsLeft s); Outstanding = None },
                [ schedule s 2000; Announce(Polite, "GitHub could not be read after a stale head; checking whether the save was written.") ])
        | SaveState.Saving, SaveInput.ReadBackReadFailed when current ->
            ok ({ s with State = SaveState.UnknownOutcome(readsLeft s); Outstanding = None },
                [ schedule s 2000; Announce(Polite, "The save may or may not have reached GitHub; checking.") ])
        | SaveState.Saving,
          (SaveInput.ReadBackMatched _ | SaveInput.ReadBackMismatched _ | SaveInput.StaleHeadUnrelated _ | SaveInput.StaleHeadSameInvestigation _
          | SaveInput.ProviderRefused _ | SaveInput.CredentialRefused | SaveInput.AnswerLost | SaveInput.FailedBeforeSending
          | SaveInput.SupersededReply _ | SaveInput.RebaseReadFailed | SaveInput.ReadBackReadFailed) -> notCurrent ()
        // ---- Offline
        | SaveState.Offline, SaveInput.Edit c -> ok ({ s with Unsaved = s.Unsaved @ [ c ] }, [])
        | SaveState.Offline, SaveInput.SaveRequested -> ok (s, [ Announce(Assertive, "Offline: nothing can be saved until the connection returns.") ])
        | SaveState.Offline, SaveInput.WentOffline -> ok (s, [])
        | SaveState.Offline, SaveInput.WentOnline -> ok ({ s with State = SaveState.Unsaved }, [ Announce(Polite, "Back online; Save is offered again.") ])
        | SaveState.Offline, SaveInput.QueuedSaveStarted -> ok (s, [])
        // ---- Conflict
        | SaveState.Conflict _, SaveInput.Edit c -> ok ({ s with Unsaved = s.Unsaved @ [ c ] }, [])
        | SaveState.Conflict _, SaveInput.WentOffline -> ok (s, [])
        | SaveState.Conflict RemoteUnavailable, SaveInput.WentOnline ->
            match ConflictMachine.step ctx RemoteUnavailable ConflictInput.ReadAgain with
            | Ok(view, effects) -> ok ({ s with State = SaveState.Conflict view }, effects)
            | Error _ -> ok (s, [])
        | SaveState.Conflict _, SaveInput.WentOnline -> ok (s, [])
        | SaveState.Conflict(Reapplied(remote, _)), SaveInput.ConflictResolvedReapplied accepted ->
            ok ({ s with State = SaveState.Unsaved; BaseHead = remote.ReadAt; Unsaved = accepted; AfterInFlight = []; Outstanding = None }, [])
        | SaveState.Conflict(Discarded remote), SaveInput.ConflictResolvedDiscarded loaded when loaded.ReadAt = remote.ReadAt ->
            ok ({ s with State = SaveState.Saved(VerifiedAt loaded); BaseHead = loaded.ReadAt; Unsaved = []; AfterInFlight = []; Outstanding = None },
                [ Announce(Polite, "The GitHub version is loaded.") ])
        // ---- UnknownOutcome
        | SaveState.UnknownOutcome _, SaveInput.Edit c -> ok ({ s with AfterInFlight = s.AfterInFlight @ [ c ] }, [])
        | SaveState.UnknownOutcome _, SaveInput.StaleHeadSameInvestigation _ when current -> ok (startConflict ctx s)
        | SaveState.UnknownOutcome _, SaveInput.ReconcileMarkerBytesMatch c when current && c.InvestigationId = s.Id -> ok (confirmed s c)
        | SaveState.UnknownOutcome _, SaveInput.ReconcileMarkerBytesDiffer oid when current ->
            ok (failed s (BytesDiffer oid) ("GitHub holds different bytes than this save wrote; nothing is reported as saved. " + keepKeptChanges))
        | SaveState.UnknownOutcome _, SaveInput.ReconcileNothingWrittenHeadUnchanged when current ->
            if s.Mutations < MutationsPerSave then send encode ctx s s.BaseHead input
            else ok (failed s RepositoryKeptChanging ("The save limit was reached; nothing more is sent. " + keepKeptChanges))
        | SaveState.UnknownOutcome _, SaveInput.ReconcileNothingWrittenHeadMoved head when current ->
            if s.RebaseAttempts < AutomaticRebases && s.Mutations < MutationsPerSave then
                send encode ctx { s with RebaseAttempts = s.RebaseAttempts + 1 } head input
            else ok (failed s RepositoryKeptChanging ("The repository kept changing; the save stopped. " + keepKeptChanges))
        | SaveState.UnknownOutcome _, SaveInput.ReconcileHistoryRewritten when current ->
            ok (failed s HistoryWasRewritten (Classify.message (HistoryRewritten s.BaseHead)))
        | SaveState.UnknownOutcome _, SaveInput.ReconcileReadFailed ->
            if s.ReconcileReads < AutomaticReconciliationReads then
                let delay = if s.ReconcileReads <= 1 then 4000 else 8000
                ok ({ s with State = SaveState.UnknownOutcome(readsLeft s); Outstanding = None }, [ schedule s delay ])
            else
                ok ({ s with State = SaveState.UnknownOutcome 0; Outstanding = None }, [ Announce(Polite, "GitHub could not be read; Check again is offered.") ])
        | SaveState.UnknownOutcome _, SaveInput.ReconcileReadsSpent ->
            ok ({ s with State = SaveState.UnknownOutcome 0; Outstanding = None }, [ Announce(Polite, "The save may or may not have reached GitHub; Check again is offered.") ])
        | SaveState.UnknownOutcome _, (SaveInput.CheckAgain | SaveInput.WentOnline) when readsSpent s ->
            if windowFull ctx s then
                let oldest = Writes.readsInWindow ctx.Now s.ReconcileReadsSentAt |> List.min
                let text = sprintf "Check again can read GitHub again at %s." (oldest.AddSeconds(60.0).ToString "u")
                ok (s, (if input = SaveInput.CheckAgain then [ Announce(Polite, text) ] else []))
            else ok (reconcileRead ctx s)
        | SaveState.UnknownOutcome _, SaveInput.WentOnline -> ok (s, [ schedule s 0 ])
        | SaveState.UnknownOutcome _, SaveInput.WentOffline -> ok (s, [])
        | SaveState.UnknownOutcome _, SaveInput.SupersededReply id when not (isOutstanding id) -> ok (s, [])
        | SaveState.UnknownOutcome _,
          (SaveInput.StaleHeadSameInvestigation _ | SaveInput.ReconcileMarkerBytesMatch _ | SaveInput.ReconcileMarkerBytesDiffer _
          | SaveInput.ReconcileNothingWrittenHeadUnchanged | SaveInput.ReconcileNothingWrittenHeadMoved _ | SaveInput.ReconcileHistoryRewritten
          | SaveInput.SupersededReply _ | SaveInput.CheckAgain) -> notCurrent ()
        // ---- SaveFailed
        | SaveState.SaveFailed _, SaveInput.Edit c -> ok ({ s with Unsaved = s.Unsaved @ [ c ] }, [])
        | SaveState.SaveFailed CredentialNotAccepted, SaveInput.SaveRequested ->
            Error(fault s input "GitHub refused the credential: Reconnect before saving again")
        | SaveState.SaveFailed _, SaveInput.SaveRequested -> startSave encode ctx s input
        | SaveState.SaveFailed _, (SaveInput.WentOffline | SaveInput.WentOnline) -> ok (s, [])
        | _ -> illegal ()

    /// ScheduleReconcileRead fired (T-S29, T-S65, INV-04).
    let reconcileDue (ctx: RequestContext) (s: InvestigationSession) : Result<InvestigationSession * SaveEffect list, SaveFault> =
        match s.State with
        | SaveState.UnknownOutcome _ when s.Outstanding.IsSome -> Ok(s, [])
        | SaveState.UnknownOutcome _ when readsLeft s > 0 ->
            if windowFull ctx s then
                let oldest = Writes.readsInWindow ctx.Now s.ReconcileReadsSentAt |> List.min
                let wait = int (Math.Ceiling((oldest.AddSeconds 60.0 - ctx.Now).TotalMilliseconds))
                Ok(s, [ schedule s (max 0 wait) ])
            else
                let s1, effects = reconcileRead ctx { s with ReconcileReads = s.ReconcileReads + 1 }
                Ok({ s1 with State = SaveState.UnknownOutcome(readsLeft s1) }, effects)
        | SaveState.UnknownOutcome _ -> step Writes.provisionalEncoder ctx s SaveInput.ReconcileReadsSpent
        | _ ->
            Error { Machine = "SM-SAVE"; State = stateName s.State; Input = "ReconcileDue"; Reason = "no reconciliation is due outside Unknown commit outcome" }

    /// What one answer means for the session (IF41-ERRORS classification, read-back, reconciliation).
    let interpret (encode: SessionEncoder) (ctx: RequestContext) (s: InvestigationSession) (outcome: HttpOutcome) : Interpretation =
        let answered =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id
        let next (awaiting: Awaiting) (request: HttpRequest) = NextRequest({ s with Outstanding = Some(ctx.NextId, awaiting) }, request)
        let prefix = Writes.sessionPrefix s
        let compareRequest head page = GitHubRequests.compare ctx.NextId ctx.Connection s.BaseHead head page
        let written () = encode s |> Result.toOption
        let filesVerdict (commit: CommitOid) (answer: ProviderAnswer) (matched: Confirmation.T -> SaveInput) (differ: CommitOid -> SaveInput) (unreadable: SaveInput) =
            match Writes.decodeFilesAt commit answer, written () with
            | Ok { Files = Some files; Truncated = false; CommittedAt = at }, Some w ->
                match Confirmation.ofReadBack s.Id commit (defaultArg at ctx.Now) w files with
                | Some c -> MachineEvent(matched c)
                | None -> MachineEvent(differ commit)
            | Ok { Files = None }, _ -> MachineEvent(differ commit)
            | _ -> MachineEvent unreadable
        match s.Outstanding with
        | Some(id, awaiting) when id = answered ->
            match awaiting with
            | MutationAnswer ->
                match Classify.classify (Operation.Mutation s.BaseHead) outcome |> Result.bind Decode.created with
                | Ok created -> next (ReadBackAt created.Oid) (Writes.filesAt ctx.NextId ctx.Connection created.Oid prefix)
                | Error(StaleHead _) -> next FreshHead (GitHubRequests.readHead ctx.NextId ctx.Connection)
                | Error AuthenticationFailed -> MachineEvent SaveInput.CredentialRefused
                | Error(NetworkUnavailable TransportFailure.FailedBeforeSending) -> MachineEvent SaveInput.FailedBeforeSending
                | Error e when Classify.isUnknownMutationOutcome e -> MachineEvent SaveInput.AnswerLost
                | Error e -> MachineEvent(SaveInput.ProviderRefused e)
            | ReadBackAt commit ->
                match Classify.classify Operation.ReadBlob outcome with
                | Ok answer -> filesVerdict commit answer SaveInput.ReadBackMatched SaveInput.ReadBackMismatched SaveInput.ReadBackReadFailed
                | Error AuthenticationFailed -> MachineEvent SaveInput.CredentialRefused
                | Error _ -> MachineEvent SaveInput.ReadBackReadFailed
            | FreshHead ->
                match Classify.classify Operation.ReadHead outcome |> Result.bind Decode.head with
                | Ok head -> next (FileComparisonAt head) (compareRequest head 1)
                | Error AuthenticationFailed -> MachineEvent SaveInput.CredentialRefused
                | Error _ -> MachineEvent SaveInput.RebaseReadFailed
            | FileComparisonAt head ->
                match Classify.classify (Operation.Compare s.BaseHead) outcome with
                | Ok answer when Writes.touchesPrefix prefix (Writes.changedFiles answer) -> MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                | Ok _ -> MachineEvent(SaveInput.StaleHeadUnrelated head)
                | Error(HistoryRewritten _) -> MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                | Error AuthenticationFailed -> MachineEvent SaveInput.CredentialRefused
                | Error _ -> MachineEvent SaveInput.RebaseReadFailed
            | ReconcileHead ->
                match Classify.classify Operation.ReadHead outcome |> Result.bind Decode.head with
                | Ok head when head = s.BaseHead -> MachineEvent SaveInput.ReconcileNothingWrittenHeadUnchanged
                | Ok head -> next (ReconcileCommits 1) (compareRequest head 1)
                | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
            | ReconcileCommits page ->
                match Classify.classify (Operation.Compare s.BaseHead) outcome with
                | Error(HistoryRewritten _) -> MachineEvent SaveInput.ReconcileHistoryRewritten
                | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
                | Ok answer ->
                    match Decode.compare answer with
                    | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
                    | Ok p ->
                        let markerText = Writes.markerText (Writes.marker s)
                        let head = p.Commits |> List.tryLast |> Option.map (fun c -> c.Oid) |> Option.defaultValue s.BaseHead
                        match p.Commits |> List.tryFind (fun c -> c.Message.Split([| '\n' |]) |> Array.exists (fun l -> l.Trim() = markerText)) with
                        | Some marked -> next (ReconcileFilesAt marked.Oid) (Writes.filesAt ctx.NextId ctx.Connection marked.Oid prefix)
                        | None when p.TotalCommits > page * 100 -> next (ReconcileCommits(page + 1)) (compareRequest head (page + 1))
                        | None when Writes.touchesPrefix prefix (Writes.changedFiles answer) -> MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                        | None -> MachineEvent(SaveInput.ReconcileNothingWrittenHeadMoved head)
            | ReconcileFilesAt commit ->
                match Classify.classify Operation.ReadBlob outcome with
                | Ok answer -> filesVerdict commit answer SaveInput.ReconcileMarkerBytesMatch SaveInput.ReconcileMarkerBytesDiffer SaveInput.ReconcileReadFailed
                | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
        | _ -> MachineEvent(SaveInput.SupersededReply answered)

    let initial (address: Writable.T) (v: VerifiedInvestigation) : InvestigationSession =
        { Id = v.Investigation.Id
          Address = address
          BaseHead = v.ReadAt
          State = SaveState.Saved(VerifiedAt v)
          Unsaved = []
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0
          ReconcileReadsSentAt = [] }

    let initialNew (address: Writable.T) (id: InvestigationId) (head: CommitOid) (start: CommandEnvelope) (online: bool) : InvestigationSession =
        { Id = id
          Address = address
          BaseHead = head
          State = (if online then SaveState.Unsaved else SaveState.Offline)
          Unsaved = [ start ]
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0
          ReconcileReadsSentAt = [] }

/// The assembled IF41-SAVE machine.
[<RequireQualifiedAccess>]
module SaveMachines =

    /// The machine over an encoder and a verifier (the IF41-STORAGE codec's Encode and Verify).
    let create
        (encode: SessionEncoder)
        (verify: WorkspaceAddress.T -> CommitOid -> FileSet -> Result<VerifiedInvestigation, VerificationFailure list>)
        : SaveMachine =
        { Initial = SaveSession.initial
          InitialNew = SaveSession.initialNew
          Interpret = SaveSession.interpret encode
          Step = SaveSession.step encode
          ReconcileDue = SaveSession.reconcileDue
          InterpretConflict = ConflictMachine.interpret verify
          StepConflict = ConflictMachine.step
          InterpretLoad = LoadMachine.interpret verify
          StepLoad = LoadMachine.step }

    /// The machine over causa-storage: saves write the base history plus the engine-decided unsaved commands
    /// (Writes.storageEncoder), loads and conflict re-reads verify with the IF41-STORAGE codec. The engine builds it
    /// with the base each session rests on (its Loaded VerifiedInvestigation; None for a new investigation).
    let withStorage (actor: Actor) (baseOf: InvestigationSession -> VerifiedInvestigation option) : SaveMachine =
        create (Writes.storageEncoder actor baseOf) StorageCodecs.codec.Verify

    /// The machine value the contract tests find by reflection. Its encoder writes a deterministic placeholder file set
    /// from the session alone, because a session carries no base history or command times; the application uses
    /// withStorage, which writes causa-storage files. Loads and conflict re-reads verify with the real codec.
    let machine : SaveMachine = create Writes.provisionalEncoder StorageCodecs.codec.Verify

/// INV-023 (UB-023): P19 decided by the owner in chat, not yet recorded (Indy-init#195). causa-storage 1.1.0 has no field for the primary
/// investigative question: UI-FIELD-MAP UB-023 maps it to InvestigationDetailsRevised.question, which 1.1.0 defers
/// to 1.2.0. Until then the question is stored as one UnknownIdentified (materiality informational, the least
/// committal value), sent as the command right after StartInvestigation (two adjacent command groups in one commit),
/// and the earliest-created unknown reads back
/// as the primary question. It also appears among the open unknowns (the owner accepted this in P19).
[<RequireQualifiedAccess>]
module PrimaryQuestion =
    open Causa.Domain

    /// The commands that start an investigation: the start, then the question as an informational unknown.
    let startCommands (start: CommandEnvelope) (question: string) (questionCommandId: Guid) (unknownId: Guid) : CommandEnvelope list =
        match NonEmptyText.create question with
        | None -> [ start ]
        | Some q ->
            [ start
              { CommandId = questionCommandId
                Investigation = start.Investigation
                ExpectedVersion = Some(AggregateVersion 1L)
                Command = Core(IdentifyUnknown { UnknownId = unknownId; Question = q; Materiality = Materiality.Informational }) } ]

    /// The primary question on reload: the question of the earliest-created unknown in history order.
    let ofInvestigation (investigation: Investigation) : string option =
        investigation.Timeline
        |> List.tryPick (fun e ->
            match e.Payload with
            | CoreEvent(UnknownIdentified u) -> Some u.Question.Value
            | _ -> None)

/// Reapply (PERSIST-012-O3/O4, T-C07): each unsaved command re-decided in its original order through the engine at
/// the GitHub version. Accepted commands become the unsaved changes on that version; refused ones are listed with
/// the engine's reason and never applied. Nothing is saved.
[<RequireQualifiedAccess>]
module Reapply =
    open Causa.Domain

    let redecide (actor: Actor) (remote: VerifiedInvestigation) (commands: CommandEnvelope list) : CommandEnvelope list * RefusedCommand list =
        let actorId = match actor with Actor.Human a | Actor.System a | Actor.Agent(a, _, _) -> a.Value
        let version = match remote.History with e :: _ -> e.Provenance.FormatVersion | [] -> "1.1.0"
        let reason (d: Decision) =
            match d with
            | Blocked bs -> bs |> List.map (fun b -> b.Message) |> String.concat " "
            | RequiresReview rs -> String.concat " " rs
            | UnsupportedSemantics ids -> "unsupported: " + String.concat ", " ids
            | Conflict(AggregateVersion e, AggregateVersion a) -> sprintf "expected version %d, found %d" e a
            | Duplicate _ -> "already applied"
            | Allowed _ -> ""
        let folder (state: Investigation, accepted, refused) (cmd: CommandEnvelope) =
            // Re-decided at the GitHub version: the version the command observed no longer applies.
            let cmd' = { cmd with ExpectedVersion = None }
            match Kernel.kernel.Decide Policies.coreDefault { ActorId = actorId } (Some state) cmd' with
            | Allowed payloads ->
                let at = Writes.requestedAt cmd.CommandId |> Option.defaultValue DateTimeOffset.UnixEpoch
                let (AggregateVersion v0) = state.Version
                let (InvestigationId g) = state.Id
                let evolved =
                    payloads
                    |> List.mapi (fun k p ->
                        let v = v0 + int64 k + 1L
                        { EventId = EventId(Guid(Canonical.eventId (Canonical.guidText g) v (Canonical.guidText cmd.CommandId)))
                          Investigation = state.Id
                          Version = AggregateVersion v
                          Provenance = { OccurredAt = at; Actor = actor; CorrelationId = cmd.CommandId; FormatVersion = version; SemanticVersion = version }
                          Payload = p })
                    |> List.fold (fun acc e -> acc |> Result.bind (fun s -> Kernel.kernel.Evolve (Some s) e)) (Ok state)
                match evolved with
                | Ok s -> s, accepted @ [ cmd' ], refused
                | Error e -> state, accepted, refused @ [ { Command = cmd; Reason = e.Reason } ]
            | other -> state, accepted, refused @ [ { Command = cmd; Reason = reason other } ]
        let _, accepted, refused = commands |> List.fold folder (remote.Investigation, [], [])
        accepted, refused
