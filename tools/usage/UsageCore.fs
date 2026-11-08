// Owner: integration. The Echelon tool-usage recorder's pure core (docs/tool-usage.md). Shared by
// tools/usage/usage.fsx (#load) and Causa.App.Tests (UsageRecorderTests). Everything here is a pure function over
// values: the environment, the files and the clock come in as arguments, and the script performs the side effects.
//
// Privacy rule: a record holds the tool, its version, the recognised subcommand words, the argument NAMES and the
// argument count. It never holds an argument value, a positional argument, file contents, a token or an environment
// value. A subcommand word is recorded only when it is in the tool's known command vocabulary below, so a positional
// value can never be mistaken for a subcommand and leak.
module Causa.Tools.Usage

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// ---- Tools -------------------------------------------------------------------------------------------------------

/// The Echelon development tools the recorder knows.
type Tool =
    | Ordo
    | Praxis
    | Percepta
    | PerceptaRepo
    | Conditor
    | Dokimos
    | RequirementsCompiler
    | R9

/// How a tool is started. The recorder never goes through a shell.
type Launch =
    /// An executable looked up on PATH.
    | OnPath of name: string
    /// A launcher in the repository (relative to the repository root), else the named executable on PATH.
    | RepositoryLauncher of relative: string * fallback: string
    /// `dotnet <dll>`.
    | DotnetDll of dll: string
    /// `dotnet fsi <script> --`.
    | DotnetFsi of script: string

/// How a tool's version is found.
type VersionProbe =
    /// Run the tool with these arguments and read the version from its output.
    | Flag of string list
    /// Read the assembly's informational version (a .NET tool with no version command).
    | AssemblyOf of dll: string
    /// The tool has no version command; the reason is recorded in the documentation, never guessed.
    | NoVersionCommand

