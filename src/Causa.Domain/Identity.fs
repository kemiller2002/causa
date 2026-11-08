// Owner: domain (T13). IF41-DOMAIN, Boundary A: the kernel's values. The command and event unions, the envelopes and
// Decision are the frozen contract (unchanged in shape); the payload types the bootstrap left opaque are defined here
// with exactly the fields of their causa-storage wire records (contract/v1.0.0 section 7, v1.1.0 section 4), so that
// encoding then decoding any stored event reproduces it. Pure: no clock, randomness, network or provider types.
namespace Causa.Domain

open System

type InvestigationId = InvestigationId of Guid
type EventId = EventId of Guid
type AggregateVersion = AggregateVersion of int64
type HypothesisId = HypothesisId of Guid
type CorrectiveActionId = CorrectiveActionId of Guid
type ProposalId = ProposalId of Guid
type CausalAssertionId = CausalAssertionId of Guid

/// Wire `text`: non-blank, kept verbatim. The only constructor refuses blank or whitespace-only text.
[<Struct; CustomEquality; CustomComparison>]
type NonEmptyText =
    private
    | NonEmptyText of string
    member t.Value = let (NonEmptyText s) = t in s
    override t.ToString() = t.Value
    override t.Equals(o) =
        match o with
        | :? NonEmptyText as other -> String.Equals(t.Value, other.Value, StringComparison.Ordinal)
        | _ -> false
    override t.GetHashCode() = StringComparer.Ordinal.GetHashCode t.Value
    interface IComparable with
        member t.CompareTo(o) =
            match o with
            | :? NonEmptyText as other -> String.CompareOrdinal(t.Value, other.Value)
            | _ -> invalidArg "o" "not a NonEmptyText"

[<RequireQualifiedAccess>]
module NonEmptyText =
    let create (text: string) : NonEmptyText option =
        if String.IsNullOrWhiteSpace text then None else Some(NonEmptyText text)

    let value (t: NonEmptyText) = t.Value

/// A semantic identity of an extension (namespace/name@version), never interpreted by the core.
type SemanticId = SemanticId of string

type ExtensionEnvelope = { Semantic: SemanticId; PayloadJson: string }

// ---- Enumerations (contract v1.0.0 section 6; relationship kinds extended by v1.1.0 section 4.3) ----------------

[<RequireQualifiedAccess>]
type Lifecycle =
    | Draft
    | Active
    | AwaitingEvidence
    | AwaitingVerification
    | Resolved
    | Closed
    | Archived

[<RequireQualifiedAccess>]
type HypothesisState =
    | Proposed
    | Plausible
    | Supported
    | Weakened
    | Falsified
    | Superseded

[<RequireQualifiedAccess>]
type UnknownState =
    | Identified
    | Investigating
    | UnknownResolved
    | UnknownAcceptedResidual
    | UnknownSuperseded

[<RequireQualifiedAccess>]
type Materiality =
    | Blocking
    | Material
    | NonMaterial
    | Informational

[<RequireQualifiedAccess>]
type AssumptionState =
    | ExplicitUnverified
    | ProvisionallyAccepted
    | AssumptionVerified
    | AssumptionRejected
    | AssumptionExpired

[<RequireQualifiedAccess>]
type ContradictionState =
    | Detected
    | Triaged
    | ContradictionInvestigating
    | ContradictionResolved
    | ContradictionAcceptedResidual
    | ContradictionSuperseded

[<RequireQualifiedAccess>]
type ActionState =
    | ActionProposed
    | ActionApproved
    | ActionInProgress
    | ActionCompleted
    | ActionInterpreted
    | ActionRejected
    | ActionCancelled

[<RequireQualifiedAccess>]
type ActionKind =
    | Test
    | Inspection
    | Query
    | Comparison
    | Experiment

[<RequireQualifiedAccess>]
type CauseState =
    | CauseProposed
    | CauseConfirmed
    | CauseReopened
    | CauseSuperseded

