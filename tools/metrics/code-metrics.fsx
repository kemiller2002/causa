// Owner: integration. Code-quality metrics for causa (docs/quality/CODE-METRICS-2026-11-08.md). F# only; the measures
// and the output are in MetricsCore.fs.
//
//   dotnet fsi tools/metrics/code-metrics.fsx -- <repo-root>      -> JSON on stdout
//
// F# complexity is an approximation (decision points per let/member binding); see the report for its limits.
#load "../shared/PythonCompat.fs" "MetricsCore.fs"

open System
open System.IO
open System.IO.Enumeration
open Causa.Tools.Metrics
open Causa.Tools.PythonCompat

// ---- Edges: walking and reading ----------------------------------------------------------------------------------

/// The repository root as Python's pathlib spells it: no empty or "." segments, no trailing slash, "." when empty.
let normalise (root: string) : string =
    let parts = root.Split('/') |> Array.filter (fun p -> p <> "" && p <> ".") |> String.concat "/"
    match root.StartsWith '/', parts with
    | true, p -> "/" + p
    | false, "" -> "."
    | false, p -> p

let join (path: string) (name: string) =
    match path with
    | "." -> name
    | p when p.EndsWith '/' -> p + name
    | p -> p + "/" + name

/// A directory's entries in directory order (the order os.scandir gives): name, and whether it is a directory that
/// is not a symbolic link. An unreadable or missing directory has none.
let entries (dir: string) : (string * bool) list =
    try
        FileSystemEnumerable<string * bool>(
            dir,
            FileSystemEnumerable.FindTransform(fun e ->
                e.FileName.ToString(), e.IsDirectory && not (e.Attributes.HasFlag FileAttributes.ReparsePoint)),
            EnumerationOptions(AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false, ReturnSpecialDirectories = false)
        )
        |> List.ofSeq
    with _ -> []

/// Python 3.13's Path.rglob(pattern) for a "*<suffix>" pattern, in its exact order: the matches directly in `start`,
/// then a depth-first walk with an explicit stack where each directory's subdirectories are visited in directory
/// order (each one's matches yielded as it is seen) and the last one pushed is walked first. Symbolic links to
/// directories are not followed.
let rglob (start: string) (suffix: string) : string list =
    let matches dir =
        entries dir |> List.filter (fun (name, _) -> name.EndsWith(suffix, StringComparison.Ordinal)) |> List.map (fst >> join dir)
    let rec walk (stack: string list) (acc: string list list) =
        match stack with
        | [] -> acc |> List.rev |> List.concat
        | dir :: rest ->
            let subdirectories = entries dir |> List.filter snd |> List.map (fst >> join dir)
            walk (List.rev subdirectories @ rest) (List.rev (subdirectories |> List.map matches) @ acc)
    walk [ start ] [ matches start ]

let relative (root: string) (path: string) =
    match root with
    | "." -> path
    | "/" -> path.Substring 1
    | r -> path.Substring(r.Length + 1)

/// Python's files(globs, under): every glob in order, skipped paths left out, then each file read as Python reads it.
let files (root: string) (globs: string list, under: string) : SourceFile list =
    globs
    |> List.collect (fun g -> rglob (join root under) (g.TrimStart '*'))
    |> List.filter (Selection.skipped >> not)
    |> List.map (fun p -> { Path = relative root p; Text = Text.decode (File.ReadAllBytes p) })

let main (argv: string list) : int =
    match argv with
    | root :: _ ->
        try
            let r = normalise root
            let sources =
                { FsSource = files r Selection.fsSource
                  FsTests = files r Selection.fsTests
                  JsBrowserTests = files r Selection.jsBrowserTests
                  JsWeb = Selection.jsWeb |> List.collect (files r) }
            stdout.Write(Report.toText sources)
            0
        with ex ->
            eprintfn "code-metrics: %s" ex.Message
            1
    | [] ->
        eprintfn "usage: dotnet fsi tools/metrics/code-metrics.fsx -- <repo-root>"
        2

exit (main (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray))
