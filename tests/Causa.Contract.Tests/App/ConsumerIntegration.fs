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
          CanSave = false
          WorkingCopy = None }

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

    [<Fact>]
    member _.``A deploy connection that cannot reach GitHub says why, with Try again, not Not connected alone``() =
        Program.reset ()
        let send (m: string) = JsonDocument.Parse(Program.dispatch m).RootElement
        let effects (r: JsonElement) = r.GetProperty("effects").EnumerateArray() |> List.ofSeq |> List.filter (fun e -> e.GetProperty("kind").GetString() = "Http")
        let corr (e: JsonElement) : string = e.GetProperty("correlationId").GetString() |> Option.ofObj |> Option.defaultValue ""
        let result (c: string) (outcome: string) = sprintf "{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"HttpResult\",\"correlationId\":\"%s\",\"outcome\":%s}}" c outcome
        let first = send "{\"kind\":\"Initialize\",\"protocolVersion\":1,\"capabilities\":[],\"location\":{\"origin\":\"https://x.test\",\"path\":\"/\",\"query\":\"\",\"hash\":\"\"}}"
        let read = effects first |> List.head
        let file = "{\"schema\":\"causa.deploy-connection/1\",\"connection\":{\"owner\":\"o\",\"repository\":\"r\",\"branch\":\"main\",\"key\":\"k\"}}"
        let connecting = send (result (corr read) (sprintf "{\"kind\":\"Success\",\"status\":200,\"body\":%s}" (JsonSerializer.Serialize file)))
        let connect = effects connecting |> List.head
        // GH-NETWORK-BEFORE: the request failed before anything was sent.
        let failed = send (result (corr connect) "{\"kind\":\"Failure\",\"reason\":\"network\"}")
        let view = failed.GetProperty "view"
        Assert.True(view.GetProperty("deploy.failed").GetBoolean())
        Assert.Contains("Could not reach GitHub to connect to o/r@main (test deployment)", view.GetProperty("deploy.failure-text").GetString())
        Assert.True(view.GetProperty("connection.connect-offered").GetBoolean(), "the Connect form is no longer offered")
        // Try again retries the same connection.
        let retried = send "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":\"UIA-DEPLOY-RETRY\"}}"
        Assert.Contains(effects retried, fun e -> e.GetProperty("url").GetString() = "https://api.github.com/repos/o/r")
        Assert.False(retried.GetProperty("view").GetProperty("deploy.failed").GetBoolean())
        Program.reset ()

    [<Fact>]
    member _.``Recording an observation and a hypothesis goes through the kernel; Cancel records nothing``() =
        let s = Fixture.started ()
        let input f v st = Fixture.ui.Dispatch (UiIntent.Input(f, v, None)) st |> fst
        let act a st = Fixture.ui.Dispatch (UiIntent.Activate(a, None)) st |> fst
        // Validation first: an empty observation is refused with the catalog's message, nothing reaches the engine.
        let refused = act Actions.recordObservation s
        Assert.NotEmpty(refused.Local.ContentErrors)
        let s =
            s
            |> input Fields.observationText "The export log stops at 02:10:04."
            |> input Fields.observationSource "export.log"
            |> act Actions.recordObservation
            |> input Fields.hypothesisStatement "The disk filled up."
            |> act Actions.recordHypothesis
        Assert.Empty(s.Local.ContentErrors)
        Assert.False(s.Local.Content.ContainsKey Fields.observationText, "the observation editor was not cleared")
        let writable = UiAdapter.workingCopy Fixture.config |> Option.get
        let ref = { Address = Writable.address writable; Id = InvestigationId Fixture.identifier }
        match Update.investigationOf Fixture.backend s.Model ref with
        | Ok(Some i) ->
            Assert.True(i.Observations |> Map.exists (fun _ o -> NonEmptyText.value o.Original = "The export log stops at 02:10:04."))
            Assert.True(i.Hypotheses |> Map.exists (fun _ h -> NonEmptyText.value h.Statement = "The disk filled up."))
        | other -> Assert.Fail(sprintf "%A" other)
        // The workspace offers both editors and shows Hypotheses.
        match (Fixture.ui.Project s).Screen with
        | WorkspaceScreen v ->
            match v.Section with
            | OverviewSection(Ready u) ->
                Assert.Equal(Some Available, (Understood.value u).Actions.TryFind Actions.recordObservation)
                Assert.Equal(Some Available, (Understood.value u).Actions.TryFind Actions.recordHypothesis)
            | other -> Assert.Fail(sprintf "%A" other)
            match v.Frame with
            | Ready f -> Assert.Contains(f.Sections, fun l -> l.Destination = HypothesesSection)
            | other -> Assert.Fail(sprintf "%A" other)
        | other -> Assert.Fail(sprintf "%A" other)
        // Cancel clears the editors and records nothing.
        let before = s.Model.Sessions
        let cancelled = s |> input Fields.hypothesisStatement "Not this one" |> act Actions.cancelEditor
        Assert.True(cancelled.Local.Content.IsEmpty)
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before, cancelled.Model.Sessions)

