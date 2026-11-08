// Owner: integration. Derives implementation progress events (indy-init.progress-event/1) from test results that
// actually ran. F# only; the rules and the pure core are in ProgressCore.fs.
//
//   dotnet fsi tools/progress/progress-from-tests.fsx -- <repo-root> <measured-commit> <trx-dir> <playwright-json>...
//
// Reads <repo-root>/.echelon/r9-work-plan.json, <repo-root>/.echelon/obligations.json,
// <repo-root>/evidence/progress/events.json, every *.trx in <trx-dir> (later files by modification time win) and each
// Playwright JSON report (later reports win), and asks git in <repo-root> which slice work is in <measured-commit>.
// Prints the NEW events (a JSON array; append them to evidence/progress/events.json yourself) on stdout and a summary
// on stderr. It writes no file. It never records verification or integration: only the governed Verification gate may.
//
// The `at` of every event is the current UTC time, or CAUSA_PROGRESS_AT (YYYY-MM-DDTHH:MM:SSZ) when set, so a run can
// be reproduced byte for byte.
#load "ProgressCore.fs"

open System
open System.Diagnostics
open System.IO
open Causa.Tools.Progress

// ---- Edges: files, git, clock ------------------------------------------------------------------------------------

let readFile (path: string) : Result<string, string> =
    try Ok(File.ReadAllText path) with ex -> Error(sprintf "cannot read %s: %s" path ex.Message)

/// Runs git in the repository. Captured: stdout is returned, stderr discarded. Inherited: both pass through.
let git (repo: string) (capture: bool) (args: string list) : int * string =
    let info = ProcessStartInfo("git", UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture)
    for a in [ "-C"; repo ] @ args do
        info.ArgumentList.Add a
    try
        match Process.Start info with
        | null -> -1, ""
        | p ->
            use p = p
            let out = if capture then p.StandardOutput.ReadToEndAsync() else Threading.Tasks.Task.FromResult ""
            if capture then p.StandardError.ReadToEndAsync() |> ignore
            p.WaitForExit()
            p.ExitCode, out.Result
    with _ -> -1, ""

let words (text: string) = text.Split([| ' '; '\t'; '\n'; '\r'; '\f'; '\v' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

/// True when a commit that belongs to this work item's slice branch, and to no other slice branch, is in `commit`.
/// Shared history merged in from other slices does not count. When git cannot list such commits, the branch tips
/// themselves are asked.
let merged (repo: string) (commit: string) (branches: string list) (workItem: string) : bool =
    let mine, others = Branches.split workItem branches
    let own = if mine.IsEmpty then [] else git repo true (Branches.ownCommitsArguments mine others) |> snd |> words
    let inCommit c = git repo false [ "merge-base"; "--is-ancestor"; c; commit ] |> fst = 0
    if own.IsEmpty then List.exists inCommit mine else List.exists inCommit own

/// The .trx files of a directory (not hidden ones), oldest modification first.
let trxFiles (dir: string) : string list =
    if Directory.Exists dir then
        Directory.GetFiles(dir, "*.trx", EnumerationOptions(MatchCasing = MatchCasing.CaseSensitive, AttributesToSkip = FileAttributes.None))
        |> Array.filter (fun f -> not ((Path.GetFileName f).StartsWith '.'))
        |> List.ofArray
        |> List.sortBy File.GetLastWriteTimeUtc
    else []

let traverse f xs = List.foldBack (fun x acc -> acc |> Result.bind (fun rest -> f x |> Result.map (fun v -> v :: rest))) xs (Ok [])

let at () : Result<string, string> =
    match Environment.GetEnvironmentVariable "CAUSA_PROGRESS_AT" |> Option.ofObj |> Option.filter (fun s -> s <> "") with
    | Some text -> Output.parseTimestamp text
    | None -> Ok(Output.timestamp DateTimeOffset.UtcNow)

// ---- Command -----------------------------------------------------------------------------------------------------

let run (repo: string) (commit: string) (trxDir: string) (reports: string list) : Result<string * string, string> =
    let under (relative: string) = Path.Combine(repo, relative)
    let bind f r = Result.bind f r
    readFile (under ".echelon/r9-work-plan.json") |> bind Inputs.plan
    |> bind (fun plan ->
        readFile (under ".echelon/obligations.json") |> bind Inputs.obligations
        |> bind (fun obligations ->
            readFile (under "evidence/progress/events.json") |> bind Inputs.events
            |> bind (fun existing ->
                let dotnet = trxFiles trxDir |> traverse (readFile >> bind Results.trx) |> Result.map List.concat
                let browser = reports |> traverse (readFile >> bind Results.playwright)
                match dotnet, browser with
                | Error e, _
                | _, Error e -> Error e
                | Ok d, Ok b ->
                    let inputs = { Plan = plan; Obligations = obligations; Existing = existing; Results = Results.merge (d :: b) }
                    let branches = git repo true [ "for-each-ref"; "--format=%(refname:short)"; "refs/remotes/origin/slice/" ] |> snd |> words
                    let onMain = plan |> List.filter (fun w -> merged repo commit branches w.Id) |> List.map (fun w -> w.Id) |> Set.ofList
                    let derivation = Derive.progress inputs onMain
                    at ()
                    |> Result.map (fun stamp ->
                        let resolve () = git repo true [ "rev-parse"; commit ] |> snd |> fun s -> s.Trim()
                        Output.events (Derive.events stamp resolve derivation), Output.summary inputs.Results derivation))))

let main (argv: string list) : int =
    match argv with
    | repo :: commit :: trxDir :: reports ->
        match run repo commit trxDir reports with
        | Ok(events, summary) ->
            stdout.Write(events + "\n")
            stdout.Flush()
            eprintfn "%s" summary
            0
        | Error e ->
            eprintfn "progress-from-tests: %s" e
            1
    | _ ->
        eprintfn "usage: dotnet fsi tools/progress/progress-from-tests.fsx -- <repo-root> <measured-commit> <trx-dir> <playwright-json>..."
        2

exit (main (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray))
