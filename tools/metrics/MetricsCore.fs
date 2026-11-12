// Owner: integration. The pure core of the code-quality metrics (tools/metrics/code-metrics.fsx,
// docs/quality/CODE-METRICS-2026-11-08.md). Shared by the script (#load, after tools/shared/PythonCompat.fs) and
// Causa.App.Tests (CodeMetricsTests). Everything here is a pure function over file paths and texts; the script lists
// and reads the files.
//
// Measures: source lines (code and comment lines) of the F# application and tests, the browser specs and the web
// scripts; the test-to-code ratio; F# function length and an approximate cyclomatic complexity (decision points per
// let/member binding); functional-style counts; and the largest files. The output is the JSON the original
// docs/quality/code_metrics.py printed, byte for byte.
module Causa.Tools.Metrics

open System
open System.Text.RegularExpressions
open Causa.Tools.PythonCompat

// ---- Model -------------------------------------------------------------------------------------------------------

/// A measured file: its path relative to the repository root ('/'-separated) and its text as Python read it
/// (PythonCompat.Text.decode).
type SourceFile = { Path: string; Text: string }

/// The four file sets the metrics measure.
type Sources =
    { FsSource: SourceFile list
      FsTests: SourceFile list
      JsBrowserTests: SourceFile list
      JsWeb: SourceFile list }

type Tally = { Files: int; Code: int; Comments: int }

/// One F# binding and its measures.
type FsFunction =
    { File: string
      Name: string
      Line: int
      Length: int
      Ccn: int
      Indent: int }

/// Which files are measured: the globs below each directory, and the path fragments that exclude a file.
module Selection =
    let fsSource = [ "*.fs" ], "src"
    let fsTests = [ "*.fs" ], "tests"
    let jsBrowserTests = [ "*.js"; "*.mjs" ], "tests/browser"
    /// The web scripts are two selections, concatenated in this order.
    let jsWeb = [ ([ "*.js" ], "web"); ([ "*.ts" ], "src/kernel") ]

    /// True when the path (as the walk spells it, root included) is in node_modules or a build output.
    let skipped (path: string) = [ "node_modules"; "/bin/"; "/obj/" ] |> List.exists (fun s -> path.Contains(s, StringComparison.Ordinal))

// ---- Size --------------------------------------------------------------------------------------------------------

module Size =
    let private fsBlockComment = Regex(@"\(\*.*?\*\)", RegexOptions.Singleline)
    let private jsBlockComment = Regex(@"/\*.*?\*/", RegexOptions.Singleline)

    let private isComment (line: string) = line.StartsWith("//", StringComparison.Ordinal)

    let private split (text: string) =
        let lines = Text.splitlines text |> List.map Text.strip
        lines |> List.filter (fun l -> l <> "" && not (isComment l)) |> List.length, lines |> List.filter isComment |> List.length

    /// F#: (code lines, comment lines) once (* block comments *) are removed; a comment line starts with //.
    let fsharp (text: string) : int * int = split (fsBlockComment.Replace(text, ""))

    /// JavaScript/TypeScript: (code lines, // comment lines plus the number of /* in the original text).
    let javascript (text: string) : int * int =
        let code, comments = split (jsBlockComment.Replace(text, ""))
        code, comments + Text.count "/*" text

    let tally (count: string -> int * int) (files: SourceFile list) : Tally =
        let rs = files |> List.map (fun f -> count f.Text)
        { Files = files.Length; Code = rs |> List.sumBy fst; Comments = rs |> List.sumBy snd }

// ---- F# functions ------------------------------------------------------------------------------------------------

module Functions =
    // The patterns of the original script, with \s, \w and \b as Python's re defines them (PythonCompat.Re).
    let private s, w, b = Re.s, Re.w, Re.b

    /// A decision point: a match arm (a leading | not followed by | or >), if, elif, when, &&, ||, while, for, try,
    /// and a `with` that ends its line.
    let decision =
        Regex(
            sprintf @"(^%s*\|(?!\|)%s*[^|>])|%sif%s|%selif%s|%swhen%s|&&|\|\||%swhile%s|%sfor%s|%stry%s|%swith%s(?=%s*$)"
                s s b b b b b b b b b b b b b b s
        )

    /// A binding: let/member/override/and, optional rec/private/inline/mutable, then the name (group 4).
    let binding = Regex(sprintf @"^(%s*)(let|member|override|and)%s+(rec%s+|private%s+|inline%s+|mutable%s+)*(\(?[\p{L}\p{Nd}\p{Nl}\p{No}_.']+)" s s s s s s)

    /// A boundary that also ends a binding: type, module, exception, an attribute, namespace.
    let boundary = Regex(sprintf @"^(%s*)(type|module|exception|\[<|namespace)%s" s b)

    /// Every binding of a file, with its length (non-blank, non-// lines up to the next binding at the same or a lower
    /// indent, or the next boundary at the same or a lower indent) and its complexity (1 + decision points).
    let ofFile (file: SourceFile) : FsFunction list =
        let lines = Text.splitlines file.Text |> Array.ofList
        let indexed = lines |> Array.indexed |> List.ofArray
        let starts =
            indexed
            |> List.choose (fun (i, l) ->
                let m = binding.Match l
                if m.Success then Some(i, m.Groups[1].Length, m.Groups[4].Value) else None)
        let boundaries =
            indexed
            |> List.choose (fun (i, l) ->
                let m = boundary.Match l
                if m.Success then Some(i, m.Groups[1].Length) else None)
        let measure (k: int) (i: int, indent: int, name: string) =
            let laterStart = starts |> List.skip (k + 1) |> List.tryFind (fun (_, j, _) -> j <= indent) |> Option.map (fun (j, _, _) -> j)
            let laterBoundary = boundaries |> List.tryFind (fun (j, ind) -> j > i && ind <= indent) |> Option.map fst
            let next = [ laterStart; laterBoundary ] |> List.choose id |> function [] -> lines.Length | ends -> List.min ends
            let block =
                lines[i .. next - 1]
                |> Array.filter (fun l -> let t = Text.strip l in t <> "" && not (t.StartsWith("//", StringComparison.Ordinal)))
            { File = file.Path
              Name = name
              Line = i + 1
              Length = block.Length
              Ccn = 1 + (block |> Array.sumBy (fun l -> decision.Matches(l).Count))
              Indent = indent }
        starts |> List.mapi measure

