// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-013 real-provider conformance: sanitized, versioned observations of GitHub; fake/real divergence is detected.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF013Tests(fixture: LiveFixture<PF013Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf013/investigations/00000000-0000-0000-0000-000000000013"
    let fakeProbes () = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
    let liveProbes () =
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created
    let hasHeader name (r: RecordedAnswer) = r.Headers |> List.exists (fun (k, _) -> k = name)

    interface IClassFixture<LiveFixture<PF013Tests>>

    /// PF-013-O1: the observations are versioned and cover status codes, bodies, relevant headers, branch behaviour,
    /// pagination, cache behaviour and retry semantics; the live run records its own sanitized observations of the
    /// same behaviours as evidence.
    [<Fact>]
    member _.``PF-013-O1``() =
        Assert.False(String.IsNullOrEmpty Recordings.Version)
        let statuses = Recordings.all |> List.map (fun r -> r.Status) |> set
        Assert.True(Set.isSubset (set [ 200; 401; 403; 404; 422; 429; 502 ]) statuses, sprintf "statuses: %A" statuses)
        Assert.Contains(Recordings.all, fun r -> hasHeader "x-ratelimit-remaining" r)
        Assert.Contains(Recordings.all, fun r -> hasHeader "cache-control" r && hasHeader "etag" r)
        Assert.Contains(Recordings.all, fun r -> hasHeader "retry-after" r)
        Assert.Contains(Recordings.all, fun r -> r.ExpectedClass = GhTruncatedList)
        Assert.Contains(Recordings.all, fun r -> r.Form = "rest-404-branch")
        Assert.All(Recordings.all, fun r -> Assert.False(String.IsNullOrWhiteSpace r.ObservedIn, r.Form))
        let live = fixture.Run
        liveProbes () |> ignore
        let evidence = IO.File.ReadAllText(live.WriteEvidence())
        Assert.Contains(sprintf "\"recordingsVersion\":\"%s\"" Recordings.Version, evidence)
        Assert.Contains(live.Log, fun o -> o.Status = Some 404)
        Assert.Contains(live.Log, fun o -> o.Status = Some 401)
        Assert.Contains(live.Log, fun o -> o.Headers |> List.exists (fun (k, _) -> k = "x-ratelimit-remaining"))

    /// PF-013-O2: tests detect fake/real divergence: a transcript that differs in one probe is reported, and the fake
    /// and real GitHub transcripts do not differ.
    [<Fact>]
    member _.``PF-013-O2``() =
        let fake = fakeProbes ()
        let altered = fake |> List.mapi (fun i p -> if i = 4 then { p with Actual = "ok" } else p)
        Assert.Equal<(string * string * string) list>([ fake[4].Name, fake[4].Actual, "ok" ], Conformance.divergence fake altered)
        Assert.Empty(Conformance.divergence fake (liveProbes ()))

    /// PF-013-O3: no secret, private payload or sensitive header is captured: sanitizing drops authorization and
    /// cookies and replaces every secret, and the live evidence holds none of the credentials or sensitive headers.
    [<Fact>]
    member _.``PF-013-O3``() =
        let kept = Recordings.sanitizeHeaders [ "Authorization", "Bearer x"; "set-cookie", "a=b"; "x-github-request-id", "1"; "etag", "\"e\"" ]
        Assert.Equal<(string * string) list>([ "etag", "\"e\"" ], kept)
        Assert.Equal("token <credential> here", Recordings.sanitizeBody [ "s3cret" ] "token s3cret here")
        for r in Recordings.all do
            for (k, _) in r.Headers do
                Assert.Contains(k, Recordings.keptHeaders)
        let live = fixture.Run
        liveProbes () |> ignore
        let evidence = IO.File.ReadAllText(live.WriteEvidence())
        for s in LiveEnv.secrets () do
            Assert.DoesNotContain(s, evidence)
        for sensitive in [ "authorization"; "set-cookie"; "x-github-request-id"; "bearer " ] do
            Assert.DoesNotContain(sensitive, evidence.ToLowerInvariant())

    /// PF-013-O4: observations are refreshed when the provider contract changes: the live run re-observes every
    /// behaviour of the script, and any change from the recorded behaviour fails here, naming the probe.
    [<Fact>]
    member _.``PF-013-O4``() =
        let real = liveProbes ()
        Assert.Equal((fakeProbes ()).Length, real.Length)
        Assert.Empty(Conformance.failures real)
