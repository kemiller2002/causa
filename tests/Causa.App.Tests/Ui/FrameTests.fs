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

    let private save kind =
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

    let private view =
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
