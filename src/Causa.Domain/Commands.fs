// Owner: domain (T13). Command names: the identity of each core command, used as the capability id the UI reads
// (R8.1) and in blocker messages.
namespace Causa.Domain

[<RequireQualifiedAccess>]
module Commands =
    let name (command: CoreCommand) =
        match command with
        | StartInvestigation _ -> "StartInvestigation"
        | ActivateInvestigation -> "ActivateInvestigation"
        | RecordObservation _ -> "RecordObservation"
        | ReviseObservation _ -> "ReviseObservation"
        | RecordHypothesis _ -> "RecordHypothesis"
        | MarkHypothesisPlausible _ -> "MarkHypothesisPlausible"
        | MarkHypothesisSupported _ -> "MarkHypothesisSupported"
        | FalsifyHypothesis _ -> "FalsifyHypothesis"
        | ReopenHypothesis _ -> "ReopenHypothesis"
        | AdmitEvidence _ -> "AdmitEvidence"
        | ReviseEvidence _ -> "ReviseEvidence"
        | RelateEvidence _ -> "RelateEvidence"
        | IdentifyUnknown _ -> "IdentifyUnknown"
        | RecordAssumption _ -> "RecordAssumption"
        | RecordContradiction _ -> "RecordContradiction"
        | RecordPrediction _ -> "RecordPrediction"
        | RecordInvestigationAction _ -> "RecordInvestigationAction"
        | RecordFinding _ -> "RecordFinding"
        | EstablishNegativeKnowledge _ -> "EstablishNegativeKnowledge"
        | RecordCausalAssertion _ -> "RecordCausalAssertion"
        | ConfirmCause _ -> "ConfirmCause"
        | RecordCorrectiveAction _ -> "RecordCorrectiveAction"
        | RecordVerificationCriterion _ -> "RecordVerificationCriterion"
        | MarkCorrectiveActionImplemented _ -> "MarkCorrectiveActionImplemented"
        | RecordVerification _ -> "RecordVerification"
        | RecordConclusion _ -> "RecordConclusion"
        | ResolveInvestigation -> "ResolveInvestigation"
        | ReopenInvestigation _ -> "ReopenInvestigation"
        | RecordProposal _ -> "RecordProposal"
        | AcceptProposal _ -> "AcceptProposal"
        | RejectProposal _ -> "RejectProposal"
        | AttachExtension _ -> "AttachExtension"
