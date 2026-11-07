// Owner: ui-3 (T22, reassigned to ui). The Hypotheses section (INV-010, UI-003): each hypothesis with its state in
// words, its mechanism and how the evidence bears on it, read-only.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module HypothesesSectionView =
    let stateTone (state: string) =
        match state with
        | "Supported" -> "positive"
        | "Weakened" -> "attention"
        | "Falsified" -> "critical"
        | "Superseded" -> "neutral"
        | _ -> "info"

    let bindings (view: WorkspaceView) : Bindings =
        let hs = SectionData.analysis view.Section |> Option.map (fun a -> a.Hypotheses) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "hypotheses.present" (not hs.IsEmpty)
              Bind.text "hypotheses.count" (string hs.Length)
              Bind.items
                  "hypotheses.items"
                  [ for h in hs ->
                        [ Bind.cellText "key" h.Id
                          Bind.cellText "statement" h.Statement
                          Bind.cellText "state" h.State
                          Bind.cellText "tone" (stateTone h.State)
                          Bind.cellText "explanation" (h.Explanation |> Option.defaultValue "")
                          Bind.cellFlag "hasExplanation" h.Explanation.IsSome
                          Bind.cellText "evidence" (SectionData.linkSummary h.Links) ] ] ]