module Tool =
    let all = [ Ordo; Praxis; Percepta; PerceptaRepo; Conditor; Dokimos; RequirementsCompiler; R9 ]

    let id =
        function
        | Ordo -> "ordo"
        | Praxis -> "praxis"
        | Percepta -> "percepta"
        | PerceptaRepo -> "percepta-repo"
        | Conditor -> "conditor"
        | Dokimos -> "dokimos"
        | RequirementsCompiler -> "requirements-compiler"
        | R9 -> "r9"

    let parse (text: string) : Result<Tool, string> =
        match all |> List.tryFind (fun t -> id t = text) with
        | Some t -> Ok t
        | None -> Error(sprintf "unknown tool '%s' (known: %s)" text (all |> List.map id |> String.concat ", "))

    /// Where the Indy-init governance tools live: INDY_INIT_ROOT, else ~/Indy-init (the lane brief's SRC).
    let indyInitRoot (env: string -> string option) =
        env "INDY_INIT_ROOT"
        |> Option.defaultWith (fun () -> Path.Combine(env "HOME" |> Option.defaultValue "", "Indy-init"))

    let requirementsCompilerDll env =
        Path.Combine(indyInitRoot env, "tools", "requirements-compiler", "src", "bin", "Release", "net10.0", "requirements-compiler.dll")

    let r9Script env = Path.Combine(indyInitRoot env, "tools", "r9", "r9.fsx")

    let launch (env: string -> string option) =
        function
        | Ordo -> OnPath "ordo"
        | Praxis -> RepositoryLauncher("praxis", "praxis")
        | Percepta -> OnPath "percepta"
        | PerceptaRepo -> OnPath "percepta-repo"
        | Conditor -> OnPath "conditor"
        | Dokimos -> OnPath "dokimos"
        | RequirementsCompiler -> DotnetDll(requirementsCompilerDll env)
        | R9 -> DotnetFsi(r9Script env)

    let versionProbe (env: string -> string option) =
        function
        | Ordo | Praxis | Percepta | PerceptaRepo -> Flag [ "--version" ]
        | Dokimos -> Flag [ "version" ]
        // Conditor 0.10.21 prints its usage and exits 1 for --version; requirements-compiler rejects --version and r9
        // has no version command. Neither is guessed.
        | Conditor -> NoVersionCommand
        | RequirementsCompiler -> AssemblyOf(requirementsCompilerDll env)
        | R9 -> NoVersionCommand

    let private paths (lines: string list) =
        lines |> List.map (fun (l: string) -> l.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

    /// The known command paths of each tool (from each tool's own --help at the pinned version). Alternatives in one
    /// path are written `a|b`.
    let vocabulary =
        function
        | Ordo ->
            paths [ "init"; "status"; "verify"; "upgrade"; "doctor"; "boundary assess"; "verification schedule"; "ecir"; "version"; "help" ]
        | Praxis ->
            paths
                [ "init"; "status"; "verify"; "upgrade"; "doctor"; "validate"; "version"; "add"; "reconcile"; "help"
                  "adapter call|publish"; "architecture check"; "artifacts validate"
                  "autonomy policy check"; "autonomy authorize draft|attach|verify"; "autonomy receipts verify"
                  "autonomy revoke|review|evaluate|merge|observations|diagnose"
                  "construct preview|apply|prepare|check|route|extend"
                  "execution start|show|actions|boundary|containment|list|evaluate|launch|expand-scope|resolve-effect|transition|rebind|cleanup"
                  "execution step declare|run|start|observe|reconcile"
                  "foundations verify"; "git status"; "handoff explain"
                  "hub register|unregister|repos|create|work|serve"
                  "inbox list|show|claim|derive|admit-plan|complete|release|reject|recover"
                  "installation register|remove|verify|reconcile|list|status|history"
                  "instance show|init|projection|register"
                  "local run|status|worker-bridge"; "local packet propose"; "local validator pin"
                  "local operator init|grant-draft|grant|revoke|review|delegate"; "local containment qualify|status"
                  "ordo ingest|assess|observe-search|observe-effect|current|handoff"; "ordo contract check"
                  "pacing status|gate|override"; "pacing state quarantine"
                  "plan analyze|simulate|compare|explain|groups|explain-group|replay|freshness|execute-group"
                  "provenance identity|record|show|audit|acknowledge-unrecorded"
                  "registry build|check"; "remote execute|classify|describe"
                  "repository identity"; "repository identity set"
                  "requirements preservation verify|sections"; "requirements manifest compile|check"
                  "step plan|begin|resume|complete|block|abandon|record|availability|checkpoint|link|list|show"
                  "streams check"; "sync status|check"
                  "telemetry adapters|show|summary|summarize|finalize|record|usage|reliability-report|ingest|classify|start"
                  "telemetry step start|complete|fail"
                  "tutela ingest|metrics"; "verifier serve|run"; "web serve"
                  "work decide|plan|context-plan|backlog-decide|backlog-promotion-plan|validate|backlog-validate|backlog-transition|capture|reidentify|update|attach|start|begin|resume|block|abandon|complete|done|reconcile|evidence|continue|context|show|ready|claim|recover|renew|release|expire|complete-verified|completion-identity|finish|validate-run|verify"
                  "work checkpoint show"; "work claims migrate"; "work history merge"
                  "work group show|add|remove|checkpoint|create|list|link|cost" ]
        | Percepta ->
            paths
                [ "compile"; "verify"; "verify-app"; "validate-evidence"; "artifact-metadata"; "status"; "doctor"; "install-browser"
                  "spec-preflight"; "version" ]
        | PerceptaRepo -> paths [ "init"; "status"; "verify"; "doctor"; "upgrade" ]
        | Conditor ->
            paths
                [ "presets"; "components"; "compatibility"; "adopt"; "plan"; "init"; "verify"; "doctor"; "status"; "repair"; "upgrade"
                  "drift"; "unattended-readiness"; "ci-policy check"; "test-env preflight|digest|resolve"; "acceptance preflight"
                  "rehearsal init"; "resume"; "handoff"; "start"; "supervise"; "repo create"; "requirements import"
                  "workstation plan|apply|register|status|reconcile"; "lifecycle plan|apply"; "bundle create|verify"; "uninstall" ]
        | Dokimos ->
            paths
                [ "version"; "capabilities"; "measure"; "analyze"; "snapshot"; "compare"; "ecir audit|recognition-audit"
                  "construction verify"; "behavior verify"; "completion verify"; "evidence plan|record"; "evaluate"; "results"
                  "store init|put|get|accept-baseline|baseline"; "init"; "status"; "verify"; "doctor"; "upgrade"; "history findings"
                  "ratchet check|rules"; "ratchet baseline init|update|diff"
                  "verification compile|scaffold|coverage|probe|requirements"; "identity" ]
        | RequirementsCompiler ->
            paths
                [ "baseline"; "verify-plan"; "verify-process"; "verify-defect-closures"; "release-comparison"; "assertion-ratchet"
                  "plan-readiness"; "propose-test-bindings"; "review-attestations"; "v4"; "v41"; "token-economics"
                  "scope compile|preflight|checkpoint|work-plan|order"; "progress"; "stop-decision"; "resume"
                  "catalog-acceptance"; "ledger"; "slice-manifest"; "slice-gate"; "completion-gate"; "test-integrity"
                  "design-contract"; "observe"; "forma-inventory"; "preflight" ]
        | R9 ->
            paths
                [ "install-conditor"; "workstation"; "govern"; "check-governance"; "gate"; "run-suites"; "suite-preflight"
                  "suite-fixture"; "authorize-implementation"; "check-sequencing"; "acceptance-status"; "spec-preflight"
                  "verification-plan" ]

// ---- Arguments ---------------------------------------------------------------------------------------------------

/// What the recorder keeps of a command line: never a value.
type ArgumentShape =
    { Subcommand: string option
      ArgNames: string list
      ArgCount: int }

module Arguments =
    let private flagPattern = Regex(@"^--?[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)

    let private isFlag (a: string) = a.StartsWith "-" && a <> "-" && a <> "--"

    /// A flag's name: `--name=value` keeps `--name`; anything that is not a well-formed name (`-5`, `--=x`) is dropped.
    let flagName (a: string) : string option =
        let name = match a.IndexOf '=' with | -1 -> a | i -> a.Substring(0, i)
        if flagPattern.IsMatch name then Some name else None

    let private accepts (vocabulary: string list list) (accepted: string list) (word: string) =
        let depth = List.length accepted
        vocabulary
        |> List.exists (fun path ->
            List.length path > depth
            && List.truncate depth path |> List.forall2 (fun (alts: string) w -> alts.Split('|') |> Array.contains w) accepted
            && (List.item depth path).Split('|') |> Array.contains word)

    /// The subcommand: the first non-flag argument that starts a known command path, followed by the consecutive
    /// non-flag arguments that continue it. A word outside the vocabulary (a value, a path, an ID) ends it.
    let subcommand (vocabulary: string list list) (args: string list) : string option =
        let rec start =
            function
            | [] -> []
            | (a: string) :: rest when isFlag a -> start rest
            | a :: rest when accepts vocabulary [] a -> extend [ a ] rest
            | _ :: rest -> start rest
        and extend accepted =
            function
            | (a: string) :: rest when not (isFlag a) && accepts vocabulary accepted a -> extend (accepted @ [ a ]) rest
            | _ -> accepted
        match start args with
        | [] -> None
        | words -> Some(String.Join(" ", words))

    let shape (tool: Tool) (args: string list) : ArgumentShape =
        { Subcommand = subcommand (Tool.vocabulary tool) args
          ArgNames = args |> List.filter isFlag |> List.choose flagName |> List.distinct
          ArgCount = List.length args }

// ---- Praxis context ----------------------------------------------------------------------------------------------

/// How the active Praxis work item and execution were found.
type Resolution =
    /// PRAXIS_EXECUTION_ID / PRAXIS_WORK_ITEM, the selectors Praxis itself reads.
    | FromEnvironment
    /// The single active execution of an active work item whose identity.sessionId is this process's session.
    | FromSession
    /// Several active executions belong to this session: none is chosen.
    | Ambiguous of candidates: int
    /// No active execution belongs to this session, or there is no Praxis state.
    | NoneActive

type PraxisContext =
    { WorkItem: string option
      ExecutionId: string option
      Resolution: Resolution }

module PraxisContext =
    let private idPattern = Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)

    /// An identifier, or nothing: a malformed value is never recorded.
    let identifier (value: string option) = value |> Option.map (fun s -> s.Trim()) |> Option.filter idPattern.IsMatch

    /// The session variables Praxis whitelists for runtime identity, in its order of precedence.
    let sessionVariables = [ "PRAXIS_TELEMETRY_SESSION_ID"; "ROS_TELEMETRY_SESSION_ID"; "CLAUDE_CODE_SESSION_ID"; "CODEX_THREAD_ID" ]

    let private parse (text: string) : JsonElement option =
        try
            use d = JsonDocument.Parse text
            Some(d.RootElement.Clone())
        with _ -> None

    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private str name e =
        prop name e
        |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None)

    let private items name e =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
        | _ -> []

    let none = { WorkItem = None; ExecutionId = None; Resolution = NoneActive }

    /// Resolves the context from the environment and Praxis's own files (.ros/context/current.json and
    /// .ros/telemetry/executions/<id>.json, read through `read` relative to the repository root).
    let resolve (env: string -> string option) (read: string -> string option) : PraxisContext =
        let executionFile id = read (sprintf ".ros/telemetry/executions/%s.json" id) |> Option.bind parse
        match identifier (env "PRAXIS_EXECUTION_ID"), identifier (env "PRAXIS_WORK_ITEM") with
        | Some exe, workItem ->
            let item = workItem |> Option.orElse (executionFile exe |> Option.bind (str "workItemId") |> identifier)
            { WorkItem = item; ExecutionId = Some exe; Resolution = FromEnvironment }
        | None, workItemFromEnv ->
            let activeItems =
                read ".ros/context/current.json"
                |> Option.bind parse
                |> Option.map (items "workItems")
                |> Option.defaultValue []
                |> List.filter (fun w -> str "state" w = Some "active")
                |> List.filter (fun w -> workItemFromEnv |> Option.forall (fun id -> str "id" w = Some id))
            let session = sessionVariables |> List.tryPick (env >> identifier)
            let candidates =
                [ for w in activeItems do
                      for e in items "telemetryExecutionIds" w do
                          if e.ValueKind = JsonValueKind.String then
                              match identifier (Option.ofObj (e.GetString())) with
                              | Some exe ->
                                  match executionFile exe with
                                  | Some record when str "status" record = Some "active" ->
                                      let owner = prop "identity" record |> Option.bind (str "sessionId")
                                      if workItemFromEnv.IsSome || (session.IsSome && owner = session) then
                                          yield identifier (str "id" w), exe
                                  | _ -> ()
                              | None -> () ]
                |> List.distinct
            match candidates, workItemFromEnv with
            | [ item, exe ], Some _ -> { WorkItem = item; ExecutionId = Some exe; Resolution = FromEnvironment }
            | [], Some item -> { WorkItem = Some item; ExecutionId = None; Resolution = FromEnvironment }
            | _ :: _ :: _, Some item -> { WorkItem = Some item; ExecutionId = None; Resolution = Ambiguous(List.length candidates) }
            | [ item, exe ], None -> { WorkItem = item; ExecutionId = Some exe; Resolution = FromSession }
            | [], None -> none
            | _, None -> { none with Resolution = Ambiguous(List.length candidates) }

