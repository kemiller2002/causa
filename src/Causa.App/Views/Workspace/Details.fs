// Owner: ui-2 (WI-INV-WORKSPACE, reassigned). Edit investigation details (INV-034, INV-035): offered only where the
// engine authorizes it (UI-004). Under causa-storage v1.0.0 there is no rename event, so a backend reports Unavailable
// or NotOffered and nothing is shown that cannot work.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module DetailsEditor =
    let private offered a =
        match a with
        | Available
        | Unavailable _ -> true
        | NotOffered -> false

    let bindings (view: WorkspaceView) : Bindings =
        let d = view.Details
        let errorOf field = d.Errors |> List.tryFind (fun e -> e.Field = FieldId field) |> Option.map (fun e -> e.Message) |> Option.defaultValue ""
        Bind.ofList
            [ Bind.flag "details.edit-offered" (d.Edit = Available && not d.Open)
              Bind.flag "details.edit-explained" (match d.Edit with Unavailable _ -> not d.Open | _ -> false)
              Bind.text "details.edit-unavailable" (match d.Edit with Unavailable r -> r | _ -> "")
              Bind.flag "details.open" (d.Open && offered d.Edit)
              Bind.text "details.title" d.Form.Title
              Bind.text "details.problem" d.Form.ProblemStatement
              Bind.text "details.question" d.Form.Question
              Bind.text "details.title-error" (errorOf "INV-002")
              Bind.text "details.problem-error" (errorOf "INV-001")
              Bind.text "details.question-error" (errorOf "INV-023")
              Bind.flag "details.errors-present" (not d.Errors.IsEmpty)
              Bind.flag "details.save-disabled" (d.SaveDetails <> Available)
              Bind.flag "details.cancel-offered" (offered d.Cancel) ]
