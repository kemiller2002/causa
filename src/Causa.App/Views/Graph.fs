// Owner: ui-3 (T23 demo piece, reassigned to ui). The causal graph of the Causal Analysis section: evidence on the left,
// hypotheses in the middle, conclusions on the right, and a line for every current evidence-hypothesis assessment,
// styled by its kind and always carrying its word (UI-016-O13). Pure and deterministic: a layout is a function of the
// projection, computed here in F#, with no JavaScript layout. The textual equivalent (UI-011-O5, UI-026) is the
// relationship index the section always renders beside it.
namespace Causa.App.Views

open System
open Causa.Ui.Contracts
open Causa.App.Views.Workspace

[<RequireQualifiedAccess>]
type GraphColumn =
    | EvidenceColumn
    | HypothesisColumn
    | ConclusionColumn

type GraphNode =
    { Key: string
      Column: GraphColumn
      X: float
      Y: float
      Width: float
      Height: float
      Label: string
      FullText: string }

[<RequireQualifiedAccess>]
type EdgeKind =
    | SupportsEdge
    | ContradictsEdge
    | OtherEdge

type GraphEdge =
    { Key: string
      From: string
      To: string
      Kind: EdgeKind
      Word: string
      Path: string
      LabelX: float
      LabelY: float
      Title: string }

type GraphLayout =
    { Width: float
      Height: float
      Nodes: GraphNode list
      Edges: GraphEdge list
      /// Evidence on record that no current assessment links to a hypothesis: listed in the summary, not drawn.
      UnlinkedEvidence: int }

