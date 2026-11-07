// Owner: integration (T10, T17, T20). The engine entry the browser host calls (src/Causa.Host/Host.cs, IF41-APP): one
// Limen message in, one Limen answer out, both as JSON. A Limen event becomes a UiIntent (Ui/Events.fs), the adapter
// (Ui/UiAdapter.fs) turns it into an engine message, the engine (Update.fs) decides it through the domain kernel and
// asks for host effects; this file turns those into Limen effects (HTTP to api.github.com, navigation) and Limen's
// results back into engine messages. The provider's read side (Connect, the investigation listing) is wired (T20);
// saving arrives with the persistence adapter (T19), until then accepted commands stay in the tab, unsaved.
// At load the app reads `deploy-connection.json` from its own origin (no credential sent) and, when the deployed test
// build injected a connection, connects with it (EXT-DATA-004-O5, ODR-COMP-P13 A).
module Causa.App.Program

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain
open Causa.Persistence
open Causa.Ui.Contracts
open Causa.App.Ui

/// The outcome of one Limen HTTP effect.
type HttpResult =
    | HttpSuccess of status: int * headers: (string * string) list * body: string
    | HttpFailure of reason: string
    | HttpCancelled
    | HttpUnknown of reason: string

/// The Limen browser-to-engine messages the engine distinguishes (Limen 0.9.1 protocol).
type Inbound =
    | Initialize of origin: string * path: string * hash: string
    | LocationChanged of hash: string
    | Event of name: string * key: string option * value: string option
    | HttpCompletedFor of correlationId: string * HttpResult
    | Other of kind: string
    | Malformed

