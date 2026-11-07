// Owner: ui. Administration (`#/administration`, UI-018, UI-020-O2): operational information, read-only. Each fact
// is shown with its words; unknown is never shown as verified.
namespace Causa.App.Views

open Causa.Ui.Contracts

module AdministrationScreen =
    let facts (view: AdministrationView) =
        [ "provider", view.Provider
          "repository", view.Repository
          "connection", view.Connection
          "catalog", view.Catalog
          "storage-contract", view.StorageContract
          "application-version", view.ApplicationVersion
          "deployed-commit", view.DeployedCommit
          "data-validation", view.DataValidation ]

    /// Forma's status-lozenge states; the words are always shown with them (UI-016-O13).
    let lozengeState (s: SystemFactStatus) =
        match s with
        | FactKnown _ -> "ok"
        | FactUnknown
        | FactUnavailable -> "unknown"
        | FactFailed _ -> "blocked"

    let bindings (view: AdministrationView) : Bindings =
        Bind.ofList
            [ Bind.items
                  "administration.facts"
                  [ for key, f in facts view ->
                        let text, tone = Language.fact f.Status
                        [ Bind.cellText "key" key
                          Bind.cellText "label" f.Label
                          Bind.cellText "value" text
                          Bind.cellText "tone" (Language.toneName tone)
                          Bind.cellText "state" (lozengeState f.Status) ] ]
              Bind.flag "administration.diagnostics-present" (not (List.isEmpty view.Diagnostics))
              Bind.items
                  "administration.diagnostics"
                  (view.Diagnostics |> List.mapi (fun i d -> [ Bind.cellText "key" (string i); Bind.cellText "text" d ])) ]