module CausalGraph =
    let nodeWidth = 240.0
    let nodeHeight = 44.0
    let rowGap = 12.0
    let columnGap = 220.0
    let margin = 16.0
    let headerHeight = 32.0
    let labelLength = 30

    let truncate (n: int) (s: string) =
        let one = s.Replace("\r", " ").Replace("\n", " ").Trim()
        if one.Length <= n then one else one.Substring(0, n - 1).TrimEnd() + "…"

    let columnX (c: GraphColumn) =
        let i =
            match c with
            | GraphColumn.EvidenceColumn -> 0.0
            | GraphColumn.HypothesisColumn -> 1.0
            | GraphColumn.ConclusionColumn -> 2.0
        margin + i * (nodeWidth + columnGap)

    let rowYAt (slot: float) = margin + headerHeight + slot * (nodeHeight + rowGap)
    let rowY (i: int) = rowYAt (float i)

    let private f (v: float) = v.ToString("0.##", Globalization.CultureInfo.InvariantCulture)

    let edgeKind (k: KindShown) =
        match k with
        | CatalogKind Supports -> EdgeKind.SupportsEdge
        | CatalogKind Contradicts -> EdgeKind.ContradictsEdge
        | _ -> EdgeKind.OtherEdge

    /// The layout of a projection. Hypotheses keep the projection's order; linked evidence is ordered by the mean row of
    /// the hypotheses it bears on (fewer crossings), ties kept in the projection's order; conclusions keep theirs.
    let layout (a: AnalysisProjection) : GraphLayout =
        let links = a.Relationships |> List.filter (fun l -> l.Current)
        let hypotheses = a.Hypotheses |> List.mapi (fun i h -> h, i)
        let hypRow = hypotheses |> List.map (fun (h, i) -> h.Id, i) |> Map.ofList
        let linked =
            a.Evidence
            |> List.mapi (fun order e -> e, order)
            |> List.choose (fun (e, order) ->
                let rows = links |> List.filter (fun l -> l.Evidence.Id = e.Id) |> List.choose (fun l -> hypRow |> Map.tryFind l.Hypothesis.Id)
                if rows.IsEmpty then None else Some(e, order, List.averageBy float rows))
            |> List.sortBy (fun (_, order, mean) -> mean, order)
            |> List.map (fun (e, _, _) -> e)
        // A shorter column is spread over the tallest one's height, so its lines fan in evenly instead of bunching
        // at the top. Slots stay at least one row apart, so nodes never overlap.
        let tallest = [ linked.Length; a.Hypotheses.Length; a.Conclusions.Length ] |> List.max
        let slot count i =
            if tallest <= count then float i
            elif count = 1 then float (tallest - 1) / 2.0
            else float i * (float (tallest - 1) / float (count - 1))
        let node column count i key (label: string) =
            { Key = key
              Column = column
              X = columnX column
              Y = rowYAt (slot count i)
              Width = nodeWidth
              Height = nodeHeight
              Label = truncate labelLength label
              FullText = label }
        let evidenceNodes =
            linked
            |> List.mapi (fun i e -> node GraphColumn.EvidenceColumn linked.Length i ("evidence:" + e.Id) (e.Label |> Option.defaultValue e.Content))
        let hypothesisNodes =
            hypotheses |> List.map (fun (h, i) -> node GraphColumn.HypothesisColumn a.Hypotheses.Length i ("hypothesis:" + h.Id) h.Statement)
        let conclusionNodes =
            a.Conclusions |> List.mapi (fun i c -> node GraphColumn.ConclusionColumn a.Conclusions.Length i ("conclusion:" + c.Id) c.Statement)
        let nodes = evidenceNodes @ hypothesisNodes @ conclusionNodes
        let byKey = nodes |> List.map (fun n -> n.Key, n) |> Map.ofList
        // Each evidence's lines are numbered so their words stack beside the node instead of on top of each other.
        let ordinal =
            links
            |> List.groupBy (fun l -> l.Evidence.Id)
            |> List.collect (fun (_, ls) -> ls |> List.mapi (fun k l -> l.Id, k))
            |> Map.ofList
        let edges =
            links
            |> List.choose (fun l ->
                match byKey |> Map.tryFind ("evidence:" + l.Evidence.Id), byKey |> Map.tryFind ("hypothesis:" + l.Hypothesis.Id) with
                | Some e, Some h ->
                    let x1, y1 = e.X + e.Width, e.Y + e.Height / 2.0
                    let x2, y2 = h.X, h.Y + h.Height / 2.0
                    let mx = (x1 + x2) / 2.0
                    let word = SectionData.kindText l.Kind
                    Some
                        { Key = l.Id
                          From = e.Key
                          To = h.Key
                          Kind = edgeKind l.Kind
                          Word = word
                          Path = sprintf "M %s %s C %s %s, %s %s, %s %s" (f x1) (f y1) (f mx) (f y1) (f mx) (f y2) (f x2) (f y2)
                          // Near the evidence end, where each line is still apart from the others.
                          LabelX = x1 + 8.0
                          LabelY = e.Y + 10.0 + float (ordinal |> Map.tryFind l.Id |> Option.defaultValue 0) * 12.0
                          Title = sprintf "%s %s %s" e.FullText (word.ToLowerInvariant()) h.FullText }
                | _ -> None)
        let rows = [ evidenceNodes.Length; hypothesisNodes.Length; conclusionNodes.Length ] |> List.max
        let columns = if conclusionNodes.IsEmpty then 2.0 else 3.0
        { Width = 2.0 * margin + columns * nodeWidth + (columns - 1.0) * columnGap
          Height = rowY (max rows 1) + margin
          Nodes = nodes
          Edges = edges
          UnlinkedEvidence = a.Evidence.Length - linked.Length }

    let summary (g: GraphLayout) =
        let count c = g.Nodes |> List.filter (fun n -> n.Column = c) |> List.length
        let kind k = g.Edges |> List.filter (fun e -> e.Kind = k) |> List.length
        let unlinked = if g.UnlinkedEvidence > 0 then sprintf " %d further evidence items have no assessment and are not drawn." g.UnlinkedEvidence else ""
        sprintf
            "Causal graph: %d evidence items linked to %d hypotheses by %d assessments (%d supporting, %d contradicting, %d other); %d conclusions.%s The relationship index below lists every assessment as text."
            (count GraphColumn.EvidenceColumn)
            (count GraphColumn.HypothesisColumn)
            g.Edges.Length
            (kind EdgeKind.SupportsEdge)
            (kind EdgeKind.ContradictsEdge)
            (kind EdgeKind.OtherEdge)
            (count GraphColumn.ConclusionColumn)
            unlinked

    let private columnName c =
        match c with
        | GraphColumn.EvidenceColumn -> "evidence"
        | GraphColumn.HypothesisColumn -> "hypothesis"
        | GraphColumn.ConclusionColumn -> "conclusion"

    let private edgeName k =
        match k with
        | EdgeKind.SupportsEdge -> "supports"
        | EdgeKind.ContradictsEdge -> "contradicts"
        | EdgeKind.OtherEdge -> "other"

    let bindings (view: WorkspaceView) : Bindings =
        let g = SectionData.analysis view.Section |> Option.map layout
        let present = g |> Option.exists (fun g -> not g.Edges.IsEmpty || g.Nodes.Length > 0)
        let g = g |> Option.defaultValue { Width = 0.0; Height = 0.0; Nodes = []; Edges = []; UnlinkedEvidence = 0 }
        let heads =
            [ GraphColumn.EvidenceColumn, "Evidence"; GraphColumn.HypothesisColumn, "Hypotheses"; GraphColumn.ConclusionColumn, "Conclusions" ]
            |> List.filter (fun (c, _) -> g.Nodes |> List.exists (fun n -> n.Column = c))
        Bind.ofList
            [ Bind.flag "graph.present" present
              Bind.text "graph.width" (f g.Width)
              Bind.text "graph.height" (f g.Height)
              Bind.text "graph.summary" (summary g)
              Bind.items
                  "graph.columns"
                  [ for c, label in heads ->
                        [ Bind.cellText "key" (columnName c)
                          Bind.cellText "label" label
                          Bind.cellText "x" (f (columnX c))
                          Bind.cellText "y" (f (margin + 18.0)) ] ]
              Bind.items
                  "graph.nodes"
                  [ for n in g.Nodes ->
                        [ Bind.cellText "key" n.Key
                          Bind.cellText "cls" ("causa-graph__node causa-graph__node--" + columnName n.Column)
                          Bind.cellText "x" (f n.X)
                          Bind.cellText "y" (f n.Y)
                          Bind.cellText "w" (f n.Width)
                          Bind.cellText "h" (f n.Height)
                          Bind.cellText "tx" (f (n.X + 12.0))
                          Bind.cellText "ty" (f (n.Y + n.Height / 2.0 + 5.0))
                          Bind.cellText "label" n.Label
                          Bind.cellText "full" n.FullText ] ]
              Bind.items
                  "graph.edges"
                  [ for e in g.Edges ->
                        [ Bind.cellText "key" e.Key
                          Bind.cellText "cls" ("causa-graph__edge causa-graph__edge--" + edgeName e.Kind)
                          Bind.cellText "d" e.Path
                          Bind.cellText "lx" (f e.LabelX)
                          Bind.cellText "ly" (f e.LabelY)
                          Bind.cellText "word" e.Word
                          Bind.cellText "title" e.Title ] ] ]
