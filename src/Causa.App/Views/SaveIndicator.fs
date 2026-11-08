// Owner: ui-2 (WI-INV-WORKSPACE, reassigned). The save state (INV-015 Save, INV-016 save state, INV-043 outcomes,
// UI-006, PERSIST-030..033): the frozen machine's projection, drawn with Forma's status lozenge and always a word. The
// word is the state's own (Language.saveState); "Saved" appears only when the projection says Saved, which the save
// machine reaches only from a read-back confirmation (PERSIST-023). Also composes the whole workspace screen.
namespace Causa.App.Views

open Causa.App
open Causa.Ui.Contracts
open Causa.App.Views.Workspace

module SaveIndicator =
    /// Forma's lozenge state for each save state (components.css: ok, attention, unknown, blocked), so every state
    /// carries the lozenge glyph as well as its word (PERSIST-030). Saving is "unknown": not yet confirmed.
    let lozengeState kind =
        match kind with
        | SavedKind -> "ok"
        | UnsavedKind
        | OfflineKind -> "attention"
        | SavingKind
        | UnknownOutcomeKind -> "unknown"
        | ConflictKind
        | SaveFailedKind -> "blocked"

    let private offered a =
        match a with
        | Available
        | Unavailable _ -> true
        | NotOffered -> false

    let private has action (save: SavePresentation) = List.contains action save.Indicator.Actions

    /// The save-state dialog ("About saving"): what the current state means for the user's changes and what to do
    /// next. It never offers to throw changes away: Discard exists only in a conflict, on request (PERSIST-024-O4).
    let explain kind =
        match kind with
        | SavedKind -> "Every change in this investigation is saved in your data repository."
        | UnsavedKind -> "Changes recorded in this tab are not saved yet. Select Save to keep them; they are lost if this tab closes first."
        | SavingKind -> "A save is in progress. Keep this tab open until the state reads Saved."
        | OfflineKind -> "You are offline. Your changes stay in this tab and can be saved when the connection returns."
        | ConflictKind -> "Someone saved a newer version of this investigation. Review the conflict to choose which changes to keep."
        | UnknownOutcomeKind -> "Causa is checking whether the last save reached your data repository. Select Check again to find out."
        | SaveFailedKind -> "The last save did not complete. Your changes are still in this tab: select Retry."

    let bindings (save: SavePresentation option) : Bindings =
        match save with
        | None -> Bind.ofList [ Bind.flag "save.present" false ]
        | Some s ->
            let word, _, tone = Language.saveState s.Kind
            Bind.ofList
                [ Bind.flag "save.present" true
                  Bind.text "save.label" word
                  Bind.text "save.state" (lozengeState s.Kind)
                  Bind.text "save.tone" (Language.toneName tone)
                  Bind.text "save.text" (if s.Indicator.Text = word then "" else s.Indicator.Text)
                  Bind.flag "save.last-saved-present" s.Indicator.LastSavedCommitTime.IsSome
                  Bind.text "save.last-saved" (Copy.dateOption s.Indicator.LastSavedCommitTime)
                  Bind.flag "save.offered" (offered s.Save)
                  Bind.flag "save.disabled" (s.Save <> Available)
                  Bind.text "save.unavailable" (match s.Save with Unavailable r -> r | _ -> "")
                  Bind.flag "save.retry-offered" (has Retry s)
                  Bind.flag "save.reconnect-offered" (has Reconnect s)
                  Bind.flag "save.check-again-offered" (has CheckAgainAction s)
                  Bind.flag "save.conflict-present" s.Conflict.IsSome
                  Bind.text "save.conflict" (s.Conflict |> Option.map (fun c -> c.Explanation) |> Option.defaultValue "")
                  Bind.flag "save.not-saved-present" (not s.NotSaved.IsEmpty)
                  Bind.items "save.not-saved" [ for n in s.NotSaved -> [ Bind.cellText "key" n; Bind.cellText "title" n ] ]
                  Bind.text "save.dialog-title" (sprintf "Save state: %s" word)
                  Bind.text "save.dialog-explanation" (explain s.Kind) ]
