// Owner: integration (T17). Limen SemanticEvent (name, key, value) -> UiIntent (shape: the governed prototype,
// proposals/r10-contract-first-ui/prototype/screens/Events.fs). Mechanical: the element's data-event IS the field or
// action id. The token field becomes the opaque Credential.T here, before any backend sees it (PF-015, INV-21).
namespace Causa.App.Ui

open Causa.Persistence
open Causa.Ui.Contracts

[<RequireQualifiedAccess>]
module Events =
    let private isField (name: string) =
        Connection.fields @ StartForm.fields @ DetailsForm.fields @ ContentForms.all
        |> List.exists (fun f -> FieldId.value f.Field = name)

    let private isAction (name: string) =
        ActionContracts.all |> List.exists (fun a -> ActionId.value a.Action = name)

    /// The intent of one Limen event; None for a name no field or action declares (a wiring bug, reported by the caller).
    let toIntent (name: string) (key: string option) (value: string option) : UiIntent option =
        let v = value |> Option.defaultValue ""
        if name = "navigate" then Some(UiIntent.Navigate(Navigation.parse v))
        elif name = FieldId.value Fields.token then Some(UiIntent.Secret(Fields.token, Credential.create v))
        elif isField name then Some(UiIntent.Input(FieldId name, v, key))
        elif isAction name then Some(UiIntent.Activate(ActionId name, key))
        else None
