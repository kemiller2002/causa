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
      Title: string }

/// One word beside an evidence card: each kind once, with its count when several lines of that kind leave the card.
type EdgeLabel = { Key: string; Evidence: string; Text: string; X: float; Y: float }

/// A note beside a hypothesis node that no current assessment links to: the graph says so in words.
type NodeNote = { Key: string; Node: string; Text: string; X: float; Y: float }

type GraphLayout =
    { Width: float
      Height: float
      Nodes: GraphNode list
      Edges: GraphEdge list
      Labels: EdgeLabel list
      /// Hypotheses with no evidence linked yet, each with its note.
      Notes: NodeNote list
      /// Evidence on record that no current assessment links to a hypothesis: listed in the summary, not drawn.
      UnlinkedEvidence: int }

/// The phone view of the graph: one panel per hypothesis, its evidence listed under it (no app state: a native
/// <details> per panel is the picker).
type FocusCard = { Key: string; EvidenceText: string; Kind: EdgeKind; Word: string; Long: bool }

type FocusPanel =
    { Key: string
      Title: string
      Supports: int
      Contradicts: int
      Others: int
      Cards: FocusCard list }

module CausalGraph =
    let nodeWidth = 240.0
    let nodeHeight = 44.0
    let rowGap = 12.0
    let columnGap = 220.0
    let margin = 16.0
    let headerHeight = 32.0
    let labelLength = 30
    /// The words a hypothesis with no current assessment carries, in the graph and in its panel.
    let noEvidenceText = "No evidence linked yet"

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

    /// The words beside each evidence card: one per kind, "Supports ×2" when two lines of that kind leave the card,
    /// stacked down the card's right edge in the order the kinds first appear (never the same word twice).
    let labelsOf (nodes: Map<string, GraphNode>) (edges: GraphEdge list) : EdgeLabel list =
        edges
        |> List.groupBy (fun e -> e.From)
        |> List.collect (fun (from, es) ->
            let n = nodes[from]
            es
            |> List.groupBy (fun e -> e.Word)
            |> List.mapi (fun k (word, same) ->
                { Key = sprintf "%s:%s" from word
                  Evidence = from
                  Text = (if same.Length > 1 then sprintf "%s ×%d" word same.Length else word)
                  X = n.X + n.Width + 8.0
                  Y = n.Y + 10.0 + float k * 12.0 }))

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
        // With no evidence linked yet the evidence column is left out, so the hypotheses start at the left edge.
        let x column =
            match column with
            | GraphColumn.EvidenceColumn -> columnX column
            | _ when linked.IsEmpty -> columnX column - (nodeWidth + columnGap)
            | _ -> columnX column
        let node column count i key (label: string) =
            { Key = key
              Column = column
              X = x column
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
                          Title = sprintf "%s %s %s" e.FullText (word.ToLowerInvariant()) h.FullText }
                | _ -> None)
        let labels = labelsOf byKey edges
        let notes =
            hypothesisNodes
            |> List.filter (fun h -> edges |> List.forall (fun e -> e.To <> h.Key))
            |> List.map (fun h -> { Key = h.Key + ":note"; Node = h.Key; Text = noEvidenceText; X = h.X + h.Width + 8.0; Y = h.Y + h.Height / 2.0 + 4.0 })
        let rows = [ evidenceNodes.Length; hypothesisNodes.Length; conclusionNodes.Length ] |> List.max
        // The columns drawn; the note beside a hypothesis needs the gap to its right, a column's width.
        let columns =
            (if evidenceNodes.IsEmpty then 0.0 else 1.0) + 1.0 + (if conclusionNodes.IsEmpty && notes.IsEmpty then 0.0 else 1.0)
        { Width = 2.0 * margin + columns * nodeWidth + (columns - 1.0) * columnGap
          Height = rowY (max rows 1) + margin
          Nodes = nodes
          Edges = edges
          Labels = labels
          Notes = notes
          UnlinkedEvidence = a.Evidence.Length - linked.Length }

    /// Panels most-supported first, then by title, then in the projection's order; within a panel supporting cards
    /// first, then contradicting, then the rest, each in the projection's order. Every current assessment appears in
    /// exactly one panel, its hypothesis's.
    let focus (a: AnalysisProjection) : FocusPanel list =
        let links = a.Relationships |> List.filter (fun l -> l.Current)
        let evidenceText =
            a.Evidence
            |> List.map (fun e -> e.Id, (match e.Label with Some l -> l + ": " + e.Content | None -> e.Content))
            |> Map.ofList
        let rank k =
            match k with
            | EdgeKind.SupportsEdge -> 0
            | EdgeKind.ContradictsEdge -> 1
            | EdgeKind.OtherEdge -> 2
        a.Hypotheses
        |> List.mapi (fun order h ->
            let cards =
                links
                |> List.filter (fun l -> l.Hypothesis.Id = h.Id)
                |> List.mapi (fun i l -> i, l)
                |> List.sortBy (fun (i, l) -> rank (edgeKind l.Kind), i)
                |> List.map (fun (_, l) ->
                    let text = evidenceText |> Map.tryFind l.Evidence.Id |> Option.defaultValue l.EvidenceLabel
                    { Key = l.Id; EvidenceText = text; Kind = edgeKind l.Kind; Word = SectionData.kindText l.Kind; Long = text.Length > 280 })
            let n k = cards |> List.filter (fun c -> c.Kind = k) |> List.length
            order,
            { Key = h.Id
              Title = h.Statement
              Supports = n EdgeKind.SupportsEdge
              Contradicts = n EdgeKind.ContradictsEdge
              Others = n EdgeKind.OtherEdge
              Cards = cards })
        |> List.sortBy (fun (order, p) -> -p.Supports, p.Title, order)
        |> List.map snd

    /// "5 supporting · 2 contradicting · 1 other", zeros left out.
    let counts (p: FocusPanel) =
        [ p.Supports, "supporting"; p.Contradicts, "contradicting"; p.Others, "other" ]
        |> List.filter (fun (n, _) -> n > 0)
        |> List.map (fun (n, w) -> sprintf "%d %s" n w)
        |> function
            | [] -> noEvidenceText
            | parts -> String.concat " · " parts

    /// How many panels the fragment has slots for; further hypotheses are named in the relationship index.
    let focusSlots = 16

    let private kindName k =
        match k with
        | EdgeKind.SupportsEdge -> "supports"
        | EdgeKind.ContradictsEdge -> "contradicts"
        | EdgeKind.OtherEdge -> "other"

    let focusBindings (view: WorkspaceView) : Bindings =
        let panels = SectionData.analysis view.Section |> Option.map focus |> Option.defaultValue []
        Bind.ofList
            [ yield Bind.flag "focus.present" (not panels.IsEmpty)
              yield Bind.flag "focus.more-present" (panels.Length > focusSlots)
              yield Bind.text "focus.more" (sprintf "%d more hypotheses are listed in the relationship index below." (max 0 (panels.Length - focusSlots)))
              for k in 1..focusSlots do
                  let p = panels |> List.tryItem (k - 1)
                  let key = sprintf "focus.p%d" k
                  yield Bind.flag (key + ".present") p.IsSome
                  yield Bind.text (key + ".title") (p |> Option.map (fun p -> p.Title) |> Option.defaultValue "")
                  yield Bind.text (key + ".counts") (p |> Option.map counts |> Option.defaultValue "")
                  yield Bind.flag (key + ".empty") (p |> Option.exists (fun p -> p.Cards.IsEmpty))
                  yield
                      Bind.items
                          (key + ".items")
                          [ for c in (p |> Option.map (fun p -> p.Cards) |> Option.defaultValue []) ->
                                [ Bind.cellText "key" c.Key
                                  Bind.cellText "kind" (kindName c.Kind)
                                  Bind.cellText "word" c.Word
                                  Bind.cellText "text" c.EvidenceText
                                  Bind.cellFlag "long" c.Long ] ] ]

    let summary (g: GraphLayout) =
        let count c = g.Nodes |> List.filter (fun n -> n.Column = c) |> List.length
        let kind k = g.Edges |> List.filter (fun e -> e.Kind = k) |> List.length
        let unlinked = if g.UnlinkedEvidence > 0 then sprintf " %d further evidence items have no assessment and are not drawn." g.UnlinkedEvidence else ""
        let bare = if g.Notes.IsEmpty then "" else sprintf " %d hypotheses have no evidence linked yet." g.Notes.Length
        sprintf
            "Causal graph: %d evidence items linked to %d hypotheses by %d assessments (%d supporting, %d contradicting, %d other); %d conclusions.%s%s The relationship index below lists every assessment as text."
            (count GraphColumn.EvidenceColumn)
            (count GraphColumn.HypothesisColumn)
            g.Edges.Length
            (kind EdgeKind.SupportsEdge)
            (kind EdgeKind.ContradictsEdge)
            (kind EdgeKind.OtherEdge)
            (count GraphColumn.ConclusionColumn)
            unlinked
            bare

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
        let g = g |> Option.defaultValue { Width = 0.0; Height = 0.0; Nodes = []; Edges = []; Labels = []; Notes = []; UnlinkedEvidence = 0 }
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
                          Bind.cellText "x" (f (g.Nodes |> List.find (fun n -> n.Column = c)).X)
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
                          Bind.cellText "word" e.Word
                          Bind.cellText "title" e.Title ] ]
              Bind.items
                  "graph.notes"
                  [ for n in g.Notes ->
                        [ Bind.cellText "key" n.Key
                          Bind.cellText "text" n.Text
                          Bind.cellText "x" (f n.X)
                          Bind.cellText "y" (f n.Y) ] ]
              Bind.items
                  "graph.labels"
                  [ for l in g.Labels ->
                        [ Bind.cellText "key" l.Key
                          Bind.cellText "text" l.Text
                          Bind.cellText "x" (f l.X)
                          Bind.cellText "y" (f l.Y) ] ] ]

    /// Both graph views' bindings.
    let allBindings (view: WorkspaceView) : Bindings = Bind.merge [ bindings view; focusBindings view ]
