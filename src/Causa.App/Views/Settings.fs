// Owner: ui. Settings (`#/settings`, UI-019, UI-020-O2): configurable preferences, each with its scope and effective
// value, kept apart from the verified connection.
namespace Causa.App.Views

open Causa.Ui.Contracts

module SettingsScreen =
    let scope s =
        match s with
        | SessionScope -> "This tab only"
        | DeviceScope -> "This device"

    let bindings (view: SettingsView) : Bindings =
        let connectionText, _ = Language.fact view.ConnectionPreference.Status
        Bind.ofList
            [ Bind.items
                  "settings.preferences"
                  [ for p in view.Preferences ->
                        [ Bind.cellText "key" p.Id
                          Bind.cellText "label" p.Label
                          Bind.cellText "scope" (scope p.Scope)
                          Bind.cellText "effective" p.Effective
                          Bind.cellText "options" (String.concat ", " p.Options) ] ]
              Bind.flag "settings.preferences-present" (not (List.isEmpty view.Preferences))
              Bind.text "settings.connection-label" view.ConnectionPreference.Label
              Bind.text "settings.connection-value" connectionText ]