// ---- Statistics --------------------------------------------------------------------------------------------------

module Stats =
    /// n, median (Python's statistics.median: the middle value, or the mean of the two middle values as a float),
    /// p90 (the value at index int(n * 0.9) - 1, Python's negative indexing included) and max; {} when empty.
    let distribution (values: int list) : Json =
        match List.sort values |> Array.ofList with
        | [||] -> JObject []
        | v ->
            let n = v.Length
            let median = if n % 2 = 1 then JInt(int64 v[n / 2]) else JFloat(float (v[n / 2 - 1] + v[n / 2]) / 2.0)
            let p = int (float n * 0.9) - 1
            JObject
                [ "n", JInt(int64 n)
                  "median", median
                  "p90", JInt(int64 v[if p < 0 then n + p else p])
                  "max", JInt(int64 v[n - 1]) ]

// ---- Style -------------------------------------------------------------------------------------------------------

module Style =
    let private s, b = Re.s, Re.b

    /// The functional-style counters, in output order. Each counts regex matches over the whole text; the loop
    /// pattern has no multiline flag, so (as in the original) it can only match at the start of a file.
    let patterns =
        [ "mutable", sprintf @"%smutable%s" b b
          "assign_<-", "<-"
          "for/while loops", sprintf @"^%s*(for|while)%s" s b
          "exceptions_raised", sprintf @"%s(failwith|failwithf|raise|invalidOp|invalidArg)%s" b b
          "Result_usage", sprintf @"%s(Ok|Error|Result)%s" b b
          "Option_usage", sprintf @"%s(Some|None|Option)%s" b b
          "pipelines_|>", @"\|>"
          "match_expressions", sprintf @"%smatch%s" b b ]
        |> List.map (fun (k, p) -> k, Regex p)

    let counts (files: SourceFile list) : (string * int) list =
        patterns |> List.map (fun (k, rx) -> k, files |> List.sumBy (fun f -> rx.Matches(f.Text).Count))

// ---- Report ------------------------------------------------------------------------------------------------------

module Report =
    let private tallyJson (t: Tally) = JObject [ "files", JInt(int64 t.Files); "code", JInt(int64 t.Code); "comments", JInt(int64 t.Comments) ]

    let private functionJson (f: FsFunction) =
        JObject
            [ "file", JString f.File
              "name", JString f.Name
              "line", JInt(int64 f.Line)
              "length", JInt(int64 f.Length)
              "ccn", JInt(int64 f.Ccn)
              "indent", JInt(int64 f.Indent) ]

    let private measures (fns: FsFunction list) =
        JObject [ "length", Stats.distribution (fns |> List.map (fun f -> f.Length)); "ccn", Stats.distribution (fns |> List.map (fun f -> f.Ccn)) ]

    /// The metrics as a JSON value, in the original script's key order. Sorting is stable, so ties keep file order.
    let build (sources: Sources) : Json =
        let fsSrc = Size.tally Size.fsharp sources.FsSource
        let fsTest = Size.tally Size.fsharp sources.FsTests
        let jsTest = Size.tally Size.javascript sources.JsBrowserTests
        let jsWeb = Size.tally Size.javascript sources.JsWeb
        let ratio = float (fsTest.Code + jsTest.Code) / float (max 1 (fsSrc.Code + jsWeb.Code)) |> Number.round 2
        let fns = sources.FsSource |> List.collect Functions.ofFile |> List.filter (fun f -> f.Length >= 3)
        let top = fns |> List.filter (fun f -> f.Indent <= 4)
        let over limit measure = fns |> List.filter (fun f -> measure f > limit) |> List.length |> int64 |> JInt
        JObject
            [ "size",
              JObject [ "fs_src", tallyJson fsSrc; "fs_test", tallyJson fsTest; "js_browser_tests", tallyJson jsTest; "js_web", tallyJson jsWeb ]
              "test_to_code", JFloat ratio
              "fs_functions_all", measures fns
              "fs_top_level", measures top
              "over_ccn10", over 10 (fun f -> f.Ccn)
              "over_ccn20", over 20 (fun f -> f.Ccn)
              "over_len60", over 60 (fun f -> f.Length)
              "worst", fns |> List.sortBy (fun f -> -f.Ccn) |> List.truncate 8 |> List.map functionJson |> JArray
              "style_src", Style.counts sources.FsSource |> List.map (fun (k, n) -> k, JInt(int64 n)) |> JObject
              "largest_files",
              sources.FsSource
              |> List.map (fun f -> f.Path, fst (Size.fsharp f.Text))
              |> List.sortBy (fun (_, code) -> -code)
              |> List.truncate 6
              |> List.map (fun (p, code) -> JArray [ JString p; JInt(int64 code) ])
              |> JArray ]

    /// What the script prints on stdout (Python's json.dumps(indent=1) plus print's newline).
    let toText (sources: Sources) : string = Json.dumps 1 (build sources) + "\n"
