// Owner: domain (T13). The investigation aggregate: the projection state of contract v1.0.0 section 8.2-8.3 (with the
// v1.1.0 section 4.7 additions), held as maps by id. Derived postures ("leading", "next best action") are never stored.
namespace Causa.Domain

open System

/// Set when an entity is first recorded; later updates never change it.
type Provenance = { Actor: Actor; RecordedAt: DateTimeOffset }

type ObservationEntity =
    { Id: Guid
      Original: NonEmptyText
      ObservedAt: DateTimeOffset option
      /// v1.1.0: null at creation, set by ObservationRevised.
      Source: NonEmptyText option
      Provenance: Provenance }

/// One hypothesis transition (R2.3): why, with which evidence, when and at which version.
type HypothesisChange =
    { From: HypothesisState
      To: HypothesisState
      /// The hypothesisCommand wire value.
      Command: string
      Reason: NonEmptyText
      EvidenceIds: Guid list
      At: DateTimeOffset
      AggregateVersion: AggregateVersion }

type HypothesisEntity =
    { Id: HypothesisId
      Statement: NonEmptyText
      Mechanism: NonEmptyText option
      State: HypothesisState
      History: HypothesisChange list
      Provenance: Provenance }

type EvidenceEntity =
    { Id: Guid
      ContentKind: ContentKind
      Content: NonEmptyText
      Category: EvidenceCategory
      Source: NonEmptyText
      CapturedAt: DateTimeOffset option
      Verification: VerificationStatus
      SupersededBy: Guid option
      AdmittedAtVersion: AggregateVersion
      /// v1.1.0: null at admission, set by EvidenceRevised.
      Label: NonEmptyText option
      SourceUrl: string option
      Provenance: Provenance }

type RelationshipEntity =
    { Id: Guid
      EvidenceId: Guid
      HypothesisId: HypothesisId
      Kind: RelationshipKind
      Rationale: NonEmptyText option
      Provenance: Provenance }

type UnknownEntity =
    { Id: Guid
      Question: NonEmptyText
      Materiality: Materiality
      State: UnknownState
      Basis: NonEmptyText option
      Provenance: Provenance }

type AssumptionEntity =
    { Id: Guid
      Proposition: NonEmptyText
      Materiality: Materiality
      State: AssumptionState
      Provenance: Provenance }

type ContradictionEntity =
    { Id: Guid
      Statement: NonEmptyText
      EvidenceIds: Guid list
      Materiality: Materiality
      State: ContradictionState
      Basis: NonEmptyText option
      Provenance: Provenance }

type PredictionEntity =
    { Id: Guid
      HypothesisId: HypothesisId
      Statement: NonEmptyText
      Strength: PredictionStrength
      Timing: PredictionTiming
      Provenance: Provenance }

type ActionEntity =
    { Id: Guid
      Kind: ActionKind
      Purpose: NonEmptyText
      TargetHypothesisIds: HypothesisId list
      State: ActionState
      Provenance: Provenance }

type FindingEntity =
    { Id: Guid
      ActionId: Guid
      OriginalResult: NonEmptyText
      Interpretation: NonEmptyText option
      Provenance: Provenance }

type NegativeKnowledgeEntity =
    { Id: Guid
      Statement: NonEmptyText
      Scope: NonEmptyText
      BasisEvidenceIds: Guid list
      Provenance: Provenance }

type CauseEntity =
    { Id: CausalAssertionId
      Outcome: NonEmptyText
      Members: CausalMember list
      Mechanism: NonEmptyText option
      State: CauseState
      Provenance: Provenance }

type CorrectiveActionEntity =
    { Id: CorrectiveActionId
      Kind: CorrectiveActionKind
      Description: NonEmptyText
      TargetCauseIds: CausalAssertionId list
      State: CorrectiveActionState
      /// The version that entered awaitingVerificationOfEffect (0 before).
      AwaitingSinceVersion: AggregateVersion
      Provenance: Provenance }

type CriterionEntity =
    { Id: Guid
      CorrectiveActionId: CorrectiveActionId
      ExpectedOutcome: NonEmptyText
      Method: NonEmptyText option
      Provenance: Provenance }

type VerificationEntity =
    { Id: Guid
      CriterionId: Guid
      Outcome: VerificationOutcome
      EvidenceIds: Guid list
      RecordedAtVersion: AggregateVersion
      Provenance: Provenance }

