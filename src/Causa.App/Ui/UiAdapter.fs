// Owner: integration (T17). The real adapter (IF41-VIEW UiBackend over the frozen IF41-APP Engine): UiIntent -> Msg and
// Model -> AppView. Its shape follows the governed sketch (proposals/r10-contract-first-ui/real-adapter-sketch). The
// engine decides; this module only translates and holds UI-local text the frozen Model does not (form fields before
// submit, the latest announcements).
namespace Causa.App.Ui

open System
open System.Security.Cryptography
open System.Text
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts

/// Command identities (causa-storage contract section 2.9): every CommandId is a UUIDv7 whose 48-bit timestamp is the
/// command's RequestedAt in milliseconds, so the time is fixed when the command is created and is the same in the view,
/// in every re-decision and in what is saved. Integration's helper until persistence's Writes.commandIdAt and
/// Writes.requestedAt (slice T19) are merged; then both delegate to those.
[<RequireQualifiedAccess>]
module CommandIds =
    /// A UUIDv7 for a command requested at `requestedAt` (millisecond precision; the rest is random).
    let at (requestedAt: DateTimeOffset) : Guid = Guid.CreateVersion7(requestedAt)

    /// The RequestedAt a UUIDv7 command id carries; None for any other id (a save then fails closed).
    let requestedAt (id: Guid) : DateTimeOffset option =
        if id.Version <> 7 then None
        else
            let b = id.ToByteArray(true)
            let ms = (int64 b[0] <<< 40) ||| (int64 b[1] <<< 32) ||| (int64 b[2] <<< 24) ||| (int64 b[3] <<< 16) ||| (int64 b[4] <<< 8) ||| int64 b[5]
            Some(DateTimeOffset.FromUnixTimeMilliseconds ms)

/// How the adapter is configured at the edge (Program.fs).
type AdapterConfig =
    { /// A fresh investigation identifier for the Start form (INV-006), from the host.
      NewIdentifier: unit -> Guid
      /// The host clock, for new command identities (CommandIds.at).
      Now: unit -> DateTimeOffset
      /// The working-copy run the investigations of this tab are created in (CONTRACT section 9.4).
      Run: string
      /// True while no persistence adapter is wired: Start stays offered and the screens say nothing is saved.
      SessionLocal: bool
      /// The Start form's validation (Views/Start.fs StartScreen.validate, which compiles after this file).
      ValidateStart: StartForm -> ValidationError list }

/// UI-local state the frozen Model does not hold. The credential is the frozen opaque type and is never rendered.
type Local =
    { Route: UiRoute
      Form: ConnectForm
      Credential: Credential.T option
      Errors: ValidationError list
      Start: StartForm
      /// The id of the Start command this form submits: fixed when the form opens, so a resubmission is a Duplicate.
      StartCommandId: Guid
      StartErrors: ValidationError list
      Submission: Submission
      /// The newest first; the shell shows the latest of each politeness.
      Announcements: (Politeness * string) list
      /// Connected with the deployed test build's injected connection (EXT-DATA-004-O5), not a typed key.
      DeployConnected: bool }

type AdapterState = { Model: Model; Local: Local }

