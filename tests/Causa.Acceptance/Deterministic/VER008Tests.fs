// Owner: integration (T11). VER-008 (CI enforcement): the mandatory gate compares the locked manifest with what is
// installed and loaded, and anything missing, stale, unverified or skipped is BLOCKED or FAIL, never PASS.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open ReleaseRecord

module InstalledGate =
    /// Pure: locked (package, version) against installed (package -> version option). Missing is BLOCKED, different FAIL.
    let compare (locked: (string * string) list) (installed: string -> string option) : string list =
        locked
        |> List.choose (fun (package, version) ->
            match installed package with
            | None -> Some(sprintf "BLOCKED: %s %s is locked but not installed" package version)
            | Some v when v <> version -> Some(sprintf "FAIL: %s %s is installed but %s is locked" package v version)
            | Some _ -> None)

    /// Pure: the compatibility evidence is usable only when it is ready, complete and newer than nothing it judges.
    let evidenceDefects (ready: bool) (counts: Map<string, int>) : string list =
        [ if not ready then yield "FAIL: the compatibility evidence is not ready"
          for k in [ "DRIFT"; "BLOCKED"; "UNVERIFIED" ] do
              match counts.TryFind k with
              | Some 0 -> ()
              | Some n -> yield sprintf "FAIL: the compatibility evidence has %d %s" n k
              | None -> yield sprintf "BLOCKED: the compatibility evidence does not report %s" k ]

    let installedNpm (package: string) =
        let file = Repo.path (sprintf "node_modules/%s/package.json" package)
        if File.Exists file then (Repo.json (sprintf "node_modules/%s/package.json" package)) |> Json.str "version" else None

    /// The test host's dependency resolution record (<assembly>.deps.json): library "name/version" -> its runtime
    /// assemblies' file versions.
    let resolvedLibraries () : Map<string, string * string list> =
        let file = Path.Combine(AppContext.BaseDirectory, "Causa.Acceptance.deps.json")
        use doc = JsonDocument.Parse(File.ReadAllText file)
        [ for target in doc.RootElement.GetProperty("targets").EnumerateObject() do
              for lib in target.Value.EnumerateObject() do
                  match lib.Name.Split('/') with
                  | [| name; version |] ->
                      let fileVersions =
                          match lib.Value.TryGetProperty "runtime" with
                          | true, runtime ->
                              [ for a in runtime.EnumerateObject() do
                                    match a.Value.TryGetProperty "fileVersion" with
                                    | true, v -> yield v.GetString() |> Option.ofObj |> Option.defaultValue ""
                                    | _ -> () ]
                          | _ -> []
                      yield name, (version, fileVersions)
                  | _ -> () ]
        |> Map.ofList

    /// The file version of an assembly loaded in this process.
    let loadedFileVersion (assembly: Assembly) =
        assembly.GetCustomAttribute<AssemblyFileVersionAttribute>() |> Option.ofObj |> Option.map (fun a -> a.Version) |> Option.defaultValue ""

open InstalledGate

