// Owner: ui-3 (T22, reassigned to ui). The content editors of a writable working copy: record an observation (INV-024,
// INV-025, INV-014), record a hypothesis (INV-026, INV-011, INV-009), admit evidence (INV-027, INV-028, INV-029,
// INV-012, INV-045), link evidence to a hypothesis and reassess the link (INV-030, INV-031, INV-038) and record a
// conclusion (INV-032, INV-033, INV-046, INV-047) naming the causes it rests on, record a cause (RecordCausalAssertion)
// and record a corrective action for a cause (RecordCorrectiveAction; R7.1, R7.2, S12), each with Cancel (INV-036). Offered only where the projection says
// the engine authorizes them (UI-004); a frozen demonstration gets none. The text is the view's drafts; the view never
// builds a command.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

/// One option of a choice group: the stored value, its readable words, its accessible name (the words, or the stored
/// word where the card that shows the choice afterwards uses it) and what it means.
type EditorChoice = { Value: string; Label: string; Name: string; Description: string }

module ContentEditors =
    /// Field ids the catalog names but Presentation.Fields does not declare yet (integration lane, SL-T23).
    module PendingFields =
        let evidenceCategory = FieldId "INV-045"
        let conclusionStrength = FieldId "INV-046"
        let residualUncertainty = FieldId "INV-047"

    /// INV-031's options in its order (OD-INDY-23), with the catalog's meaning. The accessible name is the lower-case
    /// word the evidence and hypothesis cards use afterwards ("supports “…”"; J2 picks the choice by that word); the
    /// visible words are the same, capitalised. "Not assessed" is the absence of a link, never an option.
    let relationshipKinds : EditorChoice list =
        [ "supports", "Supports", "supports", "Makes the hypothesis more credible."
          "contradicts", "Contradicts", "contradicts", "Makes the hypothesis less credible."
          "consistent-with", "Consistent with", "consistent with", "Fits, but does not favour it over other hypotheses it also fits."
          "inconclusive", "Inconclusive", "inconclusive", "Assessed, but whether it fits cannot be determined yet." ]
        |> List.map (fun (v, l, n, d) -> { Value = v; Label = l; Name = n; Description = d })

    /// INV-045's seven causa-storage evidenceCategory values (OD-CR-02 A: nothing selected at first).
    let evidenceCategories : EditorChoice list =
        [ "directObservation", "Direct observation", "Seen or heard first-hand."
          "measurement", "Measurement", "A reading from an instrument."
          "log", "Log", "A system or operator record."
          "document", "Document", "A written record, report or drawing."
          "experimentResult", "Experiment result", "The outcome of a test you ran."
          "testimony", "Testimony", "What someone says happened."
          "externalReference", "External reference", "A published source outside the investigation." ]
        |> List.map (fun (v, l, d) -> { Value = v; Label = l; Name = l; Description = d })

    /// INV-046's five causa-storage conclusionStrength values, weakest first (OD-CR-04 A).
    let conclusionStrengths : EditorChoice list =
        [ "plausibleExplanation", "Plausible explanation", "It could explain the problem; others remain open."
          "leadingExplanation", "Leading explanation", "The best explanation so far."
          "supportedCause", "Supported cause", "The evidence supports it as the cause."
          "confirmedCause", "Confirmed cause", "Confirmed by evidence or a test. Refused unless it names causes and every one is confirmed."
          "acceptedWithResidualUncertainty", "Accepted with residual uncertainty", "Accepted; state what is still uncertain." ]
        |> List.map (fun (v, l, d) -> { Value = v; Label = l; Name = l; Description = d })

    /// The kernel's cause roles (CauseRole; R6.3: root cause is distinguished from contributing factors).
    let causeRoles : EditorChoice list =
        [ "rootCause", "Root cause", "The underlying cause of the outcome."
          "contributingFactor", "Contributing factor", "Made the outcome more likely or worse, but did not cause it alone."
          "trigger", "Trigger", "The event that set the outcome off."
          "enablingCondition", "Enabling condition", "A condition that allowed the outcome to happen." ]
        |> List.map (fun (v, l, d) -> { Value = v; Label = l; Name = l; Description = d })

    /// The kernel's corrective action kinds (R7.2, S12-O8: a mitigation or workaround is told apart from a correction).
    let correctiveKinds : EditorChoice list =
        [ "mitigation", "Mitigation", "Reduces the impact while the cause remains."
          "workaround", "Workaround", "Avoids the problem without removing its cause."
          "correction", "Correction", "Removes the cause."
          "prevention", "Prevention", "Stops the cause, or one like it, from happening again." ]
        |> List.map (fun (v, l, d) -> { Value = v; Label = l; Name = l; Description = d })

    let private availability (actions: Map<ActionId, Availability>) (a: ActionId) =
        actions |> Map.tryFind a |> Option.defaultValue NotOffered

    let private errorsFor (fields: FieldId list) (errors: ValidationError list) =
        errors |> List.filter (fun e -> List.contains e.Field fields)

    let private draft (drafts: EditorDrafts) (f: FieldId) =
        drafts.Values |> Map.tryFind f |> Option.defaultValue ""

    /// After a record the form is empty again and the change is unsaved: say where the item went and what keeps it
    /// (the owner recorded a hypothesis twice when the form looked unchanged). Stated from the state, not an event.
    let statusAt (where: string) (noun: string) (unsaved: bool) (draftsEmpty: bool) (recorded: int) =
        if unsaved && draftsEmpty && recorded > 0 then
            sprintf "Recorded %s are listed %s. Your changes are not saved yet: select Save to keep them." noun where
        else ""

    let statusLine (noun: string) (unsaved: bool) (draftsEmpty: bool) (recorded: int) =
        statusAt "above" noun unsaved draftsEmpty recorded

    /// A choice group's options as rows: the stored value, its words, its description and whether the draft holds it.
    let choiceRows (prefix: string) (selected: string) (options: EditorChoice list) =
        [ for o in options ->
              [ Bind.cellText "key" o.Value
                Bind.cellText "label" o.Label
                Bind.cellText "name" o.Name
                Bind.cellText "description" o.Description
                Bind.cellText "describedBy" (sprintf "causa-%s-%s-help" prefix o.Value)
                Bind.cellFlag "checked" (o.Value = selected) ] ]

    /// A picker's options: the record id as the value and its words, with the drafted one selected.
    let pickerRows (selected: string) (options: (string * string) list) =
        [ for value, label in options ->
              [ Bind.cellText "key" value
                Bind.cellText "label" label
                Bind.cellFlag "selected" (value = selected) ] ]

    let private form (prefix: string) (status: string) (available: Availability) (fields: (string * FieldId) list) (drafts: EditorDrafts) =
        let errs = errorsFor (List.map snd fields) drafts.Errors
        [ yield Bind.text (prefix + ".status") status
          yield Bind.flag (prefix + ".offered") (match available with NotOffered -> false | _ -> true)
          yield Bind.flag (prefix + ".disabled") (match available with Available -> false | _ -> true)
          yield Bind.text (prefix + ".unavailable") (match available with Unavailable r -> r | _ -> "")
          for name, f in fields do
              yield Bind.text (prefix + "." + name) (draft drafts f)
          yield Bind.flag (prefix + ".errors-present") (not errs.IsEmpty)
          yield
              Bind.items
                  (prefix + ".errors")
                  [ for e in errs -> [ Bind.cellText "field" (match e.Field with FieldId f -> f); Bind.cellText "message" e.Message ] ] ]

    let private evidenceFields =
        [ "label", Fields.evidenceLabel
          "source", Fields.evidenceSource
          "url", Fields.evidenceUrl
          "content", Fields.evidenceContent
          "category", PendingFields.evidenceCategory ]

    let private linkFields =
        [ "evidence", Fields.linkEvidence
          "hypothesis", Fields.linkHypothesis
          "kind", Fields.relationshipKind
          "rationale", Fields.relationshipRationale ]

    let private conclusionFields =
        [ "statement", Fields.conclusionStatement
          "strength", PendingFields.conclusionStrength
          "residual", PendingFields.residualUncertainty ]

    let private causeFields =
        [ "outcome", Fields.causeOutcome
          "hypothesis", Fields.causeHypothesis
          "role", Fields.causeRole
          "mechanism", Fields.causeMechanism ]

    let private correctiveFields =
        [ "description", Fields.correctiveDescription
          "kind", Fields.correctiveKind
          "cause", Fields.correctiveCause ]

    /// A hypothesis that can take part in a causal explanation (the kernel refuses a falsified or superseded one).
    let causeEligible (h: HypothesisCard) = h.State <> "Falsified" && h.State <> "Superseded"

    /// The conclusion editor's cause toggles: every cause on record, pressed when the draft names it.
    let causeToggleRows (drafted: string) (causes: CauseCard list) =
        let chosen = drafted.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        [ for c in causes ->
              [ Bind.cellText "key" c.Id
                Bind.cellText "label" c.Outcome
                Bind.cellText "state" c.State
                Bind.cellText "pressed" (if List.contains c.Id chosen then "true" else "false") ] ]

    /// The evidence words a picker shows: its label, or its content when no label was recorded (causa-storage 1.0.0).
    let evidenceName (e: EvidenceCard) =
        e.Label |> Option.filter (fun l -> l.Trim() <> "") |> Option.defaultValue e.Content

    let bindings (view: WorkspaceView) : Bindings =
        let a = SectionData.analysis view.Section
        let actions = a |> Option.map (fun a -> a.Actions) |> Option.defaultValue Map.empty
        let unsaved = view.Save |> Option.exists (fun sv -> sv.Kind = UnsavedKind)
        let empty (fs: (string * FieldId) list) = fs |> List.forall (fun (_, f) -> (draft view.Drafts f).Trim() = "")
        let count f = a |> Option.map f |> Option.defaultValue 0
        // The start's own observation is the problem statement; only recorded ones count.
        let observations = count (fun a -> max 0 (a.Observations.Length - 1))
        let hypotheses = count (fun a -> a.Hypotheses.Length)
        let evidence = count (fun a -> a.Evidence.Length)
        let links = count (fun a -> a.Relationships |> List.filter (fun l -> l.Current) |> List.length)
        let conclusions = count (fun a -> a.Conclusions.Length)
        let causesOnRecord = a |> Option.map (fun a -> a.Causes) |> Option.defaultValue []
        let correctiveCount = count (fun a -> a.CorrectiveActionItems.Length)
        let causeHypotheses =
            a |> Option.map (fun a -> a.Hypotheses |> List.filter causeEligible |> List.map (fun h -> h.Id, h.Statement)) |> Option.defaultValue []
        let causeOptions = causesOnRecord |> List.map (fun c -> c.Id, c.Outcome)
        let cause = availability actions Actions.recordCause
        let corrective = availability actions Actions.recordCorrectiveAction
        let obsStatus = statusLine "observations" unsaved (empty [ "text", Fields.observationText; "source", Fields.observationSource ]) observations
        let hypStatus = statusLine "hypotheses" unsaved (empty [ "statement", Fields.hypothesisStatement; "explanation", Fields.hypothesisExplanation ]) hypotheses
        let evidenceOptions = a |> Option.map (fun a -> a.Evidence |> List.map (fun e -> e.Id, evidenceName e)) |> Option.defaultValue []
        let hypothesisOptions = a |> Option.map (fun a -> a.Hypotheses |> List.map (fun h -> h.Id, h.Statement)) |> Option.defaultValue []
        let reassess = availability actions Actions.reassess
        let link = availability actions Actions.linkEvidence
        Bind.ofList(
            form "editor.observation" obsStatus (availability actions Actions.recordObservation) [ "text", Fields.observationText; "source", Fields.observationSource ] view.Drafts
            @ form "editor.hypothesis" hypStatus (availability actions Actions.recordHypothesis) [ "statement", Fields.hypothesisStatement; "explanation", Fields.hypothesisExplanation ] view.Drafts
            @ form "editor.evidence" (statusLine "evidence items" unsaved (empty evidenceFields) evidence) (availability actions Actions.recordEvidence) evidenceFields view.Drafts
            @ [ Bind.items "editor.evidence.categories" (choiceRows "category" (draft view.Drafts PendingFields.evidenceCategory) evidenceCategories) ]
            @ form "editor.link" (statusAt "on each evidence card above" "assessments" unsaved (empty linkFields) links) link linkFields view.Drafts
            @ [ Bind.items "editor.link.kinds" (choiceRows "kind" (draft view.Drafts Fields.relationshipKind) relationshipKinds)
                Bind.items "editor.link.evidence-options" (pickerRows (draft view.Drafts Fields.linkEvidence) evidenceOptions)
                Bind.flag "editor.link.evidence-unchosen" ((draft view.Drafts Fields.linkEvidence) = "")
                Bind.items "editor.link.hypothesis-options" (pickerRows (draft view.Drafts Fields.linkHypothesis) hypothesisOptions)
                Bind.flag "editor.link.hypothesis-unchosen" ((draft view.Drafts Fields.linkHypothesis) = "")
                // Both sides must exist before a link can be made: say which is missing instead of an empty picker.
                Bind.flag "editor.link.ready" (not evidenceOptions.IsEmpty && not hypothesisOptions.IsEmpty)
                // The engine's own reason, when it gives one, says it; this note only fills the gap.
                Bind.text
                    "editor.link.missing"
                    (match link with
                     | Unavailable _ -> ""
                     | _ ->
                         match evidenceOptions.IsEmpty, hypothesisOptions.IsEmpty with
                         | true, true -> "Record evidence and a hypothesis first; then link them here."
                         | true, false -> "Record evidence first; then link it to a hypothesis here."
                         | false, true -> "Record a hypothesis first (Hypotheses section); then link evidence to it here."
                         | false, false -> "")
                Bind.flag "editor.link.reassess-offered" (match reassess with NotOffered -> false | _ -> true)
                Bind.flag "editor.link.reassess-disabled" (match reassess with Available -> false | _ -> true) ]
            @ form "editor.conclusion" (statusLine "conclusions" unsaved (empty conclusionFields) conclusions) (availability actions Actions.recordConclusion) conclusionFields view.Drafts
            @ [ Bind.items "editor.conclusion.strengths" (choiceRows "strength" (draft view.Drafts PendingFields.conclusionStrength) conclusionStrengths)
                Bind.flag "editor.conclusion.causes-present" (not causesOnRecord.IsEmpty)
                Bind.items "editor.conclusion.causes" (causeToggleRows (draft view.Drafts Fields.conclusionCauses) causesOnRecord) ]
            @ form "editor.cause" (statusLine "causes" unsaved (empty causeFields) causesOnRecord.Length) cause causeFields view.Drafts
            @ [ Bind.items "editor.cause.roles" (choiceRows "role" (draft view.Drafts Fields.causeRole) causeRoles)
                Bind.items "editor.cause.hypothesis-options" (pickerRows (draft view.Drafts Fields.causeHypothesis) causeHypotheses)
                Bind.flag "editor.cause.hypothesis-unchosen" ((draft view.Drafts Fields.causeHypothesis) = "")
                Bind.flag "editor.cause.ready" (not causeHypotheses.IsEmpty)
                Bind.text
                    "editor.cause.missing"
                    (match cause with
                     | Unavailable _ -> ""
                     | _ -> if causeHypotheses.IsEmpty then "Record a hypothesis first (Hypotheses section); then record it here as a cause." else "") ]
            @ form "editor.corrective" (statusLine "corrective actions" unsaved (empty correctiveFields) correctiveCount) corrective correctiveFields view.Drafts
            @ [ Bind.items "editor.corrective.kinds" (choiceRows "corrective-kind" (draft view.Drafts Fields.correctiveKind) correctiveKinds)
                Bind.items "editor.corrective.cause-options" (pickerRows (draft view.Drafts Fields.correctiveCause) causeOptions)
                Bind.flag "editor.corrective.cause-unchosen" ((draft view.Drafts Fields.correctiveCause) = "")
                Bind.flag "editor.corrective.ready" (not causeOptions.IsEmpty)
                Bind.text
                    "editor.corrective.missing"
                    (match corrective with
                     | Unavailable _ -> ""
                     | _ -> if causeOptions.IsEmpty then "Record a cause first (Causal Analysis section); then record a corrective action for it here." else "") ])
