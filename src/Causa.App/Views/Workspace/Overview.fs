// Owner: ui-3 (T22, reassigned to ui). The investigation's Overview section (UI-003, UI-029-O2): what has been observed,
// how much is on record and what has been concluded, read-only, from the domain engine's projection (Data Understood).
// Also the shared reading of a SectionView every section module uses.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

/// Which section the view carries and its projection.
module SectionData =
    let slug (s: SectionView) =
        match s with
        | OverviewSection _ -> "overview"
        | CausalAnalysisView _ -> "causal-analysis"
        | EvidenceView _ -> "evidence"
        | HypothesesView _ -> "hypotheses"
        | TimelineView _ -> "timeline"
        | CorrectiveActionsView _ -> "corrective-actions"

    let remote (s: SectionView) : Remote<Understood<AnalysisProjection>> =
        match s with
        | OverviewSection r
        | CausalAnalysisView r
        | EvidenceView r
        | HypothesesView r
        | TimelineView r
        | CorrectiveActionsView r -> r

    /// The projection, when it is ready.
    let analysis (s: SectionView) : AnalysisProjection option =
        match remote s with
        | Ready u -> Some(Understood.value u)
        | _ -> None

    let slugs = [ "overview"; "causal-analysis"; "evidence"; "hypotheses"; "timeline"; "corrective-actions" ]

    /// Tone words the stylesheet maps to Forma status tokens; the text always says the same thing (UI-016-O13).
    let kindText (k: KindShown) =
        match k with
        | CatalogKind Supports -> "Supports"
        | CatalogKind Contradicts -> "Contradicts"
        | CatalogKind ConsistentWith -> "Consistent with"
        | CatalogKind Inconclusive -> "Inconclusive"
        | UninterpretedKind s -> if s = "" then "Not stated" else string (System.Char.ToUpperInvariant s[0]) + s.Substring 1

    let kindTone (k: KindShown) =
        match k with
        | CatalogKind Supports -> "positive"
        | CatalogKind Contradicts -> "critical"
        | CatalogKind ConsistentWith -> "info"
        | _ -> "neutral"

    /// "2 supporting · 1 contradicting" for a set of assessments (current ones only).
    let linkSummary (links: LinkCard list) =
        let current = links |> List.filter (fun l -> l.Current)
        let n k = current |> List.filter (fun l -> l.Kind = k) |> List.length
        [ n (CatalogKind Supports), "supporting"
          n (CatalogKind Contradicts), "contradicting"
          n (CatalogKind ConsistentWith), "consistent"
          n (CatalogKind Inconclusive), "inconclusive" ]
        |> List.filter (fun (c, _) -> c > 0)
        |> List.map (fun (c, w) -> sprintf "%d %s" c w)
        |> function
            | [] -> if current.IsEmpty then "No evidence assessed against it yet" else sprintf "%d assessment(s)" current.Length
            | parts -> String.concat " · " parts

    /// Each current assessment as one line, "Supports — <other side>: <rationale>", for a card's detail (INV-044:
    /// the assessment shows on both the evidence and the hypothesis).
    let assessmentLines (otherSide: LinkCard -> string) (links: LinkCard list) =
        links
        |> List.filter (fun l -> l.Current)
        |> List.map (fun l ->
            let why = if l.Rationale.Trim() = "" then "" else ": " + l.Rationale
            sprintf "%s — %s%s" (kindText l.Kind) (otherSide l) why)
        |> String.concat "\n"

module OverviewSectionView =
    let bindings (view: WorkspaceView) : Bindings =
        let a = SectionData.analysis view.Section
        let count f = a |> Option.map (f >> string) |> Option.defaultValue ""
        Bind.ofList
            [ Bind.text "overview.hypotheses" (count (fun a -> a.Hypotheses.Length))
              Bind.text "overview.evidence" (count (fun a -> a.Evidence.Length))
              Bind.text "overview.relationships" (count (fun a -> a.Relationships |> List.filter (fun l -> l.Current) |> List.length))
              Bind.text "overview.conclusions" (count (fun a -> a.Conclusions.Length))
              Bind.text "overview.contradictions" (a |> Option.map (fun a -> Copy.count a.UnresolvedContradictions) |> Option.defaultValue "")
              Bind.flag "overview.observations-present" (a |> Option.exists (fun a -> not a.Observations.IsEmpty))
              Bind.items
                  "overview.observations"
                  [ for o in (a |> Option.map (fun a -> a.Observations) |> Option.defaultValue []) ->
                        [ Bind.cellText "key" o.Id
                          Bind.cellText "text" o.Text
                          Bind.cellText "source" (o.SourceReference |> Option.defaultValue "")
                          Bind.cellFlag "hasSource" o.SourceReference.IsSome
                          Bind.cellText "when" (o.ObservedAt |> Option.map Copy.date |> Option.defaultValue "") ] ] ]
