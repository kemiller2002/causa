// Owner: integration (T10). VER-002 (verify release freshness), over the release-set decision record
// (.echelon/release-selection.json) with the gate rules in VER001Tests.fs (ReleaseRecord).
namespace Causa.Acceptance.Deterministic

open System
open Xunit
open ReleaseRecord

type VER002Tests() =
    let record = record ()
    let decisions = systems record
    let checks = checks record
    let lockedAt =
        record |> Json.str "baselineLockedAt" |> Option.map DateTimeOffset.Parse |> Option.defaultWith (fun () -> failwith "no baselineLockedAt")
    let registryCommit =
        record |> Json.prop "releaseSet" |> Option.map (Json.strOr "registryCommit") |> Option.defaultValue ""

    /// Authoritative release sources (package registries, the release registry at a pinned commit, the owner's pinned
    /// toolchain lock) are queried at the baseline lock, for every selected system.
    [<Fact>]
    member _.``VER-002-O1``() =
        Gate.assertNoDefects
            "freshness checks"
            [ for d in decisions do
                  if not (checks |> List.exists (fun c -> c.System = d.Id)) then yield sprintf "%s was never checked against a release source" d.Id
              if not (checks |> List.exists (fun c -> c.System = "echelon-current")) then yield "the release set itself was never checked"
              for c in checks do
                  if not (authoritativeSource c.Source) then yield sprintf "%s: '%s' is not an authoritative release source" c.System c.Source
                  if not (checkedAtBaseline lockedAt DateTimeOffset.UtcNow c) then
                      yield sprintf "%s: checked at %s, not at the baseline lock (%O)" c.System c.CheckedAt lockedAt
                  if c.Source.StartsWith "kemiller2002/echelon-registry@" && not (c.Source.Contains registryCommit) then
                      yield sprintf "%s: checked at a registry commit other than the locked %s" c.System registryCommit ]
        // Not vacuous: a check against an arbitrary web page, or one made a week before the lock, fails.
        let sample = checks.Head
        Gate.assertRejects "a non-authoritative source" ([ { sample with Source = "https://example.com/limen" } ] |> List.filter (fun c -> not (authoritativeSource c.Source)) |> List.map (fun c -> c.System))
        Assert.False(
            checkedAtBaseline lockedAt DateTimeOffset.UtcNow { sample with CheckedAt = "2026-10-31T00:00:00Z" },
            "the gate accepted a check made a week before the baseline lock"
        )

    /// Each check records its timestamp, candidate release, source reference and why it was selected.
    [<Fact>]
    member _.``VER-002-O2``() =
        Gate.assertNoDefects "freshness records" (checks |> List.collect freshnessRecordDefects)
        Gate.assertRejects
            "a check without timestamp or reason"
            (freshnessRecordDefects { checks.Head with CheckedAt = "yesterday"; Reason = "" })

    /// The latest main-branch commit is not the latest released artifact.
    [<Fact>]
    member _.``VER-002-O3``() =
        Gate.assertNoDefects "branch heads used as releases" (checks |> List.collect branchHeadDefects)
        Gate.assertNoDefects
            "locked commits without a release"
            [ for s in lockedSystems (lock ()) do
                  if s.Commit <> "" && s.Tag = "" && s.Kind <> "data" then yield sprintf "%s is locked to commit %s with no release tag" s.Id s.Commit ]
        let sample = checks.Head
        Gate.assertRejects "a main-branch source" (branchHeadDefects { sample with Source = "https://github.com/kemiller2002/limen/tree/main" })
        Gate.assertRejects "a commit as the candidate" (branchHeadDefects { sample with Candidate = "743cdb9be8971c0a3a250906a6e2e112170dfae0" })
