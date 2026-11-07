// Owner: integration (T10). VER-001 (latest compatible verified release). This file also holds the release-set gate
// the VER-00x tests share (ReleaseRecord): pure checks over the repository's release-set decision record
// (.echelon/release-selection.json), its locked set (.echelon/dependency-lock.json) and what the repository declares.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Xunit

/// Reading the repository the tests run from (found by walking up to Causa.slnx).
module Repo =
    let root =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "Causa.slnx not found above the test assembly: the tests must run inside the repository"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let path (relative: string) = Path.Combine(root, relative)
    let exists (relative: string) = File.Exists(path relative) || Directory.Exists(path relative)
    let text (relative: string) = File.ReadAllText(path relative)

    let sha256 (relative: string) =
        use stream = File.OpenRead(path relative)
        SHA256.HashData(stream) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let json (relative: string) : JsonElement =
        use document = JsonDocument.Parse(text relative)
        document.RootElement.Clone()

    /// The project files of Causa.slnx (the application's solution; App.slnx is the Conditor scaffold).
    let causaProjects () =
        Regex.Matches(text "Causa.slnx", "Path=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq

/// Small total accessors over System.Text.Json.
module Json =
    let prop (name: string) (e: JsonElement) : JsonElement option =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        | _ -> None

    let str (name: string) (e: JsonElement) : string option =
        prop name e
        |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None)

    let strOr (name: string) (e: JsonElement) = str name e |> Option.defaultValue ""

    let items (name: string) (e: JsonElement) : JsonElement list =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
        | _ -> []

    let strings (name: string) (e: JsonElement) : string list =
        items name e
        |> List.choose (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None)

    let entries (e: JsonElement) : (string * JsonElement) list =
        match e.ValueKind with
        | JsonValueKind.Object -> e.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value) |> List.ofSeq
        | _ -> []

