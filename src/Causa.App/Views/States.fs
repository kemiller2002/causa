// Owner: ui. Loading, empty and failure states every screen shares (UI-010, WASM-LOAD-003): one ScreenState in, the
// flags and the plain-language failure copy out, under a screen's own prefix. Also the not-found screen.
namespace Causa.App.Views

open Causa.Ui.Contracts

module States =
    let bindings = ScreenStates.bindings
    let ofRemote = ScreenStates.ofRemote
    let activity = ScreenStates.activity

    /// not-found (`#/{*rest}`): what was asked for and the way back (SCREEN-CONTRACTS missing targets).
    let notFound (view: NotFoundView) : Bindings =
        Bind.ofList
            [ Bind.text "not-found.requested" view.Requested
              Bind.text "not-found.what" view.Copy.What
              Bind.text "not-found.preserved" view.Copy.Preserved
              Bind.text "not-found.next" view.Copy.NextStep ]

    /// The copy for a route nobody defines.
    let notFoundCopy : FailureCopy =
        { What = "There is no page at this address."
          Preserved = "Nothing in your data repository was changed."
          NextStep = "Go back to Investigations."
          Retry = false }
