// Owner: ui-3 (T22, reassigned to ui). Causal Analysis (UI-026, INV-044): every current evidence-hypothesis assessment
// as text, the accessible equivalent of the graph (UI-011-O5). Read-only.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module CausalAnalysisSectionView =
    let bindings (view: WorkspaceView) : Bindings =
        let ls =
            SectionData.analysis view.Section
            |> Option.map (fun a -> a.Relationships |> List.filter (fun l -> l.Current))
            |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "relationships.present" (not ls.IsEmpty)
              Bind.text "relationships.count" (string ls.Length)
              Bind.items
                  "relationships.items"
                  [ for l in ls ->
                        [ Bind.cellText "key" l.Id
                          Bind.cellText "evidence" l.EvidenceLabel
                          Bind.cellText "hypothesis" l.HypothesisStatement
                          Bind.cellText "kind" (SectionData.kindText l.Kind)
                          Bind.cellText "tone" (SectionData.kindTone l.Kind)
                          Bind.cellText "rationale" l.Rationale
                          Bind.cellFlag "hasRationale" (l.Rationale.Trim() <> "") ] ] ]