/// The release-set decision record, its lock, and the gate rules (pure functions returning defects; [] is a pass).
module ReleaseRecord =
    type SystemDecision =
        { Id: string
          Owner: string
          ReleaseChannel: string
          Integration: string
          IntegrationEvidence: string
          Selected: string }

    type FreshnessCheck =
        { System: string
          CheckedAt: string
          Source: string
          Candidate: string
          NewestStable: string
          PrereleasesObserved: string list
          Reason: string }

    type ReleaseException =
        { System: string
          Version: string
          Approval: string
          Scope: string
          Rationale: string
          Expires: string }

    type PrereleaseApproval =
        { System: string
          Version: string
          Approval: string
          Provenance: string
          CompatibilityEvidence: string
          SecurityEvidence: string }

    type LockedSystem =
        { Id: string
          Version: string
          Kind: string
          Repository: string
          Tag: string
          Commit: string
          ReleaseStage: string
          ReleaseManifestSha256: string
          NpmPackage: string
          NugetPackage: string }

    let selectionPath = ".echelon/release-selection.json"
    let lockPath = ".echelon/dependency-lock.json"

    let record () = Repo.json selectionPath
    let lock () = Repo.json lockPath

    let systems (r: JsonElement) : SystemDecision list =
        Json.items "systems" r
        |> List.map (fun s ->
            { Id = Json.strOr "id" s
              Owner = Json.strOr "owner" s
              ReleaseChannel = Json.strOr "releaseChannel" s
              Integration = Json.strOr "integration" s
              IntegrationEvidence = Json.strOr "integrationEvidence" s
              Selected = Json.strOr "selected" s })

    let notApplicable (r: JsonElement) : (string * string) list =
        Json.items "notApplicable" r |> List.map (fun s -> Json.strOr "id" s, Json.strOr "reason" s)

    let checks (r: JsonElement) : FreshnessCheck list =
        Json.items "freshnessChecks" r
        |> List.map (fun c ->
            { System = Json.strOr "system" c
              CheckedAt = Json.strOr "checkedAt" c
              Source = Json.strOr "source" c
              Candidate = Json.strOr "candidate" c
              NewestStable = Json.strOr "newestStable" c
              PrereleasesObserved = Json.strings "prereleasesObserved" c
              Reason = Json.strOr "reason" c })

    let exceptions (r: JsonElement) : ReleaseException list =
        Json.items "exceptions" r
        |> List.map (fun e ->
            { System = Json.strOr "system" e
              Version = Json.strOr "version" e
              Approval = Json.strOr "approval" e
              Scope = Json.strOr "scope" e
              Rationale = Json.strOr "rationale" e
              Expires = Json.strOr "expires" e })

    let prereleaseApprovals (r: JsonElement) : PrereleaseApproval list =
        Json.items "prereleaseApprovals" r
        |> List.map (fun a ->
            { System = Json.strOr "system" a
              Version = Json.strOr "version" a
              Approval = Json.strOr "approval" a
              Provenance = Json.strOr "provenance" a
              CompatibilityEvidence = Json.strOr "compatibilityEvidence" a
              SecurityEvidence = Json.strOr "securityEvidence" a })

    let lockedSystems (l: JsonElement) : LockedSystem list =
        Json.items "systems" l
        |> List.map (fun s ->
            { Id = Json.strOr "id" s
              Version = Json.strOr "version" s
              Kind = Json.strOr "kind" s
              Repository = Json.strOr "repository" s
              Tag = Json.strOr "tag" s
              Commit = Json.strOr "commit" s
              ReleaseStage = Json.strOr "releaseStage" s
              ReleaseManifestSha256 = Json.prop "releaseManifest" s |> Option.map (Json.strOr "sha256") |> Option.defaultValue ""
              NpmPackage = Json.strOr "npmPackage" s
              NugetPackage = Json.strOr "nugetPackage" s })

    let lockedExcluded (l: JsonElement) : string list = Json.items "excluded" l |> List.map (Json.strOr "id")

    // ---- Version shapes ----------------------------------------------------------------------------------------------

    let isExactRelease (v: string) = Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")
    let isPrerelease (v: string) = Regex.IsMatch(v, @"^\d+\.\d+\.\d+-")
    let isSha (s: string) = Regex.IsMatch(s, "^[0-9a-f]{40}$")

    // ---- What the repository declares ----------------------------------------------------------------------------------

    /// package.json dependencies and devDependencies: name -> spec.
    let packageJsonSpecs () : (string * string) list =
        let p = Repo.json "package.json"
        [ "dependencies"; "devDependencies" ]
        |> List.collect (fun section ->
            Json.prop section p |> Option.map Json.entries |> Option.defaultValue []
            |> List.map (fun (name, v) -> name, (if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj |> Option.defaultValue "" else "")))

    /// package-lock.json installed packages: name -> (version, resolved, integrity).
    let packageLockEntries () : (string * (string * string * string)) list =
        Repo.json "package-lock.json"
        |> Json.prop "packages"
        |> Option.map Json.entries
        |> Option.defaultValue []
        |> List.filter (fun (k, _) -> k.StartsWith "node_modules/")
        |> List.map (fun (k, v) -> k.Substring "node_modules/".Length, (Json.strOr "version" v, Json.strOr "resolved" v, Json.strOr "integrity" v))

    /// Directory.Packages.props: NuGet id -> version.
    let centralPackageVersions () : (string * string) list =
        Regex.Matches(Repo.text "Directory.Packages.props", "<PackageVersion\\s+Include=\"([^\"]+)\"\\s+Version=\"([^\"]*)\"")
        |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
        |> List.ofSeq

    /// Every text that can bring a NuGet package into Causa.slnx: its projects and the central build files.
    let causaBuildTexts () : (string * string) list =
        (Repo.causaProjects () @ [ "Directory.Build.props"; "Directory.Packages.props" ])
        |> List.filter Repo.exists
        |> List.map (fun p -> p, Repo.text p)

    let referencedByCausa (packageId: string) =
        packageId <> ""
        && causaBuildTexts () |> List.exists (fun (_, t) -> t.Contains("Include=\"" + packageId + "\"", StringComparison.OrdinalIgnoreCase))

    /// The versions the repository itself pins for a system, as (where, version).
    let declaredVersions (locked: LockedSystem list) (systemId: string) : (string * string) list =
        let fromLock = locked |> List.tryFind (fun s -> s.Id = systemId)
        let npm =
            match fromLock with
            | Some s when s.NpmPackage <> "" ->
                let spec = packageJsonSpecs () |> List.tryFind (fun (n, _) -> n = s.NpmPackage)
                let lockEntry = packageLockEntries () |> List.tryFind (fun (n, _) -> n = s.NpmPackage)
                [ match spec with
                  | Some(_, v) when isExactRelease v -> yield "package.json " + s.NpmPackage, v
                  | Some(_, v) ->
                      let m = Regex.Match(v, @"/releases/download/v(\d+\.\d+\.\d+[^/]*)/")
                      yield "package.json " + s.NpmPackage, (if m.Success then m.Groups[1].Value else v)
                  | None -> ()
                  match lockEntry with
                  | Some(_, (v, _, _)) -> yield "package-lock.json " + s.NpmPackage, v
                  | None -> () ]
            | _ -> []
        let toolchain =
            Repo.json ".echelon/toolchain.json" |> Json.str systemId |> Option.map (fun v -> ".echelon/toolchain.json", v) |> Option.toList
        let conditorComponents =
            Repo.json "conditor.json"
            |> Json.items "components"
            |> List.filter (fun c -> Json.strOr "id" c = systemId)
            |> List.map (fun c -> "conditor.json component", Json.strOr "version" c)
        let sdk =
            if systemId = "dotnet-sdk" then
                Repo.json "global.json" |> Json.prop "sdk" |> Option.bind (Json.str "version") |> Option.map (fun v -> "global.json", v) |> Option.toList
            else []
        npm @ toolchain @ conditorComponents @ sdk

    // ---- Gate rules (pure) ---------------------------------------------------------------------------------------------

    let hasException (excs: ReleaseException list) (system: string) (version: string) =
        excs
        |> List.exists (fun e ->
            e.System = system && e.Version = version
            && [ e.Approval; e.Scope; e.Rationale; e.Expires ] |> List.forall (String.IsNullOrWhiteSpace >> not))

    /// VER-001-O1 / VER-003-O2: a selection older than the newest stable release fails unless an authorized exception
    /// (approval, scope, rationale, expiry) names exactly that system and version.
    let staleSelections (checks: FreshnessCheck list) (excs: ReleaseException list) : string list =
        checks
        |> List.filter (fun c -> c.Candidate <> c.NewestStable && not (hasException excs c.System c.Candidate))
        |> List.map (fun c -> sprintf "%s selects %s but the newest stable release is %s, with no authorized exception" c.System c.Candidate c.NewestStable)

    /// VER-003-O3: what the repository pins must be what was selected; anything else is a silent fallback (or drift).
    let fallbacks (selected: (string * string) list) (declared: string -> (string * string) list) : string list =
        selected
        |> List.collect (fun (system, version) ->
            declared system
            |> List.filter (fun (_, v) -> v <> version)
            |> List.map (fun (where, v) -> sprintf "%s pins %s %s but the selection is %s" where system v version))

    let private utc (s: string) =
        match DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
        | true, t -> Some t
        | _ -> None

    /// VER-002-O2: every check records when, what, where from and why.
    let freshnessRecordDefects (c: FreshnessCheck) : string list =
        [ if (utc c.CheckedAt).IsNone then yield sprintf "%s: checkedAt '%s' is not an ISO 8601 UTC timestamp" c.System c.CheckedAt
          if not (isExactRelease c.Candidate) then yield sprintf "%s: candidate '%s' is not a released version" c.System c.Candidate
          if not (isExactRelease c.NewestStable) then yield sprintf "%s: newestStable '%s' is not a released version" c.System c.NewestStable
          if String.IsNullOrWhiteSpace c.Source then yield sprintf "%s: no source reference" c.System
          if String.IsNullOrWhiteSpace c.Reason then yield sprintf "%s: no reason for the selection" c.System ]

    /// VER-002-O1: the sources a release may be checked against: a package registry, the release registry at a pinned
    /// commit, the owner's pinned toolchain lock, or the SDK pin.
    let authoritativeSource (source: string) =
        [ @"^https://registry\.npmjs\.org/"
          @"^https://api\.nuget\.org/"
          @"^kemiller2002/echelon-registry@[0-9a-f]{40}:(releases|sets)/"
          @"^kemiller2002/Indy-init@[0-9a-f]{40}:kickoff/"
          @"^global\.json" ]
        |> List.exists (fun p -> Regex.IsMatch(source, p))

    /// VER-002-O1: checks happen at the baseline lock: no earlier than a day before it, never in the future.
    let checkedAtBaseline (lockedAt: DateTimeOffset) (now: DateTimeOffset) (c: FreshnessCheck) =
        match utc c.CheckedAt with
        | Some t -> t >= lockedAt.AddDays(-1.0) && t <= now
        | None -> false

    /// VER-002-O3: a branch head is not a release.
    let branchHeadDefects (c: FreshnessCheck) : string list =
        [ if Regex.IsMatch(c.Source, @"(@|/tree/|/commits?/|#)(main|master|HEAD)\b|\blatest (main|master) commit\b") then
              yield sprintf "%s: source '%s' names a branch head, not a release" c.System c.Source
          if isSha c.Candidate then yield sprintf "%s: candidate is a commit (%s), not a released version" c.System c.Candidate ]

    /// VER-005: an adopted prerelease needs explicit approval, provenance, and the same compatibility and security gates.
    let prereleaseDefects (adopted: (string * string) list) (approvals: PrereleaseApproval list) : string list =
        adopted
        |> List.filter (snd >> isPrerelease)
        |> List.choose (fun (system, version) ->
            match approvals |> List.tryFind (fun a -> a.System = system && a.Version = version) with
            | None -> Some(sprintf "%s %s is a prerelease adopted without approval" system version)
            | Some a ->
                let missing =
                    [ "approval", a.Approval; "provenance", a.Provenance; "compatibilityEvidence", a.CompatibilityEvidence; "securityEvidence", a.SecurityEvidence ]
                    |> List.filter (snd >> String.IsNullOrWhiteSpace)
                    |> List.map fst
                if missing.IsEmpty then None else Some(sprintf "%s %s prerelease approval lacks %s" system version (String.Join(", ", missing))))

    /// The systems VER-001-O2 names, by their release-set identities.
    let namedSystems = [ "praxis"; "ordo"; "dokimos"; "percepta"; "forma"; "forma-workflow"; "limen"; "folio" ]

    let integrationKinds =
        set [ "runtime"; "process-tool"; "verification-tool"; "bootstrap-tool"; "build-tool"; "locked-not-integrated" ]

