// Owner: domain (T13). The core policy: the gates the requirements name, as pure functions of the investigation that
// return typed blockers (an empty list means the gate passes). Decide and Capabilities both read these, so a legal
// action and its explanation never disagree (R8.1, R8.2).
namespace Causa.Domain

open System

[<RequireQualifiedAccess>]
module Blockers =
    let make (code: string) (message: string) : Blocker = { Code = code; Message = message }

    let notOpen (i: Investigation) =
        make "INVESTIGATION.NOT_OPEN" (sprintf "The investigation is %A, so nothing can be recorded until it is reopened." i.Lifecycle)

    let notFound (what: string) (id: obj) =
        make "ENTITY.NOT_FOUND" (sprintf "No %s with id %O exists in this investigation." what id)

    let duplicateId (what: string) (id: obj) =
        make "ID.DUPLICATE" (sprintf "A %s with id %O already exists; ids are never reused." what id)

[<RequireQualifiedAccess>]
module Policies =
    /// The core policy. Its id is the `policy` a cause confirmation records (FIELD-MAPPING: engine Policy.id).
    let coreDefault : PolicySet = { Id = "core-default/1" }

    let private openUnknownStates = [ UnknownState.Identified; UnknownState.Investigating ]

    let private openContradictionStates =
        [ ContradictionState.Detected; ContradictionState.Triaged; ContradictionState.ContradictionInvestigating ]

    /// R5.2, R6.1: a blocking unknown that is still open stops cause confirmation and resolution.
    let openBlockingUnknowns (i: Investigation) =
        i.Unknowns
        |> Map.toList
        |> List.choose (fun (_, u) ->
            if u.Materiality = Materiality.Blocking && List.contains u.State openUnknownStates then
                Some(Blockers.make "UNKNOWN.BLOCKING_OPEN" (sprintf "The blocking unknown \"%s\" is still open." u.Question.Value))
            else
                None)

    /// R3.4, R6.1: an unresolved blocking contradiction stops cause confirmation and resolution.
    let openBlockingContradictions (i: Investigation) =
        i.Contradictions
        |> Map.toList
        |> List.choose (fun (_, c) ->
            if c.Materiality = Materiality.Blocking && List.contains c.State openContradictionStates then
                Some(Blockers.make "CONTRADICTION.BLOCKING_OPEN" (sprintf "The blocking contradiction \"%s\" is unresolved." c.Statement.Value))
            else
                None)

    /// Evidence that can still carry weight: admitted, not superseded, not invalidated.
    let isUsableEvidence (i: Investigation) (id: Guid) =
        match Map.tryFind id i.Evidence with
        | Some e -> e.SupersededBy.IsNone && e.Verification <> VerificationStatus.Invalidated
        | None -> false

    /// The current relationship of each evidence-hypothesis pair is the latest recorded one (v1.1.0 S-8 B).
    let currentRelationships (i: Investigation) =
        let order =
            i.Timeline
            |> List.choose (fun env ->
                match env.Payload with
                | CoreEvent(EvidenceRelationshipRecorded r) -> Some(r.RelationshipId, env.Version)
                | _ -> None)
            |> Map.ofList
        i.Relationships
        |> Map.toList
        |> List.map snd
        |> List.groupBy (fun r -> r.EvidenceId, r.HypothesisId)
        |> List.map (fun (_, rs) -> rs |> List.maxBy (fun r -> Map.tryFind r.Id order |> Option.defaultValue (AggregateVersion 0L)))

    /// A hypothesis has current support: a `supports` relationship to usable evidence (the cause gate counts only
    /// supports; v1.1.0 section 4.6).
    let hasSupport (i: Investigation) (h: HypothesisId) =
        currentRelationships i
        |> List.exists (fun r -> r.HypothesisId = h && r.Kind = RelationshipKind.Supports && isUsableEvidence i r.EvidenceId)

    /// The cause gate (R6.1, R6.3): a proposed or reopened cause whose every member hypothesis exists, is supported
    /// and has current supporting evidence, with no open blocking unknown or contradiction.
    let causeGate (i: Investigation) (id: CausalAssertionId) : Blocker list =
        match Map.tryFind id i.Causes with
        | None -> [ Blockers.notFound "cause" id ]
        | Some cause ->
            [ if not (Investigation.isOpen i) then Blockers.notOpen i
              if cause.State <> CauseState.CauseProposed && cause.State <> CauseState.CauseReopened then
                  Blockers.make "CAUSE.ILLEGAL_TRANSITION" (sprintf "A %A cause cannot be confirmed." cause.State)
              for m in cause.Members do
                  match Map.tryFind m.HypothesisId i.Hypotheses with
                  | None -> Blockers.notFound "hypothesis" m.HypothesisId
                  | Some h ->
                      if h.State <> HypothesisState.Supported then
                          Blockers.make "CAUSE.MEMBER_NOT_SUPPORTED" (sprintf "The hypothesis \"%s\" is %A, not supported." h.Statement.Value h.State)
                      if not (hasSupport i h.Id) then
                          Blockers.make "CAUSE.MEMBER_WITHOUT_EVIDENCE" (sprintf "The hypothesis \"%s\" has no current supporting evidence." h.Statement.Value)
              yield! openBlockingUnknowns i
              yield! openBlockingContradictions i ]

    /// A verification is fresh when it was recorded after its corrective action began awaiting verification of effect.
    let private freshSatisfied (i: Investigation) (c: CorrectiveActionEntity) =
        i.Criteria
        |> Map.exists (fun _ criterion ->
            criterion.CorrectiveActionId = c.Id
            && i.Verifications
               |> Map.exists (fun _ v ->
                   v.CriterionId = criterion.Id
                   && v.Outcome = VerificationOutcome.Satisfied
                   && not v.EvidenceIds.IsEmpty
                   && v.RecordedAtVersion >= c.AwaitingSinceVersion))

    /// The resolution gate (R6.1, R7.3, R7.4): a confirmed cause, a corrective action for it whose success criterion a
    /// fresh, evidence-citing verification satisfied, and no open blocking unknown or contradiction.
    let resolutionGate (i: Investigation) : Blocker list =
        let confirmed = i.Causes |> Map.filter (fun _ c -> c.State = CauseState.CauseConfirmed)
        let verifiedCorrection =
            i.CorrectiveActions
            |> Map.exists (fun _ c ->
                c.State <> CorrectiveActionState.CorrectionCancelled
                && c.TargetCauseIds |> List.exists (fun id -> Map.containsKey id confirmed)
                && freshSatisfied i c)
        [ match i.Lifecycle with
          | Lifecycle.Active
          | Lifecycle.AwaitingVerification -> ()
          | other -> Blockers.make "LIFECYCLE.ILLEGAL_TRANSITION" (sprintf "A %A investigation cannot be resolved." other)
          if confirmed.IsEmpty then Blockers.make "RESOLUTION.NO_CONFIRMED_CAUSE" "No cause has been confirmed."
          if not verifiedCorrection then
              Blockers.make
                  "RESOLUTION.UNVERIFIED"
                  "No corrective action for a confirmed cause has a success criterion satisfied by a verification that cites evidence."
          yield! openBlockingUnknowns i
          yield! openBlockingContradictions i ]

    let private normalise (t: NonEmptyText) = t.Value.Trim().ToLowerInvariant()

    /// R5.4: a hypothesis that repeats a falsified hypothesis or established negative knowledge is refused.
    let repetition (i: Investigation) (statement: NonEmptyText) : Blocker list =
        let s = normalise statement
        [ if i.Hypotheses |> Map.exists (fun _ h -> h.State = HypothesisState.Falsified && normalise h.Statement = s) then
              Blockers.make "HYPOTHESIS.REPEATS_FALSIFIED" "This hypothesis repeats one already falsified; reopen that hypothesis with new evidence instead."
          if i.NegativeKnowledge |> Map.exists (fun _ n -> normalise n.Statement = s) then
              Blockers.make "HYPOTHESIS.REPEATS_NEGATIVE_KNOWLEDGE" "This hypothesis repeats something established not to be the case." ]

    /// An absolute http(s) URL (the engine's artifact URL rule; v1.1.0 section 4.6).
    let isHttpUrl (text: string) =
        match Uri.TryCreate(text, UriKind.Absolute) with
        | true, NonNull uri -> (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps) && not (String.IsNullOrEmpty uri.Host)
        | _ -> false
