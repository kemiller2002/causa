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

/// Connectivity reaches the sessions (SM-SAVE T-S06, T-S09; PERSIST-018): offline, Save sends nothing; back online,
/// Save is offered again.
type Connectivity() =
    [<Fact>]
    member _.``offline a save sends nothing, and back online the session is Unsaved again``() =
        let address = (WorkspaceAddress.workingCopy [] "offline-test").Value
        let writable = (Writable.ofAddress address).Value
        let backend =
            { Fixture.backend with
                Persistence =
                    Some
                        { Connector = GitHubConnect.connector
                          Machine = SaveMachines.withStorage Fixture.backend.Author
                          Lister = GitHubLister.lister
                          Codec = StorageCodecs.codec }
                WorkingCopy = Some address }
        let engine = Update.engine backend
        let model0, _ = engine.Init(Route "#/")
        let model =
            { model0 with
                Connection = Connected true
                Context = Some { Repository = { Owner = "o"; Name = "r"; Branch = "main" }; Credential = Credential.create "t" }
                Listings = Map.ofList [ address, { Address = address; ReadAt = CommitOid "head"; Investigations = [] } ] }
        let id = InvestigationId(Guid.NewGuid())
        let start =
            { CommandId = CommandIds.at Fixture.at
              Investigation = id
              ExpectedVersion = None
              Command = Core(StartInvestigation { Title = (NonEmptyText.create "Offline").Value; ObservationId = Guid.NewGuid(); Original = (NonEmptyText.create "No network.").Value }) }
        let ref = { Address = address; Id = id }
        let created, _ = engine.Update (Msg.CreateRequested(writable, start)) model
        Assert.Equal(SaveState.Unsaved, created.Sessions[ref].State)
        let offline, _ = engine.Update (Msg.Browser(ConnectivityChanged false)) created
        Assert.Equal(NetworkState.Offline, offline.Network)
        Assert.Equal(SaveState.Offline, offline.Sessions[ref].State)
        let saved, effects = engine.Update (Msg.SessionInput(ref, SaveInput.SaveRequested)) offline
        Assert.DoesNotContain(effects, fun e -> match e with Http _ -> true | _ -> false)
        Assert.Equal(SaveState.Offline, saved.Sessions[ref].State)
        let online, _ = engine.Update (Msg.Browser(ConnectivityChanged true)) saved
        Assert.Equal(SaveState.Unsaved, online.Sessions[ref].State)

/// A saved working copy reopened from the list (Loaded, no session in this tab) is editable: both editors are offered,
/// the first record opens a session on the loaded history, and Save commits at the loaded version from its head.
type ReopenedWorkingCopy() =
    [<Fact>]
    member _.``a loaded working copy offers the editors, records into a new session, and saves from its head``() =
        let address = (WorkspaceAddress.workingCopy [] "session").Value
        let backend =
            { Fixture.backend with
                Persistence =
                    Some
                        { Connector = GitHubConnect.connector
                          Machine = SaveMachines.withStorage Fixture.backend.Author
                          Lister = GitHubLister.lister
                          Codec = StorageCodecs.codec }
                WorkingCopy = Some address }
        let engine = Update.engine backend
        let config = { Fixture.config with SessionLocal = false; CanSave = true; InvestigationOf = Update.investigationOf backend; WorkingCopy = Some address }
        let ui = UiAdapter.backend config engine
        let id = InvestigationId(Guid.NewGuid())
        let start =
            { CommandId = CommandIds.at Fixture.at
              Investigation = id
              ExpectedVersion = None
              Command = Core(StartInvestigation { Title = (NonEmptyText.create "Saved earlier").Value; ObservationId = Guid.NewGuid(); Original = (NonEmptyText.create "It broke.").Value }) }
        let investigation = Update.apply backend None start |> function Ok i -> i | Error e -> failwith e
        let files = StorageCodecs.codec.EncodeNew id investigation.Timeline |> function Ok f -> f | Error e -> failwithf "%A" e
        let ref = { Address = address; Id = id }
        let verified = { Investigation = investigation; History = investigation.Timeline; ReadAt = CommitOid "loadedhead"; Address = address; Bytes = files }
        let (InvestigationId g) = id
        let route = WorkspaceRoute({ Workspace = "workspaces/demo/causa-app-session"; Role = WorkspaceRole.WorkingCopy; Id = id }, Overview)
        let s0, _ = ui.Init route
        let s =
            { s0 with
                Model =
                    { s0.Model with
                        Connection = Connected true
                        Context = Some { Repository = { Owner = "o"; Name = "r"; Branch = "main" }; Credential = Credential.create "t" }
                        Loads = Map.ofList [ ref, Loaded verified ] } }
        // Both editors are offered and Hypotheses is listed.
        match (ui.Project s).Screen with
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
        ignore g
        // Recording opens a session on the loaded history.
        let recorded =
            s
            |> (fun st -> ui.Dispatch (UiIntent.Input(Fields.observationText, "A new clue.", None)) st |> fst)
            |> (fun st -> ui.Dispatch (UiIntent.Activate(Actions.recordObservation, None)) st |> fst)
        let session = recorded.Model.Sessions |> Map.tryFind ref
        Assert.True(session.IsSome, sprintf "no session: %A" recorded.Local.Announcements)
        Assert.Equal(SaveState.Unsaved, session.Value.State)
        Assert.Equal(CommitOid "loadedhead", session.Value.BaseHead)
        Assert.Equal(Some investigation.Version, session.Value.Unsaved.Head.ExpectedVersion)
        // Save commits from the loaded head.
        let saving, effects = ui.Dispatch (UiIntent.Activate(Actions.save, None)) recorded
        let mutation = effects |> List.tryPick (function Http r when r.Body.IsSome -> Some r | _ -> None)
        Assert.True(mutation.IsSome, "Save sent no commit")
        Assert.Contains("loadedhead", mutation.Value.Body.Value)
        Assert.Equal(SaveState.Saving, saving.Model.Sessions[ref].State)