type ConclusionEntity =
    { Id: Guid
      Statement: NonEmptyText
      Strength: ConclusionStrength
      CauseIds: CausalAssertionId list
      ResidualUncertainty: NonEmptyText option
      RecordedAtVersion: AggregateVersion
      Provenance: Provenance }

type ProposalEntity =
    { Id: ProposalId
      Kind: ProposalKind
      Text: NonEmptyText
      Rationale: NonEmptyText option
      State: ProposalState
      /// The rejection reason.
      Decision: NonEmptyText option
      Provenance: Provenance }

/// The investigation aggregate (FSHARP-TYPE-SHAPES section 30; contract v1.0.0 section 8.2). The bootstrap stub held
/// only Id and Version; both are kept with the same names and types.
type Investigation =
    { Id: InvestigationId
      Version: AggregateVersion
      Title: NonEmptyText
      Lifecycle: Lifecycle
      CreatedAt: DateTimeOffset
      Observations: Map<Guid, ObservationEntity>
      Hypotheses: Map<HypothesisId, HypothesisEntity>
      Evidence: Map<Guid, EvidenceEntity>
      Relationships: Map<Guid, RelationshipEntity>
      Unknowns: Map<Guid, UnknownEntity>
      Assumptions: Map<Guid, AssumptionEntity>
      Contradictions: Map<Guid, ContradictionEntity>
      Predictions: Map<Guid, PredictionEntity>
      Actions: Map<Guid, ActionEntity>
      Findings: Map<Guid, FindingEntity>
      NegativeKnowledge: Map<Guid, NegativeKnowledgeEntity>
      Causes: Map<CausalAssertionId, CauseEntity>
      CorrectiveActions: Map<CorrectiveActionId, CorrectiveActionEntity>
      Criteria: Map<Guid, CriterionEntity>
      Verifications: Map<Guid, VerificationEntity>
      Conclusions: Map<Guid, ConclusionEntity>
      Proposals: Map<ProposalId, ProposalEntity>
      Extensions: ExtensionEnvelope list
      /// Every applied event, oldest first (the timeline the views and history read).
      Timeline: EventEnvelope list }

[<RequireQualifiedAccess>]
module Investigation =
    /// The state InvestigationStarted creates (contract v1.0.0 section 8.3): lifecycle draft, the initial
    /// observation, every other collection empty.
    let started (id: InvestigationId) (version: AggregateVersion) (provenance: Provenance) (payload: InvestigationStarted) : Investigation =
        { Id = id
          Version = version
          Title = payload.Title
          Lifecycle = Lifecycle.Draft
          CreatedAt = provenance.RecordedAt
          Observations =
            Map.ofList
                [ payload.ObservationId,
                  { Id = payload.ObservationId
                    Original = payload.Original
                    ObservedAt = None
                    Source = None
                    Provenance = provenance } ]
          Hypotheses = Map.empty
          Evidence = Map.empty
          Relationships = Map.empty
          Unknowns = Map.empty
          Assumptions = Map.empty
          Contradictions = Map.empty
          Predictions = Map.empty
          Actions = Map.empty
          Findings = Map.empty
          NegativeKnowledge = Map.empty
          Causes = Map.empty
          CorrectiveActions = Map.empty
          Criteria = Map.empty
          Verifications = Map.empty
          Conclusions = Map.empty
          Proposals = Map.empty
          Extensions = []
          Timeline = [] }

    /// Open for recording: draft, active, awaitingEvidence or awaitingVerification (v1.1.0 section 4.6).
    let isOpen (i: Investigation) =
        match i.Lifecycle with
        | Lifecycle.Draft
        | Lifecycle.Active
        | Lifecycle.AwaitingEvidence
        | Lifecycle.AwaitingVerification -> true
        | Lifecycle.Resolved
        | Lifecycle.Closed
        | Lifecycle.Archived -> false

    let versionNumber (AggregateVersion v) = v

/// Why a history cannot be replayed (IF41-DOMAIN; moved here unchanged so Evolve can return it).
type ReplayError =
    | EmptyHistory
    | NonContiguousVersion of expected: AggregateVersion * found: AggregateVersion
    | IllegalEvent of ProjectionError
    | UnknownEventKind of string
