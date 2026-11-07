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

    let private form (prefix: string) (available: Availability) (fields: (string * FieldId) list) (drafts: EditorDrafts) =
        let errs = errorsFor (List.map snd fields) drafts.Errors
        [ yield Bind.flag (prefix + ".offered") (match available with NotOffered -> false | _ -> true)
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
        Bind.ofList(
            form "editor.observation" (availability actions recordObservation) [ "text", observationText; "source", observationSource ] view.Drafts
            @ form "editor.hypothesis" (availability actions recordHypothesis) [ "statement", hypothesisStatement; "explanation", hypothesisExplanation ] view.Drafts)