// ---- Records -----------------------------------------------------------------------------------------------------

type Outcome =
    | Succeeded
    | Failed
    /// The tool could not be started (not found, not executable).
    | NotStarted

/// One invocation: the schema `causa.tool-usage/1`, one JSON line.
type UsageRecord =
    { InvocationId: string
      Tool: string
      ToolVersion: string option
      Subcommand: string option
      ArgNames: string list
      ArgCount: int
      StartedAt: DateTimeOffset
      EndedAt: DateTimeOffset
      DurationMs: int64
      ExitCode: int
      Outcome: Outcome
      Praxis: PraxisContext
      Environment: string }

module Record =
    [<Literal>]
    let Schema = "causa.tool-usage/1"

    let timestamp (t: DateTimeOffset) =
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let outcomeText =
        function
        | Succeeded -> "succeeded"
        | Failed -> "failed"
        | NotStarted -> "not-started"

    let resolutionText =
        function
        | FromEnvironment -> "environment"
        | FromSession -> "session"
        | Ambiguous _ -> "ambiguous"
        | NoneActive -> "none"

    /// A version as the tool states it: the first `major.minor.patch[-pre][+build]` in its output, or the
    /// ReleaseVersion/version member of a JSON answer. Nothing else of the output is kept.
    let version (output: string) : string option =
        let semver = Regex(@"\b\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\b", RegexOptions.CultureInvariant)
        let fromJson =
            try
                use d = JsonDocument.Parse output
                [ "ReleaseVersion"; "releaseVersion"; "version"; "Version" ]
                |> List.tryPick (fun n ->
                    match d.RootElement.ValueKind, d.RootElement.TryGetProperty n with
                    | JsonValueKind.Object, (true, v) when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
                    | _ -> None)
            with _ -> None
        fromJson
        |> Option.orElse (let m = semver.Match output in if m.Success then Some m.Value else None)
        |> Option.bind (fun v -> let m = semver.Match v in if m.Success then Some m.Value else None)

    let outcomeOf (exitCode: int) = if exitCode = 0 then Succeeded else Failed

    /// The record as one JSON line (fixed member order, no trailing newline).
    let toJson (r: UsageRecord) : string =
        use buffer = new MemoryStream()
        do
            use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
            let optString (name: string) (v: string option) =
                match v with
                | Some s -> w.WriteString(name, s)
                | None -> w.WriteNull name
            w.WriteStartObject()
            w.WriteString("schema", Schema)
            w.WriteString("invocationId", r.InvocationId)
            w.WriteString("tool", r.Tool)
            optString "toolVersion" r.ToolVersion
            optString "subcommand" r.Subcommand
            w.WriteStartArray "argNames"
            for n in r.ArgNames do
                w.WriteStringValue n
            w.WriteEndArray()
            w.WriteNumber("argCount", r.ArgCount)
            w.WriteString("startedAt", timestamp r.StartedAt)
            w.WriteString("endedAt", timestamp r.EndedAt)
            w.WriteNumber("durationMs", r.DurationMs)
            w.WriteNumber("exitCode", r.ExitCode)
            w.WriteString("outcome", outcomeText r.Outcome)
            w.WriteStartObject "praxis"
            optString "workItem" r.Praxis.WorkItem
            optString "executionId" r.Praxis.ExecutionId
            w.WriteString("resolution", resolutionText r.Praxis.Resolution)
            match r.Praxis.Resolution with
            | Ambiguous n -> w.WriteNumber("candidates", n)
            | _ -> ()
            w.WriteEndObject()
            w.WriteString("environment", r.Environment)
            w.WriteEndObject()
        Encoding.UTF8.GetString(buffer.ToArray())

    let private date (s: string) =
        match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, t -> Ok t
        | _ -> Error(sprintf "bad timestamp '%s'" s)

    /// Parses one line; Error names what is wrong (never echoes the line).
    let ofJson (line: string) : Result<UsageRecord, string> =
        try
            use d = JsonDocument.Parse line
            let e = d.RootElement
            let get (name: string) (o: JsonElement) =
                match o.ValueKind, o.TryGetProperty name with
                | JsonValueKind.Object, (true, v) -> Some v
                | _ -> None
            let s name o = get name o |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None)
            let n name o = get name o |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.Number then Some(v.GetInt64()) else None)
            match s "schema" e, s "tool" e, s "startedAt" e, s "endedAt" e, n "durationMs" e, n "exitCode" e with
            | Some Schema, Some tool, Some started, Some ended, Some duration, Some exit ->
                match date started, date ended with
                | Ok st, Ok en ->
                    let praxis = get "praxis" e
                    let ctxStr name = praxis |> Option.bind (s name)
                    let resolution =
                        match ctxStr "resolution" with
                        | Some "environment" -> FromEnvironment
                        | Some "session" -> FromSession
                        | Some "ambiguous" -> Ambiguous(praxis |> Option.bind (n "candidates") |> Option.map int |> Option.defaultValue 0)
                        | _ -> NoneActive
                    Ok
                        { InvocationId = s "invocationId" e |> Option.defaultValue ""
                          Tool = tool
                          ToolVersion = s "toolVersion" e
                          Subcommand = s "subcommand" e
                          ArgNames =
                            match get "argNames" e with
                            | Some a when a.ValueKind = JsonValueKind.Array ->
                                a.EnumerateArray() |> Seq.choose (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None) |> List.ofSeq
                            | _ -> []
                          ArgCount = n "argCount" e |> Option.map int |> Option.defaultValue 0
                          StartedAt = st
                          EndedAt = en
                          DurationMs = duration
                          ExitCode = int exit
                          Outcome =
                            match s "outcome" e with
                            | Some "not-started" -> NotStarted
                            | _ -> outcomeOf (int exit)
                          Praxis = { WorkItem = ctxStr "workItem"; ExecutionId = ctxStr "executionId"; Resolution = resolution }
                          Environment = s "environment" e |> Option.defaultValue "local" }
                | Error m, _
                | _, Error m -> Error m
            | Some other, _, _, _, _, _ when other <> Schema -> Error(sprintf "unsupported schema '%s'" other)
            | _ -> Error "missing a required member"
        with :? JsonException -> Error "not JSON"

    /// The day file a record belongs to: `<YYYY-MM-DD>.ndjson` of its UTC start.
    let fileName (r: UsageRecord) =
        r.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".ndjson"

    /// The records of an ndjson text, with the 1-based numbers of the lines that are not records.
    let parseLines (text: string) : UsageRecord list * int list =
        let parsed =
            text.Split('\n')
            |> Array.mapi (fun i l -> i + 1, l.Trim())
            |> Array.filter (fun (_, l) -> l <> "")
            |> Array.map (fun (i, l) -> i, ofJson l)
            |> List.ofArray
        (parsed |> List.choose (fun (_, r) -> match r with Ok x -> Some x | Error _ -> None)),
        (parsed |> List.choose (fun (i, r) -> match r with Error _ -> Some i | Ok _ -> None))

    /// The existing file contents followed by the new line (append-only: earlier bytes are kept as they are).
    let appended (existing: string) (line: string) =
        if existing = "" || existing.EndsWith "\n" then existing + line + "\n" else existing + "\n" + line + "\n"

