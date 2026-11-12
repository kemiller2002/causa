// Owner: integration. The code-quality metrics (tools/metrics/code-metrics.fsx): the measuring rules, determinism and
// equivalence with the Python script they replaced (docs/quality/code_metrics.py, PR #8), plus the Python behaviours
// both tools reproduce (tools/shared/PythonCompat.fs). The core is linked from tools/metrics/MetricsCore.fs.
//
// Fixtures/code-metrics/tree is a small repository; MetricsTree.build adds the files git cannot hold as-is (CRLF and
// lone-CR line endings, a byte-order mark, an undecodable byte, files under bin/, obj/ and node_modules/).
// expected.stdout is what the Python script printed for that tree. The fixture has no ties in any sorted list, so
// the output does not depend on directory order.
namespace Causa.App.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open Xunit
open Causa.Tools.Metrics
open Causa.Tools.PythonCompat

module private MetricsTree =
    let repositoryRoot =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwithf "no Causa.slnx above %s" AppContext.BaseDirectory
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let fixture = Path.Combine(repositoryRoot, "tests", "Causa.App.Tests", "Fixtures", "code-metrics")
    let script = Path.Combine(repositoryRoot, "tools", "metrics", "code-metrics.fsx")
    let expected () = File.ReadAllText(Path.Combine(fixture, "expected.stdout"))

    let bytes (text: string) = Encoding.Latin1.GetBytes text

    /// The generated files, byte for byte (Latin-1 maps each char below U+0100 to one byte).
    let generated =
        let skipped = "let skipped () =\n    if true then 1\n    else 2\n"
        [ "src/App/Crlf.fs", "ï»¿let crlf (a: int) =\r\n    if a > 0 then\r\n        a\r\n    else 0 ÿ\r\n    |> ignore\r\n"
          "src/App/Lone.fs", "let lone (s: string) =\r    s.Trim()\r    |> String.length\r\u000c  \u001c\rlet tail = 1\r"
          "src/App/bin/Skipped.fs", skipped
          "src/App/obj/Skipped.fs", skipped
          "src/node_modules/pkg/Skipped.fs", skipped
          "tests/bin/Skipped.fs", skipped ]

    let rec copy (source: string) (target: string) =
        Directory.CreateDirectory target |> ignore
        for f in Directory.GetFiles source do
            File.Copy(f, Path.Combine(target, Path.GetFileName f |> string))
        for d in Directory.GetDirectories source do
            copy d (Path.Combine(target, Path.GetFileName d |> string))

    let build () : string =
        let work = Path.Combine(Path.GetTempPath(), "causa-metrics-" + Guid.NewGuid().ToString "N")
        copy (Path.Combine(fixture, "tree")) work
        for relative, content in generated do
            let file = Path.Combine(work, relative)
            Directory.CreateDirectory(Path.GetDirectoryName file |> string) |> ignore
            File.WriteAllBytes(file, bytes content)
        work

    /// The measured files of a tree, read as the script reads them, in the given order.
    let sources (root: string) (order: string list -> string list) : Sources =
        let under (dir: string) (suffixes: string list) =
            let full = Path.Combine(root, dir)
            if Directory.Exists full then
                Directory.GetFiles(full, "*", SearchOption.AllDirectories)
                |> Array.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'))
                |> Array.filter (fun p -> suffixes |> List.exists (fun s -> p.EndsWith(s, StringComparison.Ordinal)))
                |> Array.filter (Selection.skipped >> not)
                |> List.ofArray
                |> order
                |> List.map (fun p -> { Path = p; Text = Text.decode (File.ReadAllBytes(Path.Combine(root, p))) })
            else []
        { FsSource = under "src" [ ".fs" ]
          FsTests = under "tests" [ ".fs" ]
          JsBrowserTests = under "tests/browser" [ ".js"; ".mjs" ]
          JsWeb = under "web" [ ".js" ] @ under "src/kernel" [ ".ts" ] }

    let dotnet =
        Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" |> Option.ofObj |> Option.filter File.Exists |> Option.defaultValue "dotnet"

    let run (cwd: string) (root: string) : int * string * string =
        let info = ProcessStartInfo(dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd)
        for a in [ "fsi"; script; "--"; root ] do
            info.ArgumentList.Add a
        use p =
            match Process.Start info with
            | null -> failwith "the metrics script did not start"
            | p -> p
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result, err.Result