let private tryProperty (name: string) (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None
    | _ -> None

let private tryString (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.String -> element.GetString() |> Option.ofObj
    | _ -> None

let private httpResult (outcome: JsonElement) : HttpResult =
    let str name = outcome |> tryProperty name |> Option.bind tryString
    match str "kind" with
    | Some "Success" ->
        let status = outcome |> tryProperty "status" |> Option.filter (fun s -> s.ValueKind = JsonValueKind.Number) |> Option.map (fun s -> s.GetInt32()) |> Option.defaultValue 0
        let headers =
            match outcome |> tryProperty "headers" with
            | Some h when h.ValueKind = JsonValueKind.Object ->
                [ for p in h.EnumerateObject() do
                      match tryString p.Value with
                      | Some v -> yield p.Name, v
                      | None -> () ]
            | _ -> []
        let body =
            match outcome |> tryProperty "body" with
            | Some b when b.ValueKind = JsonValueKind.String -> b.GetString() |> Option.ofObj |> Option.defaultValue ""
            | Some b when b.ValueKind = JsonValueKind.Null || b.ValueKind = JsonValueKind.Undefined -> ""
            | Some b -> b.GetRawText()
            | None -> ""
        HttpSuccess(status, headers, body)
    | Some "Failure" -> HttpFailure(str "reason" |> Option.defaultValue "network")
    | Some "Cancelled" -> HttpCancelled
    | _ -> HttpUnknown(str "reason" |> Option.defaultValue "unknown")

let parse (message: string) : Inbound =
    try
        use document = JsonDocument.Parse message
        let root = document.RootElement
        let locationField name =
            root |> tryProperty "location" |> Option.bind (tryProperty name) |> Option.bind tryString |> Option.defaultValue ""
        match root |> tryProperty "kind" |> Option.bind tryString with
        | Some "Initialize" -> Initialize(locationField "origin", locationField "path", locationField "hash")
        | Some "LocationChanged" -> LocationChanged(locationField "hash")
        | Some "Event" ->
            let event = root |> tryProperty "event"
            let field name = event |> Option.bind (tryProperty name) |> Option.bind tryString
            match field "name" with
            | Some name -> Event(name, field "key", field "value")
            | None -> Malformed
        | Some "EffectResult" ->
            let result = root |> tryProperty "result"
            match result |> Option.bind (tryProperty "kind") |> Option.bind tryString,
                  result |> Option.bind (tryProperty "correlationId") |> Option.bind tryString,
                  result |> Option.bind (tryProperty "outcome") with
            | Some "HttpResult", Some correlation, Some outcome -> HttpCompletedFor(correlation, httpResult outcome)
            | Some kind, _, _ -> Other kind
            | None, _, _ -> Malformed
        | Some kind -> Other kind
        | None -> Malformed
    with :? JsonException -> Malformed

/// The engine's backend: the one domain kernel, the host clock, the GitHub provider's read side (T18 requests and
/// classifier, T20 lister), and no persistence adapter until T19 is merged.
/// The working-copy run this deployment creates investigations in (CONTRACT section 9.4: workspaces/demo/causa-app-<run>).
let run = "preview"

let backend: Backend =
    { Kernel = Kernel.kernel
      Policies = Policies.coreDefault
      Actor = { ActorId = "causa-user" }
      Author = Actor.Human(NonEmptyText.create "causa-user" |> Option.get)
      Now = fun () -> DateTimeOffset.UtcNow
      StorageVersion = "1.1.0"
      Persistence =
        Some
            { Connector = GitHubConnect.connector
              Machine = SaveMachines.withStorage (Actor.Human(NonEmptyText.create "causa-user" |> Option.get))
              Lister = GitHubLister.lister
              Codec = StorageCodecs.codec }
      Provider = Some { Connector = GitHubConnect.connector; Lister = GitHubLister.lister }
      WorkingCopy = WorkspaceAddress.workingCopy [] run }

let config: AdapterConfig =
    { NewIdentifier = Guid.NewGuid
      Now = fun () -> DateTimeOffset.UtcNow
      Run = run
      SessionLocal = backend.Persistence.IsNone && backend.Provider.IsNone
      ValidateStart = Causa.App.Views.StartScreen.validate
      InvestigationOf = Update.investigationOf backend
      CanSave = backend.Persistence.IsSome
      WorkingCopy = backend.WorkingCopy }

let engine = Update.engine backend
let ui = UiAdapter.backend config engine

/// What a Limen effect's correlation id stands for.
type Pending =
    | EngineRequest of RequestId
    | DeployConnectionRead

/// The headers the classifier reads (IF41-ERRORS: rate limits, retry).
let private responseHeaders = [ "retry-after"; "x-ratelimit-remaining"; "x-ratelimit-reset" ]

/// An engine answer for a Limen HTTP outcome. Limen's "network" failure means nothing can have changed (the method
/// was safe or the browser was offline); any other failure may have reached the server.
let outcomeOf (rid: RequestId) (result: HttpResult) : HttpOutcome =
    match result with
    | HttpSuccess(status, headers, body) -> Answered(rid, status, headers, body)
    | HttpFailure "network" -> NotAnswered(rid, FailedBeforeSending)
    | HttpFailure _
    | HttpCancelled -> NotAnswered(rid, LostAfterSending)
    | HttpUnknown "timeout-after-dispatch" -> NotAnswered(rid, TimedOut)
    | HttpUnknown _ -> NotAnswered(rid, LostAfterSending)

/// The tab's state (the browser host is single-threaded; these are its only mutable values).
let mutable private state: AdapterState option = None
let mutable private page = ""
let mutable private pending: Map<string, Pending> = Map.empty
let mutable private correlation = 0L
/// Reconcile timers the engine asked for (HostEffect.Timer), due at a host time; fired by the boundary's tick.
let mutable private timers: (DateTimeOffset * TimerKey) list = []
/// The injected deploy connection, kept in memory only (never stored, logged or rendered) so Try again can reuse it.
let mutable private deployConnection: DeployConnection option = None

let private nextCorrelation (prefix: string) =
    correlation <- correlation + 1L
    sprintf "%s-%d" prefix correlation

/// Pure step: the next adapter state and the host effects it asked for.
let step (current: AdapterState option) (inbound: Inbound) (pendingNow: Map<string, Pending>) : AdapterState * HostEffect list * bool =
    let ensure route =
        match current with
        | Some s -> s
        | None -> ui.Init route |> fst
    match inbound with
    | Initialize(_, _, hash) ->
        let route = View.route hash
        match current with
        | None -> ui.Init route |> fst, [], true
        | Some s ->
            let s2, fx = ui.Dispatch(UiIntent.Navigate route) s
            s2, fx, false
    | LocationChanged hash ->
        let route = View.route hash
        let s2, fx = ui.Dispatch(UiIntent.Navigate route) (ensure route)
        s2, fx, false
    // Browser mechanism relayed by web/boundary.js (no application state there): connectivity and the timer tick.
    | Event("UIA-CONNECTIVITY", _, value) ->
        let s = ensure InvestigationsRoute
        let s2, fx = UiAdapter.browser engine (ConnectivityChanged(value <> Some "offline")) s
        s2, fx, false
    | Event("UIA-TICK", _, _) ->
        let s = ensure InvestigationsRoute
        let now = DateTimeOffset.UtcNow
        let due, later = timers |> List.partition (fun (at, _) -> at <= now)
        timers <- later
        let s2, fx = due |> List.fold (fun (st, acc) (_, key) -> let st2, more = UiAdapter.browser engine (TimerFired key) st in st2, acc @ more) (s, [])
        s2, fx, false
    | Event("UIA-DEPLOY-RETRY", _, _) ->
        let s = ensure InvestigationsRoute
        match deployConnection with
        | Some c ->
            let s2, fx = UiAdapter.deployConnect engine c.Repository c.Credential s
            s2, fx, false
        | None -> s, [], false
    | Event(name, key, value) ->
        let s = ensure InvestigationsRoute
        match Events.toIntent name key value with
        | Some intent ->
            let s2, fx = ui.Dispatch intent s
            s2, fx, false
        | None -> s, [], false
    | HttpCompletedFor(id, result) ->
        let s = ensure InvestigationsRoute
        match pendingNow.TryFind id, result with
        | Some(EngineRequest rid), _ ->
            let s2, fx = UiAdapter.browser engine (HttpCompleted(outcomeOf rid result)) s
            s2, fx, false
        | Some DeployConnectionRead, HttpSuccess(200, _, body) ->
            match DeployConnection.decode body with
            | Ok(Some c) ->
                deployConnection <- Some c
                let s2, fx = UiAdapter.deployConnect engine c.Repository c.Credential s
                s2, fx, false
            // No connection in the file, or a file that does not decode: the Connect form, exactly as before.
            | Ok None
            | Error _ -> s, [], false
        | _ -> s, [], false
    | Other _
    | Malformed -> ensure InvestigationsRoute, [], false

/// A Limen navigation request when the engine moved the route itself (an accepted Start opens its workspace). Never in
/// answer to LocationChanged: the browser has already moved (Limen routing rule).
let navigation (before: UiRoute option) (after: UiRoute) (inbound: Inbound) : string option =
    match inbound, before with
    | Event _, Some previous when previous <> after && page <> "" -> Some(page + Navigation.format after)
    | _ -> None

let private methodName (m: HttpMethod) =
    match m with
    | Get -> "GET"
    | Post -> "POST"
    | Put -> "PUT"
    | Patch -> "PATCH"

let private writeHttp (writer: Utf8JsonWriter) (id: string) (request: HttpRequest) =
    writer.WriteStartObject()
    writer.WriteString("kind", "Http")
    writer.WriteString("correlationId", id)
    writer.WriteString("method", methodName request.Method)
    writer.WriteString("url", request.Url)
    writer.WriteStartObject "headers"
    for k, v in request.Headers do
        writer.WriteString(k, v)
    writer.WriteEndObject()
    request.Body |> Option.iter (fun b -> writer.WriteString("body", b))
    writer.WriteNumber("timeoutMs", 30000)
    writer.WriteString("response", "text")
    writer.WriteStartArray "responseHeaders"
    for h in responseHeaders do
        writer.WriteStringValue h
    writer.WriteEndArray()
    writer.WriteString("credentials", "omit")
    writer.WriteEndObject()

/// The same-origin read of the deploy connection: a GET without credentials (TEST-DEPLOY-KEY section 2.4).
let private writeDeployRead (writer: Utf8JsonWriter) (id: string) (url: string) =
    writer.WriteStartObject()
    writer.WriteString("kind", "Http")
    writer.WriteString("correlationId", id)
    writer.WriteString("method", "GET")
    writer.WriteString("url", url)
    writer.WriteNumber("timeoutMs", 15000)
    writer.WriteString("response", "text")
    writer.WriteString("credentials", "omit")
    writer.WriteEndObject()

/// The URL of a file next to the page (a GitHub Pages project site lives under /<repository>/).
let private beside (file: string) =
    let slash = page.LastIndexOf '/'
    (if slash >= 0 then page.Substring(0, slash + 1) else page + "/") + file

let private render (view: AppView) (extra: Bindings) (effects: HostEffect list) (deployRead: bool) (push: string option) : string =
    use stream = new MemoryStream()
    (
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        View.writeBindings writer (Causa.App.Views.Bind.merge [ View.bindings view; extra ])
        writer.WriteStartArray "effects"
        for e in effects do
            match e with
            | Http request ->
                let id = nextCorrelation "http"
                pending <- pending |> Map.add id (EngineRequest request.Id)
                writeHttp writer id request
            | Timer(key, ms) -> timers <- timers @ [ DateTimeOffset.UtcNow.AddMilliseconds(float ms), key ]
            // The leave guard is the view's shell.unsaved, which web/boundary.js reads on beforeunload; downloads are not
            // offered in this build; announcements are in the view.
            | SetLeaveGuard _
            | Download _
            | LiveAnnounce _ -> ()
        if deployRead && page <> "" then
            let id = nextCorrelation "deploy"
            pending <- pending |> Map.add id DeployConnectionRead
            writeDeployRead writer id (beside DeployConnection.FileName)
        match push with
        | Some url ->
            writer.WriteStartObject()
            writer.WriteString("kind", "Navigation")
            writer.WriteString("operation", "push")
            writer.WriteString("correlationId", nextCorrelation "nav")
            writer.WriteString("url", url)
            writer.WriteEndObject()
        | None -> ()
        writer.WriteEndArray()
        writer.WriteStartArray "cancellations"
        writer.WriteEndArray()
        writer.WriteEndObject()
    )
    Encoding.UTF8.GetString(stream.ToArray())

/// One Limen message (JSON) in, one Limen answer (JSON) out.
let dispatch (message: string) : string =
    let inbound = parse message
    match inbound with
    | Initialize(origin, path, _) -> page <- origin + path
    | _ -> ()
    let before = state |> Option.map (fun s -> s.Local.Route)
    let next, effects, firstLoad = step state inbound pending
    match inbound with
    | HttpCompletedFor(id, _) -> pending <- pending |> Map.remove id
    | _ -> ()
    state <- Some next
    render (ui.Project next) (UiAdapter.shellBindings next) effects firstLoad (navigation before next.Local.Route inbound)

/// A fresh tab: what a page reload gives the host (used by the host tests; the browser reloads the page instead).
let reset () =
    state <- None
    timers <- []
    deployConnection <- None
    pending <- Map.empty
    page <- ""
