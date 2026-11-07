// Owner: integration (T10, T17). The engine entry the browser host calls (src/Causa.Host/Host.cs, IF41-APP): one Limen
// message in, one Limen answer out, both as JSON. T17 wires the real path: a Limen event becomes a UiIntent
// (Ui/Events.fs), the adapter (Ui/UiAdapter.fs) turns it into an engine message, the engine (Update.fs) decides it
// through the domain kernel, and the adapter projects the AppView that View.bindings renders. Persistence is the
// Backend's optional adapter: None until the persistence lane's slices are merged (T19/T20), so today every accepted
// command stays in the tab, unsaved, and the screens say so.
module Causa.App.Program

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain
open Causa.Ui.Contracts
open Causa.App.Ui

/// The Limen browser-to-engine messages the engine distinguishes (Limen 0.9.1 protocol).
type Inbound =
    | Initialize of origin: string * path: string * hash: string
    | LocationChanged of hash: string
    | Event of name: string * key: string option * value: string option
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

let parse (message: string) : Inbound =
    try
        use document = JsonDocument.Parse message
        let root = document.RootElement
        let hash () =
            root
            |> tryProperty "location"
            |> Option.bind (tryProperty "hash")
            |> Option.bind tryString
            |> Option.defaultValue ""
        let locationField name =
            root |> tryProperty "location" |> Option.bind (tryProperty name) |> Option.bind tryString |> Option.defaultValue ""
        match root |> tryProperty "kind" |> Option.bind tryString with
        | Some "Initialize" -> Initialize(locationField "origin", locationField "path", hash ())
        | Some "LocationChanged" -> LocationChanged(hash ())
        | Some "Event" ->
            let event = root |> tryProperty "event"
            let field name = event |> Option.bind (tryProperty name) |> Option.bind tryString
            match field "name" with
            | Some name -> Event(name, field "key", field "value")
            | None -> Malformed
        | Some kind -> Other kind
        | None -> Malformed
    with :? JsonException -> Malformed

/// The engine's backend: the one domain kernel, the host clock, and no persistence adapter yet.
let backend: Backend =
    { Kernel = Kernel.kernel
      Policies = Policies.coreDefault
      Actor = { ActorId = "causa-user" }
      Author = Actor.Human(NonEmptyText.create "causa-user" |> Option.get)
      Now = fun () -> DateTimeOffset.UtcNow
      StorageVersion = "1.1.0"
      Persistence = None }

let config: AdapterConfig =
    { NewIdentifier = Guid.NewGuid
      Now = fun () -> DateTimeOffset.UtcNow
      Run = "session"
      SessionLocal = backend.Persistence.IsNone
      ValidateStart = Causa.App.Views.StartScreen.validate }

let ui = UiAdapter.backend config (Update.engine backend)

/// The tab's state (the browser host is single-threaded; this is the only mutable value).
let mutable private state: AdapterState option = None

/// Pure step: the next adapter state for a message.
let step (current: AdapterState option) (inbound: Inbound) : AdapterState =
    let ensure route =
        match current with
        | Some s -> s
        | None -> ui.Init route |> fst
    match inbound with
    | Initialize(_, _, hash) ->
        let route = View.route hash
        match current with
        | None -> ui.Init route |> fst
        | Some s -> ui.Dispatch(UiIntent.Navigate route) s |> fst
    | LocationChanged hash ->
        let route = View.route hash
        ui.Dispatch(UiIntent.Navigate route) (ensure route) |> fst
    | Event(name, key, value) ->
        let s = ensure InvestigationsRoute
        match Events.toIntent name key value with
        | Some intent -> ui.Dispatch intent s |> fst
        | None -> s
    | Other _
    | Malformed -> ensure InvestigationsRoute

/// Where the page is served (origin and path, from Initialize): a navigation the engine asks for stays on it.
let mutable private page = ""

/// A Limen navigation request when the engine moved the route itself (an accepted Start opens its workspace). Never in
/// answer to LocationChanged: the browser has already moved (Limen routing rule).
let navigation (before: UiRoute option) (after: UiRoute) (inbound: Inbound) : string option =
    match inbound, before with
    | Event _, Some previous when previous <> after && page <> "" -> Some(page + Navigation.format after)
    | _ -> None

let mutable private correlation = 0L

let private render (view: AppView) (push: string option) : string =
    use stream = new MemoryStream()
    (
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        View.writeBindings writer (View.bindings view)
        writer.WriteStartArray "effects"
        match push with
        | Some url ->
            correlation <- correlation + 1L
            writer.WriteStartObject()
            writer.WriteString("kind", "Navigation")
            writer.WriteString("operation", "push")
            writer.WriteString("correlationId", sprintf "nav-%d" correlation)
            writer.WriteString("url", url)
            writer.WriteEndObject()
        | None -> ()
        writer.WriteEndArray()
        writer.WriteStartArray "cancellations"
        writer.WriteEndArray()
        writer.WriteEndObject()
    )
    Encoding.UTF8.GetString(stream.ToArray())

/// One Limen message (JSON) in, one Limen answer (JSON) out. Host effects other than announcements (HTTP, timers,
/// the leave guard, downloads) arrive with the persistence adapter; the adapter keeps announcements in the view.
let dispatch (message: string) : string =
    let inbound = parse message
    match inbound with
    | Initialize(origin, path, _) -> page <- origin + path
    | _ -> ()
    let before = state |> Option.map (fun s -> s.Local.Route)
    let next = step state inbound
    state <- Some next
    render (ui.Project next) (navigation before next.Local.Route inbound)
