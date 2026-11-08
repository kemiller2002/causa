// Owner: ui. The connection region of investigation-list (UI-001 before Connect; EXT-DATA-002/003/007). The credential
// never appears here: the form only knows whether one was entered (PF-015, INV-21).
namespace Causa.App.Views

open Causa.App
open Causa.Ui.Contracts

module ConnectionScreen =
    let private offered a =
        match a with
        | Available
        | Unavailable _ -> true
        | NotOffered -> false

    let private enabled a =
        match a with
        | Available -> true
        | _ -> false

    /// The SM-CONNECTION state in words, with no colour-only meaning (UI-016-O13).
    let describe (state: ConnectionState) =
        match state with
        | Disconnected -> "Not connected"
        | Connecting _ -> "Connecting"
        | Connected true -> "Connected (can save)"
        | Connected false -> "Connected (read-only)"
        | CredentialRefused -> "GitHub refused the access token"
        | Reauthenticating -> "Reconnecting"

    let writeScope s =
        match s with
        | ReadOnlyConnection -> "Read-only: this token cannot save changes"
        | Writable -> "This token can save changes"
        | WriteScopeUnknown -> ""

    let offlineText = "Offline: changes stay in this tab until the connection returns."

    let bindings (view: ConnectionView) : Bindings =
        let connected =
            match view.State with
            | Connected _ -> true
            | _ -> false
        Bind.ofList
            [ Bind.flag "connection.visible" (not connected)
              Bind.flag "connection.connected" connected
              Bind.text "connection.state" (describe view.State)
              Bind.flag "connection.connecting" (match view.State with Connecting _ | Reauthenticating -> true | _ -> false)
              Bind.flag "connection.refused" (view.State = CredentialRefused)
              Bind.text "connection.owner" view.Form.Owner
              Bind.text "connection.repository" view.Form.Repository
              Bind.text "connection.branch" view.Form.Branch
              Bind.flag "connection.errors-present" (not (List.isEmpty view.Errors))
              Bind.items
                  "connection.errors"
                  [ for e in view.Errors ->
                        [ Bind.cellText "field" (match e.Field with FieldId f -> f); Bind.cellText "message" e.Message ] ]
              Bind.flag "connection.connect-offered" (offered view.Connect)
              Bind.flag "connection.connect-disabled" (not (enabled view.Connect))
              Bind.flag "connection.disconnect-offered" (offered view.Disconnect)
              Bind.text "connection.connected-to" (view.Connected |> Option.defaultValue "")
              Bind.text "connection.write-scope" (writeScope view.WriteScope)
              // While the tab knows it is offline the line never says changes can be saved (Administration's wording).
              Bind.flag "connection.online" (connected && not view.Offline)
              Bind.flag "connection.offline" (connected && view.Offline)
              Bind.text "connection.offline-text" offlineText ]
