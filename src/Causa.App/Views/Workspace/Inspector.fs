// Owner: ui-3 (T22, reassigned to ui). The content editors of a writable working copy: record an observation (INV-024,
// INV-025, INV-014) and record a hypothesis (INV-026, INV-011, INV-009), with Cancel (INV-036). Offered only where the
// projection says the engine authorizes them (UI-004); a frozen demonstration gets none. The text is the view's
// drafts; the view never builds a command.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module ContentEditors =
    let private observationText = FieldId "INV-024"
    let private observationSource = FieldId "INV-025"
    let private hypothesisStatement = FieldId "INV-026"
    let private hypothesisExplanation = FieldId "INV-011"
    let private recordObservation = ActionId "INV-014"
    let private recordHypothesis = ActionId "INV-009"

    let private availability (actions: Map<ActionId, Availability>) (a: ActionId) =
        actions |> Map.tryFind a |> Option.defaultValue NotOffered

    let private errorsFor (fields: FieldId list) (errors: ValidationError list) =
        errors |> List.filter (fun e -> List.contains e.Field fields)

    /// After a record the form is empty again and the change is unsaved: say where the item went and what keeps it
    /// (the owner recorded a hypothesis twice when the form looked unchanged). Stated from the state, not an event.
    let statusLine (noun: string) (unsaved: bool) (draftsEmpty: bool) (recorded: int) =
        if unsaved && draftsEmpty && recorded > 0 then
            sprintf "Recorded %s are listed above. Your changes are not saved yet: select Save to keep them." noun
        else ""

    let private form (prefix: string) (status: string) (available: Availability) (fields: (string * FieldId) list) (drafts: EditorDrafts) =
        let errs = errorsFor (List.map snd fields) drafts.Errors
        [ yield Bind.text (prefix + ".status") status
          yield Bind.flag (prefix + ".offered") (match available with NotOffered -> false | _ -> true)
          yield Bind.flag (prefix + ".disabled") (match available with Available -> false | _ -> true)
          yield Bind.text (prefix + ".unavailable") (match available with Unavailable r -> r | _ -> "")
          for name, f in fields do
              yield Bind.text (prefix + "." + name) (drafts.Values |> Map.tryFind f |> Option.defaultValue "")
          yield Bind.flag (prefix + ".errors-present") (not errs.IsEmpty)
          yield
              Bind.items
                  (prefix + ".errors")
                  [ for e in errs -> [ Bind.cellText "field" (match e.Field with FieldId f -> f); Bind.cellText "message" e.Message ] ] ]

    let bindings (view: WorkspaceView) : Bindings =
        let actions =
            SectionData.analysis view.Section |> Option.map (fun a -> a.Actions) |> Option.defaultValue Map.empty
        let a = SectionData.analysis view.Section
        let unsaved = view.Save |> Option.exists (fun sv -> sv.Kind = UnsavedKind)
        let empty (fs: FieldId list) = fs |> List.forall (fun f -> view.Drafts.Values |> Map.tryFind f |> Option.forall (fun v -> v.Trim() = ""))
        let count f = a |> Option.map f |> Option.defaultValue 0
        // The start's own observation is the problem statement; only recorded ones count.
        let observations = count (fun a -> max 0 (a.Observations.Length - 1))
        let hypotheses = count (fun a -> a.Hypotheses.Length)
        let obsStatus = statusLine "observations" unsaved (empty [ observationText; observationSource ]) observations
        let hypStatus = statusLine "hypotheses" unsaved (empty [ hypothesisStatement; hypothesisExplanation ]) hypotheses
        Bind.ofList(
            form "editor.observation" obsStatus (availability actions recordObservation) [ "text", observationText; "source", observationSource ] view.Drafts
            @ form "editor.hypothesis" hypStatus (availability actions recordHypothesis) [ "statement", hypothesisStatement; "explanation", hypothesisExplanation ] view.Drafts)
