// Owner: integration (T17). The engine loop (IF41-APP Engine) over the domain kernel and, when it is wired, the
// persistence provider. The seam is the Backend record: Program.fs supplies it, and the persistence lane's adapter
// (Connector, SaveMachine, WorkspaceLister, StorageCodec; slices T19/T20) plugs in as Backend.Persistence without a
// change here. Without persistence the engine still decides every command through the one domain decision path
// (Kernel.kernel), keeps accepted commands as unsaved changes of a session, and says plainly that nothing is saved.
// Pure: time comes from Backend.Now, effects are data (HostEffect) the browser host performs.
namespace Causa.App

open System
open Causa.Domain
open Causa.Persistence
open Causa.App.Ui

/// The persistence adapter the persistence lane provides (IF41-ERRORS, IF41-SAVE, IF41-STORAGE). The save machine is
/// built per use from the base history a session rests on (its verified load, or none for a new investigation), so
/// the encoder can reproduce the exact bytes on read-back and resend (Program.fs: SaveMachines.withStorage).
type Persistence =
    { Connector: Connector
      Machine: (InvestigationSession -> VerifiedInvestigation option) -> SaveMachine
      Lister: WorkspaceLister
      Codec: StorageCodec }

/// The provider's read side: Connect and the investigation listing (T20). Used when no full persistence adapter is
/// wired; a Persistence adapter brings its own and takes precedence.
type ProviderSeam = { Connector: Connector; Lister: WorkspaceLister }

/// Everything the engine takes from outside. Swapping Persistence = None for Some adapter is the whole integration of
/// the provider; the domain side is always the one kernel.
type Backend =
    { Kernel: DomainKernel
      Policies: PolicySet
      Actor: ActorContext
      /// The actor recorded in event provenance.
      Author: Actor
      Now: unit -> DateTimeOffset
      /// The storage contract version written into new events (formatVersion and semanticVersion).
      StorageVersion: string
      Persistence: Persistence option
      Provider: ProviderSeam option
      /// The working copy this tab creates in and lists after Connect (CONTRACT section 9.4), when there is one.
      WorkingCopy: WorkspaceAddress.T option }

/// Why a message changed nothing, as words for the live region (never a silent drop).
type Refusal =
    | NotWired of what: string
    | Rejected of reason: string