/// A listing that fails shows the list as unavailable with the reason and a Retry, never the empty state.
type ListingFailure() =
    let backend =
        { Fixture.backend with Provider = Some { Connector = GitHubConnect.connector; Lister = GitHubLister.lister } }
    let engine = Update.engine backend
    let config = { Fixture.config with SessionLocal = false; InvestigationOf = Update.investigationOf backend }
    let ui = UiAdapter.backend config engine

    /// Answers the repository read, then a 403 rate limit for everything else.
    let execute (r: HttpRequest) : HttpOutcome =
        if r.Url.EndsWith "/repos/o/r" then Answered(r.Id, 200, [], """{"full_name":"o/r","permissions":{"push":true}}""")
        else Answered(r.Id, 403, [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1893456000" ], """{"message":"API rate limit exceeded for user."}""")

    let rec settle (s: AdapterState) (effects: HostEffect list) =
        match effects with
        | [] -> s
        | Http r :: rest -> let s2, more = UiAdapter.browser engine (HttpCompleted(execute r)) s in settle s2 (rest @ more)
        | _ :: rest -> settle s rest

    [<Fact>]
    member _.``a rate-limited listing is unavailable, with the reason and a Retry, never empty``() =
        let s0 = fst (ui.Init InvestigationsRoute)
        let s1 = { s0 with Local = { s0.Local with Credential = Some(Credential.create "t"); Form = { Owner = "o"; Repository = "r"; Branch = "main"; CredentialEntered = true } } }
        let s2, fx = ui.Dispatch (UiIntent.Activate(Actions.connect, None)) s1
        let s = settle s2 fx
        Assert.Equal(Connected true, s.Model.Connection)
        let view = View.bindings (ui.Project s)
        let flag k = match view.TryFind k with Some(BindingValue.Flag f) -> f | _ -> false
        Assert.False(flag "list.empty", "the empty state was shown for a failed listing")
        Assert.True(flag "list.retry-offered", "no Retry for a failed listing")
        match (ui.Project s).Screen with
        | ListScreen l -> Assert.Equal(Failed UiFailure.ProviderRateLimited, l.Investigations)
        | other -> Assert.Fail(sprintf "%A" other)
        Assert.Contains(s.Local.Announcements, fun (_, t) -> t.Contains "rate limit")

/// A listed card shows what the kernel projects once the investigation is loaded, and claims nothing before (UI-022).
type ListedSummaries() =
    [<Fact>]
    member _.``a loaded card holds the real lifecycle, activity and counts; an unloaded one holds none``() =
        let address = WorkspaceAddress.catalog (Workspace "workspaces/demo/demonstration-v1")
        let loadedId = InvestigationId(Guid.Parse "6b1c6f0e-2f7a-4a52-9a8e-0d8d6f1e3c12")
        let otherId = InvestigationId(Guid.Parse "6b1c6f0e-2f7a-4a52-9a8e-0d8d6f1e3c13")
        let start =
            { CommandId = CommandIds.at Fixture.at
              Investigation = loadedId
              ExpectedVersion = None
              Command = Core(StartInvestigation { Title = (NonEmptyText.create "Loaded case").Value; ObservationId = Guid.NewGuid(); Original = (NonEmptyText.create "It broke.").Value }) }
        let hypothesis =
            { CommandId = CommandIds.at (Fixture.at.AddMinutes 1.0)
              Investigation = loadedId
              ExpectedVersion = Some(AggregateVersion 1L)
              Command = Core(RecordHypothesis { HypothesisId = HypothesisId(Guid.NewGuid()); Statement = (NonEmptyText.create "A cause").Value; Mechanism = None }) }
        let investigation =
            [ start; hypothesis ] |> List.fold (fun acc c -> acc |> Result.bind (fun st -> Update.apply Fixture.backend st c |> Result.map Some)) (Ok None)
            |> function Ok(Some i) -> i | other -> failwithf "%A" other
        let loadedRef = { Address = address; Id = loadedId }
        let otherRef = { Address = address; Id = otherId }
        let listing =
            { Address = address
              ReadAt = CommitOid "c"
              Investigations =
                [ { Ref = loadedRef; Path = RepoPath "p1"; Title = Some "Loaded case"; ExpectedStateSha256 = None }
                  { Ref = otherRef; Path = RepoPath "p2"; Title = Some "Not yet loaded"; ExpectedStateSha256 = None } ] }
        let verified = { Investigation = investigation; History = investigation.Timeline; ReadAt = CommitOid "c"; Address = address; Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }
        let model0, _ = Fixture.engine.Init(Route "#/")
        let model = { model0 with Loads = Map.ofList [ loadedRef, Loaded verified; otherRef, LoadState.Loading(RequestId 9L, LoadHead) ] }
        match UiAdapter.summaries model listing with
        | [ loaded; other ] ->
            match loaded.Status, loaded.Counts with
            | Some(AsProjected s), Some(AsProjected c) ->
                Assert.Equal("Draft", Understood.value s)
                Assert.Equal({ Hypotheses = Known 1; Evidence = Known 0; UnresolvedContradictions = Known 0 }, Understood.value c)
            | st, c -> Assert.Fail(sprintf "loaded card: %A %A" st c)
            Assert.Equal(Some(Fixture.at.AddMinutes 1.0), loaded.LastActivity)
            Assert.Equal(None, other.Status)
            Assert.Equal(None, other.Counts)
            Assert.Equal(None, other.LastActivity)
        | other -> Assert.Fail(sprintf "%A" other)