[<RequireQualifiedAccess>]
type CauseRole =
    | RootCause
    | ContributingFactor
    | Trigger
    | EnablingCondition

[<RequireQualifiedAccess>]
type CorrectiveActionState =
    | CorrectionProposed
    | CorrectionApproved
    | CorrectionInProgress
    | CorrectionImplemented
    | CorrectionAwaitingVerificationOfEffect
    | CorrectionVerifiedEffective
    | CorrectionVerificationFailed
    | CorrectionReworkRequired
    | CorrectionCancelled

[<RequireQualifiedAccess>]
type CorrectiveActionKind =
    | Mitigation
    | Workaround
    | Correction
    | Prevention

[<RequireQualifiedAccess>]
type EvidenceCategory =
    | DirectObservation
    | Measurement
    | Log
    | Document
    | ExperimentResult
    | Testimony
    | ExternalReference

[<RequireQualifiedAccess>]
type VerificationStatus =
    | Unverified
    | PartiallyVerified
    | Verified
    | Invalidated

[<RequireQualifiedAccess>]
type RelationshipKind =
    | Supports
    | Contradicts
    | Discriminates
    | Insufficient
    /// causa-storage 1.1.0 only; never support.
    | ConsistentWith
    /// causa-storage 1.1.0 only; never support.
    | Inconclusive

[<RequireQualifiedAccess>]
type PredictionStrength =
    | Necessary
    | Expected
    | DiscriminatingPrediction
    | NonDiscriminating

[<RequireQualifiedAccess>]
type PredictionTiming =
    | Prospective
    | Retrospective

[<RequireQualifiedAccess>]
type VerificationOutcome =
    | Satisfied
    | Failed
    | Inconclusive

[<RequireQualifiedAccess>]
type ConclusionStrength =
    | PlausibleExplanation
    | LeadingExplanation
    | SupportedCause
    | ConfirmedCause
    | AcceptedWithResidualUncertainty

[<RequireQualifiedAccess>]
type ProposalKind =
    | ProposedHypothesis
    | ProposedUnknown
    | ProposedAction

[<RequireQualifiedAccess>]
type ProposalState =
    | ProposalActive
    | ProposalAccepted
    | ProposalRejected

[<RequireQualifiedAccess>]
type ContentKind =
    | Text
    | Artifact

/// Envelope actor (contract v1.0.0 section 5.3). An agent may appear only on ProposalRecorded (D-004).
[<RequireQualifiedAccess>]
type Actor =
    | Human of actorId: NonEmptyText
    | System of actorId: NonEmptyText
    | Agent of actorId: NonEmptyText * provider: NonEmptyText * model: NonEmptyText

// ---- Event payloads: exactly the wire records --------------------------------------------------------------------

type InvestigationStarted = { Title: NonEmptyText; ObservationId: Guid; Original: NonEmptyText }
type Observation = { ObservationId: Guid; Original: NonEmptyText; ObservedAt: DateTimeOffset option }
type ObservationRevision = { ObservationId: Guid; Original: NonEmptyText; Source: NonEmptyText option }
type Hypothesis = { HypothesisId: HypothesisId; Statement: NonEmptyText; Mechanism: NonEmptyText option }

/// HypothesisTransitioned without `to` and `command`, which the CoreEvent case determines (FIELD-MAPPING).
type HypothesisTransition =
    { HypothesisId: HypothesisId
      From: HypothesisState
      Reason: NonEmptyText
      EvidenceIds: Guid list }

type Evidence =
    { EvidenceId: Guid
      ContentKind: ContentKind
      Content: NonEmptyText
      Category: EvidenceCategory
      Source: NonEmptyText
      CapturedAt: DateTimeOffset option }

type EvidenceSupersession = { EvidenceId: Guid; ReplacementEvidenceId: Guid; Reason: NonEmptyText }
type EvidenceVerificationAssessment = { EvidenceId: Guid; Verification: VerificationStatus }
type EvidenceRevision = { EvidenceId: Guid; Label: NonEmptyText; Source: NonEmptyText; SourceUrl: string option }