module Gate =
    let noDefects (what: string) (defects: string list) =
        if not defects.IsEmpty then
            failwithf "%s:\n  %s" what (String.Join("\n  ", defects))

    let defectsFound (what: string) (defects: string list) =
        if defects.IsEmpty then failwithf "the gate accepted %s" what

open ReleaseRecord

type VER001Tests() =
    let record = record ()
    let locked = lockedSystems (lock ())
    let decisions = systems record

    /// Each build uses the newest available stable, published, compatible release of every applicable system, as
    /// locked at the baseline.
    [<Fact>]
    member _.``VER-001-O1``() =
        // The lock is the governed one, byte for byte.
        Gate.noDefects
            "the locked set"
            [ let pinned = record |> Json.prop "lock" |> Option.map (Json.strOr "sha256") |> Option.defaultValue ""
              if Repo.sha256 lockPath <> pinned then yield sprintf "%s sha256 %s differs from the pinned %s" lockPath (Repo.sha256 lockPath) pinned ]
        // Every decision selects what the lock holds, and the lock's version is the newest stable release.
        Gate.noDefects
            "selections against the lock"
            [ for d in decisions do
                  match locked |> List.tryFind (fun s -> s.Id = d.Id) with
                  | Some s when s.Version = d.Selected -> ()
                  | Some s -> yield sprintf "%s selects %s but the lock holds %s" d.Id d.Selected s.Version
                  | None -> yield sprintf "%s is selected but not in the lock" d.Id ]
        let checks = checks record
        Gate.noDefects
            "selections against the freshness checks"
            [ for d in decisions do
                  match checks |> List.tryFind (fun c -> c.System = d.Id) with
                  | Some c when c.Candidate = d.Selected -> ()
                  | Some c -> yield sprintf "%s selects %s but its freshness check's candidate is %s" d.Id d.Selected c.Candidate
                  | None -> yield sprintf "%s has no freshness check" d.Id ]
        Gate.noDefects "stale selections" (staleSelections checks (exceptions record))
        // The repository builds with exactly the selected versions.
        Gate.noDefects "declared versions" (fallbacks (decisions |> List.map (fun d -> d.Id, d.Selected)) (declaredVersions locked))
        // The gate is not vacuous: an older selection without an exception fails it.
        let older = { checks.Head with Candidate = "0.0.1"; NewestStable = "9.9.9" }
        Gate.defectsFound "a selection older than the newest stable release" (staleSelections [ older ] [])

    /// The applicable systems include Praxis, Ordo, Dokimos, Percepta, Forma, Forma workflow, Limen, Folio and every
    /// Echelon dependency the repository actually integrates.
    [<Fact>]
    member _.``VER-001-O2``() =
        Gate.noDefects
            "systems VER-001-O2 names"
            [ for id in namedSystems do
                  match decisions |> List.tryFind (fun d -> d.Id = id) with
                  | Some d when integrationKinds.Contains d.Integration -> ()
                  | Some d -> yield sprintf "%s has no recognized integration classification ('%s')" id d.Integration
                  | None -> yield sprintf "%s has no release decision" id ]
        let decided = decisions |> List.map (fun d -> d.Id) |> set
        let excluded = notApplicable record |> List.map fst |> set
        Gate.noDefects
            "integrated Echelon dependencies"
            [ for name, _ in packageJsonSpecs () do
                  if name.StartsWith "@echelon-foundry/" then
                      match locked |> List.tryFind (fun s -> s.NpmPackage = name) with
                      | Some s when decided.Contains s.Id -> ()
                      | _ -> yield sprintf "package.json integrates %s but no release decision covers it" name
              for project, t in causaBuildTexts () do
                  for m in Regex.Matches(t, "Include=\"(EchelonFoundry\\.[^\"]+)\"") do
                      let id = m.Groups[1].Value
                      match locked |> List.tryFind (fun s -> s.NugetPackage = id) with
                      | Some s when decided.Contains s.Id -> ()
                      | _ -> yield sprintf "%s integrates %s but no release decision covers it" project id ]
        Gate.noDefects
            "release-set components"
            [ for s in locked do
                  if s.Kind = "registry-component" && not (decided.Contains s.Id) then
                      yield sprintf "%s is in the release set but has no decision" s.Id
              for id in lockedExcluded (lock ()) do
                  if not (decided.Contains id || excluded.Contains id) then
                      yield sprintf "%s is excluded by the lock but the record neither decides it nor marks it not applicable" id ]

    /// Each system's real owner and release channel are recorded; no release is invented and no system is assumed to be
    /// a runtime dependency.
    [<Fact>]
    member _.``VER-001-O3``() =
        Gate.noDefects
            "owners and release channels"
            [ for d in decisions do
                  if String.IsNullOrWhiteSpace d.Owner then yield sprintf "%s: no owner" d.Id
                  if String.IsNullOrWhiteSpace d.ReleaseChannel then yield sprintf "%s: no release channel" d.Id
                  if String.IsNullOrWhiteSpace d.IntegrationEvidence then yield sprintf "%s: no integration evidence" d.Id
              for s in locked do
                  if s.Kind = "registry-component" then
                      if not (Regex.IsMatch(s.Repository, "^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")) then yield sprintf "%s: repository '%s'" s.Id s.Repository
                      if s.Tag = "" then yield sprintf "%s: no release tag" s.Id
                      if not (isSha s.Commit) then yield sprintf "%s: commit '%s' is not a full SHA" s.Id s.Commit
                      if not (Regex.IsMatch(s.ReleaseManifestSha256, "^[0-9a-f]{64}$")) then yield sprintf "%s: no release manifest digest" s.Id
                      match decisions |> List.tryFind (fun d -> d.Id = s.Id) with
                      | Some d when d.Owner.StartsWith s.Repository -> ()
                      | Some d -> yield sprintf "%s: recorded owner '%s' is not the release's repository %s" s.Id d.Owner s.Repository
                      | None -> () ]
        // A runtime dependency is one the application actually declares; a locked system the application does not use
        // is recorded as such, and the record must not claim otherwise.
        let npmDeclared = packageJsonSpecs () |> List.map fst |> set
        Gate.noDefects
            "runtime classification"
            [ for d in decisions do
                  let s = locked |> List.tryFind (fun s -> s.Id = d.Id)
                  let npm = s |> Option.map (fun s -> s.NpmPackage) |> Option.defaultValue ""
                  let nuget = s |> Option.map (fun s -> s.NugetPackage) |> Option.defaultValue ""
                  let used = (npm <> "" && npmDeclared.Contains npm) || referencedByCausa nuget
                  match d.Integration with
                  | "runtime" when not used -> yield sprintf "%s is recorded as a runtime dependency but the application does not declare it" d.Id
                  | "locked-not-integrated" when used -> yield sprintf "%s is recorded as not integrated but the application declares it" d.Id
                  | _ -> ()
              for id, reason in notApplicable record do
                  if String.IsNullOrWhiteSpace reason then yield sprintf "%s is not applicable without a reason" id
                  if decisions |> List.exists (fun d -> d.Id = id) then yield sprintf "%s is both decided and not applicable" id ]
