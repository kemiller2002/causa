// Owner: domain (T13). Capability projection (R8.1, R8.2, R8.4): for every core command, whether it can be taken in
// the current state and, when it cannot, the typed blockers that explain why. It reads the same gates Decide uses, so
// the UI never offers an action Decide would refuse for a reason shown here.
namespace Causa.Domain

type Capability = { Id: string; Blockers: Blocker list }

[<RequireQualifiedAccess>]
module Capabilities =
    let private needs (condition: bool) code message = if condition then [] else [ Blockers.make code message ]

    let private hypothesesIn (i: Investigation) (states: HypothesisState list) =
        i.Hypotheses |> Map.exists (fun _ h -> List.contains h.State states)

    /// Every core command with its blockers in this state (empty: available). Commands that create something new
    /// are available whenever the investigation is open; those that act on an entity need one in a suitable state.
    let evaluate (policies: PolicySet) (i: Investigation) : Capability list =
        let opened = if Investigation.isOpen i then [] else [ Blockers.notOpen i ]
        let whenOpen extra = opened @ extra
        let cap id blockers = { Id = id; Blockers = blockers }
        let anyCauseConfirmable =
            i.Causes |> Map.exists (fun id _ -> List.isEmpty (Policies.causeGate i id))
        let _ = policies
        [ cap
              "ActivateInvestigation"
              (match i.Lifecycle with
               | Lifecycle.Draft
               | Lifecycle.AwaitingEvidence
               | Lifecycle.AwaitingVerification
               | Lifecycle.Archived -> []
               | other -> [ Blockers.make "LIFECYCLE.ILLEGAL_TRANSITION" (sprintf "A %A investigation cannot be activated." other) ])
          cap "ResolveInvestigation" (Policies.resolutionGate i)
          cap
              "ReopenInvestigation"
              (needs (i.Lifecycle = Lifecycle.Resolved || i.Lifecycle = Lifecycle.Closed) "LIFECYCLE.ILLEGAL_TRANSITION" "Only a resolved or closed investigation can be reopened.")
          cap "RecordObservation" opened
          cap "ReviseObservation" opened
          cap "RecordHypothesis" opened
          cap
              "MarkHypothesisPlausible"
              (whenOpen (needs (hypothesesIn i [ HypothesisState.Proposed; HypothesisState.Weakened ]) "HYPOTHESIS.NONE_ELIGIBLE" "No hypothesis is proposed or weakened."))
          cap
              "MarkHypothesisSupported"
              (whenOpen
                  (needs (hypothesesIn i [ HypothesisState.Plausible; HypothesisState.Weakened ]) "HYPOTHESIS.NONE_ELIGIBLE" "No hypothesis is plausible or weakened."
                   @ needs (not i.Evidence.IsEmpty) "HYPOTHESIS.EVIDENCE_REQUIRED" "Support must cite evidence, and none has been admitted."))
          cap
              "FalsifyHypothesis"
              (whenOpen
                  (needs
                      (hypothesesIn i [ HypothesisState.Proposed; HypothesisState.Plausible; HypothesisState.Supported; HypothesisState.Weakened ])
                      "HYPOTHESIS.NONE_ELIGIBLE"
                      "No hypothesis can be falsified."
                   @ needs (not i.Evidence.IsEmpty) "HYPOTHESIS.EVIDENCE_REQUIRED" "Falsification must cite evidence, and none has been admitted."))
          cap "ReopenHypothesis" (whenOpen (needs (hypothesesIn i [ HypothesisState.Falsified ]) "HYPOTHESIS.NONE_ELIGIBLE" "No hypothesis is falsified."))
          cap "AdmitEvidence" opened
          cap
              "ReviseEvidence"
              (whenOpen (needs (i.Evidence |> Map.exists (fun _ e -> e.SupersededBy.IsNone)) "EVIDENCE.NONE" "No current evidence to revise."))
          cap
              "RelateEvidence"
              (whenOpen
                  (needs (i.Evidence |> Map.exists (fun id _ -> Policies.isUsableEvidence i id)) "EVIDENCE.NONE" "No usable evidence has been admitted."
                   @ needs (not i.Hypotheses.IsEmpty) "HYPOTHESIS.NONE" "No hypothesis has been recorded."))
          cap "IdentifyUnknown" opened
          cap "RecordAssumption" opened
          cap "RecordContradiction" opened
          cap "RecordPrediction" (whenOpen (needs (not i.Hypotheses.IsEmpty) "HYPOTHESIS.NONE" "No hypothesis has been recorded."))
          cap "RecordInvestigationAction" opened
          cap "RecordFinding" (whenOpen (needs (not i.Actions.IsEmpty) "ACTION.NONE" "No investigative action has been recorded."))
          cap "EstablishNegativeKnowledge" (whenOpen (needs (not i.Evidence.IsEmpty) "NEGATIVE_KNOWLEDGE.EVIDENCE_REQUIRED" "Negative knowledge must cite evidence."))
          cap
              "RecordCausalAssertion"
              (whenOpen
                  (needs
                      (hypothesesIn i [ HypothesisState.Proposed; HypothesisState.Plausible; HypothesisState.Supported; HypothesisState.Weakened ])
                      "HYPOTHESIS.NONE_ELIGIBLE"
                      "No hypothesis can take part in a causal explanation."))
          cap
              "ConfirmCause"
              (if i.Causes.IsEmpty then
                   whenOpen [ Blockers.make "CAUSE.NONE" "No causal explanation has been recorded." ]
               elif anyCauseConfirmable then
                   []
               else
                   i.Causes |> Map.toList |> List.collect (fun (id, _) -> Policies.causeGate i id) |> List.distinct)
          cap "RecordCorrectiveAction" opened
          cap "RecordVerificationCriterion" (whenOpen (needs (not i.CorrectiveActions.IsEmpty) "CORRECTIVE_ACTION.NONE" "No corrective action has been recorded."))
          cap
              "MarkCorrectiveActionImplemented"
              (whenOpen
                  (needs
                      (i.CorrectiveActions |> Map.exists (fun _ c -> c.State = CorrectiveActionState.CorrectionInProgress))
                      "CORRECTIVE_ACTION.NONE_IN_PROGRESS"
                      "No corrective action is in progress."))
          cap "RecordVerification" (whenOpen (needs (not i.Criteria.IsEmpty) "CRITERION.NONE" "No success criterion has been recorded."))
          cap "RecordConclusion" opened
          cap "RecordProposal" opened
          cap
              "AcceptProposal"
              (whenOpen
                  (needs
                      (i.Proposals |> Map.exists (fun _ p -> p.State = ProposalState.ProposalActive && p.Kind = ProposalKind.ProposedHypothesis))
                      "PROPOSAL.NONE_ACCEPTABLE"
                      "No active hypothesis proposal."))
          cap
              "RejectProposal"
              (whenOpen (needs (i.Proposals |> Map.exists (fun _ p -> p.State = ProposalState.ProposalActive)) "PROPOSAL.NONE_ACTIVE" "No active proposal.")) ]

    /// The ids of the commands available now.
    let available (policies: PolicySet) (i: Investigation) : string list =
        evaluate policies i |> List.filter (fun c -> c.Blockers.IsEmpty) |> List.map (fun c -> c.Id)
