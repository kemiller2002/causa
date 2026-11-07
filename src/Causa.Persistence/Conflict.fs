// Owner: persistence. SM-CONFLICT (state-machine-contracts.json): pure; effects are data.
namespace Causa.Persistence

open Causa.Domain

/// Reads the GitHub version of an investigation in conflict: one GraphQL request for the branch head and the three
/// files at it, so the version reviewed is one commit (T-C01, T-C11).
[<RequireQualifiedAccess>]
module ConflictMachine =

    let stateName =
        function
        | ReadingRemote _ -> "ReadingRemote"
        | Reviewable _ -> "Reviewable"
        | Reapplying _ -> "Reapplying"
        | Reapplied _ -> "Reapplied"
        | ConfirmingDiscard _ -> "ConfirmingDiscard"
        | Discarded _ -> "Discarded"
        | RemoteUnavailable -> "RemoteUnavailable"
        | Unresolvable _ -> "Unresolvable"

    let inputName =
        function
        | ConflictInput.RemoteVerified _ -> "RemoteVerified"
        | ConflictInput.RemoteReadFailed _ -> "RemoteReadFailed"
        | ConflictInput.RemoteMissingOrInvalid _ -> "RemoteMissingOrInvalid"
        | ConflictInput.ReadAgain -> "ReadAgain"
        | ConflictInput.ReviewChosen -> "ReviewChosen"
        | ConflictInput.ReapplyChosen -> "ReapplyChosen"
        | ConflictInput.ReapplyDone _ -> "ReapplyDone"
        | ConflictInput.DiscardChosen -> "DiscardChosen"
        | ConflictInput.DiscardConfirmed -> "DiscardConfirmed"
        | ConflictInput.DiscardCancelled -> "DiscardCancelled"
        | ConflictInput.ExportChosen -> "ExportChosen"
        | ConflictInput.SupersededReply _ -> "SupersededReply"

    let fault (view: ConflictView) (input: ConflictInput) (reason: string) : SaveFault =
        { Machine = "SM-CONFLICT"; State = stateName view; Input = inputName input; Reason = reason }

    /// The read of the branch's current head (a new request identity each time, T-C04).
    /// With the investigation known (a held GitHub version, or the save session starting the conflict), one read of the
    /// head and its files; otherwise the head alone.
    let remoteRead (ctx: RequestContext) (target: InvestigationRef option) : HttpRequest =
        match target with
        | Some t -> Writes.headAndFiles ctx.NextId ctx.Connection t
        | None -> GitHubRequests.readHead ctx.NextId ctx.Connection

    let private heldTarget =
        function
        | Reviewable v
        | Reapplying v
        | Reapplied(v, _)
        | ConfirmingDiscard v
        | Discarded v -> Some { Address = v.Address; Id = v.Investigation.Id }
        | _ -> None

    let step (ctx: RequestContext) (view: ConflictView) (input: ConflictInput) : Result<ConflictView * SaveEffect list, SaveFault> =
        let ok v effects = Ok(v, effects)
        let illegal () = Error(fault view input "illegal (state, event) pair: fails closed, nothing changes")
        let readAgain () = ok (ReadingRemote ctx.NextId) [ Send(remoteRead ctx (heldTarget view)) ]
        match view, input with
        | ReadingRemote _, ConflictInput.RemoteVerified remote -> ok (Reviewable remote) [ Announce(Polite, "The GitHub version is ready to review.") ]
        | ReadingRemote _, ConflictInput.RemoteReadFailed _ -> ok RemoteUnavailable [ Announce(Assertive, "GitHub could not be read; your changes are kept. Read again when the connection returns.") ]
        | ReadingRemote _, ConflictInput.RemoteMissingOrInvalid _ ->
            ok (Unresolvable "the GitHub version is missing or did not verify") [ Announce(Assertive, "This investigation was deleted or rewritten on GitHub; your changes are kept.") ]
        | ReadingRemote current, ConflictInput.SupersededReply id when id <> current -> ok view []
        | Reviewable _, ConflictInput.ReadAgain -> readAgain ()
        | Reviewable _, ConflictInput.ReviewChosen -> ok view []
        | Reviewable remote, ConflictInput.ReapplyChosen -> ok (Reapplying remote) []
        | Reviewable remote, ConflictInput.DiscardChosen -> ok (ConfirmingDiscard remote) [ Announce(Assertive, "Discard your unsaved changes and load the GitHub version?") ]
        | Reviewable _, ConflictInput.SupersededReply _ -> ok view []
        | Reapplying remote, ConflictInput.ReapplyDone(_, refused) -> ok (Reapplied(remote, refused)) []
        | ConfirmingDiscard remote, ConflictInput.DiscardConfirmed -> ok (Discarded remote) []
        | ConfirmingDiscard remote, ConflictInput.DiscardCancelled -> ok (Reviewable remote) []
        | RemoteUnavailable, ConflictInput.ReadAgain -> readAgain ()
        | Unresolvable _, ConflictInput.ReadAgain -> readAgain ()
        | Unresolvable _, ConflictInput.ExportChosen ->
            // The unsaved commands live in the SM-SAVE session; the App answers ExportCommands from it (T-C12).
            ok view [ Announce(Polite, "Your unsaved changes are offered as a file to save; nothing is written to GitHub or browser storage.") ]
        | _ -> illegal ()

    /// An answer while reading the GitHub version (a head-and-files read), verified with `verify`.
    let interpret
        (verify: WorkspaceAddress.T -> CommitOid -> FileSet -> Result<VerifiedInvestigation, VerificationFailure list>)
        (_: RequestContext)
        (view: ConflictView)
        (outcome: HttpOutcome)
        : ConflictInterpretation =
        let answered =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id
        match view with
        | ReadingRemote current when current = answered ->
            match Classify.classify Operation.ReadHead outcome with
            | Error(NetworkUnavailable f) -> ConflictEvent(ConflictInput.RemoteReadFailed f)
            | Error(NotFound _) -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid None)
            | Error _ -> ConflictEvent(ConflictInput.RemoteReadFailed TransportFailure.LostAfterSending)
            | Ok answer ->
                match Writes.verifyHeadAndFiles verify answer with
                | Ok v -> ConflictEvent(ConflictInput.RemoteVerified v)
                | Error(Unverifiable failures) -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid(List.tryHead failures))
                | Error(ProviderFailed(NotFound _)) when answer.Body.Contains "\"data\"" -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid None)
                // A head-only answer (the investigation was not known when the read was sent): it could not be read whole.
                | Error _ -> ConflictEvent(ConflictInput.RemoteReadFailed TransportFailure.LostAfterSending)
        | _ -> ConflictEvent(ConflictInput.SupersededReply answered)