// ---- Report ------------------------------------------------------------------------------------------------------

type Grouping =
    | ByTool
    | ByDay
    | ByWorkItem

type Stats =
    { Invocations: int
      TotalDurationMs: int64
      MedianDurationMs: float
      Failures: int
      FirstUse: DateTimeOffset
      LastUse: DateTimeOffset }

type SubcommandRow = { Subcommand: string; Stats: Stats }

type ToolRow =
    { Tool: string
      Versions: string list
      Stats: Stats
      Subcommands: SubcommandRow list }

type ReportGroup = { Key: string; Tools: ToolRow list }

type UsageReport =
    { Grouping: Grouping
      Since: DateOnly option
      Records: int
      SkippedLines: int
      Groups: ReportGroup list }

module Report =
    let median (values: int64 list) : float =
        match List.sort values with
        | [] -> 0.0
        | sorted ->
            let n = List.length sorted
            if n % 2 = 1 then float (List.item (n / 2) sorted)
            else (float (List.item (n / 2 - 1) sorted) + float (List.item (n / 2) sorted)) / 2.0

    let stats (records: UsageRecord list) : Stats =
        { Invocations = List.length records
          TotalDurationMs = records |> List.sumBy (fun r -> r.DurationMs)
          MedianDurationMs = records |> List.map (fun r -> r.DurationMs) |> median
          Failures = records |> List.filter (fun r -> r.Outcome <> Succeeded) |> List.length
          FirstUse = records |> List.map (fun r -> r.StartedAt) |> List.min
          LastUse = records |> List.map (fun r -> r.StartedAt) |> List.max }

    let noSubcommand = "(none)"

    let toolRows (records: UsageRecord list) : ToolRow list =
        records
        |> List.groupBy (fun r -> r.Tool)
        |> List.sortBy fst
        |> List.map (fun (tool, rs) ->
            { Tool = tool
              Versions = rs |> List.choose (fun r -> r.ToolVersion) |> List.distinct |> List.sort
              Stats = stats rs
              Subcommands =
                rs
                |> List.groupBy (fun r -> r.Subcommand |> Option.defaultValue noSubcommand)
                |> List.sortBy fst
                |> List.map (fun (sub, srs) -> { Subcommand = sub; Stats = stats srs }) })

    let parseGrouping =
        function
        | "tool" -> Ok ByTool
        | "day" -> Ok ByDay
        | "work-item" -> Ok ByWorkItem
        | other -> Error(sprintf "unknown --by '%s' (tool, day or work-item)" other)

    let groupingText =
        function
        | ByTool -> "tool"
        | ByDay -> "day"
        | ByWorkItem -> "work-item"

    let build (grouping: Grouping) (since: DateOnly option) (skipped: int) (all: UsageRecord list) : UsageReport =
        let records =
            all
            |> List.filter (fun r -> since |> Option.forall (fun d -> DateOnly.FromDateTime(r.StartedAt.UtcDateTime) >= d))
            |> List.sortBy (fun r -> r.StartedAt, r.InvocationId)
        let key (r: UsageRecord) =
            match grouping with
            | ByTool -> "all"
            | ByDay -> r.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            | ByWorkItem -> r.Praxis.WorkItem |> Option.defaultValue "(no work item)"
        { Grouping = grouping
          Since = since
          Records = List.length records
          SkippedLines = skipped
          Groups =
            records
            |> List.groupBy key
            |> List.sortBy fst
            |> List.map (fun (k, rs) -> { Key = k; Tools = toolRows rs }) }

    let private writeStats (w: Utf8JsonWriter) (s: Stats) =
        w.WriteNumber("invocations", s.Invocations)
        w.WriteNumber("totalDurationMs", s.TotalDurationMs)
        w.WriteNumber("medianDurationMs", s.MedianDurationMs)
        w.WriteNumber("failures", s.Failures)
        w.WriteString("firstUse", Record.timestamp s.FirstUse)
        w.WriteString("lastUse", Record.timestamp s.LastUse)

    let toJson (report: UsageReport) : string =
        use buffer = new MemoryStream()
        do
            use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
            w.WriteStartObject()
            w.WriteString("schema", "causa.tool-usage-report/1")
            w.WriteString("by", groupingText report.Grouping)
            match report.Since with
            | Some d -> w.WriteString("since", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            | None -> w.WriteNull "since"
            w.WriteNumber("records", report.Records)
            w.WriteNumber("skippedLines", report.SkippedLines)
            w.WriteStartArray "groups"
            for g in report.Groups do
                w.WriteStartObject()
                w.WriteString("key", g.Key)
                w.WriteStartArray "tools"
                for t in g.Tools do
                    w.WriteStartObject()
                    w.WriteString("tool", t.Tool)
                    w.WriteStartArray "versions"
                    for v in t.Versions do
                        w.WriteStringValue v
                    w.WriteEndArray()
                    writeStats w t.Stats
                    w.WriteStartArray "subcommands"
                    for s in t.Subcommands do
                        w.WriteStartObject()
                        w.WriteString("subcommand", s.Subcommand)
                        writeStats w s.Stats
                        w.WriteEndObject()
                    w.WriteEndArray()
                    w.WriteEndObject()
                w.WriteEndArray()
                w.WriteEndObject()
            w.WriteEndArray()
            w.WriteEndObject()
        Encoding.UTF8.GetString(buffer.ToArray()) + "\n"

    let toText (report: UsageReport) : string =
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore
        let day (t: DateTimeOffset) = t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        let row (name: string) (s: Stats) =
            line (
                String.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-34} {1,6} {2,10} {3,10:0.0} {4,6}  {5}  {6}",
                    name, s.Invocations, s.TotalDurationMs, s.MedianDurationMs, s.Failures, day s.FirstUse, day s.LastUse
                )
            )
        line (
            sprintf "Tool usage by %s: %d invocation(s)%s%s" (groupingText report.Grouping) report.Records
                (match report.Since with Some d -> " since " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) | None -> "")
                (if report.SkippedLines > 0 then sprintf " (%d unreadable line(s) skipped)" report.SkippedLines else "")
        )
        for g in report.Groups do
            line ""
            if report.Grouping <> ByTool then line (sprintf "== %s" g.Key)
            line (String.Format(CultureInfo.InvariantCulture, "{0,-34} {1,6} {2,10} {3,10} {4,6}  {5,-16}  {6}", "tool / subcommand", "count", "total ms", "median ms", "failed", "first use (UTC)", "last use (UTC)"))
            for t in g.Tools do
                let versions = match t.Versions with [] -> "" | vs -> " " + String.Join(",", vs)
                row (t.Tool + versions) t.Stats
                for s in t.Subcommands do
                    row ("  " + s.Subcommand) s.Stats
        sb.ToString()

