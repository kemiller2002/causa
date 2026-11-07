// Owner: ui-3 (reassigned to ui). The causal graph layout (Views/Graph.fs): deterministic, every current assessment
// drawn once between its own two nodes, no two nodes overlapping, the column order evidence | hypotheses |
// conclusions, and a summary that states the counts.
module Causa.App.Tests.Ui.GraphTests

open Xunit
open Causa.Ui.Contracts
open Causa.App.Views

let private link i (e: string) (h: string) kind current : LinkCard =
    { Id = sprintf "l%d" i
      Evidence = { Kind = EvidenceEntity; Id = e }
      EvidenceLabel = "evidence " + e
      Hypothesis = { Kind = HypothesisEntity; Id = h }
      HypothesisStatement = "hypothesis " + h
      Kind = kind
      Rationale = "because"
      Current = current }

let private projection (evidence: int) (hypotheses: int) (links: LinkCard list) (conclusions: int) : AnalysisProjection =
    { Observations = []
      Hypotheses =
        [ for i in 1..hypotheses ->
              { Id = sprintf "h%d" i; Statement = sprintf "Hypothesis %d with a statement long enough to be truncated in its node" i; State = "Proposed"; Explanation = None; Links = [] } ]
      Evidence =
        [ for i in 1..evidence ->
              { Id = sprintf "e%d" i; Label = Some(sprintf "Evidence %d" i); Content = "content"; SourceReference = None; SourceUrl = None; Category = None; CapturedAt = None; Links = [] } ]
      Relationships = links
      Timeline = []
      Conclusions = [ for i in 1..conclusions -> { Id = sprintf "c%d" i; Statement = "Conclusion"; Strength = "Confirmed cause"; ResidualUncertainty = None; RecordedAtVersion = None } ]
      NextSteps = []
      UnresolvedContradictions = Known 0
      CorrectiveActionsSupported = false
      Actions = Map.empty }

let private sample =
    projection
        6
        3
        [ link 1 "e1" "h1" (CatalogKind Supports) true
          link 2 "e2" "h1" (CatalogKind Contradicts) true
          link 3 "e3" "h2" (CatalogKind Supports) true
          link 4 "e3" "h3" (UninterpretedKind "insufficient") true
          link 5 "e5" "h3" (CatalogKind Inconclusive) true
          link 6 "e5" "h2" (CatalogKind Supports) false ]
        1

let private overlaps (a: GraphNode) (b: GraphNode) =
    a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height

[<Fact>]
let ``the layout is deterministic`` () =
    Assert.Equal(CausalGraph.layout sample, CausalGraph.layout sample)

[<Fact>]
let ``every current assessment is drawn once between its own evidence and hypothesis`` () =
    let g = CausalGraph.layout sample
    let current = sample.Relationships |> List.filter (fun l -> l.Current)
    Assert.Equal(current.Length, g.Edges.Length)
    for l in current do
        let e = g.Edges |> List.filter (fun e -> e.Key = l.Id) |> List.exactlyOne
        Assert.Equal("evidence:" + l.Evidence.Id, e.From)
        Assert.Equal("hypothesis:" + l.Hypothesis.Id, e.To)
        Assert.False(System.String.IsNullOrWhiteSpace e.Word)
    // A superseded assessment is history, not a line.
    Assert.DoesNotContain(g.Edges, fun e -> e.Key = "l6")

[<Fact>]
let ``the kind sets the line style and its word`` () =
    let g = CausalGraph.layout sample
    let kindOf key = (g.Edges |> List.find (fun e -> e.Key = key))
    Assert.Equal(EdgeKind.SupportsEdge, (kindOf "l1").Kind)
    Assert.Equal("Supports", (kindOf "l1").Word)
    Assert.Equal(EdgeKind.ContradictsEdge, (kindOf "l2").Kind)
    Assert.Equal("Contradicts", (kindOf "l2").Word)
    Assert.Equal(EdgeKind.OtherEdge, (kindOf "l4").Kind)
    Assert.Equal("Insufficient", (kindOf "l4").Word)

[<Fact>]
let ``no two nodes overlap and the columns are in order`` () =
    for g in [ CausalGraph.layout sample; CausalGraph.layout (projection 40 12 [ for i in 1..40 -> link i (sprintf "e%d" i) (sprintf "h%d" (1 + i % 12)) (CatalogKind Supports) true ] 2) ] do
        let nodes = g.Nodes
        for a in nodes do
            for b in nodes do
                if a.Key <> b.Key then Assert.False(overlaps a b, sprintf "%s overlaps %s" a.Key b.Key)
            Assert.True(a.X + a.Width <= g.Width && a.Y + a.Height <= g.Height, sprintf "%s is outside the canvas" a.Key)
        let xs c = nodes |> List.filter (fun n -> n.Column = c) |> List.map (fun n -> n.X) |> List.distinct
        Assert.True(List.exactlyOne (xs GraphColumn.EvidenceColumn) < List.exactlyOne (xs GraphColumn.HypothesisColumn))
        Assert.True(List.exactlyOne (xs GraphColumn.HypothesisColumn) < List.exactlyOne (xs GraphColumn.ConclusionColumn))

[<Fact>]
let ``only linked evidence is drawn and the summary says how much is not`` () =
    let g = CausalGraph.layout sample
    Assert.Equal(4, g.Nodes |> List.filter (fun n -> n.Column = GraphColumn.EvidenceColumn) |> List.length)
    Assert.Equal(2, g.UnlinkedEvidence)
    let s = CausalGraph.summary g
    Assert.StartsWith("Causal graph: 4 evidence items linked to 3 hypotheses by 5 assessments (2 supporting, 1 contradicting, 2 other); 1 conclusions.", s)
    Assert.Contains("2 further evidence items have no assessment", s)

[<Fact>]
let ``labels are truncated and keep their full text`` () =
    let g = CausalGraph.layout sample
    let h = g.Nodes |> List.find (fun n -> n.Key = "hypothesis:h1")
    Assert.True(h.Label.Length <= CausalGraph.labelLength)
    Assert.EndsWith("…", h.Label)
    Assert.StartsWith("Hypothesis 1 with a statement long enough", h.FullText)

[<Fact>]
let ``a working copy with one hypothesis and no assessment still draws its node`` () =
    let g = CausalGraph.layout (projection 0 1 [] 0)
    Assert.Equal(1, g.Nodes.Length)
    Assert.Empty(g.Edges)
    Assert.True(g.Width > 0.0 && g.Height > 0.0)
