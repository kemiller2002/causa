// Owner: ui-3 (T22, reassigned to ui). Conclusions (INV-033; v4.1.6 RC-6: strength and residual uncertainty shown
// together, with the causes each names), on the Overview; the causes (causal assertions: outcome, members in their
// roles, mechanism, state) on Causal Analysis; the corrective actions (R7.1, S12: kind in words, what it does, state,
// the causes it addresses) on Corrective Actions. Read-only.
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
                          Bind.cellFlag "hasResidual" c.ResidualUncertainty.IsSome
                          Bind.cellText "causes" (c.Causes |> List.map (sprintf "“%s”") |> String.concat "; ")
                          Bind.cellFlag "hasCauses" (not c.Causes.IsEmpty) ] ] ]

/// The causes on record (Causal Analysis), each with its members in their roles.
module CausesView =
    let stateTone (state: string) =
        match state with
        | "Confirmed" -> "positive"
        | "Superseded" -> "neutral"
        | _ -> "info"

    /// "Root cause: “<hypothesis>”", one line per member.
    let members (c: CauseCard) =
        c.Members |> List.map (fun m -> sprintf "%s: “%s”" m.Role m.HypothesisStatement) |> String.concat "\n"

    let bindings (view: WorkspaceView) : Bindings =
        let cs = SectionData.analysis view.Section |> Option.map (fun a -> a.Causes) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "causes.present" (not cs.IsEmpty)
              Bind.items
                  "causes.items"
                  [ for c in cs ->
                        [ Bind.cellText "key" c.Id
                          Bind.cellText "outcome" c.Outcome
                          Bind.cellText "state" c.State
                          Bind.cellText "tone" (stateTone c.State)
                          Bind.cellText "members" (members c)
                          Bind.cellText "mechanism" (c.Mechanism |> Option.defaultValue "")
                          Bind.cellFlag "hasMechanism" c.Mechanism.IsSome ] ] ]

/// The corrective actions on record (Corrective Actions section). S12-O8: the kind is always shown in words, and each
/// kind has its own tone, so a mitigation or workaround is never mistaken for a correction.
module CorrectionsView =
    let kindTone (kind: string) =
        match kind with
        | "Correction" -> "positive"
        | "Prevention" -> "info"
        | "Mitigation"
        | "Workaround" -> "attention"
        | _ -> "neutral"

    let bindings (view: WorkspaceView) : Bindings =
        let xs = SectionData.analysis view.Section |> Option.map (fun a -> a.CorrectiveActionItems) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "corrective.present" (not xs.IsEmpty)
              Bind.items
                  "corrective.items"
                  [ for x in xs ->
                        [ Bind.cellText "key" x.Id
                          Bind.cellText "kind" x.Kind
                          Bind.cellText "tone" (kindTone x.Kind)
                          Bind.cellText "description" x.Description
                          Bind.cellText "state" x.State
                          Bind.cellText "causes" (x.Causes |> List.map (sprintf "“%s”") |> String.concat "; ")
                          Bind.cellFlag "hasCauses" (not x.Causes.IsEmpty) ] ] ]