// ---- Inventory ---------------------------------------------------------------------------------------------------

/// The repository as read-only values: every file path (repository-relative, `/`-separated, sorted) and a reader.
type RepositoryView =
    { Files: string list
      Read: string -> string option }

module Inventory =
    let private count (pattern: string) (text: string) = Regex.Matches(text, pattern).Count

    let private matches (pattern: string) (group: int) (text: string) =
        Regex.Matches(text, pattern) |> Seq.map (fun m -> m.Groups[group].Value) |> List.ofSeq

    let private under (prefix: string) (files: string list) = files |> List.filter (fun f -> f.StartsWith prefix)

    let private withExt (exts: string list) (files: string list) =
        files |> List.filter (fun f -> exts |> List.exists (fun e -> f.EndsWith(e, StringComparison.Ordinal)))

    let private tally (items: string list) = items |> List.countBy id |> List.sortBy fst

    /// The projects that ship: the host project and every project it references, transitively.
    let shippedProjects (view: RepositoryView) (host: string) : string list =
        let refs (project: string) =
            let dir = match Path.GetDirectoryName project with | null -> "" | d -> d.Replace('\\', '/')
            view.Read project
            |> Option.map (matches "<ProjectReference\\s+Include=\"([^\"]+)\"" 1)
            |> Option.defaultValue []
            |> List.map (fun r -> Path.GetRelativePath(".", Path.Combine(dir, r.Replace('\\', '/'))).Replace('\\', '/'))
        let rec go (seen: Set<string>) =
            function
            | [] -> seen
            | p :: rest when seen.Contains p -> go seen rest
            | p :: rest -> go (seen.Add p) (refs p @ rest)
        go Set.empty [ host ] |> Set.toList

    let private projectDir (project: string) =
        match Path.GetDirectoryName project with
        | null -> ""
        | d -> d.Replace('\\', '/') + "/"

    /// The shipped source the inventory reads: web/** and the F# sources of the shipped projects.
    let shippedSources (view: RepositoryView) (projects: string list) =
        let code =
            projects
            |> List.collect (fun p -> view.Files |> under (projectDir p) |> withExt [ ".fs"; ".cs" ])
            |> List.filter (fun f -> not (f.Contains "/obj/" || f.Contains "/bin/"))
        (view.Files |> under "web/") @ code |> List.distinct |> List.sort

    let private lockedVersion (view: RepositoryView) (package: string) : string option =
        view.Read "package-lock.json"
        |> Option.bind (fun text ->
            try
                use d = JsonDocument.Parse text
                match d.RootElement.TryGetProperty "packages" with
                | true, p ->
                    match p.TryGetProperty("node_modules/" + package) with
                    | true, e ->
                        match e.TryGetProperty "version" with
                        | true, v when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
                        | _ -> None
                    | _ -> None
                | _ -> None
            with _ -> None)

    let private importPattern =
        "(?:import|export)\\s[^;]*?from\\s*[\"']([^\"']+)[\"']|import\\s*[\"']([^\"']+)[\"']"

    let private specifiers (text: string) =
        Regex.Matches(text, importPattern)
        |> Seq.map (fun m -> if m.Groups[1].Success then m.Groups[1].Value else m.Groups[2].Value)
        |> List.ofSeq

    let private namedImports (package: string) (text: string) =
        Regex.Matches(text, "import\\s*\\{([^}]*)\\}\\s*from\\s*[\"']" + Regex.Escape package + "(?:/[^\"']*)?[\"']")
        |> Seq.collect (fun m -> m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
        |> Seq.map (fun s -> (s.Split(" as ")[0]).Trim())
        |> Seq.filter ((<>) "")
        |> List.ofSeq

    /// The modules of a package's dist reached from an entry by static relative imports.
    let moduleClosure (view: RepositoryView) (distRoot: string) (entry: string) : string list =
        let rec go (seen: Set<string>) =
            function
            | [] -> seen
            | m :: rest when seen.Contains m -> go seen rest
            | (m: string) :: rest ->
                match view.Read(distRoot + m) with
                | None -> go seen rest
                | Some text ->
                    let dir = match Path.GetDirectoryName m with | null -> "" | d -> d.Replace('\\', '/')
                    let next =
                        specifiers text
                        |> List.filter (fun s -> s.StartsWith "./" || s.StartsWith "../")
                        |> List.map (fun s -> Path.GetRelativePath(".", Path.Combine(dir, s)).Replace('\\', '/'))
                    go (seen.Add m) (next @ rest)
        go Set.empty [ entry ] |> Set.toList

    type Section = (string * JsonValue) list

    and JsonValue =
        | JStr of string
        | JNum of int
        | JBool of bool
        | JNull
        | JArr of JsonValue list
        | JObj of Section

    let rec private write (w: Utf8JsonWriter) =
        function
        | JStr s -> w.WriteStringValue s
        | JNum n -> w.WriteNumberValue n
        | JBool b -> w.WriteBooleanValue b
        | JNull -> w.WriteNullValue()
        | JArr xs ->
            w.WriteStartArray()
            xs |> List.iter (write w)
            w.WriteEndArray()
        | JObj members ->
            w.WriteStartObject()
            for (k, v) in members do
                w.WritePropertyName k
                write w v
            w.WriteEndObject()

    let render (value: JsonValue) =
        use buffer = new MemoryStream()
        do
            use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
            write w value
        Encoding.UTF8.GetString(buffer.ToArray()) + "\n"

    let private strs (xs: string list) = JArr(xs |> List.map JStr)
    let private counts (xs: (string * int) list) = JArr(xs |> List.map (fun (k, n) -> JObj [ "name", JStr k; "uses", JNum n ]))
    let private optStr = function Some s -> JStr s | None -> JNull

    let private unavailable (reason: string) = JObj [ "status", JStr "unavailable"; "reason", JStr reason ]

    let private package (prefix: string) = "node_modules/@echelon-foundry/" + prefix

    let private texts (view: RepositoryView) (files: string list) =
        files |> List.choose (fun f -> view.Read f |> Option.map (fun t -> f, t))

    let limen (view: RepositoryView) (shipped: string list) : JsonValue =
        let scripts = shipped |> withExt [ ".js"; ".mjs" ] |> texts view
        let scaffold = view.Files |> under "src/kernel/" |> withExt [ ".ts"; ".js" ] |> texts view
        let importsOf (files: (string * string) list) =
            files
            |> List.collect (fun (f, t) ->
                specifiers t
                |> List.filter (fun s -> s = "@echelon-foundry/limen" || s.StartsWith "@echelon-foundry/limen/")
                |> List.distinct
                |> List.map (fun s ->
                    JObj [ "from", JStr f; "specifier", JStr s; "names", strs (namedImports s t |> List.distinct |> List.sort) ]))
        let dist = package "limen/dist/"
        let modules = view.Files |> under dist |> withExt [ ".js" ] |> List.map (fun f -> f.Substring dist.Length)
        let reached =
            if List.isEmpty modules then None
            else
                let entries =
                    scripts
                    |> List.collect (fun (_, t) -> specifiers t)
                    |> List.filter (fun s -> s = "@echelon-foundry/limen" || s.StartsWith "@echelon-foundry/limen/")
                    |> List.distinct
                    |> List.map (fun s -> if s = "@echelon-foundry/limen" then "index.js" else s.Substring("@echelon-foundry/limen/".Length) + ".js")
                Some(entries |> List.collect (moduleClosure view dist) |> List.distinct |> List.sort)
        let generated (xs: string list) = xs |> List.filter (fun m -> m.StartsWith "generated/")
        JObj
            [ "id", JStr "limen"
              "package", JStr "@echelon-foundry/limen"
              "lockedVersion", optStr (lockedVersion view "@echelon-foundry/limen")
              "runtimeUsage", JStr(if List.isEmpty (importsOf scripts) then "none" else "used")
              "shippedImports", JArr(importsOf scripts)
              "scaffoldImports", JArr(importsOf scaffold)
              "shippedModules",
              (match reached with
               | None -> unavailable "node_modules/@echelon-foundry/limen/dist is not installed (run npm ci)"
               | Some r ->
                   JObj
                       [ "note", JStr "Modules of the package's dist the page loads: the static-import closure of the shipped imports. build.fsx vendors the whole dist except capability packs into dist/vendor/limen; only these modules are imported."
                         "reached", JNum(List.length r)
                         "total", JNum(List.length modules)
                         "generatedReached", JNum(List.length (generated r))
                         "generatedTotal", JNum(List.length (generated modules))
                         "modules", strs r ])
              "engineBoundary",
              (let boundary = "src/Causa.App/Program.fs"
               match view.Read boundary with
               | None -> unavailable (boundary + " is missing")
               | Some t ->
                   let emitted = matches "WriteString\\(\"kind\",\\s*\"([A-Za-z]+)\"" 1 t |> tally
                   let recognised = matches "\\|\\s*Some\\s+\"([A-Z][A-Za-z]+)\"" 1 t |> List.distinct |> List.sort
                   JObj
                       [ "file", JStr boundary
                         "note", JStr "The engine side of the Limen protocol (Limen 0.9.1): the effect kinds it writes and the message/result kinds it matches."
                         "effectKindsEmitted", counts emitted
                         "kindsRecognised", strs recognised ]) ]

    let forma (view: RepositoryView) (shipped: string list) : JsonValue =
        let sources = shipped |> withExt [ ".html"; ".css"; ".js"; ".fs" ] |> texts view
        let webCss = shipped |> under "web/" |> withExt [ ".css" ] |> texts view
        let tokensUsed = sources |> List.collect (fun (_, t) -> matches "var\\(\\s*(--ef-[a-z0-9-]+)" 1 t) |> tally
        let tokensDeclared = webCss |> List.collect (fun (_, t) -> matches "(--ef-[a-z0-9-]+)\\s*:" 1 t) |> List.distinct |> List.sort
        let classAttrs =
            sources
            |> List.collect (fun (_, t) -> matches "class(?:Name)?=\\\\?\"([^\"\\\\]*)" 1 t)
            |> List.collect (fun c -> c.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
        let cssSelectors = webCss |> List.collect (fun (_, t) -> matches "\\.(ef-[a-z0-9_-]+)" 1 t)
        let classesUsed = (classAttrs @ cssSelectors) |> List.filter (fun c -> c.StartsWith "ef-") |> tally
        let dist = package "design-system/dist/"
        let packageCss = view.Files |> under dist |> withExt [ ".css" ] |> texts view
        let defined pattern = packageCss |> List.collect (fun (_, t) -> matches pattern 1 t) |> Set.ofList
        let definedTokens = defined "(--ef-[a-z0-9-]+)\\s*:"
        let definedClasses = defined "\\.(ef-[a-z0-9_-]+)"
        let workflowRefs =
            sources |> List.filter (fun (_, t) -> t.Contains "forma-workflow") |> List.map fst
        JObj
            [ "id", JStr "forma"
              "package", JStr "@echelon-foundry/design-system"
              "lockedVersion", optStr (lockedVersion view "@echelon-foundry/design-system")
              "runtimeUsage", JStr(if List.isEmpty tokensUsed && List.isEmpty classesUsed then "none" else "used")
              "tokens",
              JObj
                  [ "distinctUsed", JNum(List.length tokensUsed)
                    "references", JNum(tokensUsed |> List.sumBy snd)
                    "definedByPackage", (if packageCss.IsEmpty then unavailable "the design-system dist is not installed (run npm ci)" else JNum definedTokens.Count)
                    "usedButNotDefinedByPackage", (if packageCss.IsEmpty then JNull else strs (tokensUsed |> List.map fst |> List.filter (definedTokens.Contains >> not)))
                    "declaredByApplication", strs tokensDeclared
                    "used", counts tokensUsed ]
              "classes",
              JObj
                  [ "distinctUsed", JNum(List.length classesUsed)
                    "references", JNum(classesUsed |> List.sumBy snd)
                    "definedByPackage", (if packageCss.IsEmpty then unavailable "the design-system dist is not installed (run npm ci)" else JNum definedClasses.Count)
                    "usedButNotDefinedByPackage", (if packageCss.IsEmpty then JNull else strs (classesUsed |> List.map fst |> List.filter (definedClasses.Contains >> not)))
                    "used", counts classesUsed ]
              "formaWorkflow",
              JObj
                  [ "package", JStr "@echelon-foundry/forma-workflow"
                    "lockedVersion", optStr (lockedVersion view "@echelon-foundry/forma-workflow")
                    "shippedReferences", strs workflowRefs ] ]

    let folio (view: RepositoryView) (shipped: string list) : JsonValue =
        let register = view.Read(package "print-components/src/components/register.js")
        let elements = register |> Option.map (matches "\"(ef-print-[a-z-]+)\"" 1 >> List.distinct >> List.sort)
        let scan files =
            let ts = files |> texts view
            elements
            |> Option.defaultValue []
            |> List.map (fun e -> e, ts |> List.sumBy (fun (_, t) -> count ("<" + Regex.Escape e + "[\\s>/]") t + count ("\"" + Regex.Escape e + "\"") t))
            |> List.filter (snd >> (<) 0)
        let packageRefs files =
            files |> texts view |> List.filter (fun (_, t) -> t.Contains "print-components") |> List.map fst
        let shippedUse = scan (shipped |> withExt [ ".html"; ".js"; ".fs" ])
        let scaffold = view.Files |> under "src/kernel/"
        JObj
            [ "id", JStr "folio"
              "package", JStr "@echelon-foundry/print-components"
              "lockedVersion", optStr (lockedVersion view "@echelon-foundry/print-components")
              "runtimeUsage", JStr(if List.isEmpty shippedUse && List.isEmpty (packageRefs shipped) then "none" else "used")
              "componentsDefinedByPackage", (match elements with Some es -> JNum(List.length es) | None -> unavailable "print-components is not installed (run npm ci)")
              "shippedComponentUses", counts shippedUse
              "shippedPackageReferences", strs (packageRefs shipped)
              "scaffoldComponentUses", counts (scan scaffold)
              "scaffoldPackageReferences", strs (packageRefs scaffold) ]

    let private member' (name: string) (e: JsonElement) : JsonElement option =
        if e.ValueKind = JsonValueKind.Object then
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        else None

    let private text (e: JsonElement) : string option =
        if e.ValueKind = JsonValueKind.String then Option.ofObj (e.GetString()) else None

    let private array (e: JsonElement) : JsonElement list =
        if e.ValueKind = JsonValueKind.Array then e.EnumerateArray() |> List.ofSeq else []

    let percepta (view: RepositoryView) (shipped: string list) : JsonValue =
        let contracts = view.Files |> under ".percepta/contracts/" |> withExt [ ".json" ]
        let parsed =
            contracts
            |> List.choose (fun f ->
                view.Read f
                |> Option.bind (fun t ->
                    try
                        use d = JsonDocument.Parse t
                        let root = d.RootElement
                        let screen = member' "screen" root |> Option.bind text |> Option.defaultValue f
                        let elements name = member' name root |> Option.map array |> Option.defaultValue []
                        let ids name = elements name |> List.choose (member' "id" >> Option.bind text)
                        let caps = elements "capabilities" |> List.choose (member' "capability" >> Option.bind text) |> List.distinct
                        Some(screen, ids "regions", caps)
                    with _ -> None))
        let sources = shipped |> withExt [ ".html"; ".js"; ".fs" ] |> texts view
        let hooks = sources |> List.collect (fun (_, t) -> matches "(data-percepta-[a-z-]+)" 1 t) |> tally
        let hookValues attr =
            sources |> List.collect (fun (_, t) -> matches (Regex.Escape attr + "=\\\\?\"([^\"\\\\]+)") 1 t) |> Set.ofList
        let regionsHooked = hookValues "data-percepta-region"
        let capsHooked = hookValues "data-percepta-capability"
        JObj
            [ "id", JStr "percepta"
              "note", JStr "Percepta is a verification tool: the shipped app references it only through data-percepta-* hooks that rendered verification reads."
              "runtimeUsage", JStr(if List.isEmpty hooks then "none" else "used")
              "contracts", JNum(List.length parsed)
              "regionsDeclared", JNum(parsed |> List.sumBy (fun (_, r, _) -> List.length r))
              "capabilitiesDeclared", JNum(parsed |> List.collect (fun (_, _, c) -> c) |> List.distinct |> List.length)
              "shippedHooks", counts hooks
              "perContract",
              JArr(
                  parsed
                  |> List.sortBy (fun (s, _, _) -> s)
                  |> List.map (fun (screen, regions, caps) ->
                      JObj
                          [ "screen", JStr screen
                            "regions", JNum(List.length regions)
                            "regionsWithShippedHook", JNum(regions |> List.filter regionsHooked.Contains |> List.length)
                            "capabilities", JNum(List.length caps)
                            "capabilitiesWithShippedHook", JNum(caps |> List.filter capsHooked.Contains |> List.length) ])
              ) ]

    let aegis (view: RepositoryView) (shippedProjects: string list) : JsonValue =
        let projects = view.Files |> withExt [ ".fsproj"; ".csproj" ] |> List.filter (fun f -> not (f.StartsWith "node_modules/"))
        let referencing =
            projects
            |> List.choose (fun p ->
                view.Read p
                |> Option.bind (fun t ->
                    let m = Regex.Match(t, "<PackageReference\\s+Include=\"(EchelonFoundry\\.Aegis[^\"]*)\"(?:\\s+Version=\"([^\"]*)\")?")
                    if m.Success then Some(p, m.Groups[1].Value, (if m.Groups[2].Success then m.Groups[2].Value else "")) else None))
        let apiUses (project: string) =
            view.Files
            |> under (projectDir project)
            |> withExt [ ".fs"; ".cs" ]
            |> List.filter (fun f -> not (f.Contains "/obj/" || f.Contains "/bin/"))
            |> texts view
            |> List.collect (fun (_, t) -> matches "\\b((?:Aegis|Sinks)\\.[A-Za-z_][A-Za-z0-9_]*)" 1 t)
            |> tally
        let shippedRefs = referencing |> List.filter (fun (p, _, _) -> List.contains p shippedProjects)
        JObj
            [ "id", JStr "aegis"
              "runtimeUsage", JStr(if List.isEmpty shippedRefs then "none" else "used")
              "note", JStr "A NuGet library; shipped means referenced by a project the deployed host (src/Causa.Host) builds."
              "boundaryConfiguration", JBool(view.Files |> List.contains "aegis-boundaries.json")
              "projects",
              JArr(
                  referencing
                  |> List.map (fun (p, pkg, v) ->
                      JObj
                          [ "project", JStr p
                            "package", JStr pkg
                            "version", JStr v
                            "shipped", JBool(List.contains p shippedProjects)
                            "apiUses", counts (apiUses p) ])
              ) ]

    let ordo (view: RepositoryView) (shipped: string list) (shippedProjects: string list) : JsonValue =
        let mentions =
            shipped |> texts view |> List.filter (fun (_, t) -> Regex.IsMatch(t, "@echelon-foundry/sde|\\bEchelonFoundry\\.Ordo|\\bimport\\b[^;]*\\bordo\\b")) |> List.map fst
        let packageDep = view.Read "package.json" |> Option.exists (fun t -> t.Contains "@echelon-foundry/sde")
        let projectDep =
            shippedProjects |> List.exists (fun p -> view.Read p |> Option.exists (fun t -> Regex.IsMatch(t, "Ordo|Sde", RegexOptions.IgnoreCase) && t.Contains "PackageReference"))
        JObj
            [ "id", JStr "ordo"
              "runtimeUsage", JStr(if mentions.IsEmpty && not packageDep && not projectDep then "none" else "used")
              "statement",
              JStr
                  "Ordo (the State-Directed Engineering CLI `sde`, pinned 1.20.0) is a development-time process tool. The shipped application has no Ordo package, import or project reference, so it has no runtime usage to inventory; its use is measured only by the tool-usage records (`usage.fsx -- report`)."
              "packageDependency", JBool packageDep
              "projectReference", JBool projectDep
              "shippedReferences", strs mentions ]

    /// The whole inventory: deterministic for a given tree (sorted, no clock, no absolute path).
    let build (view: RepositoryView) : string =
        let host = "src/Causa.Host/Causa.Host.csproj"
        let projects = shippedProjects view host
        let shipped = shippedSources view projects
        JObj
            [ "schema", JStr "causa.runtime-library-inventory/1"
              "description", JStr "Static inventory of how much of each Echelon runtime library the shipped Causa application references. Generated by `dotnet fsi tools/usage/usage.fsx -- inventory`; see docs/tool-usage.md."
              "scope",
              JObj
                  [ "host", JStr host
                    "shippedProjects", strs projects
                    "shippedFiles", JNum(List.length shipped)
                    "notShipped", strs [ "src/engine (Conditor scaffold, App.slnx)"; "src/kernel (Conditor scaffold, App.slnx)" ] ]
              "libraries", JArr [ limen view shipped; forma view shipped; folio view shipped; percepta view shipped; aegis view projects; ordo view shipped projects ] ]
        |> render
