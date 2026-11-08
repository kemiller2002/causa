// Owner: ui-2 (WI-INV-WORKSPACE, reassigned). Edit investigation details (INV-034, INV-035): Edit details opens the
// editor with the title (UIF-DETAILS-TITLE), problem statement (UIF-DETAILS-PROBLEM) and primary investigative question
// (UIF-DETAILS-QUESTION); Save details (UIA-SAVE-DETAILS) records the changes, Cancel (INV-036) closes it and records
// nothing. Offered only where the engine authorizes it (UI-004): an investigation stored in causa-storage 1.2.0. An
// older one shows the edit unavailable with the reason; a frozen demonstration shows nothing.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module DetailsEditor =
    let private offered a =
        match a with
        | Available
        | Unavailable _ -> true
        | NotOffered -> false

    let private titleField = FieldId "UIF-DETAILS-TITLE"
    let private problemField = FieldId "UIF-DETAILS-PROBLEM"
    let private questionField = FieldId "UIF-DETAILS-QUESTION"

    let bindings (view: WorkspaceView) : Bindings =
        let d = view.Details
        let isOpen = d.Open && offered d.Edit
        let errorOf field = d.Errors |> List.filter (fun e -> e.Field = field) |> List.map (fun e -> e.Message) |> String.concat " "
        let invalid field = if d.Errors |> List.exists (fun e -> e.Field = field) then "true" else "false"
        Bind.ofList
            [ Bind.flag "details.edit-offered" (d.Edit = Available && not d.Open)
              Bind.flag "details.edit-explained" (match d.Edit with Unavailable _ -> not d.Open | _ -> false)
              Bind.text "details.edit-unavailable" (match d.Edit with Unavailable r -> r | _ -> "")
              Bind.flag "details.open" isOpen
              // The read-only statements give way to the editor while it is open (one control per name).
              Bind.flag "details.closed" (not isOpen)
              Bind.text "details.title" d.Form.Title
              Bind.text "details.problem" d.Form.ProblemStatement
              Bind.text "details.question" d.Form.Question
              Bind.text "details.title-error" (errorOf titleField)
              Bind.text "details.problem-error" (errorOf problemField)
              Bind.text "details.question-error" (errorOf questionField)
              Bind.text "details.title-aria-invalid" (invalid titleField)
              Bind.text "details.problem-aria-invalid" (invalid problemField)
              Bind.text "details.question-aria-invalid" (invalid questionField)
              Bind.flag "details.errors-present" (not d.Errors.IsEmpty)
              Bind.items "details.errors" [ for n, e in List.indexed d.Errors -> [ Bind.cellText "key" (string n); Bind.cellText "message" e.Message ] ]
              Bind.flag "details.save-disabled" (d.SaveDetails <> Available)
              Bind.flag "details.cancel-offered" (offered d.Cancel)
              Bind.text "details.status" (if isOpen then "" else d.Status) ]
