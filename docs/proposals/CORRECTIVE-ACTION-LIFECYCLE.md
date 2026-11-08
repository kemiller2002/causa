# Proposal: the corrective-action lifecycle commands

Status: **proposal for the owner**. Nothing in this document is implemented. Branch `feature/causes-actions`.

## The problem

`MarkCorrectiveActionImplemented` cannot be reached. The kernel accepts it only from `CorrectionInProgress`
(`Decide.fs`), but `RecordCorrectiveAction` leaves an action in `CorrectionProposed`, and no command moves it on.
STATE-TRANSITION-TABLES §11 (`STATE-TRANSITION-TABLES§11-corrective-action-lifecycle-O11..O21`) and
`docs/COMMAND-EVENT-CATALOG.md` §19 list the missing commands:

- `ApproveCorrectiveAction`
- `StartCorrectiveAction`
- `StartCorrectiveActionVerification`
- `MarkCorrectiveActionVerified`
- `MarkCorrectiveActionVerificationFailed`
- `RequireCorrectiveActionRework`
- `CancelCorrectiveAction`

None of them is in `CoreCommand`. The frozen IF41-DOMAIN contract has only `RecordCorrectiveAction`,
`RecordVerificationCriterion`, `MarkCorrectiveActionImplemented` and `RecordVerification`.

The **events** already exist. `CoreEvent` has `CorrectiveActionApproved`, `Started`, `Implemented`,
`AwaitingVerificationOfEffect`, `VerifiedEffective`, `VerificationFailed`, `ReworkRequired` and `Cancelled`. `Evolve`
applies all of them (the `correction` helper, which sets `AwaitingSinceVersion` on entering awaiting-verification).
`Semantics.EventRegistry` maps each one to the causa-storage event `CorrectiveActionTransitioned` with the target
`state`. **Only the commands, their decide rules and their capabilities are missing.**

## Headline decision points for the owner

1. **The domain contract changes; the storage contract does not.** The proposal adds seven cases to `CoreCommand`, a
   breaking change to the frozen IF41-DOMAIN union under the interface rules. Every event is already storable in
   causa-storage 1.0.0 to 1.2.0 as `CorrectiveActionTransitioned`, so **no causa-storage 1.3.0 is needed**. This was
   checked against `causa-data/contract/v1.2.0`:
   - `schemas/payloads/CorrectiveActionTransitioned.schema.json`: `{correctiveActionId, state}`.
   - `$defs/correctiveActionTarget` lists every target state.
2. **What verifies an action: a person, or the verification record?** STT §11 has a separate
   `MarkCorrectiveActionVerified` command. The resolution gate (`Policies.freshSatisfied`) already counts a satisfied,
   evidence-citing `VerificationRecorded`. The options are:
   - **(A, recommended)** Keep them separate. `MarkCorrectiveActionVerified` is refused unless a fresh satisfied
     verification of one of the action's criteria exists. `MarkCorrectiveActionVerificationFailed` is refused unless
     a fresh failed one exists. A person decides, and the record must back it.
   - **(B)** `RecordVerification` emits the transition itself when the action is awaiting verification. This means
     fewer clicks, but one command emits two events and the person is not asked.
3. **A verification recorded before the action is implemented counts today.** `AwaitingSinceVersion` is 0 until the
   action enters awaiting-verification, so `freshSatisfied` accepts a verification recorded at any version. That
   includes one recorded while the action was still proposed. The options are:
   - **(A, recommended)** The resolution gate also requires the action to be `CorrectionVerifiedEffective`.
   - **(B)** Keep it as it is. The lifecycle is then advisory.

   This changes the resolution gate (R7.4), so it is the owner's call.
4. **Does a failed verification reopen the investigation?** COMMAND-EVENT-CATALOG §19 says "possibly
   InvestigationReopened". Recommended: no. Reopening stays the explicit `ReopenInvestigation`. A failed verification
   only moves the action, and the resolution gate already blocks.
5. **Does Approve need anything beyond Proposed?** STT says no. Recommended: no. In particular, approval does not
   require a confirmed target cause: a mitigation can be approved while the cause is only proposed (R7.2).
6. **Activation for resolution (outside this proposal, but needed for "what it unlocks").** An investigation started
   in the app stays in `Draft`. The resolution gate needs `Active` or `AwaitingVerification`. `ActivateInvestigation`
   exists in the kernel but has no UI and no catalog id. Resolving in the app needs that action too, and it should be
   decided together with this proposal.

## Commands (proposed `CoreCommand` cases)