/// A hypothesis recorded once is saved once, even when the save rebases over another writer's commit and Save is
/// pressed again: exactly one hypothesis and one hypothesis event in what GitHub holds.
type SaveOnce() =
    [<Fact>]
    member _.``record, save over a moved head, save again: one hypothesis, one event``() =
        let fake = Causa.TestSupport.StatefulFake()
        let address = (WorkspaceAddress.workingCopy [] "save-once").Value
        let writable = (Writable.ofAddress address).Value
        let backend =
            { Fixture.backend with
                Persistence =
                    Some
                        { Connector = GitHubConnect.connector
                          Machine = SaveMachines.withStorage Fixture.backend.Author
                          Lister = GitHubLister.lister
                          Codec = StorageCodecs.codec }
                WorkingCopy = Some address }
        let engine = Update.engine backend
        let rec settle (model: Model) (effects: HostEffect list) =
            match effects with
            | [] -> model
            | Http r :: rest -> let m, more = engine.Update (Msg.Browser(HttpCompleted(fake.Execute r))) model in settle m (rest @ more)
            | _ :: rest -> settle model rest
        let send msg model = let m, fx = engine.Update msg model in settle m fx
        let connect () =
            let m0, _ = engine.Init(Route "#/")
            send (Msg.Connection(ConnectionInput.ConnectSubmitted(fake.WriteConnection.Repository, fake.WriteConnection.Credential))) m0
        let id = InvestigationId(Guid.NewGuid())
        let ref = { Address = address; Id = id }
        let start =
            { CommandId = CommandIds.at DateTimeOffset.UtcNow
              Investigation = id
              ExpectedVersion = None
              Command = Core(StartInvestigation { Title = (NonEmptyText.create "Save once").Value; ObservationId = Guid.NewGuid(); Original = (NonEmptyText.create "Twice?").Value }) }
        let started = connect () |> send (Msg.CreateRequested(writable, start)) |> send (Msg.SessionInput(ref, SaveInput.SaveRequested))
        Assert.True((match started.Sessions[ref].State with SaveState.Saved _ -> true | _ -> false), "the start did not save")
        let hypothesis =
            { CommandId = CommandIds.at (DateTimeOffset.UtcNow.AddSeconds 1.0)
              Investigation = id
              ExpectedVersion = Some(AggregateVersion 1L)
              Command = Core(RecordHypothesis { HypothesisId = HypothesisId(Guid.NewGuid()); Statement = (NonEmptyText.create "Only once").Value; Mechanism = None }) }
        let recorded = send (Msg.DomainCommand hypothesis) started
        // Another writer moves the head (an unrelated path), so this save rebases before it commits.
        fake.CommitAsOtherWriter("another writer", [ "workspaces/demo/elsewhere/note.txt", Some(Text.Encoding.UTF8.GetBytes "x") ]) |> ignore
        let saved = send (Msg.SessionInput(ref, SaveInput.SaveRequested)) recorded
        Assert.True((match saved.Sessions[ref].State with SaveState.Saved _ -> true | _ -> false), sprintf "the hypothesis save ended %A" saved.Sessions[ref].State)
        let savedAgain = send (Msg.SessionInput(ref, SaveInput.SaveRequested)) saved
        Assert.True((match savedAgain.Sessions[ref].State with SaveState.Saved _ -> true | _ -> false))
        // What this tab shows and what a fresh tab reads back from GitHub: one hypothesis, one hypothesis event.
        let count (i: Investigation) =
            i.Hypotheses.Count, i.Timeline |> List.filter (fun e -> match e.Payload with CoreEvent(HypothesisRecorded _) -> true | _ -> false) |> List.length
        match Update.investigationOf backend savedAgain ref with
        | Ok(Some i) -> Assert.Equal((1, 1), count i)
        | other -> Assert.Fail(sprintf "%A" other)
        let fresh = connect ()
        match fresh.Loads |> Map.tryFind ref with
        | Some(Loaded v) -> Assert.Equal((1, 1), count v.Investigation)
        | other -> Assert.Fail(sprintf "the fresh tab did not load it: %A" other)