type VER008Tests() =
    let locked = lockedSystems (lock ())

    /// The gate verifies the locked manifest against the installed and loaded artifacts and the compatibility evidence.
    [<Fact>]
    member _.``VER-008-O1``() =
        Tooling.requireNodeModules ()
        let npmLocked =
            [ for s in locked do
                  if s.NpmPackage <> "" then yield s.NpmPackage, s.Version
              for name, spec in packageJsonSpecs () do
                  if isExactRelease spec then yield name, spec ]
            |> List.distinct
        Gate.assertNoDefects "installed npm artifacts" (compare npmLocked installedNpm)
        let central = centralPackageVersions () |> Map.ofList
        let resolved = resolvedLibraries ()
        // (pinned package, resolved library, an assembly of it loaded in this process)
        let loaded =
            [ "FSharp.Core", "FSharp.Core", typeof<list<int>>.Assembly
              "xunit", "xunit.extensibility.core", typeof<FactAttribute>.Assembly ]
        Gate.assertNoDefects
            "loaded .NET artifacts"
            [ for package, library, assembly in loaded do
                  match central.TryFind package, resolved.TryFind library with
                  | None, _ -> yield sprintf "BLOCKED: %s is loaded but Directory.Packages.props pins no version" package
                  | _, None -> yield sprintf "BLOCKED: %s is not in the test host's dependency resolution" library
                  | Some pinned, Some(version, _) when version <> pinned -> yield sprintf "FAIL: %s %s resolved but %s is pinned" library version pinned
                  | Some _, Some(version, fileVersions) ->
                      let fileVersion = loadedFileVersion assembly
                      let same (a: string) (b: string) =
                          let parts (v: string) = v.Split('.') |> Array.map (fun p -> match Int32.TryParse p with | true, n -> n | _ -> -1) |> fun xs -> Array.append xs (Array.zeroCreate (max 0 (4 - xs.Length)))
                          parts a = parts b
                      if not (fileVersions |> List.exists (same fileVersion)) then
                          yield sprintf "FAIL: the loaded %s (file version %s) is not the resolved %s %s" (assembly.GetName().Name) fileVersion library version ]
        let sdk = Repo.json "global.json" |> Json.prop "sdk" |> Option.bind (Json.str "version") |> Option.defaultValue ""
        let sdkInUse = (Tooling.run Tooling.dotnet [ "--version" ] None |> Tooling.orFail).Out.Trim()
        Assert.Equal(sdk, sdkInUse)
        let drift = Repo.json "evidence/t0/stack-drift.json"
        let counts =
            drift |> Json.prop "summary" |> Option.map Json.entries |> Option.defaultValue []
            |> List.map (fun (k, v) -> k, v.GetInt32()) |> Map.ofList
        Gate.assertNoDefects "compatibility evidence" (evidenceDefects (drift |> Json.prop "ready" |> Option.exists (fun v -> v.GetBoolean())) counts)
        // The gate is mandatory: the Causa CI workflow runs this suite on every pull request with the npm tree installed.
        let ci = Repo.text ".github/workflows/ci.yml"
        Gate.assertNoDefects
            "the mandatory gate"
            [ if not (Regex.IsMatch(ci, @"(?m)^\s*pull_request:")) then yield "Causa CI does not run on pull requests"
              if not (ci.Contains "npm ci") then yield "Causa CI does not install the locked npm tree"
              if not (ci.Contains "dotnet test Causa.slnx") then yield "Causa CI does not run the Causa test suite" ]

    /// Missing dependencies, stale evidence, unverified artifacts or skipped gates give BLOCKED or FAIL, never PASS.
    [<Fact>]
    member _.``VER-008-O2``() =
        Gate.assertRejects "a locked package that is not installed" (compare [ "@echelon-foundry/limen", "0.9.1" ] (fun _ -> None))
        Gate.assertRejects "an installed version that differs from the lock" (compare [ "@echelon-foundry/limen", "0.9.1" ] (fun _ -> Some "0.9.0"))
        Gate.assertRejects "evidence that is not ready" (evidenceDefects false (Map.ofList [ "DRIFT", 0; "BLOCKED", 0; "UNVERIFIED", 0 ]))
        Gate.assertRejects "evidence with an unverified artifact" (evidenceDefects true (Map.ofList [ "DRIFT", 0; "BLOCKED", 0; "UNVERIFIED", 1 ]))
        Gate.assertRejects "evidence that omits a count" (evidenceDefects true (Map.ofList [ "DRIFT", 0 ]))
        let missing = compare [ "@echelon-foundry/limen", "0.9.1" ] (fun _ -> None)
        Assert.True((match missing with [ d ] -> d.StartsWith "BLOCKED" | _ -> false), sprintf "a missing dependency must be BLOCKED: %A" missing)
        // Stale freshness evidence fails (VER-002's baseline window), and the CI workflow refuses skipped or empty runs.
        let lockedAt = DateTimeOffset.Parse(record () |> Json.strOr "baselineLockedAt")
        let stale = { (checks (record ())).Head with CheckedAt = "2026-10-01T00:00:00Z" }
        Assert.False(checkedAtBaseline lockedAt DateTimeOffset.UtcNow stale, "stale freshness evidence was accepted")
        let ci = Repo.text ".github/workflows/ci.yml"
        Gate.assertNoDefects
            "the CI workflow's fail-closed checks"
            [ if not (ci.Contains "Skipped:[[:space:]]*[1-9]") then yield "Causa CI does not fail a run that skips tests"
              if not (ci.Contains "Tests are declared but none passed") then yield "Causa CI does not fail an empty test run"
              if not (ci.Contains "could not find dependent assembly") then yield "Causa CI does not fail a test assembly that could not load"
              if Regex.IsMatch(ci, @"continue-on-error:\s*true") then yield "Causa CI lets a step fail without failing the run" ]
        // No test in this project is skipped or switched off.
        let sources = Directory.GetFiles(Repo.path "tests", "*.fs", SearchOption.AllDirectories)
        Gate.assertNoDefects
            "skipped tests"
            [ for f in sources do
                  if Regex.IsMatch(File.ReadAllText f, @"\[<\s*(Fact|Theory)\s*\(\s*Skip\s*=") then yield sprintf "%s skips a test" f ]