[<RequireQualifiedAccess>]
module Update =
    let private nextId (RequestId n) = RequestId(n + 1L)

    /// The connector and lister in use: the persistence adapter's, else the provider seam's.
    let private connectorOf (backend: Backend) =
        match backend.Persistence, backend.Provider with
        | Some p, _ -> Some p.Connector
        | None, Some r -> Some r.Connector
        | None, None -> None

    let private listerOf (backend: Backend) =
        match backend.Persistence, backend.Provider with
        | Some p, _ -> Some p.Lister
        | None, Some r -> Some r.Lister
        | None, None -> None

    let private announce politeness text = LiveAnnounce(politeness, text)

    let private refuse (refusal: Refusal) (model: Model) : Model * HostEffect list =
        match refusal with
        | NotWired what -> model, [ announce Polite (sprintf "%s needs a connected data repository, which this build does not have yet." what) ]
        | Rejected reason -> model, [ announce Assertive reason ]

    /// The initial model: disconnected, online, nothing loaded.
    let initialModel (route: Route) : Model =
        { Connection = Disconnected
          Context = None
          NextRequestId = RequestId 1L
          Network = NetworkState.Online
          Route = route
          Loads = Map.empty
          Sessions = Map.empty
          Listings = Map.empty
          ListingsInFlight = Map.empty }

    /// The words of a decision that produced nothing.
    let describe (decision: Decision) : string =
        match decision with
        | Allowed _ -> "Accepted."
        | Blocked blockers -> blockers |> List.map (fun b -> b.Message) |> String.concat " "
        | RequiresReview reasons -> "Needs review: " + String.concat " " reasons
        | UnsupportedSemantics ids -> "This build does not understand: " + String.concat ", " ids
        | Conflict(AggregateVersion expected, AggregateVersion actual) ->
            sprintf "The investigation changed (expected version %d, found %d)." expected actual
        | Duplicate _ -> "That command was already applied."

    /// The save machine for the model's sessions, each resting on its verified base (Loads), or on none when new.
    let machineFor (p: Persistence) (model: Model) : SaveMachine =
        p.Machine(fun s ->
            match model.Loads |> Map.tryFind { Address = Writable.address s.Address; Id = s.Id } with
            | Some(Loaded v) -> Some v
            | _ -> None)

    /// Applies one command to a state through the kernel: decide, then the events it allows, evolved in order. The
    /// events carry the command's own RequestedAt (its UUIDv7 id), never the time of this re-decision.
    let apply (backend: Backend) (state: Investigation option) (command: CommandEnvelope) : Result<Investigation, string> =
        match backend.Kernel.Decide backend.Policies backend.Actor state command, CommandIds.requestedAt command.CommandId with
        | _, None -> Error "The command has no UUIDv7 identity, so its time cannot be fixed; it is refused."
        | Allowed payloads, Some requestedAt ->
            let current = state |> Option.map (fun i -> i.Version) |> Option.defaultValue (AggregateVersion 0L)
            Events.envelopes command.Investigation current command.CommandId requestedAt backend.Author backend.StorageVersion payloads
            |> List.fold
                (fun acc env -> acc |> Result.bind (fun s -> backend.Kernel.Evolve s env |> Result.map Some |> Result.mapError (fun e -> e.Reason)))
                (Ok state)
            |> Result.bind (function
                | Some i -> Ok i
                | None -> Error "The command produced no investigation.")
        | other, _ -> Error(describe other)

    /// The investigation a session shows: its verified base (a load), or nothing for a new one, with every unsaved
    /// command applied in order. Session-local until the save machine confirms it (PERSIST-031).
    let investigationOf (backend: Backend) (model: Model) (ref: InvestigationRef) : Result<Investigation option, string> =
        let baseState =
            match model.Loads |> Map.tryFind ref with
            | Some(Loaded v) -> Some v.Investigation
            | _ -> None
        match model.Sessions |> Map.tryFind ref with
        | None -> Ok baseState
        | Some session ->
            session.Unsaved @ session.AfterInFlight
            |> List.fold (fun acc c -> acc |> Result.bind (fun s -> apply backend s c |> Result.map Some)) (Ok baseState)

    /// A save the read-back confirmed (BaseHead moved to the saved commit) makes the saved history the session's new
    /// verified base: the previous base with the saved commands applied, the files the encoder wrote (the read-back
    /// matched them byte for byte), read at that commit. Later saves and the view rest on it (PERSIST-023).
    let confirmBase (backend: Backend) (p: Persistence) (ref: InvestigationRef) (before: InvestigationSession) (after: InvestigationSession) (model: Model) : Model =
        if after.BaseHead = before.BaseHead then model
        else
            let baseVerified =
                match model.Loads |> Map.tryFind ref with
                | Some(Loaded v) -> Some v
                | _ -> None
            let saved =
                before.Unsaved
                |> List.fold (fun acc c -> acc |> Result.bind (fun st -> apply backend st c |> Result.map Some)) (Ok(baseVerified |> Option.map (fun v -> v.Investigation)))
            match saved with
            | Ok(Some investigation) ->
                let history = investigation.Timeline
                let bytes =
                    match baseVerified with
                    | None -> p.Codec.EncodeNew investigation.Id history |> Result.toOption
                    | Some v ->
                        let (AggregateVersion known) = v.Investigation.Version
                        Some(p.Codec.Encode v (history |> List.filter (fun e -> let (AggregateVersion n) = e.Version in n > known)))
                match bytes with
                | Some files ->
                    let verified = { Investigation = investigation; History = history; ReadAt = after.BaseHead; Address = ref.Address; Bytes = files }
                    { model with Loads = model.Loads |> Map.add ref (Loaded verified) }
                | None -> model
            | _ -> model

    let private sessionRef (session: InvestigationSession) : InvestigationRef =
        { Address = Writable.address session.Address; Id = session.Id }

    /// A request context for persistence operations; only meaningful once connected.
    let private requestContext (backend: Backend) (model: Model) (except: InvestigationRef option) : RequestContext option =
        model.Context
        |> Option.map (fun ctx ->
            { Connection = ctx
              NextId = model.NextRequestId
              MutationInFlightElsewhere =
                model.Sessions
                |> Map.exists (fun ref s -> Some ref <> except && s.State = SaveState.Saving)
              Now = backend.Now() })

    /// Save-machine effects become host effects. A Send consumes the request id the context offered.
    let private hostEffects (effects: SaveEffect list) : HostEffect list * bool =
        let mapped =
            effects
            |> List.map (fun e ->
                match e with
                | Send request -> Http request
                | ScheduleReconcileRead(ref, ms) -> Timer(ReconcileTimer ref, ms)
                | Announce(p, text) -> LiveAnnounce(p, text)
                | ExportCommands commands -> Download("causa-unsaved-commands.txt", commands |> List.map (sprintf "%A") |> String.concat "\n"))
        mapped, effects |> List.exists (function Send _ -> true | _ -> false)

    /// v4.1.5 C1: create an investigation in a working copy from its StartInvestigation command (EXT-DATA-008).
    let private create (backend: Backend) (writable: Writable.T) (command: CommandEnvelope) (model: Model) =
        let ref = { Address = Writable.address writable; Id = command.Investigation }
        if model.Sessions.ContainsKey ref then
            refuse (Rejected "That investigation already exists in this working copy.") model
        else
            match apply backend None command with
            | Error reason -> refuse (Rejected reason) model
            | Ok investigation ->
                let online = model.Network = NetworkState.Online
                let session =
                    match backend.Persistence, model.Listings |> Map.tryFind ref.Address with
                    | Some p, Some listing -> Ok((machineFor p model).InitialNew writable command.Investigation listing.ReadAt command online)
                    | Some _, None -> Error "List the working copy before creating in it: its head is not known."
                    | None, _ ->
                        // No provider: the same state InitialNew reaches (Unsaved online, Offline offline), with no
                        // base commit, because nothing was read or written.
                        Ok
                            { Id = command.Investigation
                              Address = writable
                              BaseHead = CommitOid ""
                              State = (if online then SaveState.Unsaved else SaveState.Offline)
                              Unsaved = [ command ]
                              AfterInFlight = []
                              Outstanding = None
                              Mutations = 0
                              RebaseAttempts = 0
                              ReconcileReads = 0
                              ReconcileReadsSentAt = [] }
                match session with
                | Error reason -> refuse (Rejected reason) model
                | Ok s ->
                    { model with Sessions = model.Sessions |> Map.add ref s },
                    [ announce Polite (sprintf "Started \"%s\". It is not saved yet." (NonEmptyText.value investigation.Title)) ]

    /// A domain command on an open session: decided against the session's current state, kept as an unsaved change.
    let private command (backend: Backend) (envelope: CommandEnvelope) (model: Model) =
        match model.Sessions |> Map.tryFindKey (fun ref _ -> ref.Id = envelope.Investigation) with
        | None -> refuse (Rejected "That investigation is not open for editing here.") model
        | Some ref ->
            let session = model.Sessions[ref]
            match investigationOf backend model ref |> Result.bind (fun state -> apply backend state envelope) with
            | Error reason -> refuse (Rejected reason) model
            | Ok _ ->
                match backend.Persistence, requestContext backend model (Some ref) with
                | Some p, Some ctx ->
                    match (machineFor p model).Step ctx session (SaveInput.Edit envelope) with
                    | Ok(s, effects) ->
                        let fx, sent = hostEffects effects
                        { model with
                            Sessions = model.Sessions |> Map.add ref s
                            NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) },
                        fx
                    | Error fault -> refuse (Rejected fault.Reason) model
                | _ ->
                    let s = { session with Unsaved = session.Unsaved @ [ envelope ]; State = (if session.State = SaveState.Offline then SaveState.Offline else SaveState.Unsaved) }
                    { model with Sessions = model.Sessions |> Map.add ref s }, [ announce Polite "Change recorded. It is not saved yet." ]

    let private sessionInput (backend: Backend) (ref: InvestigationRef) (input: SaveInput) (model: Model) =
        match model.Sessions |> Map.tryFind ref, backend.Persistence, requestContext backend model (Some ref) with
        | None, _, _ -> refuse (Rejected "That investigation is not open for editing here.") model
        | Some _, None, _ -> refuse (NotWired "Saving") model
        | Some _, Some _, None -> refuse (Rejected "Connect a data repository before saving.") model
        | Some session, Some p, Some ctx ->
            match (machineFor p model).Step ctx session input with
            | Ok(s, effects) ->
                let fx, sent = hostEffects effects
                let model' =
                    { model with
                        Sessions = model.Sessions |> Map.add ref s
                        NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) }
                (confirmBase backend p ref session s model', fx)
            | Error fault -> refuse (Rejected fault.Reason) model

    let private connection (backend: Backend) (input: ConnectionInput) (model: Model) =
        match input, connectorOf backend with
        | ConnectionInput.ConnectSubmitted(repository, credential), Some connector ->
            let ctx = { Repository = repository; Credential = credential }
            { model with
                Connection = Connecting model.NextRequestId
                Context = Some ctx
                NextRequestId = nextId model.NextRequestId },
            [ Http(connector.ConnectRequest model.NextRequestId ctx) ]
        | ConnectionInput.ConnectSubmitted _, None -> refuse (NotWired "Connecting") model
        | ConnectionInput.ConnectSucceeded canWrite, _ -> { model with Connection = Connected canWrite }, [ announce Polite "Connected." ]
        | ConnectionInput.ConnectRefused _, _
        | ConnectionInput.AuthFailed, _ -> { model with Connection = CredentialRefused; Context = None }, [ announce Assertive "GitHub refused the access token." ]
        | ConnectionInput.ReconnectSubmitted credential, Some connector ->
            match model.Context with
            | Some ctx ->
                let ctx = { ctx with Credential = credential }
                { model with Connection = Reauthenticating; Context = Some ctx; NextRequestId = nextId model.NextRequestId },
                [ Http(connector.ConnectRequest model.NextRequestId ctx) ]
            | None -> refuse (Rejected "Connect first.") model
        | ConnectionInput.ReconnectSubmitted _, None -> refuse (NotWired "Reconnecting") model
        | ConnectionInput.DisconnectConfirmed, _ ->
            { model with Connection = Disconnected; Context = None; Listings = Map.empty; ListingsInFlight = Map.empty },
            [ announce Polite "Disconnected." ]

    /// Lists a workspace (v4.1.5 L1). Nothing is written (EXT-DATA-006).
    let private list (backend: Backend) (target: ListingTarget) (model: Model) =
        match listerOf backend, requestContext backend model None with
        | None, _ -> refuse (NotWired "Listing investigations") model
        | Some _, None -> refuse (Rejected "Connect a data repository first.") model
        | Some lister, Some ctx ->
            let step, request = lister.List ctx target
            { model with ListingsInFlight = model.ListingsInFlight |> Map.add request.Id (target, step); NextRequestId = nextId model.NextRequestId },
            [ Http request ]

    /// Connect accepted (EXT-DATA-005): connected with the write scope GitHub states, then the active catalog is listed
    /// (EXT-DATA-003: the connected repository's catalog, EXT-DATA-013).
    let private connected (backend: Backend) (canWrite: bool) (model: Model) =
        let model, fx = list backend ActiveCatalog { model with Connection = Connected canWrite }
        let model, fx2 =
            match backend.WorkingCopy with
            | Some wc -> list backend (WorkingCopyOf wc) model
            | None -> model, []
        model, announce Polite (if canWrite then "Connected. Changes can be saved." else "Connected, read-only.") :: fx @ fx2

    /// Opens (SM-LOAD) an investigation that is neither loaded nor being edited here.
    let openInvestigation (backend: Backend) (ref: InvestigationRef) (model: Model) : Model * HostEffect list =
        match backend.Persistence, requestContext backend model (Some ref) with
        | Some p, Some ctx when not (model.Sessions.ContainsKey ref) ->
            let current = model.Loads |> Map.tryFind ref |> Option.defaultValue NotLoaded
            match current with
            | Loaded _
            | LoadState.Loading _ -> model, []
            | _ ->
                match (machineFor p model).StepLoad ctx current (LoadInput.OpenRequested ref) with
                | Ok(l, effects) ->
                    let fx, sent = hostEffects effects
                    { model with Loads = model.Loads |> Map.add ref l; NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) }, fx
                | Error _ -> model, []
        | _ -> model, []

    /// An answer from the host goes to whatever awaits its request id; an answer nothing awaits is superseded.
    let private httpCompleted (backend: Backend) (outcome: HttpOutcome) (model: Model) =
        let rid =
            match outcome with
            | Answered(id, _, _, _) -> id
            | NotAnswered(id, _) -> id
        match connectorOf backend with
        | None -> model, []
        | Some connector ->
            let claimed =
                model.Sessions |> Map.exists (fun _ s -> s.Outstanding |> Option.exists (fun (id, _) -> id = rid))
                || model.ListingsInFlight.ContainsKey rid
                || model.Loads |> Map.exists (fun _ l -> match l with LoadState.Loading(id, _) -> id = rid | _ -> false)
            let awaitsConnect =
                match model.Connection with
                | Connecting id -> id = rid
                | Reauthenticating -> not claimed
                | _ -> false
            match awaitsConnect with
            | true ->
                match connector.InterpretConnect rid outcome with
                | ConnectAccepted canWrite -> connected backend canWrite model
                // A rate limit or an unreachable GitHub is not a refused credential: the tab stays disconnected and says why.
                | ConnectRejected(RateLimited _ as e)
                | ConnectRejected(NetworkUnavailable _ as e) -> { model with Connection = Disconnected; Context = None }, [ announce Assertive (ReadFailure.text e) ]
                | ConnectRejected e -> { model with Connection = CredentialRefused; Context = None }, [ announce Assertive (ReadFailure.text e) ]
                | ConnectSuperseded -> model, []
            | false ->
                match requestContext backend model None with
                | None -> model, []
                | Some ctx ->
                    match model.Sessions |> Map.tryFindKey (fun _ s -> s.Outstanding |> Option.exists (fun (id, _) -> id = rid)) with
                    | Some ref when backend.Persistence.IsSome ->
                        let p = backend.Persistence.Value
                        let session = model.Sessions[ref]
                        match (machineFor p model).Interpret ctx session outcome with
                        | NextRequest(s, request) ->
                            { model with Sessions = model.Sessions |> Map.add ref s; NextRequestId = nextId model.NextRequestId }, [ Http request ]
                        | MachineEvent input -> sessionInput backend ref input model
                    | _ ->
                        match model.ListingsInFlight |> Map.tryFind rid with
                        | Some(target, step) ->
                            let inFlight = model.ListingsInFlight |> Map.remove rid
                            match (listerOf backend |> Option.get).InterpretList ctx target step outcome with
                            | ListingComplete listing ->
                                let model = { model with ListingsInFlight = inFlight; Listings = model.Listings |> Map.add listing.Address listing }
                                // Every listed investigation is opened, so its card shows its title (J1-05), lifecycle and counts
                                // (UI-001, UI-022). A catalog lists a handful; each open is one load (head, then its files).
                                listing.Investigations
                                |> List.fold (fun (m, acc) i -> let m2, fx = openInvestigation backend i.Ref m in m2, acc @ fx) (model, [])
                            | ListingNextRequest(next, request) ->
                                { model with
                                    ListingsInFlight = inFlight |> Map.add request.Id (target, next)
                                    NextRequestId = nextId model.NextRequestId },
                                [ Http request ]
                            | ListingFailed f -> { model with ListingsInFlight = inFlight }, [ announce Assertive ("The investigations could not be listed: " + ReadFailure.ofLoadFailure f) ]
                        | None ->
                            match model.Loads |> Map.tryFindKey (fun _ l -> match l with LoadState.Loading(id, _) -> id = rid | _ -> false) with
                            | Some ref when backend.Persistence.IsSome ->
                                let p = backend.Persistence.Value
                                match (machineFor p model).InterpretLoad ctx model.Loads[ref] outcome with
                                | LoadNextRequest(l, request) ->
                                    { model with Loads = model.Loads |> Map.add ref l; NextRequestId = nextId model.NextRequestId }, [ Http request ]
                                | LoadEvent input ->
                                    match (machineFor p model).StepLoad ctx model.Loads[ref] input with
                                    | Ok(l, effects) ->
                                        let fx, sent = hostEffects effects
                                        { model with Loads = model.Loads |> Map.add ref l; NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) }, fx
                                    | Error fault -> refuse (Rejected fault.Reason) model
                            | _ -> model, []

    let private listRequested (backend: Backend) (target: ListingTarget) (model: Model) = list backend target model

    let private network (input: NetworkInput) (model: Model) =
        match input with
        | NetworkInput.OfflineObserved -> { model with Network = NetworkState.Offline }, [ announce Polite "You are offline." ]
        | NetworkInput.OnlineObserved -> { model with Network = NetworkState.Online }, [ announce Polite "You are back online." ]

    /// One message, one new model and the effects the host performs.
    let update (backend: Backend) (msg: Msg) (model: Model) : Model * HostEffect list =
        match msg with
        | Navigate route -> { model with Route = route }, []
        | Network input -> network input model
        | Browser(ConnectivityChanged online) ->
            network (if online then NetworkInput.OnlineObserved else NetworkInput.OfflineObserved) model
        | Browser(HttpCompleted outcome) -> httpCompleted backend outcome model
        | Browser(TimerFired(ReconcileTimer ref)) ->
            match model.Sessions |> Map.tryFind ref, backend.Persistence, requestContext backend model (Some ref) with
            | Some session, Some p, Some ctx ->
                match (machineFor p model).ReconcileDue ctx session with
                | Ok(s, effects) ->
                    let fx, sent = hostEffects effects
                    { model with Sessions = model.Sessions |> Map.add ref s; NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) }, fx
                | Error fault -> refuse (Rejected fault.Reason) model
            | _ -> model, []
        | Browser LeaveAttempted ->
            let unsaved = model.Sessions |> Map.exists (fun _ s -> not s.Unsaved.IsEmpty || not s.AfterInFlight.IsEmpty)
            model, [ SetLeaveGuard unsaved ]
        | Connection input -> connection backend input model
        | SessionInput(ref, input) -> sessionInput backend ref input model
        | ConflictStep _ -> refuse (NotWired "Resolving a conflict") model
        | LoadStep(ref, input) ->
            match backend.Persistence, requestContext backend model (Some ref) with
            | Some p, Some ctx ->
                let current = model.Loads |> Map.tryFind ref |> Option.defaultValue NotLoaded
                match (machineFor p model).StepLoad ctx current input with
                | Ok(l, effects) ->
                    let fx, sent = hostEffects effects
                    { model with Loads = model.Loads |> Map.add ref l; NextRequestId = (if sent then nextId model.NextRequestId else model.NextRequestId) }, fx
                | Error fault -> refuse (Rejected fault.Reason) model
            | None, _ -> refuse (NotWired "Opening a saved investigation") model
            | Some _, None -> refuse (Rejected "Connect a data repository first.") model
        | ListRequested target -> listRequested backend target model
        | CreateRequested(writable, envelope) -> create backend writable envelope model
        | DomainCommand envelope -> command backend envelope model

    /// The frozen Engine over a backend.
    let engine (backend: Backend) : Engine =
        { Init = fun route -> initialModel route, []
          Update = update backend }