type EvidenceRelationship =
    { RelationshipId: Guid
      EvidenceId: Guid
      HypothesisId: HypothesisId
      Kind: RelationshipKind
      Rationale: NonEmptyText option }

type Unknown = { UnknownId: Guid; Question: NonEmptyText; Materiality: Materiality }
type UnknownTransition = { UnknownId: Guid; State: UnknownState; Basis: NonEmptyText }
/// causa-storage 1.2.0 InvestigationTitleRevised {title, reason}: the title kept verbatim, with an optional reason.
type InvestigationTitleRevision = { Title: NonEmptyText; Reason: NonEmptyText option }
/// causa-storage 1.2.0 UnknownRevised {unknownId, question}: the question revised, never superseded (the id is kept).
type UnknownRevision = { UnknownId: Guid; Question: NonEmptyText }
type Assumption = { AssumptionId: Guid; Proposition: NonEmptyText; Materiality: Materiality }
type AssumptionTransition = { AssumptionId: Guid; State: AssumptionState }

type Contradiction =
    { ContradictionId: Guid
      Statement: NonEmptyText
      EvidenceIds: Guid list
      Materiality: Materiality }

type ContradictionTransition = { ContradictionId: Guid; State: ContradictionState; Basis: NonEmptyText }

type Prediction =
    { PredictionId: Guid
      HypothesisId: HypothesisId
      Statement: NonEmptyText
      Strength: PredictionStrength
      Timing: PredictionTiming }

type InvestigationAction =
    { ActionId: Guid
      Kind: ActionKind
      Purpose: NonEmptyText
      TargetHypothesisIds: HypothesisId list }

type InvestigationActionTransition = { ActionId: Guid; State: ActionState }
type Finding = { FindingId: Guid; ActionId: Guid; OriginalResult: NonEmptyText; Interpretation: NonEmptyText option }

type NegativeKnowledge =
    { NegativeKnowledgeId: Guid
      Statement: NonEmptyText
      Scope: NonEmptyText
      BasisEvidenceIds: Guid list }

type CausalMember = { HypothesisId: HypothesisId; Role: CauseRole }

type CausalAssertion =
    { CauseId: CausalAssertionId
      Outcome: NonEmptyText
      Members: CausalMember list
      Mechanism: NonEmptyText option }

/// CausalAssertionConfirmed: `policy` is the policy id the cause gate applied.
type CauseConfirmation = { CauseId: CausalAssertionId; Policy: NonEmptyText }

type CorrectiveAction =
    { CorrectiveActionId: CorrectiveActionId
      Kind: CorrectiveActionKind
      Description: NonEmptyText
      TargetCauseIds: CausalAssertionId list }

type VerificationCriterion =
    { CriterionId: Guid
      CorrectiveActionId: CorrectiveActionId
      ExpectedOutcome: NonEmptyText
      Method: NonEmptyText option }

type Verification = { VerificationId: Guid; CriterionId: Guid; Outcome: VerificationOutcome; EvidenceIds: Guid list }

type Conclusion =
    { ConclusionId: Guid
      Statement: NonEmptyText
      Strength: ConclusionStrength
      CauseIds: CausalAssertionId list
      ResidualUncertainty: NonEmptyText option }

type Proposal = { ProposalId: ProposalId; Kind: ProposalKind; Text: NonEmptyText; Rationale: NonEmptyText option }

/// A scoped statement (FSHARP-TYPE-SHAPES section 6); no stored event carries one in causa-storage v1.
type Scope = { Statement: NonEmptyText }

// ---- Command payloads: what the user entered. Ids are chosen by the caller, so the kernel stays pure --------------

