// Owner: ui-2 (WI-INV-WORKSPACE, reassigned). The workspace frame and save state: what J1 reads (problem statement,
// title, question as text; a save status whose word is "Saved" only for the Saved state; Save offered as the view says;
// only listed sections linked) and that the fragment reads only bindings the modules write.
namespace Causa.App.Tests.Ui

open System
open System.IO
open Xunit
open Causa.App
open Causa.Domain
open Causa.Ui.Contracts
open Causa.App.Views

module FrameTests =
    let private label = FixtureLabel.create "frame tests"
    let private stored v = AsStored(Visible.fixture label v)
    let private target = { Workspace = "workspaces/demo/causa-app-t"; Role = Causa.Persistence.WorkspaceRole.WorkingCopy; Id = InvestigationId(Guid "3f2a8c1e-0b4d-4e6f-9a7b-1c2d3e4f5a6b") }

    let private frame =
        { Ref = Unchecked.defaultof<_>
          Identifier = "3f2a8c1e-0b4d-4e6f-9a7b-1c2d3e4f5a6b"
          Title = "Checkout failures"
          ProblemStatement = stored "Checkout fails since 09:00.\nUS is fine."
          Question = Some(stored "Why?")
          CreatedAt = stored (DateTimeOffset(2026, 11, 7, 17, 0, 0, TimeSpan.Zero))
          InvestigationState = stored "Draft"
          Copy = WorkingCopy
          Sections = Shell.sectionLinks target Overview false |> List.filter (fun l -> l.Destination = Overview)
          Version = None }

    let internal save kind =
        let word, _, _ = Language.saveState kind
        { Kind = kind
          Indicator =
            { InvestigationId = target.Id
              Label = word
              Text = word
              Glyph = ""
              Actions = [ Save ]
              Politeness = Causa.Persistence.Polite
              LastSavedCommitTime = None }
          Save = Available
          Conflict = None
          NotSaved = [] }

    let internal view =
        { Frame = Ready frame
          Stored = NotRequested
          Section = OverviewSection NotRequested
          Drafts = { Values = Map.empty; Errors = []; OpenEditor = None }
          Details = { Open = false; Form = { Title = ""; ProblemStatement = ""; Question = "" }; Errors = []; Edit = NotOffered; SaveDetails = NotOffered; Cancel = NotOffered }
          Save = Some(save UnsavedKind)
          State = ScreenReady }

    let private text (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Text s) -> s
        | other -> failwithf "%s is %A" key other

    let private flag (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Flag f) -> f
        | other -> failwithf "%s is %A" key other

    [<Fact>]
    let ``the frame shows the title, problem statement and question as entered`` () =
        let b = InvestigationWorkspace.bindings view
        Assert.Equal("Checkout failures", text b "workspace.title")
        Assert.Equal("Checkout fails since 09:00.\nUS is fine.", text b "workspace.problem")
        Assert.True(flag b "workspace.question-present")
        Assert.Equal("Why?", text b "workspace.question")
        Assert.Equal(frame.Identifier, text b "workspace.identifier")

    [<Fact>]
    let ``an absent or blank question is not shown`` () =
        let none = InvestigationWorkspace.bindings { view with Frame = Ready { frame with Question = None } }
        Assert.False(flag none "workspace.question-present")
        let blank = InvestigationWorkspace.bindings { view with Frame = Ready { frame with Question = Some(stored "  ") } }
        Assert.False(flag blank "workspace.question-present")

    [<Fact>]
    let ``the save word is Saved only for the Saved state`` () =
        let all = [ SavedKind; UnsavedKind; SavingKind; OfflineKind; ConflictKind; UnknownOutcomeKind; SaveFailedKind ]
        for kind in all do
            let b = SaveIndicator.bindings (Some(save kind))
            Assert.Equal((kind = SavedKind), (text b "save.label" = "Saved"))
            Assert.NotEqual<string>("", text b "save.label")
        Assert.Equal("ok", text (SaveIndicator.bindings (Some(save SavedKind))) "save.state")
        Assert.Equal("blocked", text (SaveIndicator.bindings (Some(save ConflictKind))) "save.state")
        Assert.False(flag (SaveIndicator.bindings None) "save.present")

    [<Fact>]
    let ``Save is offered, disabled with the reason, or not shown, as the view says`` () =
        Assert.True(flag (SaveIndicator.bindings (Some(save UnsavedKind))) "save.offered")
        Assert.False(flag (SaveIndicator.bindings (Some(save UnsavedKind))) "save.disabled")
        let busy = SaveIndicator.bindings (Some { save SavingKind with Save = Unavailable "A save is in progress." })
        Assert.True(flag busy "save.disabled")
        Assert.Equal("A save is in progress.", text busy "save.unavailable")
        Assert.False(flag (SaveIndicator.bindings (Some { save SavedKind with Save = NotOffered })) "save.offered")

    [<Fact>]
    let ``only the sections the view lists are linked`` () =
        let b = InvestigationWorkspace.bindings view
        match Map.find "workspace.sections" b with
        | BindingValue.Items rows ->
            Assert.Equal(1, rows.Length)
            Assert.Equal(BindingPrimitive.Text "page", rows.Head.["current"])
        | other -> failwithf "%A" other
        let none = InvestigationWorkspace.bindings { view with Frame = Ready { frame with Sections = [] } }
        Assert.False(flag none "workspace.sections-present")

    [<Fact>]
    let ``the stored record shows only when the frame cannot`` () =
        let record = { Ref = Unchecked.defaultof<_>; Title = "Stored"; CreatedAt = None; UpdatedAt = None; EventCount = 2; Events = [] }
        let withStored = { view with Stored = Ready(Visible.fixture label record) }
        Assert.False(flag (InvestigationWorkspace.bindings withStored) "stored.present")
        Assert.True(flag (InvestigationWorkspace.bindings { withStored with Frame = Waiting "Opening investigation" }) "stored.present")

    [<Fact>]
    let ``the fragment carries the save status, Save and the sections navigation, and reads only written bindings`` () =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "repository root not found"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        let html = File.ReadAllText(Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "web", "screens", "workspace-frame.html"))
        Assert.Contains("class=\"ef-status-lozenge causa-save__state\" role=\"status\" aria-label=\"Save state\" aria-describedby=\"causa-save-explanation\" data-requirement-id=\"INV-016\"", html)
        Assert.Contains(">Save</button>", html)
        Assert.Contains("aria-label=\"Investigation sections\"", html)
        Assert.Contains("data-if=\"screen.investigation-workspace\"", html)
        let written = InvestigationWorkspace.bindings view |> Map.toList |> List.map fst |> Set.ofList
        let read =
            Text.RegularExpressions.Regex.Matches(html, "data-(?:text|if|each|bind-[a-z-]+)=\"((?:workspace|save|stored|details)\\.[a-z-]+)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value)
            |> Set.ofSeq
        Assert.Empty(Set.difference read written)

// The graph editors of a writable working copy (Views/Workspace/Inspector.fs): admit evidence, link evidence to a
// hypothesis (and reassess), record a conclusion. Each renders only when the projection offers its action, lists what is
// recorded above it and says "not saved yet" once a record is unsaved; the choices are the catalog's options.
module GraphEditorTests =
    open Causa.App.Views.Workspace

    let private label = FixtureLabel.create "graph editor tests"

    let private link (e: string) (h: string) kind : LinkCard =
        { Id = e + "-" + h
          Evidence = { Kind = EvidenceEntity; Id = e }
          EvidenceLabel = "Gauge " + e
          Hypothesis = { Kind = HypothesisEntity; Id = h }
          HypothesisStatement = "Valve " + h
          Kind = kind
          Rationale = "the trace rises first"
          Current = true }

    let private projection (actions: (ActionId * Availability) list) (links: LinkCard list) (conclusions: int) : AnalysisProjection =
        { Observations = []
          Hypotheses = [ { Id = "h1"; Statement = "Valve h1"; State = "Proposed"; Explanation = None; Links = links } ]
          Evidence =
            [ { Id = "e1"; Label = Some "Gauge e1"; Content = "reads 40"; SourceReference = None; SourceUrl = None; Category = None; CapturedAt = None; Links = links }
              { Id = "e2"; Label = None; Content = "unlabelled content"; SourceReference = None; SourceUrl = None; Category = None; CapturedAt = None; Links = [] } ]
          Relationships = links
          Timeline = []
          Conclusions = [ for i in 1..conclusions -> { Id = sprintf "c%d" i; Statement = "c"; Strength = "Leading explanation"; ResidualUncertainty = None; RecordedAtVersion = None } ]
          NextSteps = []
          UnresolvedContradictions = Known 0
          CorrectiveActionsSupported = false
          Actions = Map.ofList actions }

    let private offered =
        [ Actions.recordEvidence, Available; Actions.linkEvidence, Available; Actions.reassess, Available; Actions.recordConclusion, Available ]

    let private viewOf section (p: AnalysisProjection) drafts saveKind : WorkspaceView =
        let base' = FrameTests.view
        { base' with
            Section = section (Ready(Understood.fixture label p))
            Drafts = drafts
            Save = saveKind |> Option.map FrameTests.save }

    let private noDrafts = { Values = Map.empty; Errors = []; OpenEditor = None }
    let private bind section p drafts saveKind = InvestigationWorkspace.bindings (viewOf section p drafts saveKind)

    let private text (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Text s) -> s
        | other -> failwithf "%s is %A" key other

    let private flag (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Flag f) -> f
        | other -> failwithf "%s is %A" key other

    let private rows (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Items rs) -> rs
        | other -> failwithf "%s is %A" key other

    let private cell (row: Map<string, BindingPrimitive>) k =
        match row.[k] with
        | BindingPrimitive.Text s -> s
        | BindingPrimitive.Flag f -> string f
        | other -> failwithf "%A" other

    [<Fact>]
    let ``the three editors render only when the projection offers their action`` () =
        let on = bind EvidenceView (projection offered [] 0) noDrafts (Some SavedKind)
        for prefix in [ "editor.evidence"; "editor.link"; "editor.conclusion" ] do
            Assert.True(flag on (prefix + ".offered"), prefix)
            Assert.False(flag on (prefix + ".disabled"), prefix)
        Assert.True(flag on "editor.link.reassess-offered")
        // A frozen demonstration: the projection offers nothing, so no form renders.
        let off = bind EvidenceView (projection [] [] 0) noDrafts (Some SavedKind)
        for prefix in [ "editor.evidence"; "editor.link"; "editor.conclusion" ] do
            Assert.False(flag off (prefix + ".offered"), prefix)
        Assert.False(flag off "editor.link.reassess-offered")

    [<Fact>]
    let ``an unavailable action is shown disabled with the engine's reason`` () =
        let p = projection [ Actions.recordConclusion, Unavailable "A blocking unknown is open: state the residual uncertainty." ] [] 0
        let b = bind OverviewSection p noDrafts (Some SavedKind)
        Assert.True(flag b "editor.conclusion.offered")
        Assert.True(flag b "editor.conclusion.disabled")
        Assert.Equal("A blocking unknown is open: state the residual uncertainty.", text b "editor.conclusion.unavailable")

    [<Fact>]
    let ``the link pickers list the recorded evidence and hypotheses, with the drafted ones selected`` () =
        let drafts = { noDrafts with Values = Map.ofList [ Fields.linkEvidence, "e1"; Fields.linkHypothesis, "h1"; Fields.relationshipKind, "supports" ] }
        let b = bind EvidenceView (projection offered [] 0) drafts (Some UnsavedKind)
        let ev = rows b "editor.link.evidence-options"
        Assert.Equal<string list>([ "e1"; "e2" ], ev |> List.map (fun r -> cell r "key"))
        // An evidence item with no label is named by its content.
        Assert.Equal<string list>([ "Gauge e1"; "unlabelled content" ], ev |> List.map (fun r -> cell r "label"))
        Assert.Equal<string list>([ "True"; "False" ], ev |> List.map (fun r -> cell r "selected"))
        Assert.False(flag b "editor.link.evidence-unchosen")
        let hs = rows b "editor.link.hypothesis-options"
        Assert.Equal<string list>([ "Valve h1" ], hs |> List.map (fun r -> cell r "label"))
        Assert.True(flag b "editor.link.ready")
        Assert.Equal("", text b "editor.link.missing")
        let kinds = rows b "editor.link.kinds"
        Assert.Equal<string list>([ "supports" ], kinds |> List.filter (fun r -> cell r "checked" = "True") |> List.map (fun r -> cell r "key"))

    [<Fact>]
    let ``with nothing to link the editor says what to record first instead of an empty picker`` () =
        let p = { projection offered [] 0 with Hypotheses = [] }
        let b = bind EvidenceView p noDrafts (Some SavedKind)
        Assert.False(flag b "editor.link.ready")
        Assert.Contains("Record a hypothesis first", text b "editor.link.missing")
        Assert.True(flag b "editor.link.evidence-unchosen")

    [<Fact>]
    let ``until evidence exists the link editor shows the engine's reason and no second note`` () =
        let p = { projection [ Actions.linkEvidence, Unavailable "No usable evidence has been admitted." ] [] 0 with Evidence = [] }
        let b = bind EvidenceView p noDrafts (Some SavedKind)
        Assert.True(flag b "editor.link.offered")
        Assert.True(flag b "editor.link.disabled")
        Assert.Equal("No usable evidence has been admitted.", text b "editor.link.unavailable")
        Assert.Equal("", text b "editor.link.missing")
        Assert.False(flag b "editor.link.ready")

    [<Fact>]
    let ``the choice groups offer exactly the catalog's options, none chosen at first`` () =
        let kindRule =
            ContentForms.link
            |> List.find (fun f -> f.Field = Fields.relationshipKind)
            |> fun f -> f.Rules |> List.pick (function OneOf vs -> Some vs | _ -> None)
        Assert.Equal<string list>(kindRule, ContentEditors.relationshipKinds |> List.map (fun c -> c.Value))
        // Readable words on screen; the accessible name is the lower-case word the cards use afterwards ("supports “…”").
        Assert.Equal<string list>([ "Supports"; "Contradicts"; "Consistent with"; "Inconclusive" ], ContentEditors.relationshipKinds |> List.map (fun c -> c.Label))
        Assert.Equal<string list>([ "supports"; "contradicts"; "consistent with"; "inconclusive" ], ContentEditors.relationshipKinds |> List.map (fun c -> c.Name))
        Assert.Equal("Direct observation", (ContentEditors.evidenceCategories |> List.head).Label)
        Assert.Equal<string list>(
            [ "directObservation"; "measurement"; "log"; "document"; "experimentResult"; "testimony"; "externalReference" ],
            ContentEditors.evidenceCategories |> List.map (fun c -> c.Value))
        Assert.Equal<string list>(
            [ "plausibleExplanation"; "leadingExplanation"; "supportedCause"; "confirmedCause"; "acceptedWithResidualUncertainty" ],
            ContentEditors.conclusionStrengths |> List.map (fun c -> c.Value))
        let b = bind OverviewSection (projection offered [] 0) noDrafts (Some SavedKind)
        for key in [ "editor.evidence.categories"; "editor.link.kinds"; "editor.conclusion.strengths" ] do
            Assert.True(rows b key |> List.forall (fun r -> cell r "checked" = "False"), key)
            // Every description has its own id for aria-describedby, unique on the page.
            let ids = rows b key |> List.map (fun r -> cell r "describedBy")
            Assert.Equal(ids.Length, (List.distinct ids).Length)

    [<Fact>]
    let ``the drafts are shown back in their fields`` () =
        let drafts =
            { noDrafts with
                Values =
                    Map.ofList
                        [ Fields.evidenceLabel, "Gauge"; Fields.evidenceContent, "reads 40"; Fields.evidenceSource, "panel"
                          Fields.conclusionStatement, "The valve stuck"; ContentEditors.PendingFields.residualUncertainty, "Seal age unknown"
                          ContentEditors.PendingFields.conclusionStrength, "leadingExplanation"; Fields.relationshipRationale, "rises first" ] }
        let b = bind OverviewSection (projection offered [] 0) drafts (Some UnsavedKind)
        Assert.Equal("Gauge", text b "editor.evidence.label")
        Assert.Equal("reads 40", text b "editor.evidence.content")
        Assert.Equal("panel", text b "editor.evidence.source")
        Assert.Equal("The valve stuck", text b "editor.conclusion.statement")
        Assert.Equal("Seal age unknown", text b "editor.conclusion.residual")
        Assert.Equal("rises first", text b "editor.link.rationale")
        Assert.Equal<string list>(
            [ "leadingExplanation" ],
            rows b "editor.conclusion.strengths" |> List.filter (fun r -> cell r "checked" = "True") |> List.map (fun r -> cell r "key"))

    [<Fact>]
    let ``each editor shows only its own validation messages`` () =
        let err f m = { Field = f; Rule = Required; Message = m }
        let drafts = { noDrafts with Errors = [ err Fields.evidenceLabel "Enter an evidence label."; err Fields.relationshipRationale "Say why." ] }
        let b = bind EvidenceView (projection offered [] 0) drafts (Some SavedKind)
        Assert.True(flag b "editor.evidence.errors-present")
        Assert.Equal<string list>([ "Enter an evidence label." ], rows b "editor.evidence.errors" |> List.map (fun r -> cell r "message"))
        Assert.True(flag b "editor.link.errors-present")
        Assert.Equal<string list>([ "Say why." ], rows b "editor.link.errors" |> List.map (fun r -> cell r "message"))
        Assert.False(flag b "editor.conclusion.errors-present")

    [<Fact>]
    let ``once a record is unsaved and the form is empty each editor says where it went and that it is not saved yet`` () =
        let links = [ link "e1" "h1" (CatalogKind Supports) ]
        let unsaved = bind EvidenceView (projection offered links 1) noDrafts (Some UnsavedKind)
        Assert.StartsWith("Recorded evidence items are listed above.", text unsaved "editor.evidence.status")
        Assert.StartsWith("Recorded assessments are listed on each evidence card above.", text unsaved "editor.link.status")
        Assert.StartsWith("Recorded conclusions are listed above.", text unsaved "editor.conclusion.status")
        for prefix in [ "editor.evidence"; "editor.link"; "editor.conclusion" ] do
            Assert.Contains("not saved yet", text unsaved (prefix + ".status"))
        // Saved: nothing to say. Typing again: the form is the focus, not the status.
        let saved = bind EvidenceView (projection offered links 1) noDrafts (Some SavedKind)
        Assert.Equal("", text saved "editor.evidence.status")
        let typing = bind EvidenceView (projection offered links 1) { noDrafts with Values = Map.ofList [ Fields.evidenceLabel, "x" ] } (Some UnsavedKind)
        Assert.Equal("", text typing "editor.evidence.status")
        Assert.NotEqual<string>("", text typing "editor.link.status")
        // Nothing recorded yet: no status.
        let fresh = bind EvidenceView ({ projection offered [] 0 with Evidence = [] }) noDrafts (Some UnsavedKind)
        Assert.Equal("", text fresh "editor.evidence.status")

    [<Fact>]
    let ``a link reads back on the evidence card and on the hypothesis card in the words the editor offered`` () =
        let links = [ link "e1" "h1" (CatalogKind Supports) ]
        let b = bind EvidenceView (projection offered links 0) noDrafts (Some SavedKind)
        let card = rows b "evidence.items" |> List.find (fun r -> cell r "key" = "e1")
        Assert.Equal("supports “Valve h1”. Why: the trace rises first", cell card "assessments")
        Assert.Equal("1 supporting", cell card "bearing")
        let h = bind HypothesesView (projection offered links 0) noDrafts (Some SavedKind)
        let hcard = rows h "hypotheses.items" |> List.head
        Assert.Equal("“Gauge e1” supports it. Why: the trace rises first", cell hcard "assessments")
        let incon = bind EvidenceView (projection offered [ link "e1" "h1" (CatalogKind Inconclusive) ] 0) noDrafts (Some SavedKind)
        Assert.StartsWith("is inconclusive about", rows incon "evidence.items" |> List.head |> fun r -> cell r "assessments")

    [<Fact>]
    let ``the fragment's editors read only bindings the view writes, and post the catalog's field and action ids`` () =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "repository root not found"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        let html = File.ReadAllText(Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "web", "screens", "workspace-frame.html"))
        let written = bind OverviewSection (projection offered [] 0) noDrafts (Some SavedKind) |> Map.toList |> List.map fst |> Set.ofList
        let read =
            Text.RegularExpressions.Regex.Matches(html, "data-(?:text|if|each|bind-[a-z-]+)=\"(editor\\.[a-z.-]+)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value)
            |> Set.ofSeq
        Assert.Empty(Set.difference read written)
        for id in [ "UIA-RECORD-EVIDENCE"; "INV-027"; "INV-028"; "INV-029"; "INV-012"; "INV-045"; "INV-030"; "UIF-LINK-EVIDENCE"; "UIF-LINK-HYPOTHESIS"
                    "INV-031"; "INV-038"; "UIA-REASSESS"; "INV-032"; "INV-033"; "INV-046"; "INV-047"; "INV-036" ] do
            Assert.Contains(sprintf "data-event=\"%s\"" id, html)
        // The names the governed journey (J2) reaches the controls by.
        for name in [ "Evidence label"; "Evidence source reference"; "Evidence original content"; "Evidence category"; "Evidence relationship kind"
                      "Evidence relationship rationale"; "Conclusion statement"; "Conclusion strength"; "Residual uncertainty"
                      ">Record evidence</button>"; ">Link evidence to a hypothesis</button>"; ">Reassess</button>"; ">Record a conclusion</button>" ] do
            Assert.Contains(name, html)
