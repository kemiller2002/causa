// Owner: domain (T13). The storage semantics the kernel's values carry on the wire: enumeration wire values
// (contract v1.0.0 section 6, v1.1.0 section 4.3), the event registry and the engine command strings (section 7,
// FIELD-MAPPING), the storage version an event needs (v1.1.0 sections 2-3) and event identity (section 5.2).
// Pure data and functions; the byte encoding itself is the persistence role's (IF41-STORAGE).
namespace Causa.Domain

open System
open System.Security.Cryptography
open System.Text

/// A bidirectional, exhaustive mapping between a kernel enumeration and its wire values.
type WireEnum<'T when 'T: equality> =
    { Name: string
      Pairs: ('T * string) list }

[<RequireQualifiedAccess>]
module WireEnum =
    let toWire (e: WireEnum<'T>) (value: 'T) : string =
        e.Pairs |> List.find (fun (v, _) -> v = value) |> snd

    /// Wire values are matched exactly (case-sensitive); anything else is refused, never mapped to a default.
    let ofWire (e: WireEnum<'T>) (text: string) : 'T option =
        e.Pairs |> List.tryFind (fun (_, w) -> String.Equals(w, text, StringComparison.Ordinal)) |> Option.map fst

[<RequireQualifiedAccess>]
module Wire =
    let lifecycle =
        { Name = "lifecycle"
          Pairs =
            [ Lifecycle.Draft, "draft"
              Lifecycle.Active, "active"
              Lifecycle.AwaitingEvidence, "awaitingEvidence"
              Lifecycle.AwaitingVerification, "awaitingVerification"
              Lifecycle.Resolved, "resolved"
              Lifecycle.Closed, "closed"
              Lifecycle.Archived, "archived" ] }

    let hypothesisState =
        { Name = "hypothesisState"
          Pairs =
            [ HypothesisState.Proposed, "proposed"
              HypothesisState.Plausible, "plausible"
              HypothesisState.Supported, "supported"
              HypothesisState.Weakened, "weakened"
              HypothesisState.Falsified, "falsified"
              HypothesisState.Superseded, "superseded" ] }

    let unknownState =
        { Name = "unknownState"
          Pairs =
            [ UnknownState.Identified, "identified"
              UnknownState.Investigating, "investigating"
              UnknownState.UnknownResolved, "resolved"
              UnknownState.UnknownAcceptedResidual, "acceptedResidual"
              UnknownState.UnknownSuperseded, "superseded" ] }

    let materiality =
        { Name = "materiality"
          Pairs =
            [ Materiality.Blocking, "blocking"
              Materiality.Material, "material"
              Materiality.NonMaterial, "nonMaterial"
              Materiality.Informational, "informational" ] }

    let assumptionState =
        { Name = "assumptionState"
          Pairs =
            [ AssumptionState.ExplicitUnverified, "explicitUnverified"
              AssumptionState.ProvisionallyAccepted, "provisionallyAccepted"
              AssumptionState.AssumptionVerified, "verified"
              AssumptionState.AssumptionRejected, "rejected"
              AssumptionState.AssumptionExpired, "expired" ] }

    let contradictionState =
        { Name = "contradictionState"
          Pairs =
            [ ContradictionState.Detected, "detected"
              ContradictionState.Triaged, "triaged"
              ContradictionState.ContradictionInvestigating, "investigating"
              ContradictionState.ContradictionResolved, "resolved"
              ContradictionState.ContradictionAcceptedResidual, "acceptedResidual"
              ContradictionState.ContradictionSuperseded, "superseded" ] }

    let actionState =
        { Name = "actionState"
          Pairs =
            [ ActionState.ActionProposed, "proposed"
              ActionState.ActionApproved, "approved"
              ActionState.ActionInProgress, "inProgress"
              ActionState.ActionCompleted, "completed"
              ActionState.ActionInterpreted, "interpreted"
              ActionState.ActionRejected, "rejected"
              ActionState.ActionCancelled, "cancelled" ] }

    let actionKind =
        { Name = "actionKind"
          Pairs =
            [ ActionKind.Test, "test"
              ActionKind.Inspection, "inspection"
              ActionKind.Query, "query"
              ActionKind.Comparison, "comparison"
              ActionKind.Experiment, "experiment" ] }

    let causeState =
        { Name = "causeState"
          Pairs =
            [ CauseState.CauseProposed, "proposed"
              CauseState.CauseConfirmed, "confirmed"
              CauseState.CauseReopened, "reopened"
              CauseState.CauseSuperseded, "superseded" ] }

    let causeRole =
        { Name = "causeRole"
          Pairs =
            [ CauseRole.RootCause, "rootCause"
              CauseRole.ContributingFactor, "contributingFactor"
              CauseRole.Trigger, "trigger"
              CauseRole.EnablingCondition, "enablingCondition" ] }

    let correctiveActionState =
        { Name = "correctiveActionState"
          Pairs =
            [ CorrectiveActionState.CorrectionProposed, "proposed"
              CorrectiveActionState.CorrectionApproved, "approved"
              CorrectiveActionState.CorrectionInProgress, "inProgress"
              CorrectiveActionState.CorrectionImplemented, "implemented"
              CorrectiveActionState.CorrectionAwaitingVerificationOfEffect, "awaitingVerificationOfEffect"
              CorrectiveActionState.CorrectionVerifiedEffective, "verifiedEffective"
              CorrectiveActionState.CorrectionVerificationFailed, "verificationFailed"
              CorrectiveActionState.CorrectionReworkRequired, "reworkRequired"
              CorrectiveActionState.CorrectionCancelled, "cancelled" ] }

    let correctiveActionKind =
        { Name = "correctiveActionKind"
          Pairs =
            [ CorrectiveActionKind.Mitigation, "mitigation"
              CorrectiveActionKind.Workaround, "workaround"
              CorrectiveActionKind.Correction, "correction"
              CorrectiveActionKind.Prevention, "prevention" ] }

    let evidenceCategory =
        { Name = "evidenceCategory"
          Pairs =
            [ EvidenceCategory.DirectObservation, "directObservation"
              EvidenceCategory.Measurement, "measurement"
              EvidenceCategory.Log, "log"
              EvidenceCategory.Document, "document"
              EvidenceCategory.ExperimentResult, "experimentResult"
              EvidenceCategory.Testimony, "testimony"
              EvidenceCategory.ExternalReference, "externalReference" ] }

    let verificationStatus =
        { Name = "verificationStatus"
          Pairs =
            [ VerificationStatus.Unverified, "unverified"
              VerificationStatus.PartiallyVerified, "partiallyVerified"
              VerificationStatus.Verified, "verified"
              VerificationStatus.Invalidated, "invalidated" ] }

    /// The 1.1.0 enumeration; consistentWith and inconclusive exist only in 1.1.0 data.
    let relationshipKind =
        { Name = "relationshipKind"
          Pairs =
            [ RelationshipKind.Supports, "supports"
              RelationshipKind.Contradicts, "contradicts"
              RelationshipKind.Discriminates, "discriminates"
              RelationshipKind.Insufficient, "insufficient"
              RelationshipKind.ConsistentWith, "consistentWith"
              RelationshipKind.Inconclusive, "inconclusive" ] }

    let predictionStrength =
        { Name = "predictionStrength"
          Pairs =
            [ PredictionStrength.Necessary, "necessary"
              PredictionStrength.Expected, "expected"
              PredictionStrength.DiscriminatingPrediction, "discriminating"
              PredictionStrength.NonDiscriminating, "nonDiscriminating" ] }

    let predictionTiming =
        { Name = "predictionTiming"
          Pairs = [ PredictionTiming.Prospective, "prospective"; PredictionTiming.Retrospective, "retrospective" ] }

    let verificationOutcome =
        { Name = "verificationOutcome"
          Pairs =
            [ VerificationOutcome.Satisfied, "satisfied"
              VerificationOutcome.Failed, "failed"
              VerificationOutcome.Inconclusive, "inconclusive" ] }

    let conclusionStrength =
        { Name = "conclusionStrength"
          Pairs =
            [ ConclusionStrength.PlausibleExplanation, "plausibleExplanation"
              ConclusionStrength.LeadingExplanation, "leadingExplanation"
              ConclusionStrength.SupportedCause, "supportedCause"
              ConclusionStrength.ConfirmedCause, "confirmedCause"
              ConclusionStrength.AcceptedWithResidualUncertainty, "acceptedWithResidualUncertainty" ] }

    let proposalKind =
        { Name = "proposalKind"
          Pairs =
            [ ProposalKind.ProposedHypothesis, "hypothesis"
              ProposalKind.ProposedUnknown, "unknown"
              ProposalKind.ProposedAction, "action" ] }

    let proposalState =
        { Name = "proposalState"
          Pairs =
            [ ProposalState.ProposalActive, "active"
              ProposalState.ProposalAccepted, "accepted"
              ProposalState.ProposalRejected, "rejected" ] }

    let contentKind =
        { Name = "contentKind"
          Pairs = [ ContentKind.Text, "text"; ContentKind.Artifact, "artifact" ] }

/// The stored form of one CoreEvent: its registered eventType and, for the two transition events that carry it, the
/// engine command (lifecycleCommand / hypothesisCommand wire values) and the target state.
type WireEventKind =
    { EventType: string
      Command: string option
      Target: string option }

[<RequireQualifiedAccess>]
module StorageVersion =
    let v100 = "1.0.0"
    let v110 = "1.1.0"
    /// causa-storage 1.2.0: InvestigationTitleRevised and UnknownRevised (owner-approved). New investigations use it.
    let v120 = "1.2.0"

    /// The versions in order: an investigation of a later version can hold every event an earlier one can.
    let ordered = [ v100; v110; v120 ]

    /// The position of a version in `ordered`; None for a version this build does not know.
    let rank (version: string) = ordered |> List.tryFindIndex (fun v -> v = version)

[<RequireQualifiedAccess>]
module EventRegistry =
    /// v1.0.0 section 7 (31 types), then the two v1.1.0 types (v1.1.0 section 4.4).
    let v100Types =
        [ "InvestigationStarted"; "LifecycleChanged"; "ObservationRecorded"; "HypothesisRecorded"; "HypothesisTransitioned"
          "EvidenceAdmitted"; "EvidenceSuperseded"; "EvidenceVerificationAssessed"; "EvidenceRelated"; "UnknownIdentified"
          "UnknownTransitioned"; "AssumptionRecorded"; "AssumptionTransitioned"; "ContradictionRecorded"
          "ContradictionTransitioned"; "PredictionRecorded"; "ActionRecorded"; "ActionTransitioned"; "FindingRecorded"
          "NegativeKnowledgeEstablished"; "CausalAssertionRecorded"; "CausalAssertionConfirmed"; "CausalAssertionReopened"
          "CorrectiveActionRecorded"; "CorrectiveActionTransitioned"; "CriterionRecorded"; "VerificationRecorded"
          "ConclusionRecorded"; "ProposalRecorded"; "ProposalWasAccepted"; "ProposalWasRejected" ]

    let v110Types = v100Types @ [ "EvidenceRevised"; "ObservationRevised" ]

    /// v1.2.0 section 4.3: the 33 of v1.1.0, then the two revisions (35 types).
    let v120Types = v110Types @ [ "InvestigationTitleRevised"; "UnknownRevised" ]

    let private lifecycleChange command target =
        Some { EventType = "LifecycleChanged"; Command = Some command; Target = Some target }

    let private hypothesisChange command target =
        Some { EventType = "HypothesisTransitioned"; Command = Some command; Target = Some target }

    let private plain eventType = Some { EventType = eventType; Command = None; Target = None }

    let private correction (state: CorrectiveActionState) =
        Some { EventType = "CorrectiveActionTransitioned"; Command = None; Target = Some(WireEnum.toWire Wire.correctiveActionState state) }

    /// The wire form of a core event, or None when causa-storage v1 cannot store it (CAUSA.UNREPRESENTABLE).
    let kindOf (event: CoreEvent) : WireEventKind option =
        match event with
        | InvestigationStarted _ -> plain "InvestigationStarted"
        | InvestigationActivated _ -> lifecycleChange "activateInvestigation" "active"
        | InvestigationMarkedAwaitingEvidence _ -> lifecycleChange "markAwaitingEvidence" "awaitingEvidence"
        | InvestigationMarkedAwaitingVerification _ -> lifecycleChange "markAwaitingVerification" "awaitingVerification"
        | InvestigationResolved _ -> lifecycleChange "resolveInvestigation" "resolved"
        | InvestigationClosed _ -> lifecycleChange "closeInvestigation" "closed"
        | InvestigationReopened _ -> lifecycleChange "reopenInvestigation" "active"
        | InvestigationVerificationFailed _ -> lifecycleChange "verificationFailed" "active"
        | ObservationRecorded _ -> plain "ObservationRecorded"
        | ObservationRevised _ -> plain "ObservationRevised"
        | HypothesisRecorded _ -> plain "HypothesisRecorded"
        | HypothesisMarkedPlausible _ -> hypothesisChange "markPlausible" "plausible"
        | HypothesisMarkedSupported _ -> hypothesisChange "markSupported" "supported"
        | HypothesisWeakened _ -> hypothesisChange "markWeakened" "weakened"
        | HypothesisFalsified _ -> hypothesisChange "falsify" "falsified"
        | HypothesisSuperseded _ -> hypothesisChange "supersede" "superseded"
        | HypothesisReopened _ -> hypothesisChange "reopen" "plausible"
        | EvidenceAdmitted _ -> plain "EvidenceAdmitted"
        | EvidenceSuperseded _ -> plain "EvidenceSuperseded"
        | EvidenceVerificationAssessed _ -> plain "EvidenceVerificationAssessed"
        | EvidenceRevised _ -> plain "EvidenceRevised"
        | EvidenceRelationshipRecorded _ -> plain "EvidenceRelated"
        | UnknownIdentified _ -> plain "UnknownIdentified"
        | UnknownTransitioned _ -> plain "UnknownTransitioned"
        | AssumptionRecorded _ -> plain "AssumptionRecorded"
        | AssumptionTransitioned _ -> plain "AssumptionTransitioned"
        | ContradictionRecorded _ -> plain "ContradictionRecorded"
        | ContradictionTransitioned _ -> plain "ContradictionTransitioned"
        | PredictionRecorded _ -> plain "PredictionRecorded"
        | InvestigationActionRecorded _ -> plain "ActionRecorded"
        | InvestigationActionTransitioned _ -> plain "ActionTransitioned"
        | FindingRecorded _ -> plain "FindingRecorded"
        | NegativeKnowledgeEstablished _ -> plain "NegativeKnowledgeEstablished"
        | CausalAssertionRecorded _ -> plain "CausalAssertionRecorded"
        | CauseConfirmed _ -> plain "CausalAssertionConfirmed"
        | ConfirmedCauseReopened _ -> plain "CausalAssertionReopened"
        | CorrectiveActionRecorded _ -> plain "CorrectiveActionRecorded"
        | CorrectiveActionApproved _ -> correction CorrectiveActionState.CorrectionApproved
        | CorrectiveActionStarted _ -> correction CorrectiveActionState.CorrectionInProgress
        | CorrectiveActionImplemented _ -> correction CorrectiveActionState.CorrectionImplemented
        | CorrectiveActionAwaitingVerificationOfEffect _ -> correction CorrectiveActionState.CorrectionAwaitingVerificationOfEffect
        | CorrectiveActionVerifiedEffective _ -> correction CorrectiveActionState.CorrectionVerifiedEffective
        | CorrectiveActionVerificationFailed _ -> correction CorrectiveActionState.CorrectionVerificationFailed
        | CorrectiveActionReworkRequired _ -> correction CorrectiveActionState.CorrectionReworkRequired
        | CorrectiveActionCancelled _ -> correction CorrectiveActionState.CorrectionCancelled
        | VerificationCriterionRecorded _ -> plain "CriterionRecorded"
        | VerificationRecorded _ -> plain "VerificationRecorded"
        | ConclusionRecorded _ -> plain "ConclusionRecorded"
        | ProposalRecorded _ -> plain "ProposalRecorded"
        | ProposalAccepted _ -> plain "ProposalWasAccepted"
        | ProposalRejected _ -> plain "ProposalWasRejected"
        | ExtensionAttached _ -> None
        | InvestigationTitleRevised _ -> plain "InvestigationTitleRevised"
        | UnknownRevised _ -> plain "UnknownRevised"

    /// The oldest storage version that can hold the event: the evidence and observation revisions and the two new
    /// relationship kinds need 1.1.0; the title and unknown revisions need 1.2.0.
    let requiredVersion (event: CoreEvent) : string option =
        match event with
        | ExtensionAttached _ -> None
        | InvestigationTitleRevised _
        | UnknownRevised _ -> Some StorageVersion.v120
        | EvidenceRevised _
        | ObservationRevised _ -> Some StorageVersion.v110
        | EvidenceRelationshipRecorded r when r.Kind = RelationshipKind.ConsistentWith || r.Kind = RelationshipKind.Inconclusive ->
            Some StorageVersion.v110
        | _ -> Some StorageVersion.v100

    /// Whether the event can be appended to an investigation stored in the given version (S-6 A: an investigation keeps
    /// the version it was created in; a 1.0.0 one refuses an event that needs 1.1.0, a 1.1.0 one an event that needs
    /// 1.2.0). An unknown version holds nothing.
    let representableIn (storageVersion: string) (event: CoreEvent) =
        match requiredVersion event |> Option.bind StorageVersion.rank, StorageVersion.rank storageVersion with
        | Some needed, Some stored -> needed <= stored
        | _ -> false

/// Event identity (contract v1.0.0 section 5.2): UUIDv5(N, investigationId + "/" + aggregateVersion + "/" + correlationId).
[<RequireQualifiedAccess>]
module EventIdentity =
    /// RFC 4122 network byte order of a Guid.
    let private toBytes (g: Guid) =
        let hex = g.ToString "N"
        Array.init 16 (fun i -> Convert.ToByte(hex.Substring(i * 2, 2), 16))

    let private ofBytes (b: byte[]) =
        Guid(BitConverter.ToString(b).Replace("-", "").ToLowerInvariant())

    /// RFC 4122 name-based UUID, version 5 (SHA-1).
    let uuidV5 (ns: Guid) (name: string) : Guid =
        let hash = SHA1.HashData(Array.append (toBytes ns) (Encoding.UTF8.GetBytes name))
        let b = Array.sub hash 0 16
        b.[6] <- (b.[6] &&& 0x0Fuy) ||| 0x50uy
        b.[8] <- (b.[8] &&& 0x3Fuy) ||| 0x80uy
        ofBytes b

    let namespaceUrl = Guid "6ba7b811-9dad-11d1-80b4-00c04fd430c8"

    /// N = UUIDv5(NAMESPACE_URL, "https://github.com/kemiller2002/causa-data/contract/v1/event-id").
    let eventNamespace = uuidV5 namespaceUrl "https://github.com/kemiller2002/causa-data/contract/v1/event-id"

    let canonical (g: Guid) = g.ToString("D").ToLowerInvariant()

    let derive (InvestigationId investigation) (AggregateVersion version) (correlationId: Guid) : EventId =
        EventId(uuidV5 eventNamespace (canonical investigation + "/" + string version + "/" + canonical correlationId))
