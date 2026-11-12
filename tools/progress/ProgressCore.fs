// Owner: integration. The pure core of the progress deriver (tools/progress/progress-from-tests.fsx). Shared by the
// script (#load, after tools/shared/PythonCompat.fs) and Causa.App.Tests (ProgressFromTestsTests). Everything here is a pure function over values: file
// contents, git answers and the clock come in as arguments, and the script performs the side effects.
//
// Derives implementation progress events (indy-init.progress-event/1) from test results that actually ran.
//
// A work item is "started" when work from its slice branch (slice/SL-<id>, or slice/SL-<id>-*) is in the measured
// commit. A requirement is "implementation-completed" when every one of its obligations that can be tested locally
// (all categories except live-provider, independent-acceptance, process-record and owner-decision) has at least one
// passing test and no failing test, and it has at least one such obligation. Obligations are matched to tests by the
// obligation identity in the test name (e.g. "UI-029-O1") or by the plan's test-to-obligation mapping
// (.echelon/obligations.json). This is implementer-run evidence: it never records verification or integration, which
// only the governed Verification gate may do.
module Causa.Tools.Progress

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq
open Causa.Tools.PythonCompat

// ---- Model -------------------------------------------------------------------------------------------------------

/// One work item of .echelon/r9-work-plan.json.
type WorkItem = { Id: string; Requirements: string list }

/// One obligation of .echelon/obligations.json.
type Obligation = { Id: string; Requirement: string; Categories: Set<string> }

/// .echelon/obligations.json: the test-to-obligation mapping (later duplicate test ids win) and the obligations.
type ObligationPlan = { TestObligations: Map<string, string list>; Obligations: Obligation list }

/// A progress event already in evidence/progress/events.json, as far as the deriver reads it.
type RecordedEvent =
    | WasStarted of workItem: string
    | WasCompleted of requirements: string list
    | OtherEvent of kind: string

/// A new indy-init.progress-event/1 event.
type ProgressEvent =
    | ImplementationStarted of workItem: WorkItem * at: string
    | ImplementationCompleted of requirements: string list * commit: string * at: string

/// Everything the derivation reads. Results: test identity ("dotnet:..." or "playwright:...") -> passed.
type Inputs =
    { Plan: WorkItem list
      Obligations: ObligationPlan
      Existing: RecordedEvent list
      Results: Map<string, bool> }

/// What the derivation decides, before the commit and the clock are attached.
type Derivation =
    { NewlyStarted: WorkItem list
      NewlyCompleted: string list }

// ---- JSON reading ------------------------------------------------------------------------------------------------