type StartInvestigation = { Title: NonEmptyText; ObservationId: Guid; Original: NonEmptyText }
/// A source, when entered, travels in an ObservationRevised of the same group (v1.1.0 section 4.5).
type RecordObservation = { ObservationId: Guid; Original: NonEmptyText; ObservedAt: DateTimeOffset option; Source: NonEmptyText option }
type ReviseObservation = { ObservationId: Guid; Original: NonEmptyText; Source: NonEmptyText option }
type RecordHypothesis = { HypothesisId: HypothesisId; Statement: NonEmptyText; Mechanism: NonEmptyText option }
type MarkHypothesis = { HypothesisId: HypothesisId; Reason: NonEmptyText; EvidenceIds: Guid list }
type FalsifyHypothesis = { HypothesisId: HypothesisId; Reason: NonEmptyText; EvidenceIds: Guid list }
type ReopenHypothesis = { HypothesisId: HypothesisId; Reason: NonEmptyText; EvidenceIds: Guid list }

/// AdmitEvidence emits EvidenceAdmitted then EvidenceRevised with the label and URL (v1.1.0 section 4.5).
type AdmitEvidence =
    { EvidenceId: Guid
      ContentKind: ContentKind
      Content: NonEmptyText
      Category: EvidenceCategory
      Source: NonEmptyText
      CapturedAt: DateTimeOffset option
      Label: NonEmptyText
      SourceUrl: string option }

type ReviseEvidence = { EvidenceId: Guid; Label: NonEmptyText; Source: NonEmptyText; SourceUrl: string option }

type RelateEvidence =
    { RelationshipId: Guid
      EvidenceId: Guid
      HypothesisId: HypothesisId
      Kind: RelationshipKind
      Rationale: NonEmptyText option }

type IdentifyUnknown = { UnknownId: Guid; Question: NonEmptyText; Materiality: Materiality }
/// INV-034: a new title (causa-storage 1.2.0), with an optional reason. A group of its own.
type ReviseTitle = { Title: NonEmptyText; Reason: NonEmptyText option }
/// INV-034: an open unknown's question revised (causa-storage 1.2.0); the unknown keeps its id. A group of its own.
type ReviseUnknown = { UnknownId: Guid; Question: NonEmptyText }
type RecordAssumption = { AssumptionId: Guid; Proposition: NonEmptyText; Materiality: Materiality }
type RecordContradiction = { ContradictionId: Guid; Statement: NonEmptyText; EvidenceIds: Guid list; Materiality: Materiality }

/// The timing is decided by the engine, not requested (A11).
type RecordPrediction = { PredictionId: Guid; HypothesisId: HypothesisId; Statement: NonEmptyText; Strength: PredictionStrength }

type RecordInvestigationAction = { ActionId: Guid; Kind: ActionKind; Purpose: NonEmptyText; TargetHypothesisIds: HypothesisId list }
type RecordFinding = { FindingId: Guid; ActionId: Guid; OriginalResult: NonEmptyText; Interpretation: NonEmptyText option }
type EstablishNegativeKnowledge = { NegativeKnowledgeId: Guid; Statement: NonEmptyText; Scope: NonEmptyText; BasisEvidenceIds: Guid list }
type RecordCausalAssertion = { CauseId: CausalAssertionId; Outcome: NonEmptyText; Members: CausalMember list; Mechanism: NonEmptyText option }
type ConfirmCause = { CauseId: CausalAssertionId }
type RecordCorrectiveAction = { CorrectiveActionId: CorrectiveActionId; Kind: CorrectiveActionKind; Description: NonEmptyText; TargetCauseIds: CausalAssertionId list }
type RecordVerificationCriterion = { CriterionId: Guid; CorrectiveActionId: CorrectiveActionId; ExpectedOutcome: NonEmptyText; Method: NonEmptyText option }
type RecordVerification = { VerificationId: Guid; CriterionId: Guid; Outcome: VerificationOutcome; EvidenceIds: Guid list }
type RecordConclusion = { ConclusionId: Guid; Statement: NonEmptyText; Strength: ConclusionStrength; CauseIds: CausalAssertionId list; ResidualUncertainty: NonEmptyText option }
type ReopenInvestigation = { Reason: NonEmptyText }
type RecordProposal = { ProposalId: ProposalId; Kind: ProposalKind; Text: NonEmptyText; Rationale: NonEmptyText option }