module CodeMetricsTests =
    let private file path text = { Path = path; Text = text }

    let private withTree (f: string -> unit) =
        let work = MetricsTree.build ()
        try f work finally Directory.Delete(work, true)

    // ---- Equivalence with the Python script ---------------------------------------------------------------------

    [<Fact>]
    let ``Equivalence: the script prints the Python script's bytes for the fixture tree`` () =
        withTree (fun work ->
            let code, out, err = MetricsTree.run work "."
            Assert.True((code = 0), err)
            Assert.Equal("", err)
            Assert.Equal(MetricsTree.expected (), out)
            // The root may be spelled any way pathlib accepts: absolute, with a trailing slash.
            let _, absolute, _ = MetricsTree.run (Path.GetTempPath()) (work + "/")
            Assert.Equal(MetricsTree.expected (), absolute))

    [<Fact>]
    let ``Equivalence: the core reproduces the Python script's bytes from the files alone`` () =
        withTree (fun work -> Assert.Equal(MetricsTree.expected (), Report.toText (MetricsTree.sources work id)))

    // ---- Determinism ---------------------------------------------------------------------------------------------

    [<Fact>]
    let ``Determinism: the same files give the same bytes, in any file order when nothing ties`` () =
        withTree (fun work ->
            let forward = Report.toText (MetricsTree.sources work List.sort)
            Assert.Equal(forward, Report.toText (MetricsTree.sources work List.sort))
            Assert.Equal(forward, Report.toText (MetricsTree.sources work (List.sort >> List.rev))))

    [<Fact>]
    let ``Determinism: ties keep file order, as Python's stable sort does`` () =
        let fn name = sprintf "let %s () =\n    if true then\n        1\n    else 2\n" name
        let sources = { FsSource = [ file "src/B.fs" (fn "b"); file "src/A.fs" (fn "a") ]; FsTests = []; JsBrowserTests = []; JsWeb = [] }
        let worst = Report.build sources |> function JObject fields -> fields |> List.find (fst >> (=) "worst") |> snd | _ -> JArray []
        match worst with
        | JArray [ JObject first; JObject _ ] -> Assert.Equal(JString "src/B.fs", first |> List.find (fst >> (=) "file") |> snd)
        | other -> failwithf "unexpected %A" other

    // ---- Measuring rules -----------------------------------------------------------------------------------------

    [<Fact>]
    let ``Rule: F# lines are code unless blank, // comments, or inside (* block comments *)`` () =
        Assert.Equal((2, 1), Size.fsharp "let a = 1\n\n// note\n(* gone\n let b = 2 *)\n   let c = 3   \n")
        // An unterminated block comment hides nothing.
        Assert.Equal((2, 0), Size.fsharp "(* open\nlet a = 1\n")

    [<Fact>]
    let ``Rule: JavaScript comments are // lines plus every /* in the original text`` () =
        Assert.Equal((2, 3), Size.javascript "// one\n/* two\n three */ const a = 1;\nf(/* four */);\n")

    [<Fact>]
    let ``Rule: a binding runs to the next binding or boundary at the same or a lower indent`` () =
        let fns =
            Functions.ofFile (
                file
                    "src/X.fs"
                    "let outer x =\n    let inner y =\n        y + 1\n    if x then inner 1 else 0\n[<Attr>]\nlet next = 1\ntype T = A\n"
            )
        let summary = fns |> List.map (fun f -> f.Name, f.Line, f.Length, f.Ccn, f.Indent)
        Assert.Equal<(string * int * int * int * int) list>(
            [ "outer", 1, 4, 2, 0; "inner", 2, 3, 2, 4; "next", 6, 1, 1, 0 ],
            summary
        )

    [<Fact>]
    let ``Rule: decision points are match arms, if, elif, when, &&, ||, while, for, try and a line-ending with`` () =
        let count (line: string) = Functions.decision.Matches(line).Count
        Assert.Equal(1, count "    | Some x -> x")
        Assert.Equal(0, count "    |> List.map f")
        // A leading || is not a match arm, but it is still an ||.
        Assert.Equal(1, count "    || ok")
        Assert.Equal(3, count "    | n when n > 0 && ok -> n")
        Assert.Equal(2, count "if a then b elif c then d")
        Assert.Equal(1, count "    match x with")
        Assert.Equal(0, count "    { r with A = 1 }")
        Assert.Equal(0, count "    gift, form, trying, elifant, whence")

    [<Fact>]
    let ``Rule: distributions use Python's median and p90 indexing`` () =
        let text values = Json.dumps 1 (Stats.distribution values)
        Assert.Equal("{}", text [])
        Assert.Equal("{\n \"n\": 1,\n \"median\": 4,\n \"p90\": 4,\n \"max\": 4\n}", text [ 4 ])
        Assert.Equal("{\n \"n\": 2,\n \"median\": 2.5,\n \"p90\": 2,\n \"max\": 3\n}", text [ 3; 2 ])
        Assert.Equal("{\n \"n\": 4,\n \"median\": 2.0,\n \"p90\": 2,\n \"max\": 9\n}", text [ 9; 1; 2; 2 ])

    [<Fact>]
    let ``Rule: node_modules, bin and obj paths are not measured`` () =
        Assert.True(Selection.skipped "src/node_modules/x.fs")
        Assert.True(Selection.skipped "tests/App/bin/Release/x.fs")
        Assert.True(Selection.skipped "/repo/src/obj/x.fs")
        Assert.False(Selection.skipped "src/binary/x.fs")

    // ---- Python behaviours ---------------------------------------------------------------------------------------

    [<Fact>]
    let ``Python: float repr`` () =
        let cases =
            [ 7.0, "7.0"; 7.5, "7.5"; 1.46, "1.46"; 0.1, "0.1"; 100.0, "100.0"; 0.0001, "0.0001"; 1e-05, "1e-05"
              1e15, "1000000000000000.0"; 1e16, "1e+16"; 1.5e300, "1.5e+300"; -2.25, "-2.25"; 0.0, "0.0"; 1.0 / 3.0, "0.3333333333333333" ]
        for value, repr in cases do
            Assert.Equal(repr, Json.floatRepr value)

    [<Fact>]
    let ``Python: round(x, 2) rounds the exact binary value, ties to even`` () =
        let cases = [ 0.125, 0.12; 0.375, 0.38; 2.675, 2.67; 1.005, 1.0; 1.4567, 1.46; -0.125, -0.12; 3.0, 3.0 ]
        for value, rounded in cases do
            Assert.Equal(rounded, Number.round 2 value)

    [<Fact>]
    let ``Python: splitlines, strip and text-mode reading`` () =
        Assert.Equal<string list>([ "a"; "b"; ""; "c"; "d"; "e" ], Text.splitlines "a\r\nb\n\rc\u000cd e\n")
        Assert.Equal<string list>([], Text.splitlines "")
        Assert.Equal("x", Text.strip "\u001c\t x   ")
        Assert.Equal("﻿a\nb\nc", Text.decode [| 0xEFuy; 0xBBuy; 0xBFuy; byte 'a'; 0x0Duy; 0x0Auy; byte 'b'; 0xFFuy; 0x0Duy; byte 'c' |])
        Assert.Equal(2, Text.count "/*" "/*/* x")
