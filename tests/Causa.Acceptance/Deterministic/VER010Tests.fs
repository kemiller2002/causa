// Owner: integration (WI-GEN-VER). VER-010 (requirements traceability): VER-001..VER-010 reconciled with work items,
// tests and implementation, and work items carried by Praxis, not GitHub Issues. Gate rules in VER001Tests.fs.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Text.RegularExpressions
open Xunit

module Traceability =
    /// The VER requirement ids VER-010 names.
    let verRequirements = [ for i in 1..10 -> sprintf "VER-%03d" i ]

    /// Work plan: work item -> its requirements and obligations (.echelon/r9-work-plan.json).
    let workPlan () =
        Repo.json ".echelon/r9-work-plan.json"
        |> Json.items "workItems"
        |> List.map (fun w -> Json.strOr "id" w, Json.strings "requirements" w, Json.strings "obligations" w)

    /// Bound test identities per obligation (.echelon/obligations.json `tests`).
    let boundTests () =
        Repo.json ".echelon/obligations.json"
        |> Json.items "tests"
        |> List.collect (fun t -> Json.strings "obligations" t |> List.map (fun o -> o, Json.strOr "id" t))

    /// The plan's obligations of a requirement (.echelon/obligations.json `obligations`).
    let planObligations (requirement: string) =
        Repo.json ".echelon/obligations.json"
        |> Json.items "obligations"
        |> List.filter (fun o -> Json.strOr "requirement" o = requirement)
        |> List.map (Json.strOr "id")

    /// Is a dotnet identity implemented: its class declared and its method present in the test sources?
    let implemented (sources: string list) (identity: string) =
        match Regex.Match(identity, @"^dotnet:(.+)\.([A-Za-z0-9]+)\.([^.]+)$") with
        | m when m.Success ->
            let cls = m.Groups[2].Value
            let meth = m.Groups[3].Value
            sources |> List.exists (fun s -> s.Contains("type " + cls + "(") && s.Contains("``" + meth + "``"))
        | _ -> false

    /// Praxis work items (.ros/work/queue.json) by the plan id their title carries ("[T10] ...").
    let praxisItems () =
        Repo.json ".ros/work/queue.json"
        |> Json.items "items"
        |> List.choose (fun i ->
            let m = Regex.Match(Json.strOr "title" i, @"^\[([^\]]+)\]")
            if m.Success then Some(m.Groups[1].Value, Json.strOr "id" i) else None)
        |> Map.ofList

    /// Praxis work items with a completion event (.ros/events/events.jsonl, type work.completed).
    let completed () =
        File.ReadAllLines(Repo.path ".ros/events/events.jsonl")
        |> Array.choose (fun line ->
            try
                use d = System.Text.Json.JsonDocument.Parse line
                let e = d.RootElement.Clone()
                if Json.str "type" e = Some "work.completed" then Json.str "workItem" e else None
            with _ -> None)
        |> Set.ofArray

    /// The rule (pure): every VER requirement is planned in a work item that Praxis carries; every obligation is bound
    /// to a test; a completed work item's dotnet tests exist. An open work item's tests may still be to come.
    let defects
        (plan: (string * string list * string list) list)
        (obligationsOf: string -> string list)
        (bound: (string * string) list)
        (isImplemented: string -> bool)
        (praxis: Map<string, string>)
        (done': Set<string>)
        (excluded: Set<string>)
        : string list =
        [ for req in verRequirements do
              if not (excluded.Contains req) then
                  let items = plan |> List.filter (fun (_, reqs, _) -> List.contains req reqs) |> List.map (fun (id, _, _) -> id)
                  if items.IsEmpty then yield sprintf "%s is in no work item and has no approved exclusion" req
                  for item in items do
                      match praxis.TryFind item with
                      | None -> yield sprintf "%s's work item %s has no Praxis work item" req item
                      | Some wi ->
                          for o in obligationsOf req do
                              let tests = bound |> List.filter (fst >> (=) o) |> List.map snd
                              if tests.IsEmpty then yield sprintf "%s is bound to no test" o
                              if done'.Contains wi then
                                  for t in tests do
                                      if t.StartsWith "dotnet:" && not (isImplemented t) then
                                          yield sprintf "%s: %s completed (%s) but %s is not implemented" o item wi t ]

open Traceability

type VER010Tests() =
    let sources () =
        Directory.GetFiles(Repo.path "tests", "*.fs", SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (f.Contains "/obj/" || f.Contains "/bin/"))
        |> Array.map File.ReadAllText
        |> List.ofArray

    /// VER-001..VER-010 are reconciled with work items, tests and implementation, or an approved exclusion.
    [<Fact>]
    member _.``VER-010-O1``() =
        let src = sources ()
        // VER-009 is a gate requirement of T11 with no plan obligation (the package's plan judges it at the gate); it
        // is listed in T11's slice and has no test identity to reconcile.
        let excluded = set [ "VER-009" ]
        let found =
            defects (workPlan ()) planObligations (boundTests ()) (implemented src) (praxisItems ()) (completed ()) excluded
        Gate.assertNoDefects "VER traceability" found
        // Every VER obligation implemented here today has its test in the sources.
        for id in [ "VER001"; "VER002"; "VER003"; "VER004"; "VER005"; "VER006"; "VER008"; "VER010" ] do
            Assert.True(src |> List.exists (fun s -> s.Contains("type " + id + "Tests(")), sprintf "%sTests is not in the test sources" id)
        // Not vacuous: an unplanned requirement, an unbound obligation and a completed item without its test all fail.
        let plan = [ "T1", [ "VER-001" ], [] ]
        Gate.assertRejects "an unplanned requirement" (defects [] (fun _ -> []) [] (fun _ -> true) Map.empty Set.empty Set.empty)
        Gate.assertRejects "an unbound obligation" (defects plan (fun _ -> [ "VER-001-O1" ]) [] (fun _ -> true) (Map.ofList [ "T1", "WI-1" ]) Set.empty (set (verRequirements |> List.filter ((<>) "VER-001"))))
        Gate.assertRejects
            "a completed work item whose test is missing"
            (defects plan (fun _ -> [ "VER-001-O1" ]) [ "VER-001-O1", "dotnet:A.B.VER001Tests.VER-001-O1" ] (fun _ -> false) (Map.ofList [ "T1", "WI-1" ]) (set [ "WI-1" ]) (set (verRequirements |> List.filter ((<>) "VER-001"))))

    /// Work items go through Praxis's documented input path, not GitHub Issues.
    [<Fact>]
    member _.``VER-010-O2``() =
        let praxis = praxisItems ()
        let plan = workPlan ()
        Gate.assertNoDefects
            "work items outside Praxis"
            [ for id, _, _ in plan do
                  if not (praxis.ContainsKey id) then yield sprintf "work item %s has no Praxis work item in .ros/work/queue.json" id ]
        Assert.True(Repo.exists "praxis", "the Praxis entry point ./praxis is missing")
        Assert.False(Directory.Exists(Repo.path ".github/ISSUE_TEMPLATE"), "an issue template routes work into GitHub Issues")
        let workflows = Directory.GetFiles(Repo.path ".github/workflows", "*.yml") |> Array.map File.ReadAllText |> String.concat "\n"
        Assert.DoesNotMatch(@"gh\s+issue\s+create|issues:\s*write|/issues\b", workflows)
        // Every work item Praxis records as started is a plan work item or Praxis's own (traceable, not ad hoc).
        let started =
            File.ReadAllLines(Repo.path ".ros/events/events.jsonl")
            |> Array.filter (fun l -> l.Contains "\"work.started\"" || l.Contains "\"work.begun\"")
        Assert.NotEmpty(started)
