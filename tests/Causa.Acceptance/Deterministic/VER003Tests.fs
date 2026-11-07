// Owner: integration (T10). VER-003 (compatibility over blind upgrades), over the release-set decision record, the
// T0 compatibility evidence and the repository's own pins, with the gate rules in VER001Tests.fs (ReleaseRecord).
namespace Causa.Acceptance.Deterministic

open System
open Xunit
open ReleaseRecord

type VER003Tests() =
    let record = record ()
    let locked = lockedSystems (lock ())
    let decisions = systems record
    let checks = checks record
    let excs = exceptions record

    /// The latest stable release is preferred only where integration contracts and acceptance evidence pass.
    [<Fact>]
    member _.``VER-003-O1``() =
        let drift = Repo.json "evidence/t0/stack-drift.json"
        let summary = drift |> Json.prop "summary"
        let count k = summary |> Option.bind (Json.prop k) |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue -1
        let findings = Json.items "findings" drift
        Gate.assertNoDefects
            "compatibility evidence"
            [ if drift |> Json.prop "ready" |> Option.map (fun v -> v.GetBoolean()) <> Some true then yield "the T0 stack-drift report is not ready"
              for k in [ "DRIFT"; "BLOCKED"; "UNVERIFIED" ] do
                  if count k <> 0 then yield sprintf "the T0 stack-drift report has %d %s" (count k) k
              for d in decisions do
                  let passed =
                      findings
                      |> List.exists (fun f ->
                          Json.strOr "system" f = d.Id && Json.strOr "status" f = "PASS"
                          && (Json.strOr "expected" f).StartsWith d.Selected)
                  if not passed then yield sprintf "%s %s has no passing compatibility check in evidence/t0/stack-drift.json" d.Id d.Selected
              for e in Json.items "compatibilityEvidence" record do
                  let p = Json.strOr "path" e
                  if not (Repo.exists p) then yield sprintf "compatibility evidence %s is missing" p ]
        // The installed npm tree is exactly the verified release: version, release tarball, integrity.
        Gate.assertNoDefects
            "installed npm packages"
            [ for s in locked do
                  if s.NpmPackage <> "" then
                      match packageLockEntries () |> List.tryFind (fun (n, _) -> n = s.NpmPackage) with
                      | Some(_, (v, resolved, integrity)) ->
                          if v <> s.Version then yield sprintf "%s installs %s, locked %s" s.NpmPackage v s.Version
                          if not (resolved.Contains s.Version) then yield sprintf "%s resolves to %s, not the %s release" s.NpmPackage resolved s.Version
                          if not (integrity.StartsWith "sha512-") then yield sprintf "%s has no sha512 integrity" s.NpmPackage
                      | None when decisions |> List.exists (fun d -> d.Id = s.Id && d.Integration = "runtime") ->
                          yield sprintf "%s is a runtime dependency but package-lock.json does not install it" s.NpmPackage
                      | None -> () ]

    /// An incompatible newest release fails the upgrade gate unless a documented, authorized exception names the newest
    /// known compatible release.
    [<Fact>]
    member _.``VER-003-O2``() =
        Gate.assertNoDefects "stale selections" (staleSelections checks excs)
        Gate.assertNoDefects
            "exceptions"
            [ for e in excs do
                  if not (hasException excs e.System e.Version) then yield sprintf "the exception for %s lacks approval, scope, rationale or expiry" e.System
                  if not (decisions |> List.exists (fun d -> d.Id = e.System && d.Selected = e.Version)) then
                      yield sprintf "the exception for %s %s names no selected release" e.System e.Version ]
        let older = { checks.Head with System = "limen"; Candidate = "0.9.0"; NewestStable = "0.9.1" }
        Gate.assertRejects "a fallback without an exception" (staleSelections [ older ] [])
        let unapproved = { System = "limen"; Version = "0.9.0"; Approval = ""; Scope = "run"; Rationale = "incompatible"; Expires = "next run" }
        Gate.assertRejects "a fallback whose exception has no approval" (staleSelections [ older ] [ unapproved ])
        Gate.assertNoDefects
            "a fallback with an authorized exception"
            (staleSelections [ older ] [ { unapproved with Approval = "ODR-EXAMPLE A" } ])

    /// Never silently fall back to an old version: every pin in the repository is the selected release.
    [<Fact>]
    member _.``VER-003-O3``() =
        Gate.assertNoDefects "silent fallbacks" (fallbacks (decisions |> List.map (fun d -> d.Id, d.Selected)) (declaredVersions locked))
        Gate.assertNoDefects
            "lock against selection"
            [ for s in locked do
                  match decisions |> List.tryFind (fun d -> d.Id = s.Id) with
                  | Some d when d.Selected <> s.Version -> yield sprintf "%s: lock %s, selection %s" s.Id s.Version d.Selected
                  | _ -> () ]
        Gate.assertRejects
            "an older pinned version"
            (fallbacks [ "limen", "0.9.1" ] (fun _ -> [ "package.json @echelon-foundry/limen", "0.9.0" ]))
