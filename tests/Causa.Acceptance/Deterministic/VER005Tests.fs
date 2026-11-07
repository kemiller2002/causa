// Owner: integration (T10). VER-005 (no unreviewed prereleases), over every version the repository adopts, with the
// gate rules in VER001Tests.fs (ReleaseRecord).
namespace Causa.Acceptance.Deterministic

open System
open System.Text.RegularExpressions
open Xunit
open ReleaseRecord

type VER005Tests() =
    let record = record ()
    let locked = lockedSystems (lock ())
    let decisions = systems record

    /// Every version the build adopts, as (what, version).
    let adopted () =
        [ for d in decisions -> d.Id, d.Selected
          for s in locked do
              if isExactRelease s.Version || isPrerelease s.Version then yield s.Id, s.Version
          for n, (v, _, _) in packageLockEntries () -> n, v
          for n, v in centralPackageVersions () -> n, v ]

    /// No alpha, beta, RC, unpublished source or unverified local build is adopted because it is newer.
    [<Fact>]
    member _.``VER-005-O1``() =
        Gate.assertNoDefects
            "prereleases or unpublished sources"
            [ for what, v in adopted () do
                  if isPrerelease v then yield sprintf "%s %s is a prerelease" what v
              for name, spec in packageJsonSpecs () do
                  let release = isExactRelease spec || Regex.IsMatch(spec, @"^https://github\.com/[^/]+/[^/]+/releases/download/v\d+\.\d+\.\d+/")
                  if not release then yield sprintf "package.json %s '%s' is not an exact published release" name spec
              for name, v in centralPackageVersions () do
                  if not (isExactRelease v) then yield sprintf "Directory.Packages.props %s '%s' is not an exact release" name v
              for name, (_, resolved, _) in packageLockEntries () do
                  if resolved <> "" && not (resolved.StartsWith "https://") then yield sprintf "package-lock.json %s resolves to %s" name resolved
              for s in locked do
                  if s.Kind = "registry-component" && s.ReleaseStage <> "stable" then yield sprintf "%s release stage is '%s'" s.Id s.ReleaseStage
              for c in checks record do
                  if c.PrereleasesObserved |> List.contains c.Candidate then yield sprintf "%s selects the prerelease %s" c.System c.Candidate ]
        // A newer prerelease was seen (Forma's main-branch builds) and not adopted.
        let seen = checks record |> List.collect (fun c -> c.PrereleasesObserved |> List.map (fun p -> c.System, p))
        Gate.assertNoDefects
            "observed prereleases"
            [ for system, p in seen do
                  if adopted () |> List.exists (fun (_, v) -> v = p) then yield sprintf "%s prerelease %s is adopted" system p ]

    /// A prerelease is used only with explicit approval, provenance, and the same compatibility and security gates.
    [<Fact>]
    member _.``VER-005-O2``() =
        let approvals = prereleaseApprovals record
        Gate.assertNoDefects "unapproved prereleases" (prereleaseDefects (adopted ()) approvals)
        Gate.assertNoDefects
            "prerelease approvals"
            (prereleaseDefects (approvals |> List.map (fun a -> a.System, a.Version)) approvals)
        let candidate = [ "forma", "0.13.0-main.265.1" ]
        Gate.assertRejects "a prerelease without approval" (prereleaseDefects candidate [])
        let partial =
            { System = "forma"; Version = "0.13.0-main.265.1"; Approval = "ODR-EXAMPLE A"; Provenance = ""; CompatibilityEvidence = "run 1"; SecurityEvidence = "" }
        Gate.assertRejects "a prerelease approval without provenance or security evidence" (prereleaseDefects candidate [ partial ])
        Gate.assertNoDefects
            "a fully approved prerelease"
            (prereleaseDefects candidate [ { partial with Provenance = "forma@abc build 265"; SecurityEvidence = "audit 1" } ])
