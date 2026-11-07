// Owner: ui-3 (T22, reassigned to ui). Conclusions (INV-033; v4.1.6 RC-6: strength and residual uncertainty shown
// together), on the Overview. Read-only.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module ConclusionsView =
    let bindings (view: WorkspaceView) : Bindings =
        let cs = SectionData.analysis view.Section |> Option.map (fun a -> a.Conclusions) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "conclusions.present" (not cs.IsEmpty)
              Bind.items
                  "conclusions.items"
                  [ for c in cs ->
                        [ Bind.cellText "key" c.Id
                          Bind.cellText "statement" c.Statement
                          Bind.cellText "strength" c.Strength
                          Bind.cellText "residual" (c.ResidualUncertainty |> Option.defaultValue "")
                          Bind.cellFlag "hasResidual" c.ResidualUncertainty.IsSome ] ] ]
