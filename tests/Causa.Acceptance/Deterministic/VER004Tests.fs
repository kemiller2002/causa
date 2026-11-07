// Owner: integration (T11). VER-004 (reproducible lock). Also holds Tooling, the process helpers the T11 tests share
// (dotnet fsi build.fsx, node with the installed npm tree), which fail closed: a missing tool or npm tree is reported
// as BLOCKED and fails the test, never passes it.
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Xunit
open ReleaseRecord

module Tooling =
    type Outcome = { ExitCode: int; Out: string; Err: string }

    let run (file: string) (arguments: string list) (stdin: string option) : Result<Outcome, string> =
        try
            let info =
                ProcessStartInfo(
                    file,
                    arguments,
                    WorkingDirectory = Repo.root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = stdin.IsSome
                )
            use p = match Process.Start info with | null -> failwithf "%s did not start" file | p -> p
            stdin |> Option.iter (fun s -> p.StandardInput.Write s; p.StandardInput.Close())
            let out = p.StandardOutput.ReadToEndAsync()
            let err = p.StandardError.ReadToEndAsync()
            if not (p.WaitForExit 300_000) then
                p.Kill true
                Error(sprintf "BLOCKED: %s did not finish within five minutes" file)
            else
                Ok { ExitCode = p.ExitCode; Out = out.Result; Err = err.Result }
        with e ->
            Error(sprintf "BLOCKED: %s cannot be started (%s)" file e.Message)

    /// The dotnet host running these tests (dotnet test sets DOTNET_HOST_PATH), else the one on PATH.
    let dotnet =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null
        | "" -> "dotnet"
        | host -> host

    let orFail (r: Result<Outcome, string>) =
        match r with
        | Ok o when o.ExitCode = 0 -> o
        | Ok o -> failwithf "exit %d\n%s\n%s" o.ExitCode o.Out o.Err
        | Error e -> failwith e

    /// `dotnet fsi build.fsx <args>`.
    let build (arguments: string list) = run dotnet ("fsi" :: Repo.path "build.fsx" :: arguments) None |> orFail

    /// The installed npm tree (npm ci); its absence is BLOCKED.
    let requireNodeModules () =
        if not (Directory.Exists(Repo.path "node_modules/@echelon-foundry")) then
            failwith "BLOCKED: node_modules is not installed (run npm ci from package-lock.json); the installed artifacts cannot be verified"

    /// Runs an ES module script with node from the repository root; stdout is its answer.
    let node (script: string) : string =
        requireNodeModules ()
        let file = Path.Combine(Path.GetTempPath(), sprintf "causa-%s.mjs" (Guid.NewGuid().ToString "N"))
        File.WriteAllText(file, script)
        try (run "node" [ file ] None |> orFail).Out.Trim()
        finally File.Delete file

    let tempDir () =
        let d = Path.Combine(Path.GetTempPath(), "causa-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory d |> ignore
        d

type VER004Tests() =
    let record = record ()
    let lockJson = lock ()
    let locked = lockedSystems lockJson

    /// Exact package versions and immutable digests or commit SHAs are pinned in a machine-readable manifest.
    [<Fact>]
    member _.``VER-004-O1``() =
        let digestOf (id: string) =
            Json.items "systems" lockJson
            |> List.tryFind (fun s -> Json.strOr "id" s = id)
            |> Option.map (fun s ->
                [ s |> Json.prop "packageDigest" |> Option.map (Json.strOr "sha256") |> Option.defaultValue ""
                  Json.strOr "sha256" s ])
            |> Option.defaultValue []
            |> List.filter (fun d -> Regex.IsMatch(d, "^[0-9a-f]{64}$"))
        Gate.noDefects
            "the dependency manifest"
            [ for s in locked do
                  match s.Kind with
                  | "registry-component" ->
                      if not (isExactRelease s.Version) then yield sprintf "%s version '%s' is not exact" s.Id s.Version
                      if not (isSha s.Commit) then yield sprintf "%s has no commit SHA" s.Id
                      if not (Regex.IsMatch(s.ReleaseManifestSha256, "^[0-9a-f]{64}$")) then yield sprintf "%s has no release manifest digest" s.Id
                      if (s.NpmPackage <> "" || s.NugetPackage <> "") && (digestOf s.Id).IsEmpty then yield sprintf "%s package has no sha256 digest" s.Id
                  | "npm-pinned" ->
                      if not (isExactRelease s.Version) then yield sprintf "%s version '%s' is not exact" s.Id s.Version
                      if (digestOf s.Id).IsEmpty then yield sprintf "%s has no tarball digest" s.Id
                  | "tag-pinned" -> if not (isSha s.Commit) then yield sprintf "%s has no commit SHA" s.Id
                  | "data" -> if not (isSha s.Commit) then yield sprintf "%s has no commit SHA" s.Id
                  | "sdk" ->
                      let rollForward = Repo.json "global.json" |> Json.prop "sdk" |> Option.bind (Json.str "rollForward")
                      if rollForward <> Some "disable" then yield "global.json lets the SDK roll forward"
                  | _ -> () ]
        Gate.noDefects
            "installed-tree pins"
            [ for name, (version, resolved, integrity) in packageLockEntries () do
                  if not (isExactRelease version) then yield sprintf "package-lock.json %s version '%s'" name version
                  if resolved = "" || not (integrity.StartsWith "sha512-") then yield sprintf "package-lock.json %s has no resolved tarball with sha512 integrity" name
              for name, v in centralPackageVersions () do
                  if not (isExactRelease v) then yield sprintf "Directory.Packages.props %s '%s'" name v
              for m in Regex.Matches(Repo.text ".github/workflows/ci.yml", @"uses:\s*([^\s#]+)") do
                  let reference = m.Groups[1].Value
                  if not (Regex.IsMatch(reference, "@[0-9a-f]{40}$")) then yield sprintf "ci.yml uses %s, not a commit SHA" reference ]

    /// The lock is fixed for the run: what the T0 baseline verified is what the repository holds now.
    [<Fact>]
    member _.``VER-004-O2``() =
        let t0 = Repo.json "evidence/t0/t0.json" |> Json.strOr "t0"
        let run = record |> Json.prop "run"
        let releaseSet = lockJson |> Json.prop "releaseSet"
        let setSha = releaseSet |> Option.map (Json.strOr "sha256") |> Option.defaultValue ""
        let registryCommit = releaseSet |> Option.map (Json.strOr "registryCommit") |> Option.defaultValue ""
        let findings = Repo.json "evidence/t0/stack-drift.json" |> Json.items "findings"
        let expected system check =
            findings
            |> List.filter (fun f -> Json.strOr "system" f = system && Json.strOr "check" f = check && Json.strOr "status" f = "PASS")
            |> List.map (Json.strOr "expected")
        let pinned = record |> Json.prop "lock" |> Option.map (Json.strOr "sha256") |> Option.defaultValue ""
        Gate.noDefects
            "the run lock"
            [ if Repo.sha256 lockPath <> pinned then yield sprintf "%s changed after it was pinned (%s, pinned %s)" lockPath (Repo.sha256 lockPath) pinned
              if run |> Option.map (Json.strOr "frozenAt") <> Some t0 then yield sprintf "the run is not frozen at T0 %s" t0
              if record |> Json.strOr "baselineLockedAt" <> t0 then yield "baselineLockedAt is not T0"
              if not (expected "echelon-current" "release set" |> List.exists (fun e -> e.EndsWith setSha)) then
                  yield sprintf "the T0 baseline did not verify release set %s" setSha
              if not (expected "echelon-current" "toolchain lock registry commit" |> List.contains registryCommit) then
                  yield sprintf "the T0 baseline did not verify registry commit %s" registryCommit
              for s in locked do
                  if s.Kind = "registry-component" && not (expected s.Id "release manifest" |> List.exists (fun e -> e = s.Version + " " + s.ReleaseManifestSha256)) then
                      yield sprintf "%s %s was not the release manifest the T0 baseline verified" s.Id s.Version
              // Upstream releases seen during the run are drift, recorded, never a silent change of the lock.
              for c in checks record do
                  match locked |> List.tryFind (fun s -> s.Id = c.System) with
                  | Some s when s.Version <> c.Candidate -> yield sprintf "%s: the lock holds %s but the run selected %s" s.Id s.Version c.Candidate
                  | _ -> () ]

    /// Latest versions are re-resolved for each new run or authorized refresh, and the change against the previous run
    /// is explicit.
    [<Fact>]
    member _.``VER-004-O3``() =
        let previous = record |> Json.prop "previousRun"
        let changes = previous |> Option.map (Json.items "changes") |> Option.defaultValue []
        let lockedAt = DateTimeOffset.Parse(Json.strOr "baselineLockedAt" record)
        Gate.noDefects
            "re-resolution for this run"
            [ if String.IsNullOrWhiteSpace(Json.strOr "refreshPolicy" record) then yield "no refresh policy"
              match previous with
              | None -> yield "no previous-run record: the update against the last run is not explicit"
              | Some p ->
                  if not (Regex.IsMatch(Json.strOr "lockSha256" p, "^[0-9a-f]{64}$")) then yield "the previous lock has no digest"
                  if Json.strOr "lockSha256" p = Repo.sha256 lockPath then yield "the previous run's lock is this run's: nothing was re-resolved"
              for c in changes do
                  let system = Json.strOr "system" c
                  match locked |> List.tryFind (fun s -> s.Id = system) with
                  | Some s when s.Version = Json.strOr "current" c -> ()
                  | _ -> yield sprintf "the previous-run change for %s does not match the current lock" system
                  if String.IsNullOrWhiteSpace(Json.strOr "reason" c) then yield sprintf "the change for %s has no reason" system
              // Every system was resolved again in this run, at its baseline.
              for d in systems record do
                  match checks record |> List.tryFind (fun c -> c.System = d.Id) with
                  | Some c when checkedAtBaseline lockedAt DateTimeOffset.UtcNow c -> ()
                  | _ -> yield sprintf "%s was not re-resolved for this run" d.Id
              for e in exceptions record do
                  if String.IsNullOrWhiteSpace e.Expires then yield sprintf "the exception for %s never expires" e.System ]
