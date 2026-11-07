// Owner: persistence. SM-LOAD (state-machine-contracts.json): pure; effects are data.
namespace Causa.Persistence

open Causa.Domain

[<RequireQualifiedAccess>]
module LoadMachine =

    let stateName =
        function
        | NotLoaded -> "NotLoaded"
        | Loading _ -> "Loading"
        | Loaded _ -> "Loaded"
        | LoadFailed _ -> "LoadFailed"
        | UnavailableOffline -> "UnavailableOffline"

    let inputName =
        function
        | LoadInput.OpenRequested _ -> "OpenRequested"
        | LoadInput.LoadVerified _ -> "LoadVerified"
        | LoadInput.LoadRejected _ -> "LoadRejected"
        | LoadInput.WentOffline -> "WentOffline"
        | LoadInput.WentOnline -> "WentOnline"
        | LoadInput.RefreshRequested -> "RefreshRequested"
        | LoadInput.SupersededReply _ -> "SupersededReply"

    let step (ctx: RequestContext) (state: LoadState) (input: LoadInput) : Result<LoadState * SaveEffect list, SaveFault> =
        let open' target = Ok(Loading(ctx.NextId, LoadHead), [ Send(Writes.headAndFiles ctx.NextId ctx.Connection target) ])
        match state, input with
        | NotLoaded, LoadInput.OpenRequested target -> open' target
        | Loading _, LoadInput.LoadVerified v -> Ok(Loaded v, [])
        | Loading _, LoadInput.LoadRejected f -> Ok(LoadFailed f, [ Announce(Assertive, "The investigation could not be loaded.") ])
        | Loading(current, _), LoadInput.SupersededReply id when id <> current -> Ok(state, [])
        | Loaded _, LoadInput.WentOffline -> Ok(state, [])
        | Loaded v, LoadInput.RefreshRequested -> open' { Address = v.Address; Id = v.Investigation.Id }
        | Loaded _, LoadInput.SupersededReply _ -> Ok(state, [])
        | LoadFailed _, LoadInput.OpenRequested target -> open' target
        | UnavailableOffline, LoadInput.WentOnline -> Ok(NotLoaded, [])
        | _ ->
            Error
                { Machine = "SM-LOAD"
                  State = stateName state
                  Input = inputName input
                  Reason = "illegal (state, event) pair: fails closed, nothing changes" }

    /// The answer to headAndFiles: verified with `verify`, or the provider's or the verifier's refusal.
    let interpret
        (verify: WorkspaceAddress.T -> CommitOid -> FileSet -> Result<VerifiedInvestigation, VerificationFailure list>)
        (_: RequestContext)
        (state: LoadState)
        (outcome: HttpOutcome)
        : LoadInterpretation =
        let answered =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id
        match state with
        | Loading(current, _) when current = answered ->
            match Classify.classify Operation.ReadHead outcome with
            | Error e -> LoadEvent(LoadInput.LoadRejected(ProviderFailed e))
            | Ok answer ->
                match Writes.verifyHeadAndFiles verify answer with
                | Ok v -> LoadEvent(LoadInput.LoadVerified v)
                | Error f -> LoadEvent(LoadInput.LoadRejected f)
        | _ -> LoadEvent(LoadInput.SupersededReply answered)