[<RequireQualifiedAccess>]
module UiAdapter =
    /// Total over the frozen SM-SAVE states: an eighth state, or a missing one, fails to compile (PERSIST-031).
    let saveKind (s: SaveState) : SaveStateKind =
        match s with
        | SaveState.Saved _ -> SavedKind
        | SaveState.Unsaved -> UnsavedKind
        | SaveState.Saving -> SavingKind
        | SaveState.Offline -> OfflineKind
        | SaveState.Conflict _ -> ConflictKind
        | SaveState.UnknownOutcome _ -> UnknownOutcomeKind
        | SaveState.SaveFailed _ -> SaveFailedKind

    /// A stable identifier derived from another and a purpose (the start command and its first observation), so the
    /// same submission always produces the same command (idempotent retries; Decide reports Duplicate).
    let derivedId (from: Guid) (purpose: string) =
        // SHA-256, not MD5: MD5 is not available in the browser (.NET WebAssembly).
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(from.ToString("D") + "/" + purpose)) |> Array.truncate 16
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x30uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(bytes)

    let workingCopy (config: AdapterConfig) =
        WorkspaceAddress.workingCopy [] config.Run |> Option.bind Writable.ofAddress

    /// The StartInvestigation command a valid Start form describes (INV-004).
    let startCommand (config: AdapterConfig) (commandId: Guid) (form: StartForm) : Result<CommandEnvelope, ValidationError list * string list> =
        match config.ValidateStart form, Guid.TryParse form.Identifier with
        | [], (true, id) ->
            let problem = form.ProblemStatement.Trim()
            // Coordinator's implementer decision (WI-INV-START wiring): INV-002 is optional but the kernel's Title cannot
            // be empty, so a blank title takes the problem statement's first line, trimmed and cut to 200 characters.
            let title =
                if String.IsNullOrWhiteSpace form.Title then
                    let first = problem.Split('\n').[0].Trim()
                    if first.Length > 200 then first.Substring(0, 200) else first
                else form.Title.Trim()
            match NonEmptyText.create title, NonEmptyText.create problem with
            | Some t, Some original ->
                Ok
                    { CommandId = commandId
                      Investigation = InvestigationId id
                      ExpectedVersion = None
                      Command = Core(StartInvestigation { Title = t; ObservationId = derivedId commandId "start-observation"; Original = original }) }
            | _ -> Error([], [ "A problem statement is required." ])
        | [], _ -> Error([], [ "The investigation identifier is not a GUID." ])
        | errors, _ -> Error(errors, errors |> List.map (fun e -> e.Message))

    let private announcementsOf (effects: HostEffect list) =
        effects |> List.choose (function LiveAnnounce(p, text) -> Some(p, text) | _ -> None) |> List.rev

    /// Runs one engine message and keeps the announcements it asked for.
    let private step (engine: Engine) (msg: Msg) (s: AdapterState) : AdapterState * HostEffect list =
        let model, effects = engine.Update msg s.Model
        { Model = model; Local = { s.Local with Announcements = announcementsOf effects @ s.Local.Announcements |> List.truncate 8 } }, effects

    let private connect (engine: Engine) (s: AdapterState) =
        match Connection.validate s.Local.Form, s.Local.Credential with
        | [], Some credential ->
            let repository = { Owner = s.Local.Form.Owner; Name = s.Local.Form.Repository; Branch = s.Local.Form.Branch }
            step engine (Msg.Connection(ConnectionInput.ConnectSubmitted(repository, credential))) { s with Local = { s.Local with Errors = [] } }
        | errors, credential ->
            let missing =
                match credential with
                | Some _ -> []
                | None -> [ { Field = Fields.token; Rule = Required; Message = "Enter an access token." } ]
            { s with Local = { s.Local with Errors = errors @ missing |> List.distinct } }, []

    let private freshStart (config: AdapterConfig) (local: Local) =
        { local with
            Start = StartForm.empty (config.NewIdentifier().ToString "D")
            StartCommandId = CommandIds.at (config.Now())
            StartErrors = []
            Submission = Submission.NotSubmitted }

    /// P19 = A: the primary question (INV-023) is persisted as an informational unknown, in its own command group right
    /// after the start (persistence lane's PrimaryQuestion.startCommands shape, slice T19 e55a5d5). None when blank.
    /// TODO(T19 merge): build both through PrimaryQuestion.startCommands and Writes.commandIdAt.
    let questionCommand (config: AdapterConfig) (start: CommandEnvelope) (question: string) : CommandEnvelope option =
        NonEmptyText.create (question.Trim())
        |> Option.map (fun q ->
            let commandId = CommandIds.at (config.Now())
            { CommandId = commandId
              Investigation = start.Investigation
              ExpectedVersion = Some(AggregateVersion 1L)
              Command = Core(IdentifyUnknown { UnknownId = derivedId commandId "primary-question"; Question = q; Materiality = Materiality.Informational }) })

    /// INV-004: validate, then CreateRequested in the working copy (and the question as the next command); on
    /// acceptance open the new investigation's workspace.
    let private start (config: AdapterConfig) (engine: Engine) (s: AdapterState) =
        match startCommand config s.Local.StartCommandId s.Local.Start, workingCopy config with
        | Error(errors, reasons), _ ->
            { s with Local = { s.Local with StartErrors = errors; Submission = Submission.Refused reasons } }, []
        | Ok _, None -> { s with Local = { s.Local with Submission = Submission.Refused [ "No working copy is available." ] } }, []
        | Ok envelope, Some writable ->
            let address = Writable.address writable
            let ref = { Address = address; Id = envelope.Investigation }
            let before = s.Model.Sessions.ContainsKey ref
            let s2, effects = step engine (Msg.CreateRequested(writable, envelope)) { s with Local = { s.Local with Submission = Submission.Submitting; StartErrors = [] } }
            if not before && s2.Model.Sessions.ContainsKey ref then
                let s2, effects =
                    match questionCommand config envelope s.Local.Start.Question with
                    | Some q ->
                        let s3, fx = step engine (Msg.DomainCommand q) s2
                        s3, effects @ fx
                    | None -> s2, effects
                let (Workspace workspace) = address.Workspace
                let route = WorkspaceRoute({ Workspace = workspace; Role = WorkspaceRole.WorkingCopy; Id = envelope.Investigation }, Overview)
                let s3, fx = step engine (Msg.Navigate(Route(Navigation.format route))) s2
                { s3 with Local = { s3.Local with Route = route; Submission = Submission.Accepted } }, effects @ fx
            else
                { s2 with Local = { s2.Local with Submission = Submission.Refused(announcementsOf effects |> List.map snd) } }, effects

    /// INV-022: back to the list, the draft dropped, nothing written.
    let private cancelStart (config: AdapterConfig) (engine: Engine) (s: AdapterState) =
        let s2, fx = step engine (Msg.Navigate(Route(Navigation.format InvestigationsRoute))) s
        { s2 with Local = { freshStart config s2.Local with Route = InvestigationsRoute } }, fx

    /// A browser signal for the engine (an HTTP answer, connectivity, a timer, a leave attempt).
    let browser (engine: Engine) (signal: BrowserSignal) (s: AdapterState) : AdapterState * HostEffect list =
        step engine (Msg.Browser signal) s

    /// The deployed test build's injected connection (EXT-DATA-004-O5): used only while the tab is disconnected, so a
    /// connection the user already made is never replaced. The key stays the opaque credential.
    let deployConnect (engine: Engine) (repository: Repository) (credential: Credential.T) (s: AdapterState) : AdapterState * HostEffect list =
        match s.Model.Connection with
        | Disconnected ->
            let s2, fx = step engine (Msg.Connection(ConnectionInput.ConnectSubmitted(repository, credential))) s
            { s2 with
                Local =
                    { s2.Local with
                        DeployConnected = true
                        Form = { Owner = repository.Owner; Repository = repository.Name; Branch = repository.Branch; CredentialEntered = true } } },
            fx
        | _ -> s, []

    let private currentRef (s: AdapterState) =
        match s.Local.Route with
        | WorkspaceRoute(t, _) ->
            s.Model.Sessions |> Map.tryFindKey (fun ref _ -> ref.Id = t.Id && ref.Address.Workspace = Workspace t.Workspace)
        | _ -> None

    let dispatch (config: AdapterConfig) (engine: Engine) (intent: UiIntent) (s: AdapterState) : AdapterState * HostEffect list =
        match intent with
        | UiIntent.Navigate r ->
            let s2, fx = step engine (Msg.Navigate(Route(Navigation.format r))) s
            let local =
                match r, s.Local.Route with
                | StartRoute, StartRoute -> s2.Local
                | StartRoute, _ -> freshStart config s2.Local
                | _ -> s2.Local
            { s2 with Local = { local with Route = r } }, fx
        | UiIntent.Input(f, v, _) when Connection.fields |> List.exists (fun x -> x.Field = f) ->
            { s with Local = { s.Local with Form = Connection.withField f v s.Local.Form } }, []
        | UiIntent.Input(f, v, _) when StartForm.fields |> List.exists (fun x -> x.Field = f) ->
            { s with Local = { s.Local with Start = StartForm.withField f v s.Local.Start; Submission = Submission.NotSubmitted } }, []
        | UiIntent.Input _ -> s, []
        | UiIntent.Secret(_, c) ->
            { s with Local = { s.Local with Credential = Some c; Form = { s.Local.Form with CredentialEntered = true } } }, []
        | UiIntent.Activate(a, _) when a = Actions.connect -> connect engine s
        | UiIntent.Activate(a, _) when a = Actions.disconnect -> step engine (Msg.Connection ConnectionInput.DisconnectConfirmed) s
        | UiIntent.Activate(a, _) when a = Actions.startInvestigation -> start config engine s
        | UiIntent.Activate(a, _) when a = Actions.cancelStart -> cancelStart config engine s
        | UiIntent.Activate(a, _) when a = Actions.save ->
            match currentRef s with
            | Some ref -> step engine (Msg.SessionInput(ref, SaveInput.SaveRequested)) s
            | None -> s, []
        | UiIntent.Activate(a, _) when a = Actions.retryLoad ->
            match s.Model.Connection with
            | Connected _ -> step engine (Msg.ListRequested ActiveCatalog) s
            | _ -> s, []
        // Content commands arrive with the workspace screens (T21 onwards); until then they change nothing.
        | UiIntent.Activate _ -> s, []

    let connectionView (s: AdapterState) : ConnectionView =
        { State = s.Model.Connection
          Form = s.Local.Form
          Errors = s.Local.Errors
          Connect = (match s.Model.Connection with Connected _ | Connecting _ -> NotOffered | _ -> Available)
          Disconnect = (match s.Model.Connection with Connected _ -> Available | _ -> NotOffered)
          Connected =
            s.Model.Context
            |> Option.map (fun c ->
                if s.Local.DeployConnected then sprintf "%s/%s@%s (test deployment)" c.Repository.Owner c.Repository.Name c.Repository.Branch
                else sprintf "%s/%s on %s" c.Repository.Owner c.Repository.Name c.Repository.Branch)
          WriteScope = (match s.Model.Connection with Connected true -> Writable | Connected false -> ReadOnlyConnection | _ -> WriteScopeUnknown) }

    let private sessionLocalFailure =
        UiFailure.CapabilityAbsent(DataVisible, "No data repository is connected in this build: changes stay in this tab and are not saved.")

    let startView (config: AdapterConfig) (s: AdapterState) : StartView =
        let destination =
            match s.Model.Context, s.Model.Connection with
            | Some c, Connected canWrite -> Ready { Repository = c.Repository.Owner + "/" + c.Repository.Name; Branch = c.Repository.Branch; Writable = canWrite }
            | _ when config.SessionLocal -> Failed sessionLocalFailure
            | _ -> Failed(UiFailure.CapabilityAbsent(DataVisible, "Connect a data repository to choose where the investigation is saved."))
        { Form = s.Local.Start
          Errors = s.Local.StartErrors
          Destination = destination
          Profile = None
          Start =
            (match s.Model.Connection with
             | Connected true -> Available
             | Connected false -> Unavailable "This token cannot save changes."
             | _ when config.SessionLocal -> Available
             | _ -> Unavailable "Connect a data repository first.")
          Cancel = Available
          Submission = s.Local.Submission }

    /// One card per listed investigation (UI-001, UI-022): title and place as stored (Data Visible); lifecycle and
    /// counts are not claimed before the investigation is opened and replayed.
    let summaries (listing: Listing) : InvestigationSummary list =
        listing.Investigations
        |> List.map (fun i ->
            let (Workspace workspace) = i.Ref.Address.Workspace
            let catalog = i.Ref.Address.Role = WorkspaceRole.Catalog
            let route =
                WorkspaceRoute(
                    { Workspace = workspace; Role = (if catalog then WorkspaceRole.Catalog else WorkspaceRole.WorkingCopy); Id = i.Ref.Id },
                    Overview
                )
            { Ref = i.Ref
              Title = i.Title |> Option.defaultValue (let (InvestigationId g) = i.Ref.Id in g.ToString "D")
              Status = None
              LastActivity = None
              Counts = None
              Copy = (if catalog then FrozenDemonstration else WorkingCopy)
              Href = Navigation.format route })

    let listView (config: AdapterConfig) (s: AdapterState) : InvestigationListView =
        let listings = s.Model.Listings |> Map.toList |> List.map snd
        let investigations, state =
            match s.Model.Connection, listings with
            | Connected _, [] when s.Model.ListingsInFlight.IsEmpty ->
                Failed(UiFailure.ProviderError "The investigations could not be listed."), ScreenFailed(UiFailure.ProviderError "The investigations could not be listed.")
            | Connected _, [] -> Waiting "Listing investigations", ScreenLoading
            | Connected _, _ ->
                let all = listings |> List.collect summaries
                let (CommitOid commit) = (List.head listings).ReadAt
                Ready(Visible.stored { Path = "catalog/active.json"; Commit = Some commit } all), (if all.IsEmpty then ScreenEmpty else ScreenReady)
            | _ -> NotRequested, ScreenReady
        { Connection = connectionView s
          Investigations = investigations
          Sort = ByUpdated
          NewInvestigation =
            (match s.Model.Connection with
             | Connected true -> Available
             | Connected false -> Unavailable "This token cannot save changes."
             | _ when config.SessionLocal -> Available
             | _ -> NotOffered)
          State = state }

    let private notFound (requested: string) : NotFoundView =
        { Requested = requested
          Copy =
            { What = sprintf "There is no page at %s." requested
              Preserved = "Nothing was changed."
              NextStep = "Go to Investigations."
              Retry = false } }

    let private unknownFact label = { Label = label; Status = FactUnknown }

    let administrationView (s: AdapterState) : AdministrationView =
        let connected = s.Model.Context |> Option.map (fun c -> sprintf "%s/%s on %s" c.Repository.Owner c.Repository.Name c.Repository.Branch)
        { Provider = { Label = "Persistence provider"; Status = FactKnown "GitHub" }
          Repository = (match connected with Some r -> { Label = "Data repository"; Status = FactKnown r } | None -> unknownFact "Data repository")
          Connection = { Label = "Connection"; Status = FactKnown(Connection.describe s.Model.Connection) }
          Catalog = unknownFact "Investigation catalog"
          StorageContract = unknownFact "Storage contract"
          ApplicationVersion = unknownFact "Application version"
          DeployedCommit = unknownFact "Deployed commit"
          DataValidation = unknownFact "Data validation"
          Diagnostics = [] }

    let settingsView (s: AdapterState) : SettingsView =
        { Preferences = []
          ConnectionPreference = { Label = "Data repository connection"; Status = FactKnown(Connection.describe s.Model.Connection) } }

    let project (config: AdapterConfig) (s: AdapterState) : AppView =
        let screen =
            match s.Local.Route with
            | InvestigationsRoute -> ListScreen(listView config s)
            | StartRoute -> StartScreen(startView config s)
            | AdministrationRoute -> AdministrationScreen(administrationView s)
            | SettingsRoute -> SettingsScreen(settingsView s)
            | NotFoundRoute requested -> NotFoundScreen(notFound requested)
            // The workspace screens arrive with the ui-2/ui-3 slices; until then a workspace route shows the list.
            | WorkspaceRoute _ -> ListScreen(listView config s)
        { Route = s.Local.Route
          Primary = Navigation.primary s.Local.Route
          Screen = screen
          Announcements = s.Local.Announcements
          Origin = IntegratedBackend }

    let init (config: AdapterConfig) (engine: Engine) (route: UiRoute) : AdapterState * HostEffect list =
        let model, effects = engine.Init(Route(Navigation.format route))
        { Model = model
          Local =
            { Route = route
              Form = Connection.emptyForm
              Credential = None
              Errors = []
              Start = StartForm.empty (config.NewIdentifier().ToString "D")
              StartCommandId = CommandIds.at (config.Now())
              StartErrors = []
              Submission = Submission.NotSubmitted
              Announcements = []
              DeployConnected = false } },
        effects

    /// The frozen Engine as a UiBackend. Effects are the frozen HostEffect, performed by the browser host.
    let backend (config: AdapterConfig) (engine: Engine) : UiBackend<AdapterState, HostEffect> =
        { Provides = (if config.SessionLocal then [ DataEditable ] else [ DataVisible; DataEditable ])
          Init = init config engine
          Dispatch = dispatch config engine
          Project = project config }