// ---- The frozen contract (IF41-DOMAIN), unchanged ----------------------------------------------------------------

type CoreCommand =
    | StartInvestigation of StartInvestigation
    | ActivateInvestigation
    | RecordObservation of RecordObservation
    | ReviseObservation of ReviseObservation
    | RecordHypothesis of RecordHypothesis
    | MarkHypothesisPlausible of MarkHypothesis
    | MarkHypothesisSupported of MarkHypothesis
    | FalsifyHypothesis of FalsifyHypothesis
    | ReopenHypothesis of ReopenHypothesis
    | AdmitEvidence of AdmitEvidence
    | ReviseEvidence of ReviseEvidence
    | RelateEvidence of RelateEvidence
    | IdentifyUnknown of IdentifyUnknown
    | RecordAssumption of RecordAssumption
    | RecordContradiction of RecordContradiction
    | RecordPrediction of RecordPrediction
    | RecordInvestigationAction of RecordInvestigationAction
    | RecordFinding of RecordFinding
    | EstablishNegativeKnowledge of EstablishNegativeKnowledge
    | RecordCausalAssertion of RecordCausalAssertion
    | ConfirmCause of ConfirmCause
    | RecordCorrectiveAction of RecordCorrectiveAction
    | RecordVerificationCriterion of RecordVerificationCriterion
    | MarkCorrectiveActionImplemented of CorrectiveActionId
    | RecordVerification of RecordVerification
    | RecordConclusion of RecordConclusion
    | ResolveInvestigation
    | ReopenInvestigation of ReopenInvestigation
    | RecordProposal of RecordProposal
    | AcceptProposal of ProposalId
    | RejectProposal of ProposalId * reason: NonEmptyText
    | AttachExtension of ExtensionEnvelope
    /// causa-storage 1.2.0 (owner-approved contract change): INV-034 title and question revisions.
    | ReviseTitle of ReviseTitle
    | ReviseUnknown of ReviseUnknown
type Command =
    | Core of CoreCommand
    | Extension of SemanticId * payloadJson: string