/// The tab that saved keeps every section of the investigation when it reopens it from the list: the sections come
/// from the current investigation, with the working copy's editors offered, on the session and the loaded branch alike.
type ReopenAfterSave() =
    [<Fact>]
    member _.``start, record a hypothesis, save, list, reopen in the same tab: Causal Analysis is listed``() =
        let fake = Causa.TestSupport.StatefulFake()
        let address = (WorkspaceAddress.workingCopy [] "reopen").Value
        let backend =
            { Fixture.backend with
                Persistence =
                    Some
                        { Connector = GitHubConnect.connector
                          Machine = SaveMachines.withStorage Fixture.backend.Author
                          Lister = GitHubLister.lister
                          Codec = StorageCodecs.codec }
                WorkingCopy = Some address
                Now = fun () -> DateTimeOffset.UtcNow }
        let engine = Update.engine backend
        let config =
            { Fixture.config with
                NewIdentifier = Guid.NewGuid
                Run = "reopen"
                Now = fun () -> DateTimeOffset.UtcNow
                SessionLocal = false
                CanSave = true
                InvestigationOf = Update.investigationOf backend
                WorkingCopy = Some address }
        let ui = UiAdapter.backend config engine
        let rec settle (s: AdapterState) (effects: HostEffect list) =
            match effects with
            | [] -> s
            | Http r :: rest -> let s2, more = UiAdapter.browser engine (HttpCompleted(fake.Execute r)) s in settle s2 (rest @ more)
            | _ :: rest -> settle s rest
        let go intent s = let s2, fx = ui.Dispatch intent s in settle s2 fx
        let event name value s =
            match Events.toIntent name None value with
            | Some intent -> go intent s
            | None -> failwithf "no intent for event %s" name
        let sections (s: AdapterState) =
            match (ui.Project s).Screen with
            | WorkspaceScreen v ->
                match v.Frame with
                | Ready f -> f.Sections |> List.map (fun l -> l.Destination)
                | other -> failwithf "no frame: %A" other
            | other -> failwithf "not a workspace: %A" other
        let s0 = fst (ui.Init InvestigationsRoute)
        let connected =
            { s0 with
                Local =
                    { s0.Local with
                        Credential = Some fake.WriteConnection.Credential
                        Form = { Owner = fake.Owner; Repository = fake.Name; Branch = fake.Branch; CredentialEntered = true } } }
            |> go (UiIntent.Activate(Actions.connect, None))
        Assert.Equal(Connected true, connected.Model.Connection)
        let started =
            connected
            |> go (UiIntent.Navigate StartRoute)
            |> event "INV-001" (Some "The nightly export stopped at 02:10.")
            |> event "INV-023" (Some "Why did the export stop?")
            |> event "INV-004" None
        let route = started.Local.Route
        let target =
            match route with
            | WorkspaceRoute(t, _) -> t
            | other -> failwithf "the start did not open the workspace: %A %A" other started.Local.Submission
        let recorded =
            started
            |> go (UiIntent.Input(Fields.hypothesisStatement, "The export job lost its database lock", None))
            |> go (UiIntent.Activate(Actions.recordHypothesis, None))
        Assert.Contains(CausalAnalysis, sections recorded)
        let saved = recorded |> go (UiIntent.Activate(Actions.save, None))
        Assert.True(
            saved.Model.Sessions |> Map.forall (fun _ v -> match v.State with SaveState.Saved _ -> true | _ -> false),
            sprintf "the save did not finish: %A" (saved.Model.Sessions |> Map.map (fun _ v -> v.State)))
        Assert.Contains(CausalAnalysis, sections saved)
        let reopened =
            saved
            |> go (UiIntent.Navigate InvestigationsRoute)
            |> go (UiIntent.Navigate(WorkspaceRoute(target, Overview)))
        Assert.Equal<Section list>([ Overview; CausalAnalysis; HypothesesSection; TimelineSection ], sections reopened)

