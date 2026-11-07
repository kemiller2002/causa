// Owner: integration (T10). The engine entry the browser host calls (src/Causa.Host/Host.cs, IF41-APP): one Limen
// message in, one Limen answer out, both as JSON. T10 boots the engine and answers with its first view; the slices
// that wire Engine/Update/View replace `view` with the real model loop.
module Causa.App.Program

open System.IO
open System.Text
open System.Text.Json

/// The kinds of Limen browser-to-engine message (Limen 0.9.1 protocol) the boot engine distinguishes.
type Inbound =
    | Initialize of path: string
    | Other of kind: string
    | Malformed

/// A Limen ViewPrimitive.
type ViewPrimitive =
    | Text of string
    | Flag of bool

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
        match root |> tryProperty "kind" |> Option.bind tryString with
        | Some "Initialize" ->
            root
            |> tryProperty "location"
            |> Option.bind (tryProperty "path")
            |> Option.bind tryString
            |> Option.defaultValue "/"
            |> Initialize
        | Some kind -> Other kind
        | None -> Malformed
    with :? JsonException -> Malformed

/// The engine's view for a message: a flat Limen ViewState.
let view (inbound: Inbound) : (string * ViewPrimitive) list =
    match inbound with
    | Initialize path -> [ "engine.ready", Flag true; "route", Text path ]
    | Other _ -> [ "engine.ready", Flag true ]
    | Malformed -> [ "engine.ready", Flag true; "engine.fault", Flag true ]

let private render (entries: (string * ViewPrimitive) list) : string =
    use stream = new MemoryStream()
    (
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteStartObject "view"
        for key, value in entries do
            match value with
            | Text s -> writer.WriteString(key, s)
            | Flag b -> writer.WriteBoolean(key, b)
        writer.WriteEndObject()
        writer.WriteStartArray "effects"
        writer.WriteEndArray()
        writer.WriteStartArray "cancellations"
        writer.WriteEndArray()
        writer.WriteEndObject()
    )
    Encoding.UTF8.GetString(stream.ToArray())

/// One Limen message (JSON) in, one Limen answer (JSON) out.
let dispatch (message: string) : string = message |> parse |> view |> render
