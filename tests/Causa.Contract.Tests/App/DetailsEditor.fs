// INV-034 contract tests (the whole path the browser uses: Limen event names -> Ui/Events -> UiAdapter ->
// Update.engine -> Kernel.kernel.Decide -> session -> WorkspaceView -> bindings): Edit details (INV-034), the fields
// UIF-DETAILS-TITLE/PROBLEM/QUESTION with their validation, Save details (UIA-SAVE-DETAILS) sending ReviseTitle,
// ReviseObservation and ReviseUnknown as separate command groups for changed fields only at the current version, and
// Cancel (INV-036). Offered only on a causa-storage 1.2.0 working copy; a 1.1.0 one shows it unavailable with the
// reason, and a revision sent there anyway is refused as CAUSA.UNREPRESENTABLE before anything is sent.
namespace Causa.Contract.Tests.App

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.App.Ui
open Causa.Ui.Contracts

module private Details =
    let at = DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero)
    let identifier = Guid.Parse "8c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5"
    let problem = "The nightly export stopped at 02:10."
    let question = "Why did the export stop?"

    let backend (version: string) : Backend =
        { Kernel = Kernel.kernel
          Policies = Policies.coreDefault
          Actor = { ActorId = "test" }
          Author = Actor.Human(NonEmptyText.create "test" |> Option.get)
          Now = fun () -> at
          StorageVersion = version
          Persistence = None
          Provider = None
          WorkingCopy = None }

    let config (b: Backend) : AdapterConfig =
        { NewIdentifier = fun () -> identifier
          Now = fun () -> at
          Run = "session"
          SessionLocal = true
          ValidateStart = Causa.App.Views.StartScreen.validate
          InvestigationOf = Update.investigationOf b
          CanSave = false
          WorkingCopy = None }

    type Harness =
        { Backend: Backend
          Ui: UiBackend<AdapterState, HostEffect> }

    let harness version =
        let b = backend version
        { Backend = b; Ui = UiAdapter.backend (config b) (Update.engine b) }

    /// Drives the adapter the way the browser does: Limen events by name.
    let event (h: Harness) (name: string) (value: string option) (s: AdapterState) =
        match Events.toIntent name None value with
        | Some intent -> h.Ui.Dispatch intent s |> fst
        | None -> failwithf "no intent for event %s" name

    let started (h: Harness) =
        h.Ui.Init StartRoute
        |> fst
        |> event h "INV-001" (Some problem)
        |> event h "INV-002" (Some "Nightly export")
        |> event h "INV-023" (Some question)
        |> event h "INV-004" None

    let ref (h: Harness) =
        let writable = UiAdapter.workingCopy (config h.Backend) |> Option.get
        { Address = Writable.address writable; Id = InvestigationId identifier }

    let session (h: Harness) (s: AdapterState) = s.Model.Sessions[ref h]

    let investigation (h: Harness) (s: AdapterState) =
        match Update.investigationOf h.Backend s.Model (ref h) with
        | Ok(Some i) -> i
        | other -> failwithf "%A" other

    let workspace (h: Harness) (s: AdapterState) =
        match (h.Ui.Project s).Screen with
        | WorkspaceScreen v -> v
        | other -> failwithf "not the workspace: %A" other

    let commandsAfterStart (h: Harness) (s: AdapterState) =
        (session h s).Unsaved |> List.skip 2

