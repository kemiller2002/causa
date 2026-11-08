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

    /// Whether this tab holds changes that Discard unsaved changes would drop: recorded but not saved (also while
    /// offline, or after a save failed). Not while a save is in flight or its outcome is being checked, and not in a
    /// conflict, which has its own Review.
    let discardPossible kind =
        match kind with
        | UnsavedKind
        | OfflineKind
        | SaveFailedKind -> true
        | SavedKind
        | SavingKind
        | UnknownOutcomeKind
        | ConflictKind -> false

    /// The Discard dialog's heading and explanation for each state: what would be discarded, or why nothing can be.
    let discardCopy kind =
        match kind with
        | UnsavedKind
        | OfflineKind
        | SaveFailedKind ->
            "Discard unsaved changes?",
            "The changes recorded in this tab since the last save will be removed, and the investigation returns to its last saved version. This cannot be undone."
        | SavedKind -> "Nothing to discard", "Every change in this investigation is saved."
        | SavingKind -> "Nothing to discard yet", "A save is in progress. Once it finishes, there is nothing left to discard."
        | UnknownOutcomeKind ->
            "Nothing to discard yet",
            "Causa is checking whether the last save reached your data repository. Use Check again; nothing can be discarded until the outcome is known."
        | ConflictKind -> "Use Review instead", "This save is in conflict with a newer version. Review the conflict to choose which changes to keep."

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
                  Bind.flag "save.discard-possible" (discardPossible s.Kind)
                  Bind.flag "save.discard-nothing" (not (discardPossible s.Kind))
                  Bind.text "save.discard-title" (fst (discardCopy s.Kind))
                  Bind.text "save.discard-explanation" (snd (discardCopy s.Kind)) ]