Each command carries the `CorrectiveActionId` (`CorrectiveActionId of Guid`), as `MarkCorrectiveActionImplemented`
already does. A cancellation carries a reason.

```fsharp
| ApproveCorrectiveAction of CorrectiveActionId
| StartCorrectiveAction of CorrectiveActionId                    // from Approved, or from ReworkRequired
| StartCorrectiveActionVerification of CorrectiveActionId
| MarkCorrectiveActionVerified of CorrectiveActionId
| MarkCorrectiveActionVerificationFailed of CorrectiveActionId
| RequireCorrectiveActionRework of CorrectiveActionId
| CancelCorrectiveAction of CancelCorrectiveAction
// with
type CancelCorrectiveAction = { CorrectiveActionId: CorrectiveActionId; Reason: NonEmptyText }
```

Decision point 7: `CorrectiveActionTransitioned` has no `reason` field (`additionalProperties: false`), so a
cancellation reason cannot be stored in 1.x. The options are:

- **(A, recommended)** `CancelCorrectiveAction of CorrectiveActionId`, with no reason.
- **(B)** A reason, which needs causa-storage 1.3.0 (an optional `reason` on `CorrectiveActionTransitioned`).

## Decide rules and blockers

The rules below are the same pattern as the existing `MarkCorrectiveActionImplemented`. The ordinary checks apply
first, unchanged:

- investigation id
- expected version (`Conflict`)
- repeated command id (`Duplicate`)
- `INVESTIGATION.NOT_OPEN`

Then each command needs the action to exist (`ENTITY.NOT_FOUND` "No corrective action with id … exists in this
investigation.") and to be in one of its "from" states. Otherwise it is refused with
`CORRECTIVE_ACTION.ILLEGAL_TRANSITION` "… cannot be … ; this one is <state>."

| Command | From (STT §11) | Extra rule (blocker) | Event |
|---|---|---|---|
| ApproveCorrectiveAction | Proposed | none | `CorrectiveActionApproved id` |
| StartCorrectiveAction | Approved, ReworkRequired | none | `CorrectiveActionStarted id` |
| MarkCorrectiveActionImplemented (exists) | InProgress | none | `CorrectiveActionImplemented id` |
| StartCorrectiveActionVerification | Implemented | `CRITERION.NONE`: the action has no success criterion (R7.3: "Corrective actions must have explicit success criteria") | `CorrectiveActionAwaitingVerificationOfEffect id` |
| MarkCorrectiveActionVerified | AwaitingVerificationOfEffect | decision 2A: `VERIFICATION.NOT_SATISFIED`: no satisfied, evidence-citing verification of one of its criteria recorded at or after `AwaitingSinceVersion` (R7.4) | `CorrectiveActionVerifiedEffective id` |
| MarkCorrectiveActionVerificationFailed | AwaitingVerificationOfEffect | decision 2A: `VERIFICATION.NOT_FAILED`: no failed verification of one of its criteria at or after `AwaitingSinceVersion` | `CorrectiveActionVerificationFailed id` |
| RequireCorrectiveActionRework | VerificationFailed | none | `CorrectiveActionReworkRequired id` |
| CancelCorrectiveAction | Proposed, Approved | none | `CorrectiveActionCancelled id` |

**Evolve.** No change. Each event already sets the action's `State` through `Evolve.correction`, and
`CorrectiveActionAwaitingVerificationOfEffect` also sets `AwaitingSinceVersion` to the event's version. Replaying
1.x data already holding `CorrectiveActionTransitioned` events is unchanged.

**Capabilities.** One entry per command, offered when any action is in a "from" state (with the extra rule), with
blockers like the existing `"MarkCorrectiveActionImplemented"`: `CORRECTIVE_ACTION.NONE_<STATE>` "No corrective action
is <state words>." The UI decides each action with the kernel itself (a `Decide` probe, as the hypothesis status
actions and Confirm cause now do), so it never offers a refused transition and always shows the kernel's reason.

**Verification commands that exist but have no UI:**

- `RecordVerificationCriterion` `{CriterionId; CorrectiveActionId; ExpectedOutcome; Method option}`, stored as
  `CriterionRecorded`.
- `RecordVerification` `{VerificationId; CriterionId; Outcome (satisfied | failed | inconclusive); EvidenceIds}`,
  stored as `VerificationRecorded`. A satisfied outcome must cite evidence (`VERIFICATION.EVIDENCE_REQUIRED`, R7.4).

Both are already v1.0.0 storage events. They need no domain change, only UI.

## Storage check (causa-storage 1.2.0)