/// v4.1.5 E1 (historical compatibility, OD-HC-01 A; ODR-R10-25 A): one case for every causa-storage v1.0.0 event
/// (CONTRACT.md section 7), so every catalog history is representable without loss. Every v4.1.4 case name is kept;
/// nine cases carry the wire's extra fields; 22 cases are added; HypothesisFalsified no longer carries a Scope the
/// wire does not have. Payload types stay the domain owner's to define (opaque here, as in v4.1.2), with exactly
/// the fields of their wire records: encoding then decoding any stored event must reproduce its canonical bytes.
/// v4.1.6 (ODR-V416-01): the two causa-storage 1.1.0 events (S-1 B), each exactly its wire record:
/// EvidenceRevised {evidenceId, label, source, sourceUrl} and ObservationRevised {observationId, original, source}.
/// The 1.1.0 relationship kinds consistentWith and inconclusive belong to EvidenceRelationship's kind, which is the
/// domain owner's type; no case is added for them. A reassessment is a new EvidenceRelationshipRecorded for the same
/// pair and the latest is current (S-8 B).
type CoreEvent =
    | InvestigationStarted of InvestigationStarted
    | InvestigationActivated of from: Lifecycle
    | InvestigationMarkedAwaitingEvidence of from: Lifecycle * reason: NonEmptyText
    | InvestigationMarkedAwaitingVerification of from: Lifecycle
    | InvestigationResolved of from: Lifecycle
    | InvestigationClosed of from: Lifecycle
    | InvestigationReopened of from: Lifecycle * reason: NonEmptyText
    | InvestigationVerificationFailed of reason: NonEmptyText
    | ObservationRecorded of Observation
    | ObservationRevised of ObservationRevision
    | HypothesisRecorded of Hypothesis
    | HypothesisMarkedPlausible of HypothesisTransition
    | HypothesisMarkedSupported of HypothesisTransition
    | HypothesisWeakened of HypothesisTransition
    | HypothesisFalsified of HypothesisTransition
    | HypothesisSuperseded of HypothesisTransition
    | HypothesisReopened of HypothesisTransition
    | EvidenceAdmitted of Evidence
    | EvidenceSuperseded of EvidenceSupersession
    | EvidenceVerificationAssessed of EvidenceVerificationAssessment
    | EvidenceRevised of EvidenceRevision
    | EvidenceRelationshipRecorded of EvidenceRelationship
    | UnknownIdentified of Unknown
    | UnknownTransitioned of UnknownTransition
    | AssumptionRecorded of Assumption
    | AssumptionTransitioned of AssumptionTransition
    | ContradictionRecorded of Contradiction
    | ContradictionTransitioned of ContradictionTransition
    | PredictionRecorded of Prediction
    | InvestigationActionRecorded of InvestigationAction
    | InvestigationActionTransitioned of InvestigationActionTransition
    | FindingRecorded of Finding
    | NegativeKnowledgeEstablished of NegativeKnowledge
    | CausalAssertionRecorded of CausalAssertion
    | CauseConfirmed of CauseConfirmation
    | ConfirmedCauseReopened of CausalAssertionId * reason: NonEmptyText
    | CorrectiveActionRecorded of CorrectiveAction
    | CorrectiveActionApproved of CorrectiveActionId
    | CorrectiveActionStarted of CorrectiveActionId
    | CorrectiveActionImplemented of CorrectiveActionId
    | CorrectiveActionAwaitingVerificationOfEffect of CorrectiveActionId
    | CorrectiveActionVerifiedEffective of CorrectiveActionId
    | CorrectiveActionVerificationFailed of CorrectiveActionId
    | CorrectiveActionReworkRequired of CorrectiveActionId
    | CorrectiveActionCancelled of CorrectiveActionId
    | VerificationCriterionRecorded of VerificationCriterion
    | VerificationRecorded of Verification
    | ConclusionRecorded of Conclusion
    | ProposalRecorded of Proposal
    | ProposalAccepted of ProposalId * targetId: Guid
    | ProposalRejected of ProposalId * reason: NonEmptyText
    | ExtensionAttached of ExtensionEnvelope
    /// causa-storage 1.2.0 (owner-approved contract change): InvestigationTitleRevised and UnknownRevised.
    | InvestigationTitleRevised of InvestigationTitleRevision
    | UnknownRevised of UnknownRevision
type CommandEnvelope = { CommandId: Guid; Investigation: InvestigationId; ExpectedVersion: AggregateVersion option; Command: Command }
type EventPayload =
    | CoreEvent of CoreEvent
    | ExtensionEvent of SemanticId * payloadJson: string
/// v4.1.5 E1: the stored envelope's provenance (occurredAt, actor, correlationId, formatVersion, semanticVersion),
/// opaque here and the domain owner's to define with exactly the wire fields.
type EventProvenance =
    { OccurredAt: DateTimeOffset
      Actor: Actor
      CorrelationId: Guid
      FormatVersion: string
      SemanticVersion: string }
type EventEnvelope = { EventId: EventId; Investigation: InvestigationId; Version: AggregateVersion; Provenance: EventProvenance; Payload: EventPayload }
type Blocker = { Code: string; Message: string }
type Decision =
    | Allowed of EventPayload list
    | Blocked of Blocker list
    | RequiresReview of string list
    | UnsupportedSemantics of string list
    | Conflict of expected: AggregateVersion * actual: AggregateVersion
    | Duplicate of EventId list
type PolicySet = { Id: string }
type ActorContext = { ActorId: string }
type ProjectionError = { At: AggregateVersion; Reason: string }