module private Read =
    let parse (what: string) (text: string) : Result<JsonElement, string> =
        try
            use d = JsonDocument.Parse(text, JsonDocumentOptions(AllowTrailingCommas = false))
            Ok(d.RootElement.Clone())
        with ex -> Error(sprintf "%s is not valid JSON: %s" what ex.Message)

    let prop (name: string) (e: JsonElement) : JsonElement option =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let required (what: string) (name: string) (e: JsonElement) : Result<JsonElement, string> =
        prop name e |> Option.map Ok |> Option.defaultValue (Error(sprintf "%s has no '%s'" what name))

    let str (what: string) (e: JsonElement) : Result<string, string> =
        match e.ValueKind with
        | JsonValueKind.String -> Ok(e.GetString() |> string)
        | k -> Error(sprintf "%s is %A, not a string" what k)

    let array (what: string) (e: JsonElement) : Result<JsonElement list, string> =
        match e.ValueKind with
        | JsonValueKind.Array -> Ok(e.EnumerateArray() |> List.ofSeq)
        | k -> Error(sprintf "%s is %A, not an array" what k)

    /// Result.traverse over a list, keeping order and stopping at the first error.
    let all (f: 'a -> Result<'b, string>) (xs: 'a list) : Result<'b list, string> =
        List.foldBack (fun x acc -> Result.bind (fun rest -> f x |> Result.map (fun v -> v :: rest)) acc) xs (Ok [])

    let strings (what: string) (e: JsonElement) = array what e |> Result.bind (all (str what))

    /// An optional array of strings (Python's `x.get(name, [])`).
    let stringsOr (what: string) (name: string) (e: JsonElement) =
        match prop name e with
        | None -> Ok []
        | Some v -> strings what v

    let requiredString what name e = required what name e |> Result.bind (str (what + "." + name))
    let requiredStrings what name e = required what name e |> Result.bind (strings (what + "." + name))

// ---- Inputs ------------------------------------------------------------------------------------------------------

module Inputs =
    /// .echelon/r9-work-plan.json -> its work items, in plan order.
    let plan (text: string) : Result<WorkItem list, string> =
        Read.parse "the work plan" text
        |> Result.bind (Read.required "the work plan" "workItems")
        |> Result.bind (Read.array "workItems")
        |> Result.bind (
            Read.all (fun w ->
                Read.requiredString "work item" "id" w
                |> Result.bind (fun id ->
                    Read.requiredStrings "work item" "requirements" w |> Result.map (fun rs -> { Id = id; Requirements = rs })))
        )

    /// .echelon/obligations.json -> the test mapping and the obligations.
    let obligations (text: string) : Result<ObligationPlan, string> =
        Read.parse "the obligations" text
        |> Result.bind (fun root ->
            let tests =
                Read.required "the obligations" "tests" root
                |> Result.bind (Read.array "tests")
                |> Result.bind (
                    Read.all (fun t ->
                        Read.requiredString "test" "id" t
                        |> Result.bind (fun id -> Read.stringsOr "test.obligations" "obligations" t |> Result.map (fun os -> id, os)))
                )
            let obligations =
                Read.required "the obligations" "obligations" root
                |> Result.bind (Read.array "obligations")
                |> Result.bind (
                    Read.all (fun o ->
                        Read.requiredString "obligation" "id" o
                        |> Result.bind (fun id ->
                            Read.requiredStrings "obligation" "categories" o
                            |> Result.bind (fun cs ->
                                Read.requiredString "obligation" "requirement" o
                                |> Result.map (fun r -> { Id = id; Requirement = r; Categories = Set.ofList cs }))))
                )
            match tests, obligations with
            | Ok ts, Ok os -> Ok { TestObligations = Map.ofList ts; Obligations = os }
            | Error e, _
            | _, Error e -> Error e)

    /// evidence/progress/events.json -> the events the deriver reads.
    let events (text: string) : Result<RecordedEvent list, string> =
        Read.parse "the progress events" text
        |> Result.bind (Read.array "the progress events")
        |> Result.bind (
            Read.all (fun e ->
                Read.requiredString "event" "kind" e
                |> Result.bind (function
                    | "implementation-started" -> Read.requiredString "event" "workItem" e |> Result.map WasStarted
                    | "implementation-completed" -> Read.requiredStrings "event" "requirements" e |> Result.map WasCompleted
                    | other -> Ok(OtherEvent other)))
        )

// ---- Test results ------------------------------------------------------------------------------------------------

module Results =
    let private teamTest = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

    /// One .trx file -> (dotnet:<testName>, passed) in document order. A result with no testName is named "None",
    /// as the original Python deriver named it.
    let trx (text: string) : Result<(string * bool) list, string> =
        try
            XDocument.Parse(text).Descendants(XName.Get("UnitTestResult", teamTest))
            |> Seq.map (fun u ->
                let attr (n: string) = u.Attribute(XName.Get n) |> Option.ofObj |> Option.map (fun a -> a.Value)
                "dotnet:" + (attr "testName" |> Option.defaultValue "None"), attr "outcome" = Some "Passed")
            |> List.ofSeq
            |> Ok
        with ex -> Error(sprintf "a .trx file is not valid XML: %s" ex.Message)

    /// A Playwright JSON report -> (playwright:<spec title>, passed) in report order. A spec passes when every one of
    /// its tests has status "expected" (a spec with no tests passes).
    let playwright (text: string) : Result<(string * bool) list, string> =
        let arrayOr name e =
            match Read.prop name e with
            | None -> Ok []
            | Some v -> Read.array name v
        let expected (t: JsonElement) =
            match Read.prop "status" t with
            | Some s when s.ValueKind = JsonValueKind.String -> s.GetString() = "expected"
            | _ -> false
        let rec walk (suite: JsonElement) : Result<(string * bool) list, string> =
            let here =
                arrayOr "specs" suite
                |> Result.bind (
                    Read.all (fun spec ->
                        Read.requiredString "spec" "title" spec
                        |> Result.bind (fun title ->
                            arrayOr "tests" spec |> Result.map (fun ts -> "playwright:" + title, List.forall expected ts)))
                )
            let nested = arrayOr "suites" suite |> Result.bind (Read.all walk) |> Result.map List.concat
            match here, nested with
            | Ok h, Ok n -> Ok(h @ n)
            | Error e, _
            | _, Error e -> Error e
        Read.parse "a Playwright report" text
        |> Result.bind (arrayOr "suites")
        |> Result.bind (Read.all walk)
        |> Result.map List.concat

    /// Merges result lists in order: a later outcome for the same test wins.
    let merge (lists: (string * bool) list list) : Map<string, bool> =
        lists |> List.concat |> List.fold (fun m (name, passed) -> Map.add name passed m) Map.empty

// ---- Slice branches ----------------------------------------------------------------------------------------------

module Branches =
    /// The bootstrap commit: history before it belongs to no slice.
    let bootstrap = "c7068166e78fe9c21e6aed5e392242f779b27e7f"

    /// Splits the origin/slice/* branches into this work item's own (origin/slice/SL-<id> or origin/slice/SL-<id>-*)
    /// and every other, both in the given order.
    let split (workItem: string) (branches: string list) : string list * string list =
        let own = sprintf "origin/slice/SL-%s" workItem
        branches |> List.partition (fun b -> b = own || b.StartsWith(own + "-", StringComparison.Ordinal))

    /// The `git rev-list` arguments that list commits belonging to `mine` and to no other slice branch.
    let ownCommitsArguments (mine: string list) (others: string list) : string list =
        [ "rev-list" ] @ mine @ [ "^" + bootstrap; "--not" ] @ others

// ---- Derivation --------------------------------------------------------------------------------------------------

module Derive =
    /// Categories whose obligations cannot be evidenced by a local test run.
    let untestable = set [ "live-provider"; "independent-acceptance"; "process-record"; "owner-decision" ]

    // \b as Python's re defines it (PythonCompat.Re.b); .NET's \d is Python's (Unicode decimal digits).
    let private obligationIdentity =
        Regex(Re.b + @"([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{3}-O\d+)" + Re.b, RegexOptions.CultureInvariant)

    /// The obligations a test evidences: identities named in the test name, plus the plan's mapping for the name
    /// (with and without the "dotnet:" prefix).
    let obligationsOfTest (mapping: Map<string, string list>) (name: string) : Set<string> =
        let named = obligationIdentity.Matches name |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
        let lookup k = Map.tryFind k mapping |> Option.defaultValue [] |> Set.ofList
        Set.unionMany [ named; lookup name; lookup (name.Replace("dotnet:", "")) ]

    /// Obligation -> the outcomes of every test that evidences it.
    let coverage (mapping: Map<string, string list>) (results: Map<string, bool>) : Map<string, bool list> =
        results
        |> Map.fold
            (fun acc name passed ->
                obligationsOfTest mapping name
                |> Set.fold (fun m o -> Map.add o (passed :: (Map.tryFind o m |> Option.defaultValue [])) m) acc)
            Map.empty

    /// True when the requirement has at least one locally testable obligation and each has a passing test and no
    /// failing test.
    let evidenced (obligations: Obligation list) (coverage: Map<string, bool list>) (requirement: string) : bool =
        let categories = obligations |> List.fold (fun m o -> Map.add o.Id o.Categories m) Map.empty
        let testable =
            obligations
            |> List.filter (fun o -> o.Requirement = requirement)
            |> List.map (fun o -> o.Id)
            |> List.filter (fun id -> Map.tryFind id categories |> Option.defaultValue Set.empty |> Set.intersect untestable |> Set.isEmpty)
        let covered obligation =
            match Map.tryFind obligation coverage with
            | Some(_ :: _ as outcomes) -> List.forall id outcomes
            | _ -> false
        not testable.IsEmpty && List.forall covered testable

    /// The new progress: work items on main not yet started, and started requirements newly evidenced (sorted).
    /// `onMain` holds the ids of the work items whose slice work is in the measured commit.
    let progress (inputs: Inputs) (onMain: Set<string>) : Derivation =
        let startedBefore = inputs.Existing |> List.choose (function WasStarted w -> Some w | _ -> None) |> Set.ofList
        let completedBefore = inputs.Existing |> List.collect (function WasCompleted rs -> rs | _ -> []) |> Set.ofList
        let newlyStarted =
            inputs.Plan |> List.filter (fun w -> onMain.Contains w.Id && not (startedBefore.Contains w.Id))
        let startedIds = newlyStarted |> List.map (fun w -> w.Id) |> Set.ofList |> Set.union startedBefore
        let startedRequirements =
            inputs.Plan |> List.filter (fun w -> startedIds.Contains w.Id) |> List.collect (fun w -> w.Requirements) |> Set.ofList
        let cover = coverage inputs.Obligations.TestObligations inputs.Results
        let completed =
            Set.difference startedRequirements completedBefore
            |> Set.filter (evidenced inputs.Obligations.Obligations cover)
            |> Set.toList
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        { NewlyStarted = newlyStarted; NewlyCompleted = completed }

    /// The events to append. `commit` is asked for only when there is a completion to record.
    let events (at: string) (commit: unit -> string) (d: Derivation) : ProgressEvent list =
        (d.NewlyStarted |> List.map (fun w -> ImplementationStarted(w, at)))
        @ (if d.NewlyCompleted.IsEmpty then [] else [ ImplementationCompleted(d.NewlyCompleted, commit (), at) ])

// ---- Output ------------------------------------------------------------------------------------------------------

module Output =
    let private strings xs = xs |> List.map JString |> JArray

    let eventJson (e: ProgressEvent) : Json =
        match e with
        | ImplementationStarted(w, at) ->
            JObject [ "kind", JString "implementation-started"; "workItem", JString w.Id; "requirements", strings w.Requirements; "at", JString at ]
        | ImplementationCompleted(rs, commit, at) ->
            JObject [ "kind", JString "implementation-completed"; "requirements", strings rs; "commit", JString commit; "at", JString at ]

    /// The events as the deriver prints them on stdout (without the trailing newline).
    let events (es: ProgressEvent list) : string = es |> List.map eventJson |> JArray |> Json.dumps 2

    /// Python's repr of a string (the quoting the summary line has always used).
    let private repr (s: string) =
        let quoteChar = if s.Contains '\'' && not (s.Contains '"') then '"' else '\''
        let b = StringBuilder().Append quoteChar
        for c in s do
            match c with
            | '\\' -> b.Append "\\\\" |> ignore
            | '\n' -> b.Append "\\n" |> ignore
            | '\r' -> b.Append "\\r" |> ignore
            | '\t' -> b.Append "\\t" |> ignore
            | c when c = quoteChar -> b.Append('\\').Append c |> ignore
            | c -> b.Append c |> ignore
        b.Append(quoteChar).ToString()

    /// The summary line the deriver prints on stderr (without the trailing newline).
    let summary (results: Map<string, bool>) (d: Derivation) : string =
        let passed = results |> Map.filter (fun _ p -> p) |> Map.count
        let started = d.NewlyStarted |> List.map (fun w -> repr w.Id) |> String.concat ", "
        sprintf
            "tests: %d (%d passed); work items newly started: [%s]; requirements newly implementation-completed: %d"
            results.Count passed started d.NewlyCompleted.Length

    /// The UTC timestamp format of the `at` field (seconds, Z).
    let timestamp (t: DateTimeOffset) : string =
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

    /// Accepts an `at` override only in the exact timestamp format.
    let parseTimestamp (text: string) : Result<string, string> =
        match DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, _ -> Ok text
        | _ -> Error(sprintf "'%s' is not a timestamp of the form YYYY-MM-DDTHH:MM:SSZ" text)
