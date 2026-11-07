// Owner: integration (T10; change request CR-2 from T16). The engine's view: an AppView becomes the Limen view state,
// composed from the shell's bindings and the current screen's bindings (Views/*, owned by the UI roles).
module Causa.App.View

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain
open Causa.Persistence
open Causa.Ui.Contracts
open Causa.App.Views

/// The UiRoute a location hash names (SCREEN-CONTRACTS URL contract): `#/`, `#/administration`, `#/settings`,
/// `#/investigations/new`, `#/investigations/{guid}?ws=&role=&tab=`; anything else is NotFoundRoute.
let route (hash: string) : UiRoute =
    let path = HashPath.parse hash
    let role (s: string) =
        match s with
        | "catalog" -> Some WorkspaceRole.Catalog
        | "working-copy" -> Some WorkspaceRole.WorkingCopy
        | _ -> None
    let section (tab: string option) =
        match tab with
        | None
        | Some "overview" -> Some Overview
        | Some "causal-analysis" -> Some CausalAnalysis
        | Some "evidence" -> Some EvidenceSection
        | Some "hypotheses" -> Some HypothesesSection
        | Some "timeline" -> Some TimelineSection
        | Some "corrective-actions" -> Some CorrectiveActions
        | Some _ -> None
    match path.Segments with
    | [] -> InvestigationsRoute
    | [ "administration" ] -> AdministrationRoute
    | [ "settings" ] -> SettingsRoute
    | [ "investigations"; "new" ] -> StartRoute
    | [ "investigations"; id ] ->
        let target =
            match Guid.TryParse id, path.Query.TryFind "ws", path.Query.TryFind "role" |> Option.bind role with
            | (true, g), Some ws, Some r when ws <> "" -> Some { Workspace = ws; Role = r; Id = InvestigationId g }
            | _ -> None
        match target, section (path.Query.TryFind "tab") with
        | Some t, Some s -> WorkspaceRoute(t, s)
        | _ -> NotFoundRoute("/" + path.Raw.TrimStart('/'))
    | _ -> NotFoundRoute("/" + path.Raw.TrimStart('/'))

/// The current screen's own bindings.
let screenBindings (screen: ScreenView) : Bindings =
    match screen with
    | ListScreen v -> InvestigationListScreen.bindings v
    | AdministrationScreen v -> AdministrationScreen.bindings v
    | SettingsScreen v -> SettingsScreen.bindings v
    | NotFoundScreen v -> States.notFound v
    | StartScreen v -> StartScreen.bindings v
    | WorkspaceScreen _ -> Map.empty

/// The whole Limen view state of an AppView.
let bindings (view: AppView) : Bindings = Bind.merge [ Shell.bindings view; screenBindings view.Screen ]

let private writePrimitive (writer: Utf8JsonWriter) (key: string) (value: BindingPrimitive) =
    match value with
    | BindingPrimitive.Text s -> writer.WriteString(key, s)
    | BindingPrimitive.Number n -> writer.WriteNumber(key, n)
    | BindingPrimitive.Flag b -> writer.WriteBoolean(key, b)

/// Writes a Bindings map as a Limen ViewState object: Text, Flag and Number become JSON primitives, Items an array of
/// flat objects.
let writeBindings (writer: Utf8JsonWriter) (b: Bindings) =
    writer.WriteStartObject()
    for KeyValue(key, value) in b do
        match value with
        | BindingValue.Text s -> writer.WriteString(key, s)
        | BindingValue.Number n -> writer.WriteNumber(key, n)
        | BindingValue.Flag f -> writer.WriteBoolean(key, f)
        | BindingValue.Items rows ->
            writer.WriteStartArray key
            for row in rows do
                writer.WriteStartObject()
                for KeyValue(k, v) in row do
                    writePrimitive writer k v
                writer.WriteEndObject()
            writer.WriteEndArray()
    writer.WriteEndObject()

/// A Bindings map as Limen ViewState JSON.
let toJson (b: Bindings) : string =
    use stream = new MemoryStream()
    (
        use writer = new Utf8JsonWriter(stream)
        writeBindings writer b
    )
    Encoding.UTF8.GetString(stream.ToArray())