| Kernel event | Stored as | In 1.2.0 | Change needed |
|---|---|---|---|
| CorrectiveActionApproved | `CorrectiveActionTransitioned {state: "approved"}` | yes (1.0.0 type) | none |
| CorrectiveActionStarted | `… {state: "inProgress"}` | yes | none |
| CorrectiveActionImplemented | `… {state: "implemented"}` | yes | none |
| CorrectiveActionAwaitingVerificationOfEffect | `… {state: "awaitingVerificationOfEffect"}` | yes | none |
| CorrectiveActionVerifiedEffective | `… {state: "verifiedEffective"}` | yes | none |
| CorrectiveActionVerificationFailed | `… {state: "verificationFailed"}` | yes | none |
| CorrectiveActionReworkRequired | `… {state: "reworkRequired"}` | yes | none |
| CorrectiveActionCancelled | `… {state: "cancelled"}` | yes | none (a stored reason would need 1.3.0, decision 7) |
| VerificationCriterionRecorded | `CriterionRecorded` | yes | none |
| VerificationRecorded | `VerificationRecorded` | yes | none |

Note that the 1.x wire event has no `command` field, so the stored history does not say which command produced a
state. That is fine because each target state has exactly one command. The exception is `inProgress`, reached by
`StartCorrectiveAction` from Approved or from ReworkRequired, and the previous state tells those apart.

## The UI it enables

This follows the patterns on this branch: the kernel decides availability, the kernel's own reason is shown, forms
appear only when offered, there are 44 px targets, and recorded items are listed above the form with a "not saved
yet" line. On each corrective action card (Corrective Actions section):

- **Approve**, **Start**, **Mark implemented**, **Start verification**, **Mark verified effective**,
  **Mark verification failed**, **Require rework** and **Cancel**: one button each, shown where the kernel's table
  allows, and disabled with the kernel's reason otherwise. These are card actions with the action id as the row key,
  like Confirm cause.
- **Add a success criterion** (`RecordVerificationCriterion`): "Expected outcome" (required) and "Method"
  (optional). The card lists its criteria.
- **Record a verification** (`RecordVerification`): pick a criterion, an outcome group (satisfied, failed,
  inconclusive) and an evidence picker. The kernel requires evidence for satisfied.

**Requirement ids involved:**

- R7.1, R7.3, R7.4
- S12-O5 (status), S12-O6 (implementation evidence), S12-O7 (verification criteria)
- STATE-TRANSITION-TABLES§11 O11..O21
- UI-032-O1 (next steps for pending verification)
- UI-036-O1 (completeness: corrective actions and verification)
- R12.2 and UI-009-O1 (the report's corrective action and verification outcome)
- KICKOFF-EXECUTION-PACKET§preamble-O100 ("End-to-end cause -> correction -> verification -> resolution path works")

The catalog (`causa-investigation.requirement-catalog.json`) has no ids for any of these. As with causes, the UI
would use self-named `UIA-*` and `UIF-*` ids, for example `UIA-APPROVE-CORRECTIVE-ACTION`,
`UIF-CRITERION-EXPECTED-OUTCOME` and `UIF-VERIFICATION-OUTCOME`, unless the owner adds catalog entries.

## What it unlocks

- **Mark implemented** becomes reachable (Proposed, then Approve, then Start, then Mark implemented).
- **The whole STT §11 lifecycle** becomes reachable, including verification failure and rework.
- **Resolving an investigation in the app (R7.4, `ResolveInvestigation`).** With decision 6 (an Activate action), the
  resolution gate's three conditions can all be met in the app:
  - a confirmed cause (reachable on this branch);
  - a corrective action for it whose criterion has a fresh, satisfied, evidence-citing verification (this proposal);
  - no open blocking unknown or contradiction.

  With decision 3A, the action must also be verified effective. This closes the
  "cause → correction → verification → resolution" path of KICKOFF-EXECUTION-PACKET O100.
- **The completeness checklist and next steps (UI-036, UI-032)** can then name real pending verification states,
  instead of nothing.

## Size of the change, if approved

- **Domain:**
  - seven `CoreCommand` cases and one record type;
  - seven Decide arms (shown above);
  - seven capability entries;
  - `Commands.name` entries;
  - contract tests updated for the new union cases, in the same pull request.
- **Persistence:** none. `EventRegistry` already maps the events, and the codec already reads and writes
  `CorrectiveActionTransitioned`, `CriterionRecorded` and `VerificationRecorded`.
- **App:** the card actions and the two forms above, with contract tests per path and a 390 px Playwright spec
  covering: record an action → approve → start → mark implemented → add a criterion → start verification → record a
  satisfied verification with evidence → mark verified effective → (activate →) resolve.
