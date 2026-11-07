// Owner: integration (T10). The engine entry the browser host calls (src/Causa.Host/Host.cs, IF41-APP): one Limen
// message in, one Limen answer out, both as JSON. The engine keeps one value, the current route, and projects an
// AppView for it through View.bindings (CR-2). Until the slices that wire Engine/Update over the real persistence
// land, the AppView comes from the boot backend below, which says so: its Origin is a FixtureBackend label, never
// IntegratedBackend, and it shows no data it does not have (every system fact is FactUnknown).
module Causa.App.Program

open System.IO
open System.Text
open System.Text.Json
open Causa.Ui.Contracts

/// The Limen browser-to-engine messages the engine distinguishes (Limen 0.9.1 protocol).
type Inbound =
    | Initialize of hash: string
    | LocationChanged of hash: string
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
        match root |> tryProperty "kind" |> Option.bind tryString with
        | Some "Initialize" -> Initialize(hash ())
        | Some "LocationChanged" -> LocationChanged(hash ())
        | Some kind -> Other kind
        | None -> Malformed
    with :? JsonException -> Malformed

/// The boot backend: what the engine can show before persistence is wired, with nothing invented.
module Boot =
    let origin = FixtureBackend(FixtureLabel.create "integration boot backend (no persistence wired yet)")

    let connection: ConnectionView =
        { State = Disconnected
          Form = { Owner = ""; Repository = ""; Branch = ""; CredentialEntered = false }
          Errors = []
          Connect = Available
          Disconnect = NotOffered
          Connected = None
          WriteScope = WriteScopeUnknown }

    let list: InvestigationListView =
        { Connection = connection
          Investigations = NotRequested
          Sort = ByUpdated
          NewInvestigation = NotOffered
          State = ScreenReady }

    let private unknown label = { Label = label; Status = FactUnknown }

    let administration: AdministrationView =
        { Provider = unknown "Persistence provider"
          Repository = unknown "Data repository"
          Connection = unknown "Connection"
          Catalog = unknown "Investigation catalog"
          StorageContract = unknown "Storage contract"
          ApplicationVersion = unknown "Application version"
          DeployedCommit = unknown "Deployed commit"
          DataValidation = unknown "Data validation"
          Diagnostics = [] }

    let settings: SettingsView =
        { Preferences =
            [ { Id = "theme"
                Label = "Colour theme"
                Scope = SessionScope
                Options = [ "Light" ]
                Effective = "Light" } ]
          ConnectionPreference = unknown "Data repository connection" }

    let notFound (requested: string) : NotFoundView =
        { Requested = requested
          Copy = Causa.App.Views.States.notFoundCopy }

    /// Start and workspace routes need a connected repository; before Connect the list's connection region is shown.
    let screen (route: UiRoute) : ScreenView =
        match route with
        | InvestigationsRoute
        | StartRoute
        | WorkspaceRoute _ -> ListScreen list
        | AdministrationRoute -> AdministrationScreen administration
        | SettingsRoute -> SettingsScreen settings
        | NotFoundRoute requested -> NotFoundScreen(notFound requested)

    let view (route: UiRoute) : AppView =
        { Route = route
          Primary = Causa.App.Views.Shell.primaryLinks route
          Screen = screen route
          Announcements = []
          Origin = origin }

/// The engine's state: the current route.
let mutable private current: UiRoute = InvestigationsRoute

/// Pure step: the next route for a message.
let step (state: UiRoute) (inbound: Inbound) : UiRoute =
    match inbound with
    | Initialize hash
    | LocationChanged hash -> View.route hash
    | Other _
    | Malformed -> state

let private render (view: AppView) : string =
    use stream = new MemoryStream()
    (
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        View.writeBindings writer (View.bindings view)
        writer.WriteStartArray "effects"
        writer.WriteEndArray()
        writer.WriteStartArray "cancellations"
        writer.WriteEndArray()
        writer.WriteEndObject()
    )
    Encoding.UTF8.GetString(stream.ToArray())

/// One Limen message (JSON) in, one Limen answer (JSON) out.
let dispatch (message: string) : string =
    current <- step current (parse message)
    current |> Boot.view |> render
