// Owner: domain (T13). Decide: the one domain decision path (STATE-TRANSITION-TABLES sections 16-17). It checks the
// command's investigation and expected version, refuses a repeated command, then the structural transition tables and
// the core gates (Policies), and only then proposes events. A refusal is a typed Decision, never an exception, and is
// returned before any side effect (AR-006).
namespace Causa.Domain

open System

[<RequireQualifiedAccess>]
module Decide =
    let private core (events: CoreEvent list) = Allowed(events |> List.map CoreEvent)

    /// Allowed when there are no blockers.
    let private decideWith (blockers: Blocker list) (events: unit -> CoreEvent list) =
        match blockers with
        | [] -> core (events ())
        | bs -> Blocked bs

    let private distinct what (ids: 'a list) =
        if List.length (List.distinct ids) <> List.length ids then
            [ Blockers.make "ID.REPEATED" (sprintf "The %s list names the same id more than once." what) ]
        else
            []

    let private evidenceExist (i: Investigation) (ids: Guid list) =
        ids |> List.filter (fun id -> not (Map.containsKey id i.Evidence)) |> List.map (fun id -> Blockers.notFound "evidence" id)

    let private evidenceUsable (i: Investigation) (ids: Guid list) =
        ids
        |> List.filter (fun id -> Map.containsKey id i.Evidence && not (Policies.isUsableEvidence i id))
        |> List.map (fun id -> Blockers.make "EVIDENCE.SUPERSEDED" (sprintf "The evidence %O is superseded or invalidated." id))

    let private requireEvidence what (ids: Guid list) =
        if ids.IsEmpty then [ Blockers.make "HYPOTHESIS.EVIDENCE_REQUIRED" (sprintf "%s must cite the evidence that caused it (R2.3)." what) ] else []

    let private hypothesisExists (i: Investigation) (id: HypothesisId) =
        if Map.containsKey id i.Hypotheses then [] else [ Blockers.notFound "hypothesis" id ]

    let private urlRule (url: string option) =
        match url with
        | Some u when not (Policies.isHttpUrl u) -> [ Blockers.make "EVIDENCE.URL" "The source URL must be an absolute http or https address." ]
        | _ -> []

    /// A hypothesis transition: legal from the listed states (STATE-TRANSITION-TABLES section 2).
    let private transition (i: Investigation) (id: HypothesisId) (allowed: HypothesisState list) (extra: HypothesisEntity -> Blocker list) (event: HypothesisTransition -> CoreEvent) reason evidence =
        match Map.tryFind id i.Hypotheses with
        | None -> Blocked [ Blockers.notFound "hypothesis" id ]
        | Some h ->
            let blockers =
                [ if not (List.contains h.State allowed) then
                      Blockers.make "HYPOTHESIS.ILLEGAL_TRANSITION" (sprintf "This change is not possible from %A." h.State)
                  yield! distinct "evidence" evidence
                  yield! evidenceExist i evidence
                  yield! extra h ]
            decideWith blockers (fun () -> [ event { HypothesisId = id; From = h.State; Reason = reason; EvidenceIds = evidence } ])

    /// A new entity must use a fresh id.
    let private fresh what (exists: bool) (id: obj) = if exists then [ Blockers.duplicateId what id ] else []

    /// R4.2 / A11: a prediction is prospective while no finding exists yet for an action that targets its
    /// hypothesis; once a result is in, a prediction about it can only be retrospective.
    let private timingFor (i: Investigation) (h: HypothesisId) =
        let resultIn =
            i.Findings
            |> Map.exists (fun _ f ->
                match Map.tryFind f.ActionId i.Actions with
                | Some a -> List.contains h a.TargetHypothesisIds
                | None -> false)
        if resultIn then PredictionTiming.Retrospective else PredictionTiming.Prospective

    let private openInvestigation (policies: PolicySet) (commandId: Guid) (i: Investigation) (cmd: CoreCommand) : Decision =
        match cmd with
        | StartInvestigation _ -> Blocked [ Blockers.make "INVESTIGATION.EXISTS" "This investigation has already been started." ]
        | ActivateInvestigation ->
            match i.Lifecycle with
            | Lifecycle.Draft
            | Lifecycle.AwaitingEvidence
            | Lifecycle.AwaitingVerification
            | Lifecycle.Archived -> core [ InvestigationActivated i.Lifecycle ]
            | other -> Blocked [ Blockers.make "LIFECYCLE.ILLEGAL_TRANSITION" (sprintf "A %A investigation cannot be activated." other) ]
        | ResolveInvestigation -> decideWith (Policies.resolutionGate i) (fun () -> [ InvestigationResolved i.Lifecycle ])
        | ReopenInvestigation r ->
            match i.Lifecycle with
            | Lifecycle.Resolved
            | Lifecycle.Closed -> core [ InvestigationReopened(i.Lifecycle, r.Reason) ]
            | other -> Blocked [ Blockers.make "LIFECYCLE.ILLEGAL_TRANSITION" (sprintf "Only a resolved or closed investigation can be reopened; this one is %A." other) ]
        | AttachExtension ext -> UnsupportedSemantics [ (let (SemanticId s) = ext.Semantic in s) ]
        | _ when not (Investigation.isOpen i) -> Blocked [ Blockers.notOpen i ]
        | RecordObservation o ->
            decideWith (fresh "observation" (Map.containsKey o.ObservationId i.Observations) o.ObservationId) (fun () ->
                [ ObservationRecorded { ObservationId = o.ObservationId; Original = o.Original; ObservedAt = o.ObservedAt }
                  match o.Source with
                  | Some source -> ObservationRevised { ObservationId = o.ObservationId; Original = o.Original; Source = Some source }
                  | None -> () ])
        | ReviseObservation o ->
            decideWith
                (if Map.containsKey o.ObservationId i.Observations then [] else [ Blockers.notFound "observation" o.ObservationId ])
                (fun () -> [ ObservationRevised { ObservationId = o.ObservationId; Original = o.Original; Source = o.Source } ])
        | ReviseTitle t ->
            // INV-034: the title is revised verbatim; a value equal to the current one changes nothing and is refused.
            decideWith
                (if t.Title = i.Title then [ Blockers.make "DETAILS.UNCHANGED" "The title is unchanged." ] else [])
                (fun () -> [ InvestigationTitleRevised { Title = t.Title; Reason = t.Reason } ])
        | ReviseUnknown u ->
            // INV-034: the question is revised, not superseded, so the unknown keeps its id; only an open unknown.
            let blockers =
                match Map.tryFind u.UnknownId i.Unknowns with
                | None -> [ Blockers.notFound "unknown" u.UnknownId ]
                | Some x when x.State <> UnknownState.Identified && x.State <> UnknownState.Investigating ->
                    [ Blockers.make "UNKNOWN.NOT_OPEN" (sprintf "Only an identified or investigating unknown can be revised; this one is %A." x.State) ]
                | Some x when x.Question = u.Question -> [ Blockers.make "DETAILS.UNCHANGED" "The question is unchanged." ]
                | Some _ -> []
            decideWith blockers (fun () -> [ UnknownRevised { UnknownId = u.UnknownId; Question = u.Question } ])
        | RecordHypothesis h ->
            decideWith
                (fresh "hypothesis" (Map.containsKey h.HypothesisId i.Hypotheses) h.HypothesisId @ Policies.repetition i h.Statement)
                (fun () -> [ HypothesisRecorded { HypothesisId = h.HypothesisId; Statement = h.Statement; Mechanism = h.Mechanism } ])
        | MarkHypothesisPlausible m ->
            transition i m.HypothesisId [ HypothesisState.Proposed; HypothesisState.Weakened ] (fun _ -> []) HypothesisMarkedPlausible m.Reason m.EvidenceIds
        | MarkHypothesisSupported m ->
            transition
                i
                m.HypothesisId
                [ HypothesisState.Plausible; HypothesisState.Weakened ]
                (fun _ -> requireEvidence "Marking a hypothesis supported" m.EvidenceIds @ evidenceUsable i m.EvidenceIds)
                HypothesisMarkedSupported
                m.Reason
                m.EvidenceIds
        | FalsifyHypothesis f ->
            transition
                i
                f.HypothesisId
                [ HypothesisState.Proposed; HypothesisState.Plausible; HypothesisState.Supported; HypothesisState.Weakened ]
                (fun _ -> requireEvidence "Falsifying a hypothesis" f.EvidenceIds)
                HypothesisFalsified
                f.Reason
                f.EvidenceIds
        | ReopenHypothesis r ->
            // R2.5: reopening needs new evidence, admitted after the hypothesis was last falsified, explicitly linked.
            let newEvidence (h: HypothesisEntity) =
                let falsifiedAt =
                    h.History
                    |> List.filter (fun c -> c.To = HypothesisState.Falsified)
                    |> List.map (fun c -> c.AggregateVersion)
                    |> List.fold max (AggregateVersion 0L)
                if r.EvidenceIds |> List.exists (fun id -> match Map.tryFind id i.Evidence with Some e -> e.AdmittedAtVersion > falsifiedAt | None -> false) then
                    []
                else
                    [ Blockers.make "HYPOTHESIS.REOPEN_NEEDS_NEW_EVIDENCE" "Reopening needs evidence admitted after the hypothesis was falsified (R2.5)." ]
            transition
                i
                r.HypothesisId
                [ HypothesisState.Falsified ]
                (fun h -> requireEvidence "Reopening a hypothesis" r.EvidenceIds @ newEvidence h)
                HypothesisReopened
                r.Reason
                r.EvidenceIds
        | AdmitEvidence e ->
            let blockers =
                [ yield! fresh "evidence" (Map.containsKey e.EvidenceId i.Evidence) e.EvidenceId
                  if e.ContentKind = ContentKind.Artifact && not (Policies.isHttpUrl e.Content.Value) then
                      Blockers.make "EVIDENCE.URL" "An artifact is referenced by an absolute http or https URL, never embedded (R3.5)."
                  yield! urlRule e.SourceUrl ]
            decideWith blockers (fun () ->
                [ EvidenceAdmitted
                      { EvidenceId = e.EvidenceId
                        ContentKind = e.ContentKind
                        Content = e.Content
                        Category = e.Category
                        Source = e.Source
                        CapturedAt = e.CapturedAt }
                  EvidenceRevised { EvidenceId = e.EvidenceId; Label = e.Label; Source = e.Source; SourceUrl = e.SourceUrl } ])
        | ReviseEvidence e ->
            let blockers =
                match Map.tryFind e.EvidenceId i.Evidence with
                | None -> [ Blockers.notFound "evidence" e.EvidenceId ]
                | Some existing ->
                    [ if existing.SupersededBy.IsSome then Blockers.make "EVIDENCE.SUPERSEDED" "Superseded evidence cannot be revised."
                      yield! urlRule e.SourceUrl ]
            decideWith blockers (fun () -> [ EvidenceRevised { EvidenceId = e.EvidenceId; Label = e.Label; Source = e.Source; SourceUrl = e.SourceUrl } ])
        | RelateEvidence r ->
            let blockers =
                [ yield! fresh "relationship" (Map.containsKey r.RelationshipId i.Relationships) r.RelationshipId
                  yield! evidenceExist i [ r.EvidenceId ]
                  yield! evidenceUsable i [ r.EvidenceId ]
                  yield! hypothesisExists i r.HypothesisId ]
            decideWith blockers (fun () ->
                [ EvidenceRelationshipRecorded
                      { RelationshipId = r.RelationshipId
                        EvidenceId = r.EvidenceId
                        HypothesisId = r.HypothesisId
                        Kind = r.Kind
                        Rationale = r.Rationale } ])
        | IdentifyUnknown u ->
            decideWith (fresh "unknown" (Map.containsKey u.UnknownId i.Unknowns) u.UnknownId) (fun () ->
                [ UnknownIdentified { UnknownId = u.UnknownId; Question = u.Question; Materiality = u.Materiality } ])
        | RecordAssumption a ->
            decideWith (fresh "assumption" (Map.containsKey a.AssumptionId i.Assumptions) a.AssumptionId) (fun () ->
                [ AssumptionRecorded { AssumptionId = a.AssumptionId; Proposition = a.Proposition; Materiality = a.Materiality } ])
        | RecordContradiction c ->
            let blockers =
                fresh "contradiction" (Map.containsKey c.ContradictionId i.Contradictions) c.ContradictionId
                @ distinct "evidence" c.EvidenceIds
                @ evidenceExist i c.EvidenceIds
            decideWith blockers (fun () ->
                [ ContradictionRecorded
                      { ContradictionId = c.ContradictionId
                        Statement = c.Statement
                        EvidenceIds = c.EvidenceIds
                        Materiality = c.Materiality } ])
        | RecordPrediction p ->
            let blockers = fresh "prediction" (Map.containsKey p.PredictionId i.Predictions) p.PredictionId @ hypothesisExists i p.HypothesisId
            decideWith blockers (fun () ->
                [ PredictionRecorded
                      { PredictionId = p.PredictionId
                        HypothesisId = p.HypothesisId
                        Statement = p.Statement
                        Strength = p.Strength
                        Timing = timingFor i p.HypothesisId } ])
        | RecordInvestigationAction a ->
            let blockers =
                fresh "action" (Map.containsKey a.ActionId i.Actions) a.ActionId
                @ distinct "hypothesis" a.TargetHypothesisIds
                @ (a.TargetHypothesisIds |> List.collect (hypothesisExists i))
            decideWith blockers (fun () ->
                [ InvestigationActionRecorded
                      { ActionId = a.ActionId
                        Kind = a.Kind
                        Purpose = a.Purpose
                        TargetHypothesisIds = a.TargetHypothesisIds } ])
        | RecordFinding f ->
            let blockers =
                fresh "finding" (Map.containsKey f.FindingId i.Findings) f.FindingId
                @ (if Map.containsKey f.ActionId i.Actions then [] else [ Blockers.notFound "action" f.ActionId ])
            decideWith blockers (fun () ->
                [ FindingRecorded
                      { FindingId = f.FindingId
                        ActionId = f.ActionId
                        OriginalResult = f.OriginalResult
                        Interpretation = f.Interpretation } ])
        | EstablishNegativeKnowledge n ->
            let blockers =
                [ yield! fresh "negative knowledge" (Map.containsKey n.NegativeKnowledgeId i.NegativeKnowledge) n.NegativeKnowledgeId
                  if n.BasisEvidenceIds.IsEmpty then
                      Blockers.make "NEGATIVE_KNOWLEDGE.EVIDENCE_REQUIRED" "Negative knowledge must cite the evidence it rests on (R5.3)."
                  yield! distinct "evidence" n.BasisEvidenceIds
                  yield! evidenceExist i n.BasisEvidenceIds ]
            decideWith blockers (fun () ->
                [ NegativeKnowledgeEstablished
                      { NegativeKnowledgeId = n.NegativeKnowledgeId
                        Statement = n.Statement
                        Scope = n.Scope
                        BasisEvidenceIds = n.BasisEvidenceIds } ])
        | RecordCausalAssertion c ->
            let blockers =
                [ yield! fresh "cause" (Map.containsKey c.CauseId i.Causes) c.CauseId
                  if c.Members.IsEmpty then Blockers.make "CAUSE.MEMBERS_REQUIRED" "A causal explanation names at least one hypothesis."
                  if List.length (List.distinct c.Members) <> List.length c.Members then
                      Blockers.make "ID.REPEATED" "The same member is listed twice with the same role."
                  for m in c.Members do
                      match Map.tryFind m.HypothesisId i.Hypotheses with
                      | None -> Blockers.notFound "hypothesis" m.HypothesisId
                      | Some h when h.State = HypothesisState.Falsified || h.State = HypothesisState.Superseded ->
                          Blockers.make "CAUSE.MEMBER_EXCLUDED" (sprintf "The hypothesis \"%s\" is %A." h.Statement.Value h.State)
                      | Some _ -> () ]
            decideWith blockers (fun () ->
                [ CausalAssertionRecorded { CauseId = c.CauseId; Outcome = c.Outcome; Members = c.Members; Mechanism = c.Mechanism } ])
        | ConfirmCause c ->
            match NonEmptyText.create policies.Id with
            | None -> Blocked [ Blockers.make "POLICY.UNNAMED" "The policy set has no id, so a confirmation could not name the policy applied." ]
            | Some policy -> decideWith (Policies.causeGate i c.CauseId) (fun () -> [ CauseConfirmed { CauseId = c.CauseId; Policy = policy } ])
        | RecordCorrectiveAction c ->
            let blockers =
                fresh "corrective action" (Map.containsKey c.CorrectiveActionId i.CorrectiveActions) c.CorrectiveActionId
                @ distinct "cause" c.TargetCauseIds
                @ (c.TargetCauseIds |> List.filter (fun id -> not (Map.containsKey id i.Causes)) |> List.map (fun id -> Blockers.notFound "cause" id))
            decideWith blockers (fun () ->
                [ CorrectiveActionRecorded
                      { CorrectiveActionId = c.CorrectiveActionId
                        Kind = c.Kind
                        Description = c.Description
                        TargetCauseIds = c.TargetCauseIds } ])
        | RecordVerificationCriterion c ->
            let blockers =
                fresh "criterion" (Map.containsKey c.CriterionId i.Criteria) c.CriterionId
                @ (if Map.containsKey c.CorrectiveActionId i.CorrectiveActions then [] else [ Blockers.notFound "corrective action" c.CorrectiveActionId ])
            decideWith blockers (fun () ->
                [ VerificationCriterionRecorded
                      { CriterionId = c.CriterionId
                        CorrectiveActionId = c.CorrectiveActionId
                        ExpectedOutcome = c.ExpectedOutcome
                        Method = c.Method } ])
        | MarkCorrectiveActionImplemented id ->
            match Map.tryFind id i.CorrectiveActions with
            | None -> Blocked [ Blockers.notFound "corrective action" id ]
            | Some c when c.State = CorrectiveActionState.CorrectionInProgress -> core [ CorrectiveActionImplemented id ]
            | Some c ->
                Blocked [ Blockers.make "CORRECTIVE_ACTION.ILLEGAL_TRANSITION" (sprintf "Only an in-progress corrective action can be marked implemented; this one is %A." c.State) ]
        | RecordVerification v ->
            let blockers =
                [ yield! fresh "verification" (Map.containsKey v.VerificationId i.Verifications) v.VerificationId
                  if not (Map.containsKey v.CriterionId i.Criteria) then Blockers.notFound "criterion" v.CriterionId
                  if v.Outcome = VerificationOutcome.Satisfied && v.EvidenceIds.IsEmpty then
                      Blockers.make "VERIFICATION.EVIDENCE_REQUIRED" "A satisfied verification must cite evidence (R7.4)."
                  yield! distinct "evidence" v.EvidenceIds
                  yield! evidenceExist i v.EvidenceIds ]
            decideWith blockers (fun () ->
                [ VerificationRecorded
                      { VerificationId = v.VerificationId
                        CriterionId = v.CriterionId
                        Outcome = v.Outcome
                        EvidenceIds = v.EvidenceIds } ])
        | RecordConclusion c ->
            let openUncertainty =
                i.Unknowns |> Map.exists (fun _ u -> u.State = UnknownState.Identified || u.State = UnknownState.Investigating)
            let blockers =
                [ yield! fresh "conclusion" (Map.containsKey c.ConclusionId i.Conclusions) c.ConclusionId
                  yield! distinct "cause" c.CauseIds
                  for id in c.CauseIds do
                      if not (Map.containsKey id i.Causes) then Blockers.notFound "cause" id
                  if c.Strength = ConclusionStrength.ConfirmedCause
                     && (c.CauseIds.IsEmpty
                         || c.CauseIds |> List.exists (fun id -> match Map.tryFind id i.Causes with Some x -> x.State <> CauseState.CauseConfirmed | None -> true)) then
                      Blockers.make "CONCLUSION.CAUSE_NOT_CONFIRMED" "A confirmed-cause conclusion names only confirmed causes (R6.1)."
                  if c.ResidualUncertainty.IsNone && (openUncertainty || c.Strength = ConclusionStrength.AcceptedWithResidualUncertainty) then
                      Blockers.make "CONCLUSION.RESIDUAL_UNCERTAINTY_REQUIRED" "Open unknowns remain, so the conclusion must state its residual uncertainty (R6.4)." ]
            decideWith blockers (fun () ->
                [ ConclusionRecorded
                      { ConclusionId = c.ConclusionId
                        Statement = c.Statement
                        Strength = c.Strength
                        CauseIds = c.CauseIds
                        ResidualUncertainty = c.ResidualUncertainty } ])
        | RecordProposal p ->
            decideWith (fresh "proposal" (Map.containsKey p.ProposalId i.Proposals) p.ProposalId) (fun () ->
                [ ProposalRecorded { ProposalId = p.ProposalId; Kind = p.Kind; Text = p.Text; Rationale = p.Rationale } ])
        | AcceptProposal id ->
            // The entity the acceptance creates takes the command id as its id, so the decision stays pure and the
            // ProposalWasAccepted targetId names the preceding event of the same group.
            match Map.tryFind id i.Proposals with
            | None -> Blocked [ Blockers.notFound "proposal" id ]
            | Some p when p.State <> ProposalState.ProposalActive -> Blocked [ Blockers.make "PROPOSAL.NOT_ACTIVE" (sprintf "The proposal is already %A." p.State) ]
            | Some p ->
                match p.Kind with
                | ProposalKind.ProposedHypothesis ->
                    let target = HypothesisId commandId
                    decideWith
                        (fresh "hypothesis" (Map.containsKey target i.Hypotheses) commandId @ Policies.repetition i p.Text)
                        (fun () -> [ HypothesisRecorded { HypothesisId = target; Statement = p.Text; Mechanism = None }; ProposalAccepted(id, commandId) ])
                | ProposalKind.ProposedUnknown
                | ProposalKind.ProposedAction ->
                    Blocked
                        [ Blockers.make
                              "PROPOSAL.DETAILS_REQUIRED"
                              "Accepting this proposal needs details it does not carry (an unknown's materiality or an action's kind); record the unknown or action directly." ]
        | RejectProposal(id, reason) ->
            match Map.tryFind id i.Proposals with
            | None -> Blocked [ Blockers.notFound "proposal" id ]
            | Some p when p.State = ProposalState.ProposalActive -> core [ ProposalRejected(id, reason) ]
            | Some p -> Blocked [ Blockers.make "PROPOSAL.NOT_ACTIVE" (sprintf "The proposal is already %A." p.State) ]

    /// The command's id already produced events: the command is a repeat and produces nothing new.
    let private repeated (i: Investigation) (commandId: Guid) =
        i.Timeline |> List.filter (fun e -> e.Provenance.CorrelationId = commandId) |> List.map (fun e -> e.EventId)

    let decide (policies: PolicySet) (_actor: ActorContext) (state: Investigation option) (envelope: CommandEnvelope) : Decision =
        match envelope.Command, state with
        | Extension(SemanticId semantic, _), _ -> UnsupportedSemantics [ semantic ]
        | Core(StartInvestigation s), None ->
            match envelope.ExpectedVersion with
            | Some(AggregateVersion v) when v <> 0L -> Conflict(AggregateVersion v, AggregateVersion 0L)
            | _ -> core [ InvestigationStarted { Title = s.Title; ObservationId = s.ObservationId; Original = s.Original } ]
        | Core _, None ->
            Blocked [ Blockers.make "INVESTIGATION.NOT_STARTED" "The investigation does not exist; start it first." ]
        | Core command, Some i ->
            if envelope.Investigation <> i.Id then
                Blocked [ Blockers.make "INVESTIGATION.MISMATCH" "The command names another investigation." ]
            else
                match repeated i envelope.CommandId with
                | _ :: _ as ids -> Duplicate ids
                | [] ->
                    match envelope.ExpectedVersion with
                    | Some expected when expected <> i.Version -> Conflict(expected, i.Version)
                    | _ -> openInvestigation policies envelope.CommandId i command
