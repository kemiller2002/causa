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

    /// One status action of a card (UIA-MARK-PLAUSIBLE ...): offered, disabled, and the kernel's reason when disabled.
    let transitionCells (h: HypothesisCard) (prefix: string) (action: ActionId) =
        let a = h.Transitions |> Map.tryFind action |> Option.defaultValue NotOffered
        [ Bind.cellFlag (prefix + "Offered") (match a with NotOffered -> false | _ -> true)
          Bind.cellFlag (prefix + "Disabled") (match a with Available -> false | _ -> true) ]

    /// The kernel's reasons for the status actions shown but unavailable, one line each: "Mark supported: <reason>".
    let transitionNotes (h: HypothesisCard) =
        [ Actions.markPlausible, "Mark plausible"; Actions.markSupported, "Mark supported"; Actions.falsifyHypothesis, "Falsify"; Actions.reopenHypothesis, "Reopen" ]
        |> List.choose (fun (a, label) ->
            match h.Transitions |> Map.tryFind a with
            | Some(Unavailable reason) -> Some(sprintf "%s: %s" label reason)
            | _ -> None)
        |> String.concat "\n"

    let bindings (view: WorkspaceView) : Bindings =
        let hs = SectionData.analysis view.Section |> Option.map (fun a -> a.Hypotheses) |> Option.defaultValue []
        Bind.ofList(
            [ Bind.flag "hypotheses.present" (not hs.IsEmpty)
              Bind.text "hypotheses.count" (string hs.Length) ]
            @ SectionData.paged "hypotheses" "hypotheses"
                  [ for h in hs ->
                        [ Bind.cellText "key" h.Id
                          Bind.cellText "statement" h.Statement
                          Bind.cellText "state" h.State
                          Bind.cellText "tone" (stateTone h.State)
                          Bind.cellText "explanation" (h.Explanation |> Option.defaultValue "")
                          Bind.cellFlag "hasExplanation" h.Explanation.IsSome
                          Bind.cellText "evidence" (SectionData.linkSummary h.Links)
                          Bind.cellText "bearings" (SectionData.hypothesisBearings h.Links)
                          Bind.cellText "assessments" (SectionData.hypothesisAssessments h.Links)
                          Bind.cellFlag "hasRationale" (SectionData.hasRationale h.Links)
                          Bind.cellFlag "hasAssessments" (h.Links |> List.exists (fun l -> l.Current))
                          Bind.cellFlag "hasStatusActions" (not h.Transitions.IsEmpty)
                          Bind.cellText "statusNotes" (transitionNotes h)
                          Bind.cellFlag "hasStatusNotes" (transitionNotes h <> "") ]
                        @ transitionCells h "plausible" Actions.markPlausible
                        @ transitionCells h "supported" Actions.markSupported
                        @ transitionCells h "falsify" Actions.falsifyHypothesis
                        @ transitionCells h "reopen" Actions.reopenHypothesis ])
