// Owner: integration. The Echelon tool-usage recorder (docs/tool-usage.md). F# only; the pure core is UsageCore.fs.
//
//   dotnet fsi tools/usage/usage.fsx -- run <tool> -- <args...>
//       Runs the real tool with stdin, stdout and stderr inherited (passed through unchanged) and exits with its exit
//       code. Then appends one causa.tool-usage/1 line to <dir>/<YYYY-MM-DD>.ndjson. A recording failure only warns on
//       stderr; it never changes the exit code.
//   dotnet fsi tools/usage/usage.fsx -- report [--since YYYY-MM-DD] [--by tool|day|work-item] [--json]
//   dotnet fsi tools/usage/usage.fsx -- inventory [--out PATH] [--check]
//
// <dir> is CAUSA_TOOL_USAGE_DIR when set (CI points it outside the checkout), else .ros/telemetry/tool-usage under the
// repository root (the nearest directory above the current one that holds ros.json).
#load "UsageCore.fs"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Causa.Tools.Usage

let env (name: string) : string option =
    Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.filter (fun s -> s.Trim() <> "")

let warn (message: string) = eprintfn "tool-usage: warning: %s" message

// ---- Repository and record directory -----------------------------------------------------------------------------

let repositoryRoot () : string =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> Environment.CurrentDirectory
        | d when File.Exists(Path.Combine(d.FullName, "ros.json")) -> d.FullName
        | d -> up d.Parent
    up (DirectoryInfo Environment.CurrentDirectory)

let recordDirectory (root: string) =
    env "CAUSA_TOOL_USAGE_DIR" |> Option.defaultWith (fun () -> Path.Combine(root, ".ros", "telemetry", "tool-usage"))

let readRelative (root: string) (relative: string) : string option =
    let file = Path.Combine(root, relative)
    try if File.Exists file then Some(File.ReadAllText file) else None with _ -> None

// ---- Launching ---------------------------------------------------------------------------------------------------

let onPath (name: string) : string option =
    env "PATH"
    |> Option.defaultValue ""
    |> fun p -> p.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun d -> Path.Combine(d, name))
    |> Array.tryFind File.Exists

