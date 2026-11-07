// Owner: ui. Investigation language (UI-022, UI-016-O13): the words every status is shown with, so no lifecycle,
// confidence, danger or sync state is carried by colour alone, and system vocabulary stays out of investigation copy.
namespace Causa.App.Views

open Causa.Ui.Contracts

/// The semantic tone a status is drawn with. CSS maps each to a Forma status token (UI-016-O8); the text always
/// accompanies it.
type Tone =
    | Neutral
    | Positive
    | Attention
    | Critical
    | Informational

module Language =
    let toneName t =
        match t with
        | Neutral -> "neutral"
        | Positive -> "positive"
        | Attention -> "attention"
        | Critical -> "critical"
        | Informational -> "info"

    /// Save states (PERSIST-030): the word, a non-colour glyph and the tone.
    let saveState kind =
        match kind with
        | SavedKind -> "Saved", "✓", Positive
        | UnsavedKind -> "Unsaved", "●", Attention
        | SavingKind -> "Saving", "…", Informational
        | OfflineKind -> "Offline", "⊘", Attention
        | ConflictKind -> "Conflict", "⚠", Critical
        | UnknownOutcomeKind -> "Unknown commit outcome", "?", Attention
        | SaveFailedKind -> "Save failed", "✕", Critical

    /// Words a user-facing investigation screen never uses (UI-022: system detail belongs in Administration).
    let forbiddenInInvestigationCopy = [ "replay"; "schema"; "commit SHA"; "sha256"; "catalog identifier" ]

    let isInvestigationSafe (text: string) =
        forbiddenInInvestigationCopy |> List.forall (fun w -> not (text.Contains(w, System.StringComparison.OrdinalIgnoreCase)))

    /// A system fact in words: unknown, unavailable, failed and known are distinct (UI-018).
    let fact (s: SystemFactStatus) =
        match s with
        | FactKnown v -> v, Positive
        | FactUnknown -> "Not known", Neutral
        | FactUnavailable -> "Not available", Neutral
        | FactFailed why -> "Failed: " + why, Critical
