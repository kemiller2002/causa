// Owner: integration (T10). The hash-route grammar below the UI route type: a location hash as path segments and query
// parameters. Routes.fs compiles before Projections.fs (UiRoute, IF41-VIEW), so View.fs maps this shape to a UiRoute.
namespace Causa.App

open System

/// A location hash, decoded: `#/investigations/<id>?ws=a&role=b` is Segments ["investigations"; "<id>"] with Query.
type HashPath =
    { Raw: string
      Segments: string list
      Query: Map<string, string> }

[<RequireQualifiedAccess>]
module HashPath =
    let private decode (s: string) =
        try Uri.UnescapeDataString(s.Replace('+', ' ')) with :? UriFormatException -> s

    let private query (q: string) =
        q.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun pair ->
            match pair.Split('=', 2) with
            | [| k; v |] -> Some(decode k, decode v)
            | [| k |] -> Some(decode k, "")
            | _ -> None)
        |> Map.ofArray

    /// Total: any string is a HashPath; an empty or missing hash is the root `/`.
    let parse (hash: string | null) : HashPath =
        let raw = (match hash with | null -> "" | h -> h).TrimStart('#')
        let raw = if raw = "" then "/" else raw
        let path, q =
            match raw.IndexOf '?' with
            | -1 -> raw, ""
            | i -> raw.Substring(0, i), raw.Substring(i + 1)
        { Raw = raw
          Segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.map decode |> List.ofArray
          Query = query q }