let toResult (error: string) (o: 'a option) : Result<'a, string> = match o with Some v -> Ok v | None -> Error error

/// The executable and the arguments that come before the user's.
let resolve (root: string) (tool: Tool) : Result<string * string list, string> =
    match Tool.launch env tool with
    | OnPath name -> onPath name |> Option.map (fun f -> f, []) |> toResult (sprintf "%s is not on PATH" name)
    | RepositoryLauncher(relative, fallback) ->
        let local = Path.Combine(root, relative)
        if File.Exists local then Ok(local, [])
        else onPath fallback |> Option.map (fun f -> f, []) |> toResult (sprintf "neither ./%s nor %s on PATH" relative fallback)
    | DotnetDll dll -> if File.Exists dll then Ok("dotnet", [ dll ]) else Error(sprintf "%s does not exist" dll)
    | DotnetFsi script -> if File.Exists script then Ok("dotnet", [ "fsi"; script; "--" ]) else Error(sprintf "%s does not exist" script)

let start (info: ProcessStartInfo) : Process =
    match Process.Start info with
    | null -> failwithf "%s did not start" info.FileName
    | p -> p

let startInfo (file: string) (args: string list) (redirect: bool) =
    let info = ProcessStartInfo(file, UseShellExecute = false, RedirectStandardOutput = redirect, RedirectStandardError = redirect)
    for a in args do
        info.ArgumentList.Add a
    info

/// Runs the tool with inherited standard streams: nothing it writes passes through the recorder.
let execute (file: string) (args: string list) : Result<int, string> =
    try
        use p = start (startInfo file args false)
        p.WaitForExit()
        Ok p.ExitCode
    with ex -> Error ex.Message

// ---- Version (cached per process) --------------------------------------------------------------------------------

let versions = Dictionary<Tool, string option>()

let probeVersion (root: string) (tool: Tool) : string option =
    match versions.TryGetValue tool with
    | true, v -> v
    | _ ->
        let v =
            match Tool.versionProbe env tool with
            | NoVersionCommand -> None
            | AssemblyOf dll ->
                try
                    if File.Exists dll then FileVersionInfo.GetVersionInfo(dll).ProductVersion |> Option.ofObj |> Option.bind Record.version else None
                with _ -> None
            | Flag flags ->
                match resolve root tool with
                | Error _ -> None
                | Ok(file, prefix) ->
                    try
                        use p = start (startInfo file (prefix @ flags) true)
                        let out = p.StandardOutput.ReadToEndAsync()
                        p.StandardError.ReadToEndAsync() |> ignore
                        if p.WaitForExit 30000 && p.ExitCode = 0 then Record.version out.Result
                        else
                            (try p.Kill true with _ -> ())
                            None
                    with _ -> None
        versions[tool] <- v
        v

// ---- Append-only, same-directory atomic write --------------------------------------------------------------------

/// Appends one line under an exclusive lock: the existing file plus the line go to a temporary file in the same
/// directory, which then atomically replaces the file (Praxis's same-directory temporary + atomic replace). An
/// interrupted writer leaves the previous complete file.
let append (dir: string) (fileName: string) (line: string) : Result<string, string> =
    try
        Directory.CreateDirectory dir |> ignore
        let full = Path.GetFullPath dir
        let key = SHA256.HashData(Encoding.UTF8.GetBytes full) |> Convert.ToHexString |> fun s -> s.Substring(0, 16).ToLowerInvariant()
        let lockFile = Path.Combine(Path.GetTempPath(), sprintf "causa-tool-usage-%s.lock" key)
        let rec acquire attempt =
            try Ok(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            with :? IOException when attempt < 100 ->
                Thread.Sleep 50
                acquire (attempt + 1)
        match (try acquire 0 with ex -> Error ex.Message) with
        | Error e -> Error(sprintf "could not lock %s: %s" lockFile e)
        | Ok lockStream ->
            use _ = lockStream
            let target = Path.Combine(full, fileName)
            let existing = if File.Exists target then File.ReadAllText target else ""
            let temp = Path.Combine(full, sprintf ".%s.%s.tmp" fileName (Guid.NewGuid().ToString "N"))
            try
                File.WriteAllText(temp, Record.appended existing line, UTF8Encoding false)
                File.Move(temp, target, true)
                Ok target
            finally
                if File.Exists temp then File.Delete temp
    with ex -> Error ex.Message

// ---- Commands ----------------------------------------------------------------------------------------------------

let runTool (toolId: string) (args: string list) : int =
    let root = repositoryRoot ()
    match Tool.parse toolId with
    | Error e ->
        eprintfn "tool-usage: %s" e
        2
    | Ok tool ->
        let started = DateTimeOffset.UtcNow
        let clock = Stopwatch.StartNew()
        let exitCode, outcome =
            match resolve root tool |> Result.bind (fun (file, prefix) -> execute file (prefix @ args)) with
            | Ok code -> code, Record.outcomeOf code
            | Error e ->
                eprintfn "tool-usage: %s could not be started: %s" toolId e
                127, NotStarted
        clock.Stop()
        let ended = started + clock.Elapsed
        try
            let shape = Arguments.shape tool args
            let record =
                { InvocationId = Guid.NewGuid().ToString "N"
                  Tool = Tool.id tool
                  ToolVersion = if outcome = NotStarted then None else probeVersion root tool
                  Subcommand = shape.Subcommand
                  ArgNames = shape.ArgNames
                  ArgCount = shape.ArgCount
                  StartedAt = started
                  EndedAt = ended
                  DurationMs = int64 clock.Elapsed.TotalMilliseconds
                  ExitCode = exitCode
                  Outcome = outcome
                  Praxis = PraxisContext.resolve env (readRelative root)
                  Environment = if env "CI" = Some "true" || env "GITHUB_ACTIONS" = Some "true" then "ci" else "local" }
            match append (recordDirectory root) (Record.fileName record) (Record.toJson record) with
            | Ok _ -> ()
            | Error e -> warn (sprintf "the invocation was not recorded: %s" e)
        with ex -> warn (sprintf "the invocation was not recorded: %s" ex.Message)
        exitCode

let report (args: string list) : int =
    let rec parse (since, by, json) =
        function
        | [] -> Ok(since, by, json)
        | "--since" :: d :: rest ->
            match DateOnly.TryParseExact(d, "yyyy-MM-dd") with
            | true, v -> parse (Some v, by, json) rest
            | _ -> Error(sprintf "--since takes YYYY-MM-DD, not '%s'" d)
        | "--by" :: b :: rest -> Report.parseGrouping b |> Result.bind (fun g -> parse (since, g, json) rest)
        | "--json" :: rest -> parse (since, by, true) rest
        | other :: _ -> Error(sprintf "unknown report option '%s'" other)
    match parse (None, ByTool, false) args with
    | Error e ->
        eprintfn "tool-usage: %s" e
        2
    | Ok(since, by, json) ->
        let dir = recordDirectory (repositoryRoot ())
        let files = if Directory.Exists dir then Directory.GetFiles(dir, "*.ndjson") |> Array.sort |> List.ofArray else []
        let parsed = files |> List.map (File.ReadAllText >> Record.parseLines)
        let records = parsed |> List.collect fst
        let skipped = parsed |> List.sumBy (snd >> List.length)
        let r = Report.build by since skipped records
        stdout.Write(if json then Report.toJson r else Report.toText r)
        0

let inventory (args: string list) : int =
    let root = repositoryRoot ()
    let rec parse (out, check) =
        function
        | [] -> Ok(out, check)
        | "--out" :: p :: rest -> parse (p, check) rest
        | "--check" :: rest -> parse (out, true) rest
        | other :: _ -> Error(sprintf "unknown inventory option '%s'" other)
    match parse ("evidence/tool-usage/inventory.json", false) args with
    | Error e ->
        eprintfn "tool-usage: %s" e
        2
    | Ok(out, check) ->
        let skip = set [ ".git"; "bin"; "obj"; "TestResults"; "test-results"; "playwright-report" ]
        // Build outputs and CI checkouts at the root are not source.
        let skipAtRoot = set [ "dist"; ".ci" ]
        let rec walk (dir: string) : string list =
            [ for f in Directory.GetFiles dir do
                  yield Path.GetRelativePath(root, f).Replace('\\', '/')
              for d in Directory.GetDirectories dir do
                  let name = Path.GetFileName d
                  let rel = Path.GetRelativePath(root, d).Replace('\\', '/')
                  // node_modules: only the Echelon packages the inventory reads.
                  if rel = "node_modules" then
                      let echelon = Path.Combine(d, "@echelon-foundry")
                      if Directory.Exists echelon then yield! walk echelon
                  elif not (skip.Contains name || skipAtRoot.Contains rel) then
                      yield! walk d ]
        let view = { Files = walk root |> List.sort; Read = readRelative root }
        let text = Inventory.build view
        let target = Path.Combine(root, out)
        if check then
            match readRelative root out with
            | Some existing when existing = text ->
                printfn "%s is current" out
                0
            | _ ->
                eprintfn "tool-usage: %s is out of date; run `dotnet fsi tools/usage/usage.fsx -- inventory`" out
                1
        else
            match Path.GetDirectoryName target with
            | null -> ()
            | d -> Directory.CreateDirectory d |> ignore
            File.WriteAllText(target, text, UTF8Encoding false)
            printfn "wrote %s" out
            0

let usage () =
    eprintfn "usage: dotnet fsi tools/usage/usage.fsx -- run <tool> -- <args...>"
    eprintfn "       dotnet fsi tools/usage/usage.fsx -- report [--since YYYY-MM-DD] [--by tool|day|work-item] [--json]"
    eprintfn "       dotnet fsi tools/usage/usage.fsx -- inventory [--out PATH] [--check]"
    eprintfn "tools: %s" (Tool.all |> List.map Tool.id |> String.concat ", ")
    2

let main (argv: string list) =
    match argv with
    | "run" :: tool :: "--" :: rest -> runTool tool rest
    | "run" :: tool :: rest -> runTool tool rest
    | "report" :: rest -> report rest
    | "inventory" :: rest -> inventory rest
    | _ -> usage ()

exit (main (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray))
