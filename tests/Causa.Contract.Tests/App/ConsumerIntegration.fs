// Owner: integration (T17). Integration checkpoint 1 (ADRP-012 "end-to-end legal action"): integration consumes the
// IF41-APP engine and the IF41-VIEW adapter. A legal command travels the whole path the browser uses — Limen event JSON
// -> Ui/Events -> UiAdapter -> Update.engine -> Kernel.kernel.Decide -> session -> AppView -> view JSON — and an
// illegal one is refused with the kernel's reason, changing nothing.
namespace Causa.Contract.Tests.App

open System
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.App.Ui
open Causa.Ui.Contracts

module private Fixture =
    let at = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero)

    let backend: Backend =
        { Kernel = Kernel.kernel
          Policies = Policies.coreDefault
          Actor = { ActorId = "test" }
          Author = Actor.Human(NonEmptyText.create "test" |> Option.get)
          Now = fun () -> at
          StorageVersion = "1.1.0"
          Persistence = None
          Provider = None
          WorkingCopy = None }

    let identifier = Guid.Parse "6b1c6f0e-2f7a-4a52-9a8e-0d8d6f1e3c11"

    let config: AdapterConfig =
        { NewIdentifier = fun () -> identifier
          Now = fun () -> at
          Run = "session"
          SessionLocal = true
          ValidateStart = Causa.App.Views.StartScreen.validate
          InvestigationOf = Update.investigationOf backend
          CanSave = false }

    let engine = Update.engine backend
    let ui = UiAdapter.backend config engine

    /// Drives the adapter the way the browser does: Limen events by name.
    let event (name: string) (value: string option) (s: AdapterState) =
        match Events.toIntent name None value with
        | Some intent -> ui.Dispatch intent s |> fst
        | None -> failwithf "no intent for event %s" name

    let started () =
        ui.Init StartRoute
        |> fst
        |> event "INV-001" (Some "The nightly export stopped at 02:10.\nIt had run for a year.")
        |> event "INV-023" (Some "Why did the export stop?")
        |> event "INV-004" None

type ConsumerIntegration() =
    [<Fact>]
    member _.``A legal Start command runs end to end and is kept unsaved``() =
        let s = Fixture.started ()
        let writable = UiAdapter.workingCopy Fixture.config |> Option.get
        let ref = { Address = Writable.address writable; Id = InvestigationId Fixture.identifier }
        // The engine created a session holding the start and the question (P19 = A), nothing saved.
        let session = s.Model.Sessions |> Map.tryFind ref
        Assert.True(session.IsSome, "no session was created")
        Assert.Equal(SaveState.Unsaved, session.Value.State)
        Assert.Equal(2, session.Value.Unsaved.Length)
        Assert.Equal(Submission.Accepted, s.Local.Submission)
        // The session's state is the kernel's: replaying its commands gives the investigation with the derived title.
        let investigation = Update.investigationOf Fixture.backend s.Model ref
        match investigation with
        | Ok(Some i) ->
            Assert.Equal("The nightly export stopped at 02:10.", NonEmptyText.value i.Title)
            Assert.Equal(1, i.Unknowns.Count)
            Assert.Equal(AggregateVersion 2L, i.Version)
            // Events carry the command's own time (its UUIDv7), not the time of a re-decision.
            Assert.True(i.Timeline |> List.forall (fun e -> e.Provenance.OccurredAt = Fixture.at))
        | other -> Assert.Fail(sprintf "the session does not replay: %A" other)
        // The adapter opened the new workspace and announced it.
        match s.Local.Route with
        | WorkspaceRoute(target, Overview) -> Assert.Equal(InvestigationId Fixture.identifier, target.Id)
        | other -> Assert.Fail(sprintf "the route is %A" other)
        Assert.Contains(s.Local.Announcements, fun (_, text) -> text.Contains "not saved yet")

    [<Fact>]
    member _.``An illegal command is refused with the kernel's reason and changes nothing``() =
        let s = Fixture.started ()
        let before = s.Model
        // Starting the same investigation again: Decide refuses (it already exists), the model is unchanged.
        let again = Fixture.ui.Dispatch (UiIntent.Navigate StartRoute) s |> fst
        let again = { again with Local = { again.Local with Start = { again.Local.Start with ProblemStatement = "x"; Identifier = Fixture.identifier.ToString() } } }
        let after = Fixture.ui.Dispatch (UiIntent.Activate(Actions.startInvestigation, None)) again |> fst
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before.Sessions, after.Model.Sessions)
        match after.Local.Submission with
        | Submission.Refused reasons -> Assert.NotEmpty(reasons)
        | other -> Assert.Fail(sprintf "the second start was %A" other)
        // A blank problem statement never reaches the engine: the form says why (INV-019).
        let blank = Fixture.ui.Dispatch (UiIntent.Navigate StartRoute) (fst (Fixture.ui.Init InvestigationsRoute)) |> fst
        let refused = Fixture.ui.Dispatch (UiIntent.Activate(Actions.startInvestigation, None)) blank |> fst
        Assert.True(refused.Model.Sessions.IsEmpty)
        Assert.NotEmpty(refused.Local.StartErrors)

    [<Fact>]
    member _.``Cancel leaves the start screen and writes nothing``() =
        let s = Fixture.ui.Init StartRoute |> fst |> Fixture.event "INV-001" (Some "draft")
        let cancelled = Fixture.event "INV-022" None s
        Assert.Equal(InvestigationsRoute, cancelled.Local.Route)
        Assert.True(cancelled.Model.Sessions.IsEmpty)
        Assert.Equal("", cancelled.Local.Start.ProblemStatement)

    [<Fact>]
    member _.``The browser path renders the start screen and the accepted command as Limen view state``() =
        let send (m: string) = JsonDocument.Parse(Program.dispatch m).RootElement.GetProperty "view"
        let location hash = sprintf "{\"origin\":\"http://x\",\"path\":\"/\",\"query\":\"\",\"hash\":\"%s\"}" hash
        let initial = send (sprintf "{\"kind\":\"Initialize\",\"protocolVersion\":1,\"capabilities\":[],\"location\":%s}" (location "#/investigations/new"))
        Assert.True(initial.GetProperty("screen.investigation-start").GetBoolean())
        let event name value = sprintf "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":\"%s\",\"value\":\"%s\"}}" name value
        send (event "INV-001" "A checkpoint problem statement") |> ignore
        let after = send "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":\"INV-004\"}}"
        // The browser build writes to a data repository: before Connect, Start is refused with the reason, nothing created.
        Assert.True(after.GetProperty("start.refused").GetBoolean())
