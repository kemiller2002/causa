// Owner: domain (T13). Evolve and Replay: the normative fold of contract v1.0.0 section 8.3 (with the v1.1.0 section
// 4.7 additions). Pure: every value it needs is in the event envelope.
namespace Causa.Domain

open System

[<RequireQualifiedAccess>]
module Evolve =
    let private error (version: AggregateVersion) reason : Result<Investigation, ProjectionError> =
        Error { At = version; Reason = reason }

    let private next (AggregateVersion v) = AggregateVersion(v + 1L)

    /// A new entity is added once; a second record of the same id is inadmissible and never overwrites the first.
    let private addNew key value (map: Map<'K, 'V>) =
        if Map.containsKey key map then map else Map.add key value map

    /// Updates to an id that does not exist leave state unchanged (contract v1.0.0 section 8.3).
    let private update key (f: 'V -> 'V) (map: Map<'K, 'V>) =
        match Map.tryFind key map with
        | Some v -> Map.add key (f v) map
        | None -> map

    let private hypothesisCommand (event: CoreEvent) =
        EventRegistry.kindOf event |> Option.bind (fun k -> k.Command) |> Option.defaultValue ""

    let private transitionHypothesis (s: Investigation) (env: EventEnvelope) (event: CoreEvent) (t: HypothesisTransition) (target: HypothesisState) =
        match Map.tryFind t.HypothesisId s.Hypotheses with
        | Some h when h.State <> t.From ->
            error env.Version (sprintf "hypothesis transition from %A, but the hypothesis is %A" t.From h.State)
        | _ ->
            let change =
                { From = t.From
                  To = target
                  Command = hypothesisCommand event
                  Reason = t.Reason
                  EvidenceIds = t.EvidenceIds
                  At = env.Provenance.OccurredAt
                  AggregateVersion = env.Version }
            Ok
                { s with
                    Hypotheses = s.Hypotheses |> update t.HypothesisId (fun h -> { h with State = target; History = h.History @ [ change ] }) }

    let private lifecycle (s: Investigation) (env: EventEnvelope) (from: Lifecycle option) (target: Lifecycle) =
        match from with
        | Some f when f <> s.Lifecycle -> error env.Version (sprintf "lifecycle change from %A, but the investigation is %A" f s.Lifecycle)
        | _ -> Ok { s with Lifecycle = target }

    let private correction (s: Investigation) (env: EventEnvelope) id (target: CorrectiveActionState) =
        Ok
            { s with
                CorrectiveActions =
                    s.CorrectiveActions
                    |> update id (fun c ->
                        { c with
                            State = target
                            AwaitingSinceVersion =
                                if target = CorrectiveActionState.CorrectionAwaitingVerificationOfEffect then env.Version else c.AwaitingSinceVersion }) }

    let private applyCore (s: Investigation) (env: EventEnvelope) (p: Provenance) (event: CoreEvent) : Result<Investigation, ProjectionError> =
        match event with
        | InvestigationStarted _ -> error env.Version "InvestigationStarted can only begin a history"
        | InvestigationActivated from -> lifecycle s env (Some from) Lifecycle.Active
        | InvestigationMarkedAwaitingEvidence(from, _) -> lifecycle s env (Some from) Lifecycle.AwaitingEvidence
        | InvestigationMarkedAwaitingVerification from -> lifecycle s env (Some from) Lifecycle.AwaitingVerification
        | InvestigationResolved from -> lifecycle s env (Some from) Lifecycle.Resolved
        | InvestigationClosed from -> lifecycle s env (Some from) Lifecycle.Closed
        | InvestigationReopened(from, _) -> lifecycle s env (Some from) Lifecycle.Active
        | InvestigationVerificationFailed _ -> lifecycle s env (Some Lifecycle.AwaitingVerification) Lifecycle.Active
        | ObservationRecorded o ->
            Ok
                { s with
                    Observations =
                        s.Observations
                        |> addNew o.ObservationId { Id = o.ObservationId; Original = o.Original; ObservedAt = o.ObservedAt; Source = None; Provenance = p } }
        | ObservationRevised r ->
            Ok { s with Observations = s.Observations |> update r.ObservationId (fun o -> { o with Original = r.Original; Source = r.Source }) }
        | HypothesisRecorded h ->
            Ok
                { s with
                    Hypotheses =
                        s.Hypotheses
                        |> addNew
                            h.HypothesisId
                            { Id = h.HypothesisId
                              Statement = h.Statement
                              Mechanism = h.Mechanism
                              State = HypothesisState.Proposed
                              History = []
                              Provenance = p } }
        | HypothesisMarkedPlausible t -> transitionHypothesis s env event t HypothesisState.Plausible
        | HypothesisMarkedSupported t -> transitionHypothesis s env event t HypothesisState.Supported
        | HypothesisWeakened t -> transitionHypothesis s env event t HypothesisState.Weakened
        | HypothesisFalsified t -> transitionHypothesis s env event t HypothesisState.Falsified
        | HypothesisSuperseded t -> transitionHypothesis s env event t HypothesisState.Superseded
        | HypothesisReopened t -> transitionHypothesis s env event t HypothesisState.Plausible
        | EvidenceAdmitted e ->
            Ok
                { s with
                    Evidence =
                        s.Evidence
                        |> addNew
                            e.EvidenceId
                            { Id = e.EvidenceId
                              ContentKind = e.ContentKind
                              Content = e.Content
                              Category = e.Category
                              Source = e.Source
                              CapturedAt = e.CapturedAt
                              Verification = VerificationStatus.Unverified
                              SupersededBy = None
                              AdmittedAtVersion = env.Version
                              Label = None
                              SourceUrl = None
                              Provenance = p } }
        | EvidenceSuperseded x ->
            Ok { s with Evidence = s.Evidence |> update x.EvidenceId (fun e -> { e with SupersededBy = Some x.ReplacementEvidenceId }) }
        | EvidenceVerificationAssessed x ->
            Ok { s with Evidence = s.Evidence |> update x.EvidenceId (fun e -> { e with Verification = x.Verification }) }
        | EvidenceRevised r ->
            Ok
                { s with
                    Evidence = s.Evidence |> update r.EvidenceId (fun e -> { e with Label = Some r.Label; Source = r.Source; SourceUrl = r.SourceUrl }) }
        | EvidenceRelationshipRecorded r ->
            Ok
                { s with
                    Relationships =
                        s.Relationships
                        |> addNew
                            r.RelationshipId
                            { Id = r.RelationshipId
                              EvidenceId = r.EvidenceId
                              HypothesisId = r.HypothesisId
                              Kind = r.Kind
                              Rationale = r.Rationale
                              Provenance = p } }
        | UnknownIdentified u ->
            Ok
                { s with
                    Unknowns =
                        s.Unknowns
                        |> addNew
                            u.UnknownId
                            { Id = u.UnknownId
                              Question = u.Question
                              Materiality = u.Materiality
                              State = UnknownState.Identified
                              Basis = None
                              Provenance = p } }
        | UnknownTransitioned t ->
            Ok { s with Unknowns = s.Unknowns |> update t.UnknownId (fun u -> { u with State = t.State; Basis = Some t.Basis }) }
        | AssumptionRecorded a ->
            Ok
                { s with
                    Assumptions =
                        s.Assumptions
                        |> addNew
                            a.AssumptionId
                            { Id = a.AssumptionId
                              Proposition = a.Proposition
                              Materiality = a.Materiality
                              State = AssumptionState.ExplicitUnverified
                              Provenance = p } }
        | AssumptionTransitioned t -> Ok { s with Assumptions = s.Assumptions |> update t.AssumptionId (fun a -> { a with State = t.State }) }
        | ContradictionRecorded c ->
            Ok
                { s with
                    Contradictions =
                        s.Contradictions
                        |> addNew
                            c.ContradictionId
                            { Id = c.ContradictionId
                              Statement = c.Statement
                              EvidenceIds = c.EvidenceIds
                              Materiality = c.Materiality
                              State = ContradictionState.Detected
                              Basis = None
                              Provenance = p } }
        | ContradictionTransitioned t ->
            Ok
                { s with
                    Contradictions = s.Contradictions |> update t.ContradictionId (fun c -> { c with State = t.State; Basis = Some t.Basis }) }
        | PredictionRecorded x ->
            Ok
                { s with
                    Predictions =
                        s.Predictions
                        |> addNew
                            x.PredictionId
                            { Id = x.PredictionId
                              HypothesisId = x.HypothesisId
                              Statement = x.Statement
                              Strength = x.Strength
                              Timing = x.Timing
                              Provenance = p } }
        | InvestigationActionRecorded a ->
            Ok
                { s with
                    Actions =
                        s.Actions
                        |> addNew
                            a.ActionId
                            { Id = a.ActionId
                              Kind = a.Kind
                              Purpose = a.Purpose
                              TargetHypothesisIds = a.TargetHypothesisIds
                              State = ActionState.ActionProposed
                              Provenance = p } }
        | InvestigationActionTransitioned t -> Ok { s with Actions = s.Actions |> update t.ActionId (fun a -> { a with State = t.State }) }
        | FindingRecorded f ->
            Ok
                { s with
                    Findings =
                        s.Findings
                        |> addNew
                            f.FindingId
                            { Id = f.FindingId
                              ActionId = f.ActionId
                              OriginalResult = f.OriginalResult
                              Interpretation = f.Interpretation
                              Provenance = p } }
        | NegativeKnowledgeEstablished n ->
            Ok
                { s with
                    NegativeKnowledge =
                        s.NegativeKnowledge
                        |> addNew
                            n.NegativeKnowledgeId
                            { Id = n.NegativeKnowledgeId
                              Statement = n.Statement
                              Scope = n.Scope
                              BasisEvidenceIds = n.BasisEvidenceIds
                              Provenance = p } }
        | CausalAssertionRecorded c ->
            Ok
                { s with
                    Causes =
                        s.Causes
                        |> addNew
                            c.CauseId
                            { Id = c.CauseId
                              Outcome = c.Outcome
                              Members = c.Members
                              Mechanism = c.Mechanism
                              State = CauseState.CauseProposed
                              Provenance = p } }
        | CauseConfirmed c -> Ok { s with Causes = s.Causes |> update c.CauseId (fun x -> { x with State = CauseState.CauseConfirmed }) }
        | ConfirmedCauseReopened(id, _) -> Ok { s with Causes = s.Causes |> update id (fun x -> { x with State = CauseState.CauseReopened }) }
        | CorrectiveActionRecorded c ->
            Ok
                { s with
                    CorrectiveActions =
                        s.CorrectiveActions
                        |> addNew
                            c.CorrectiveActionId
                            { Id = c.CorrectiveActionId
                              Kind = c.Kind
                              Description = c.Description
                              TargetCauseIds = c.TargetCauseIds
                              State = CorrectiveActionState.CorrectionProposed
                              AwaitingSinceVersion = AggregateVersion 0L
                              Provenance = p } }
        | CorrectiveActionApproved id -> correction s env id CorrectiveActionState.CorrectionApproved
        | CorrectiveActionStarted id -> correction s env id CorrectiveActionState.CorrectionInProgress
        | CorrectiveActionImplemented id -> correction s env id CorrectiveActionState.CorrectionImplemented
        | CorrectiveActionAwaitingVerificationOfEffect id -> correction s env id CorrectiveActionState.CorrectionAwaitingVerificationOfEffect
        | CorrectiveActionVerifiedEffective id -> correction s env id CorrectiveActionState.CorrectionVerifiedEffective
        | CorrectiveActionVerificationFailed id -> correction s env id CorrectiveActionState.CorrectionVerificationFailed
        | CorrectiveActionReworkRequired id -> correction s env id CorrectiveActionState.CorrectionReworkRequired
        | CorrectiveActionCancelled id -> correction s env id CorrectiveActionState.CorrectionCancelled
        | VerificationCriterionRecorded c ->
            Ok
                { s with
                    Criteria =
                        s.Criteria
                        |> addNew
                            c.CriterionId
                            { Id = c.CriterionId
                              CorrectiveActionId = c.CorrectiveActionId
                              ExpectedOutcome = c.ExpectedOutcome
                              Method = c.Method
                              Provenance = p } }
        | VerificationRecorded v ->
            Ok
                { s with
                    Verifications =
                        s.Verifications
                        |> addNew
                            v.VerificationId
                            { Id = v.VerificationId
                              CriterionId = v.CriterionId
                              Outcome = v.Outcome
                              EvidenceIds = v.EvidenceIds
                              RecordedAtVersion = env.Version
                              Provenance = p } }
        | ConclusionRecorded c ->
            Ok
                { s with
                    Conclusions =
                        s.Conclusions
                        |> addNew
                            c.ConclusionId
                            { Id = c.ConclusionId
                              Statement = c.Statement
                              Strength = c.Strength
                              CauseIds = c.CauseIds
                              ResidualUncertainty = c.ResidualUncertainty
                              RecordedAtVersion = env.Version
                              Provenance = p } }
        | ProposalRecorded x ->
            Ok
                { s with
                    Proposals =
                        s.Proposals
                        |> addNew
                            x.ProposalId
                            { Id = x.ProposalId
                              Kind = x.Kind
                              Text = x.Text
                              Rationale = x.Rationale
                              State = ProposalState.ProposalActive
                              Decision = None
                              Provenance = p } }
        | ProposalAccepted(id, _) -> Ok { s with Proposals = s.Proposals |> update id (fun x -> { x with State = ProposalState.ProposalAccepted }) }
        | ProposalRejected(id, reason) ->
            Ok { s with Proposals = s.Proposals |> update id (fun x -> { x with State = ProposalState.ProposalRejected; Decision = Some reason }) }
        | ExtensionAttached ext -> Ok { s with Extensions = s.Extensions @ [ ext ] }

    /// Applies one event. The first event must be InvestigationStarted at version 1; every later one must name the same
    /// investigation and carry the next version. Every event sets the version, even one that changes nothing else.
    let evolve (state: Investigation option) (env: EventEnvelope) : Result<Investigation, ProjectionError> =
        let provenance = { Actor = env.Provenance.Actor; RecordedAt = env.Provenance.OccurredAt }
        match state, env.Payload with
        | None, CoreEvent(InvestigationStarted started) ->
            if env.Version <> AggregateVersion 1L then
                error env.Version "InvestigationStarted must be aggregate version 1"
            else
                Ok { Investigation.started env.Investigation env.Version provenance started with Timeline = [ env ] }
        | None, _ -> error env.Version "the history must begin with InvestigationStarted"
        | Some s, payload ->
            if env.Investigation <> s.Id then
                error env.Version "the event names another investigation"
            elif env.Version <> next s.Version then
                error env.Version (sprintf "expected aggregate version %d" (Investigation.versionNumber (next s.Version)))
            else
                let applied =
                    match payload with
                    | CoreEvent event -> applyCore s env provenance event
                    | ExtensionEvent _ -> Ok s
                applied |> Result.map (fun s' -> { s' with Version = env.Version; Timeline = s'.Timeline @ [ env ] })

    /// Folds a whole history in order: empty histories, gaps and illegal events are refused, never repaired.
    let replay (history: EventEnvelope list) : Result<Investigation, ReplayError> =
        match history with
        | [] -> Error EmptyHistory
        | _ ->
            let folder (acc: Result<Investigation option, ReplayError>) (env: EventEnvelope) =
                acc
                |> Result.bind (fun state ->
                    let expected =
                        match state with
                        | None -> AggregateVersion 1L
                        | Some s -> next s.Version
                    if env.Version <> expected then
                        Error(NonContiguousVersion(expected, env.Version))
                    else
                        evolve state env |> Result.map Some |> Result.mapError IllegalEvent)
            history
            |> List.fold folder (Ok None)
            |> Result.bind (function
                | Some s -> Ok s
                | None -> Error EmptyHistory)
