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

    /// The kind as the verb of a sentence, in the stored word's own lower case so it reads the same as the choice the
    /// user made in the link editor (INV-031): "supports", "contradicts", "is consistent with", "is inconclusive about".
    let kindVerb (k: KindShown) =
        match k with
        | CatalogKind Supports -> "supports"
        | CatalogKind Contradicts -> "contradicts"
        | CatalogKind ConsistentWith -> "is consistent with"
        | CatalogKind Inconclusive -> "is inconclusive about"
        | UninterpretedKind "insufficient" -> "is insufficient to judge"
        | UninterpretedKind s -> if s.Trim() = "" then "bears (kind not stated) on" else s

    let private why (l: LinkCard) = if l.Rationale.Trim() = "" then "" else " Why: " + l.Rationale

    /// Each current assessment as one sentence for a card's detail (INV-044: the assessment shows on both the evidence
    /// and the hypothesis). On an evidence card the evidence is the subject: "supports “<hypothesis>”. Why: …".
    let evidenceAssessments (links: LinkCard list) =
        links
        |> List.filter (fun l -> l.Current)
        |> List.map (fun l -> sprintf "%s \u201c%s\u201d.%s" (kindVerb l.Kind) l.HypothesisStatement (why l))
        |> String.concat "\n"

    /// On a hypothesis card the evidence is named: "“<evidence>” contradicts it. Why: …".
    let hypothesisAssessments (links: LinkCard list) =
        links
        |> List.filter (fun l -> l.Current)
        |> List.map (fun l -> sprintf "\u201c%s\u201d %s it.%s" l.EvidenceLabel (kindVerb l.Kind) (why l))
        |> String.concat "\n"

    /// How many records a section shows before the rest are folded into a "Show more" disclosure.
    let firstShown = 10

    /// A long list as two data-each lists: the first rows, and the rest behind a disclosure (no engine state needed:
    /// the disclosure is the browser's own <details>).
    let paged (prefix: string) (noun: string) (rows: (string * BindingPrimitive) list list) : (string * BindingValue) list =
        let first, rest = if rows.Length <= firstShown then rows, [] else List.splitAt firstShown rows
        [ Bind.items (prefix + ".items") first
          Bind.items (prefix + ".more") rest
          Bind.flag (prefix + ".more-present") (not rest.IsEmpty)
          Bind.text (prefix + ".more-label") (sprintf "Show %d more %s" rest.Length noun) ]

module OverviewSectionView =
    let bindings (view: WorkspaceView) : Bindings =
        let a = SectionData.analysis view.Section
        let count f = a |> Option.map (f >> string) |> Option.defaultValue ""
        Bind.ofList(
            [ Bind.text "overview.hypotheses" (count (fun a -> a.Hypotheses.Length))
              Bind.text "overview.evidence" (count (fun a -> a.Evidence.Length))
              Bind.text "overview.relationships" (count (fun a -> a.Relationships |> List.filter (fun l -> l.Current) |> List.length))
              Bind.text "overview.conclusions" (count (fun a -> a.Conclusions.Length))
              Bind.text "overview.contradictions" (a |> Option.map (fun a -> Copy.count a.UnresolvedContradictions) |> Option.defaultValue "")
              Bind.flag "overview.observations-present" (a |> Option.exists (fun a -> not a.Observations.IsEmpty)) ]
            @ SectionData.paged "overview.observations" "observations"
                  [ for o in (a |> Option.map (fun a -> a.Observations) |> Option.defaultValue []) ->
                        [ Bind.cellText "key" o.Id
                          Bind.cellText "text" o.Text
                          Bind.cellText "source" (o.SourceReference |> Option.defaultValue "")
                          Bind.cellFlag "hasSource" o.SourceReference.IsSome
                          Bind.cellText "when" (o.ObservedAt |> Option.map Copy.date |> Option.defaultValue "") ] ])
