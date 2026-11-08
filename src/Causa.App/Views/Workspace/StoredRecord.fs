// Owner: ui-2 (WI-INV-WORKSPACE, reassigned). The investigation as stored (Data Visible): shown when the kernel has no
// projection to offer, so the user still sees what GitHub holds, with no replay claimed (UI-010 fail closed, UI-023).
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module StoredRecord =
    let bindings (view: WorkspaceView) : Bindings =
        let stored =
            match view.Stored with
            | Ready v -> Some(Visible.value v)
            | _ -> None
        // Only when the frame (the kernel's view) is not available: the stored record never competes with it.
        let shownAlone = stored.IsSome && (match view.Frame with Ready _ -> false | _ -> true)
        Bind.ofList
            [ Bind.flag "stored.present" shownAlone
              Bind.text "stored.title" (stored |> Option.map (fun s -> s.Title) |> Option.defaultValue "")
              Bind.text "stored.created" (stored |> Option.map (fun s -> Copy.dateOption s.CreatedAt) |> Option.defaultValue "")
              Bind.text "stored.updated" (stored |> Option.map (fun s -> Copy.dateOption s.UpdatedAt) |> Option.defaultValue "")
              Bind.text "stored.count" (stored |> Option.map (fun s -> string s.EventCount) |> Option.defaultValue "")
              Bind.items
                  "stored.events"
                  [ for e in (stored |> Option.map (fun s -> s.Events) |> Option.defaultValue []) ->
                        [ Bind.cellText "key" (string e.Version)
                          Bind.cellText "version" (string e.Version)
                          Bind.cellText "kind" e.EventType
                          Bind.cellText "when" (Copy.date e.OccurredAt)
                          Bind.cellText "who" e.ActorId ] ] ]