/// The evidence, link and conclusion editors (UIA-RECORD-EVIDENCE, INV-030, INV-032): availability from the kernel's
/// capabilities, validation into ContentErrors, the command at the investigation's version, Cancel clears the drafts.
type EvidenceLinkConclusion() =
    let input f v st = Fixture.ui.Dispatch (UiIntent.Input(f, v, None)) st |> fst
    let act a st = Fixture.ui.Dispatch (UiIntent.Activate(a, None)) st |> fst
    let ref =
        let writable = UiAdapter.workingCopy Fixture.config |> Option.get
        { Address = Writable.address writable; Id = InvestigationId Fixture.identifier }
    let investigation (s: AdapterState) =
        match Update.investigationOf Fixture.backend s.Model ref with
        | Ok(Some i) -> i
        | other -> failwithf "%A" other
    let analysis (s: AdapterState) =
        match (Fixture.ui.Project s).Screen with
        | WorkspaceScreen v ->
            match v.Section with
            | OverviewSection(Ready u) -> Understood.value u
            | other -> failwithf "%A" other
        | other -> failwithf "%A" other
    let lastEnvelope (s: AdapterState) = s.Model.Sessions[ref].Unsaved |> List.last
    let withHypothesis () =
        Fixture.started () |> input Fields.hypothesisStatement "The disk filled up." |> act Actions.recordHypothesis
    let withEvidence () =
        withHypothesis ()
        |> input Fields.evidenceLabel "df at 02:10"
        |> input Fields.evidenceSource "ops runbook capture"
        |> input Fields.evidenceContent "/var 100% used"
        |> input Fields.evidenceCategory "measurement"
        |> act Actions.recordEvidence

    [<Fact>]
    member _.``availability comes from the kernel: linking waits for evidence, the other two are offered``() =
        let s = withHypothesis ()
        let actions = (analysis s).Actions
        Assert.Equal(Some Available, actions.TryFind Actions.recordEvidence)
        Assert.Equal(Some Available, actions.TryFind Actions.recordConclusion)
        Assert.Equal(Some(Unavailable "No usable evidence has been admitted."), actions.TryFind Actions.linkEvidence)
        Assert.Equal(Some Available, (analysis (withEvidence ())).Actions.TryFind Actions.linkEvidence)

    [<Fact>]
    member _.``invalid editors put their errors in ContentErrors and send nothing``() =
        let s = withHypothesis ()
        let before = s.Model.Sessions
        let evidence = s |> input Fields.evidenceLabel "df at 02:10" |> act Actions.recordEvidence
        Assert.Contains(evidence.Local.ContentErrors, fun e -> e.Field = Fields.evidenceCategory)
        Assert.Contains(evidence.Local.ContentErrors, fun e -> e.Field = Fields.evidenceSource)
        Assert.Contains(evidence.Local.ContentErrors, fun e -> e.Field = Fields.evidenceContent)
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before, evidence.Model.Sessions)
        let link =
            s
            |> input Fields.linkEvidence (Guid.NewGuid().ToString())
            |> input Fields.linkHypothesis (Guid.NewGuid().ToString())
            |> input Fields.relationshipKind "supports"
            |> input Fields.relationshipRationale "why"
            |> act Actions.linkEvidence
        Assert.Contains(link.Local.ContentErrors, fun e -> e.Field = Fields.linkEvidence)
        Assert.Contains(link.Local.ContentErrors, fun e -> e.Field = Fields.linkHypothesis)
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before, link.Model.Sessions)
        let conclusion = s |> input Fields.conclusionStatement "The disk filled." |> input Fields.conclusionStrength "certain" |> act Actions.recordConclusion
        Assert.Contains(conclusion.Local.ContentErrors, fun e -> e.Field = Fields.conclusionStrength)
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before, conclusion.Model.Sessions)

    [<Fact>]
    member _.``a working copy with one evidence item that supports one hypothesis shows it in the causal graph``() =
        let s = withEvidence ()
        Assert.Empty(s.Local.ContentErrors)
        let i1 = investigation s
        let evidenceId, evidence = i1.Evidence |> Map.toList |> List.exactlyOne
        Assert.Equal("df at 02:10", evidence.Label |> Option.map NonEmptyText.value |> Option.defaultValue "")
        Assert.Equal(EvidenceCategory.Measurement, evidence.Category)
        let (HypothesisId h) = i1.Hypotheses |> Map.toList |> List.exactlyOne |> fst
        Assert.False(s.Local.Content.ContainsKey Fields.evidenceLabel, "the evidence editor was not cleared")
        let linked =
            s
            |> input Fields.linkEvidence (evidenceId.ToString())
            |> input Fields.linkHypothesis (h.ToString())
            |> input Fields.relationshipKind "supports"
            |> input Fields.relationshipRationale "A full /var stops every write."
            |> act Actions.linkEvidence
        Assert.Empty(linked.Local.ContentErrors)
        // The command went at the version the investigation had, with an id from CommandIds.
        let sent = lastEnvelope linked
        Assert.Equal(Some i1.Version, sent.ExpectedVersion)
        let i2 = investigation linked
        let relationship = i2.Relationships |> Map.toList |> List.exactlyOne |> snd
        Assert.Equal(Causa.Domain.RelationshipKind.Supports, relationship.Kind)
        // Causal Analysis and Evidence are listed, and the graph draws one supports line from the evidence.
        match (Fixture.ui.Project linked).Screen with
        | WorkspaceScreen v ->
            match v.Frame with
            | Ready f ->
                Assert.Contains(f.Sections, fun l -> l.Destination = CausalAnalysis)
                Assert.Contains(f.Sections, fun l -> l.Destination = EvidenceSection)
            | other -> Assert.Fail(sprintf "%A" other)
        | other -> Assert.Fail(sprintf "%A" other)
        let graph = Causa.App.Views.CausalGraph.layout (analysis linked)
        let edge = graph.Edges |> List.exactlyOne
        Assert.Equal(Causa.App.Views.EdgeKind.SupportsEdge, edge.Kind)
        Assert.Equal(0, graph.UnlinkedEvidence)
        // A conclusion with its strength; the start's open question needs the residual uncertainty (R6.4).
        let concluded =
            linked
            |> input Fields.conclusionStatement "The disk filled up and the export stopped."
            |> input Fields.conclusionStrength "leadingExplanation"
            |> input Fields.conclusionResidual "Why /var filled is not known yet."
            |> act Actions.recordConclusion
        Assert.Empty(concluded.Local.ContentErrors)
        Assert.Equal(Some i2.Version, (lastEnvelope concluded).ExpectedVersion)
        let c = (investigation concluded).Conclusions |> Map.toList |> List.exactlyOne |> snd
        Assert.Equal(ConclusionStrength.LeadingExplanation, c.Strength)
        Assert.Equal(Some "Why /var filled is not known yet.", c.ResidualUncertainty |> Option.map NonEmptyText.value)

    [<Fact>]
    member _.``Cancel clears the evidence, link and conclusion drafts and records nothing``() =
        let s = withHypothesis ()
        let before = s.Model.Sessions
        let cancelled =
            s
            |> input Fields.evidenceLabel "draft"
            |> input Fields.evidenceCategory "log"
            |> input Fields.relationshipRationale "draft"
            |> input Fields.conclusionStatement "draft"
            |> input Fields.conclusionStrength "plausibleExplanation"
            |> act Actions.cancelEditor
        Assert.True(cancelled.Local.Content.IsEmpty)
        Assert.Equal<Map<InvestigationRef, InvestigationSession>>(before, cancelled.Model.Sessions)