type DetailsEditor() =
    [<Fact>]
    member _.``On a 1.2.0 working copy Edit details is offered and opens the editor with the current details``() =
        let h = Details.harness StorageVersion.v120
        let s = Details.started h
        let v = Details.workspace h s
        Assert.Equal(Available, v.Details.Edit)
        Assert.False(v.Details.Open)
        let opened = Details.event h "INV-034" None s
        let d = (Details.workspace h opened).Details
        Assert.True(d.Open)
        Assert.Equal({ Title = "Nightly export"; ProblemStatement = Details.problem; Question = Details.question }, d.Form)
        Assert.Equal(Available, d.SaveDetails)
        Assert.Equal(Available, d.Cancel)
        // Opening the editor records nothing.
        Assert.Equal(2, (Details.session h opened).Unsaved.Length)

    [<Fact>]
    member _.``Save details sends the three revisions as separate command groups at the current version``() =
        let h = Details.harness StorageVersion.v120
        let s = Details.started h
        let before = Details.investigation h s
        let saved =
            s
            |> Details.event h "INV-034" None
            |> Details.event h "UIF-DETAILS-TITLE" (Some "  Nightly export stopped at 02:10  ")
            |> Details.event h "UIF-DETAILS-PROBLEM" (Some "The nightly export stopped at 02:10 and wrote no file.")
            |> Details.event h "UIF-DETAILS-QUESTION" (Some "Why did the export stop,\nand why was no alert raised?")
            |> Details.event h "UIA-SAVE-DETAILS" None
        let sent = Details.commandsAfterStart h saved
        Assert.Equal(3, sent.Length)
        Assert.Equal(3, sent |> List.map (fun c -> c.CommandId) |> List.distinct |> List.length)
        // ExpectedVersion = the version each command observed: 2, then 3, then 4.
        Assert.Equal<AggregateVersion option list>([ Some(AggregateVersion 2L); Some(AggregateVersion 3L); Some(AggregateVersion 4L) ], sent |> List.map (fun c -> c.ExpectedVersion))
        match sent |> List.map (fun c -> c.Command) with
        | [ Core(ReviseTitle t); Core(ReviseObservation o); Core(ReviseUnknown u) ] ->
            Assert.Equal("  Nightly export stopped at 02:10  ", t.Title.Value)
            Assert.Equal(None, t.Reason)
            Assert.Equal(InvestigationDetails.startObservation before |> Option.map (fun x -> x.Id), Some o.ObservationId)
            Assert.Equal(InvestigationDetails.primaryQuestion before |> Option.map (fun x -> x.Id), Some u.UnknownId)
        | other -> failwithf "%A" other
        // The investigation keeps its identifier; the frame reads the revised values; history keeps the earlier ones.
        let after = Details.investigation h saved
        Assert.Equal(before.Id, after.Id)
        Assert.Equal(AggregateVersion 5L, after.Version)
        let v = Details.workspace h saved
        match v.Frame with
        | Ready f ->
            Assert.Equal("  Nightly export stopped at 02:10  ", f.Title)
            Assert.Equal(Details.identifier.ToString "D", f.Identifier)
            Assert.Equal("The nightly export stopped at 02:10 and wrote no file.", Causa.App.Views.Workspace.WorkspaceFrame.shown f.ProblemStatement)
            Assert.Equal(Some "Why did the export stop,\nand why was no alert raised?", f.Question |> Option.map Causa.App.Views.Workspace.WorkspaceFrame.shown)
        | other -> failwithf "%A" other
        Assert.Contains(after.Timeline, fun e -> match e.Payload with CoreEvent(InvestigationStarted st) -> st.Original.Value = Details.problem | _ -> false)
        // The editor closed and says the change is not saved yet; each event is 1.2.0.
        Assert.False(v.Details.Open)
        Assert.Equal(InvestigationDetails.notSavedYet, v.Details.Status)
        Assert.True(after.Timeline |> List.forall (fun e -> e.Provenance.FormatVersion = "1.2.0"))

    [<Fact>]
    member _.``Only changed fields are sent; an unchanged form sends nothing``() =
        let h = Details.harness StorageVersion.v120
        let s = Details.started h
        let onlyQuestion =
            s
            |> Details.event h "INV-034" None
            |> Details.event h "UIF-DETAILS-QUESTION" (Some "Why did the export stop at 02:10?")
            |> Details.event h "UIA-SAVE-DETAILS" None
        match Details.commandsAfterStart h onlyQuestion |> List.map (fun c -> c.Command) with
        | [ Core(ReviseUnknown u) ] -> Assert.Equal("Why did the export stop at 02:10?", u.Question.Value)
        | other -> failwithf "%A" other
        let unchanged = s |> Details.event h "INV-034" None |> Details.event h "UIA-SAVE-DETAILS" None
        Assert.Empty(Details.commandsAfterStart h unchanged)
        Assert.False((Details.workspace h unchanged).Details.Open)
        Assert.Equal("", (Details.workspace h unchanged).Details.Status)

    [<Fact>]
    member _.``Validation refuses a blank or too long value into the details errors and sends nothing``() =
        let h = Details.harness StorageVersion.v120
        let opened = Details.started h |> Details.event h "INV-034" None
        let attempt field value =
            let s = opened |> Details.event h field (Some value) |> Details.event h "UIA-SAVE-DETAILS" None
            let d = (Details.workspace h s).Details
            Assert.Empty(Details.commandsAfterStart h s)
            Assert.True(d.Open, sprintf "%s=%A closed the editor" field value)
            d.Errors |> List.map (fun e -> FieldId.value e.Field) |> List.distinct
        Assert.Equal<string list>([ "UIF-DETAILS-PROBLEM" ], attempt "UIF-DETAILS-PROBLEM" "")
        Assert.Equal<string list>([ "UIF-DETAILS-PROBLEM" ], attempt "UIF-DETAILS-PROBLEM" "  \n ")
        Assert.Equal<string list>([ "UIF-DETAILS-PROBLEM" ], attempt "UIF-DETAILS-PROBLEM" (String('p', 10001)))
        Assert.Equal<string list>([ "UIF-DETAILS-TITLE" ], attempt "UIF-DETAILS-TITLE" (String('t', 201)))
        Assert.Equal<string list>([ "UIF-DETAILS-TITLE" ], attempt "UIF-DETAILS-TITLE" "   ")
        Assert.Equal<string list>([ "UIF-DETAILS-QUESTION" ], attempt "UIF-DETAILS-QUESTION" (String('q', 10001)))
        Assert.Equal<string list>([ "UIF-DETAILS-QUESTION" ], attempt "UIF-DETAILS-QUESTION" "")
        // One step inside each limit is accepted (AR-009).
        let ok =
            opened
            |> Details.event h "UIF-DETAILS-TITLE" (Some(String('t', 200)))
            |> Details.event h "UIF-DETAILS-PROBLEM" (Some(String('p', 10000)))
            |> Details.event h "UIF-DETAILS-QUESTION" (Some(String('q', 10000)))
            |> Details.event h "UIA-SAVE-DETAILS" None
        Assert.Equal(3, (Details.commandsAfterStart h ok).Length)
        // The errors reach the view as text and aria-invalid on their field.
        let bad = opened |> Details.event h "UIF-DETAILS-PROBLEM" (Some "") |> Details.event h "UIA-SAVE-DETAILS" None
        let b = Causa.App.Views.Workspace.DetailsEditor.bindings (Details.workspace h bad)
        Assert.Equal(BindingValue.Text "true", b["details.problem-aria-invalid"])
        Assert.Equal(BindingValue.Text "false", b["details.title-aria-invalid"])
        Assert.NotEqual(BindingValue.Text "", b["details.problem-error"])
        Assert.Equal(BindingValue.Flag true, b["details.errors-present"])

    [<Fact>]
    member _.``Cancel closes the editor and records nothing``() =
        let h = Details.harness StorageVersion.v120
        let s = Details.started h
        let cancelled =
            s
            |> Details.event h "INV-034" None
            |> Details.event h "UIF-DETAILS-TITLE" (Some "Something else")
            |> Details.event h "INV-036" None
        let d = (Details.workspace h cancelled).Details
        Assert.False(d.Open)
        Assert.Empty(Details.commandsAfterStart h cancelled)
        Assert.Equal("Nightly export", d.Form.Title)
        // Reopening starts from the current details, not the cancelled text.
        let reopened = Details.event h "INV-034" None cancelled
        Assert.Equal("Nightly export", (Details.workspace h reopened).Details.Form.Title)

    [<Fact>]
    member _.``On a 1.1.0 investigation the edit is unavailable with the reason and a revision is refused before anything is sent``() =
        let h = Details.harness StorageVersion.v110
        let s = Details.started h
        let i = Details.investigation h s
        Assert.True(i.Timeline |> List.forall (fun e -> e.Provenance.FormatVersion = "1.1.0"))
        let d = (Details.workspace h s).Details
        match d.Edit with
        | Unavailable reason ->
            Assert.Contains("CAUSA.UNREPRESENTABLE", reason)
            Assert.Contains("1.1.0", reason)
        | other -> failwithf "the edit is %A on a 1.1.0 investigation" other
        let b = Causa.App.Views.Workspace.DetailsEditor.bindings (Details.workspace h s)
        Assert.Equal(BindingValue.Flag false, b["details.edit-offered"])
        Assert.Equal(BindingValue.Flag true, b["details.edit-explained"])
        // Activating the edit opens nothing.
        let tried = Details.event h "INV-034" None s
        Assert.False((Details.workspace h tried).Details.Open)
        // A revision sent anyway is refused by the engine as CAUSA.UNREPRESENTABLE; the session is unchanged.
        let envelope =
            { CommandId = CommandIds.at Details.at
              Investigation = InvestigationId Details.identifier
              ExpectedVersion = Some i.Version
              Command = Core(ReviseTitle { Title = NonEmptyText.create "Renamed" |> Option.get; Reason = None }) }
        let model, effects = (Update.engine h.Backend).Update (Msg.DomainCommand envelope) s.Model
        Assert.Equal<CommandEnvelope list>((Details.session h s).Unsaved, model.Sessions[Details.ref h].Unsaved)
        Assert.Contains(effects, fun e -> match e with LiveAnnounce(_, text) -> text.Contains "CAUSA.UNREPRESENTABLE" | _ -> false)
        Assert.DoesNotContain(effects, fun e -> match e with Http _ -> true | _ -> false)
        // A change 1.1.0 can hold still works there (a new observation).
        Assert.Equal(Available, (WorkspaceAnalysis.contentActions i)[Actions.recordObservation])

    [<Fact>]
    member _.``The Limen names map to the details fields and actions, and the fragment wires them``() =
        for f in [ "UIF-DETAILS-TITLE"; "UIF-DETAILS-PROBLEM"; "UIF-DETAILS-QUESTION" ] do
            Assert.Equal(Some(UiIntent.Input(FieldId f, "x", None)), Events.toIntent f None (Some "x"))
        for a in [ "INV-034"; "UIA-SAVE-DETAILS"; "INV-036" ] do
            Assert.Equal(Some(UiIntent.Activate(ActionId a, None)), Events.toIntent a None None)
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "repository root not found"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        let html = File.ReadAllText(Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "web", "screens", "workspace-frame.html"))
        for spec in DetailsForm.fields do
            let (FieldId f) = spec.Field
            Assert.Contains(sprintf "data-event=\"%s\"" f, html)
            Assert.Contains(sprintf ">%s</label>" spec.Label, html)
        Assert.Contains("data-event=\"INV-034\">Edit details</button>", html)
        Assert.Contains("data-event=\"UIA-SAVE-DETAILS\"", html)
        Assert.Contains(">Save details</button>", html)
        Assert.Contains("data-text=\"details.status\"", html)
