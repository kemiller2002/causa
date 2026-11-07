// Owner: ui-3 (T22, reassigned to ui). The Evidence section (INV-013, UI-005): each item as recorded, with its source;
// only a safe http(s) URL becomes a link (INV-029). Read-only.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module EvidenceSectionView =
    let bindings (view: WorkspaceView) : Bindings =
        let es = SectionData.analysis view.Section |> Option.map (fun a -> a.Evidence) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "evidence.present" (not es.IsEmpty)
              Bind.text "evidence.count" (string es.Length)
              Bind.items
                  "evidence.items"
                  [ for e in es ->
                        let url = e.SourceUrl |> Option.map SafeUrl.value
                        [ Bind.cellText "key" e.Id
                          Bind.cellText "label" (e.Label |> Option.defaultValue "No label recorded")
                          Bind.cellText "content" e.Content
                          Bind.cellText "category" (e.Category |> Option.defaultValue "Not categorised")
                          Bind.cellText "source" (e.SourceReference |> Option.defaultValue "Not recorded")
                          Bind.cellFlag "hasUrl" url.IsSome
                          Bind.cellText "url" (url |> Option.defaultValue "")
                          Bind.cellText "captured" (e.CapturedAt |> Option.map Copy.date |> Option.defaultValue "Not recorded")
                          Bind.cellText "bearing" (SectionData.linkSummary e.Links)
                          Bind.cellText "assessments" (SectionData.assessmentLines (fun l -> l.HypothesisStatement) e.Links)
                          Bind.cellFlag "hasAssessments" (e.Links |> List.exists (fun l -> l.Current)) ] ] ]
