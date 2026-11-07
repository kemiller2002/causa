// Owner: persistence. T18: PERSIST-025 testable provider boundary: one port, the stateful fake and real GitHub.
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PERSIST025Tests(fixture: LiveFixture<PERSIST025Tests>) =

    let prefix = RepoPath "workspaces/demo/causa-app-persist025/investigations/00000000-0000-0000-0000-000000000025"
    let path name = let (RepoPath p) = prefix in RepoPath(p + "/" + name)
    let put name (text: string) = PutFile(path name, Encoding.UTF8.GetBytes text)
    let ok what r = r |> Result.defaultWith (fun e -> failwithf "%s failed: %A" what e)

    /// An Execute that answers each request with the recorded real form for it, the third adapter of the same port.
    let replay (form: string) : Execute = fun request -> Recordings.outcome request.Id (Recordings.form form)

    interface IClassFixture<LiveFixture<PERSIST025Tests>>

    /// PERSIST-025-O1: the GitHub adapter sits behind the provider port. The port is built over nothing but an Execute,
    /// so the stateful fake, recorded real answers and the real network are interchangeable adapters with the same
    /// classification and result types.
    [<Fact>]
    member _.``PERSIST-025-O1``() =
        let fake = StatefulFake()
        let overFake = Provider.over fake.Execute fake.WriteConnection
        Assert.Equal(Ok(fake.Head()), overFake.ReadHead())
        let overRecording = Provider.over (replay "rest-ref-head") fake.WriteConnection
        Assert.Equal(Ok Recordings.expectedHead, overRecording.ReadHead())
        let overRecordedStale = Provider.over (replay "graphql-forbidden-moved-head") fake.WriteConnection
        Assert.Equal("StaleHead", Conformance.kind (overRecordedStale.Commit Recordings.expectedHead prefix "x" [ put "a.json" "{}" ]))
        let live = fixture.Run
        let overNetwork : ProviderPort = live.Reader
        Assert.Equal("ok", Conformance.kind (overNetwork.ReadHead()))

    /// PERSIST-025-O2: the stateful fake enforces expectedHeadOid, keeps real commit ancestry, accepts commits from
    /// simulated other writers, and loses an answer after applying or before applying a mutation.
    [<Fact>]
    member _.``PERSIST-025-O2``() =
        let fake = StatefulFake()
        let writer = Provider.over fake.Execute fake.WriteConnection
        let h0 = fake.Head()
        let c1 = writer.Commit h0 prefix "one" [ put "a.json" "1" ] |> ok "first commit"
        Assert.True(fake.IsAncestor(h0, c1.Oid))
        Assert.Equal(Some(let (CommitOid o) = h0 in o), (fake.Commit c1.Oid).Value.Parent)
        // expectedHeadOid is enforced: a commit on the old head is refused and writes nothing.
        Assert.Equal("StaleHead", Conformance.kind (writer.Commit h0 prefix "stale" [ put "b.json" "2" ]))
        Assert.Equal(c1.Oid, fake.Head())
        // Another writer moves the head; ancestry is real, so compare lists exactly its commit after ours.
        let other = fake.CommitAsOtherWriter("other writer", [ "workspaces/demo/causa-app-other/investigations/x/o.json", Some(Encoding.UTF8.GetBytes "o") ])
        Assert.True(fake.IsAncestor(c1.Oid, other))
        Assert.Equal("StaleHead", Conformance.kind (writer.Commit c1.Oid prefix "after other" [ put "c.json" "3" ]))
        let page = writer.Compare c1.Oid other 1 |> ok "compare"
        Assert.Equal("ahead", page.Status)
        Assert.Equal<CommitOid list>([ other ], page.Commits |> List.map (fun c -> c.Oid))
        // Lost before applying: nothing written.
        fake.Inject LoseBeforeApplying
        Assert.Equal("NetworkUnavailable.LostAfterSending", Conformance.kind (writer.Commit other prefix "lost before" [ put "d.json" "4" ]))
        Assert.Equal(other, fake.Head())
        // Lost after applying: the commit exists, with the marker in its message, though the answer never came.
        fake.Inject LoseAfterApplying
        Assert.Equal("NetworkUnavailable.LostAfterSending", Conformance.kind (writer.Commit other prefix "lost after marker-7" [ put "e.json" "5" ]))
        let applied = fake.Head()
        Assert.NotEqual(other, applied)
        Assert.Equal("lost after marker-7", (fake.Commit applied).Value.Message)
        Assert.Equal(Some(Encoding.UTF8.GetBytes "5"), fake.FileAt(applied, path "e.json"))
        // Rewritten history: the expected head is no longer reachable.
        let rewritten = fake.RewriteHistory()
        Assert.Equal("HistoryRewritten", Conformance.kind (writer.Compare applied rewritten 1))
        // The whole probe script passes over the fake.
        Assert.Empty(Conformance.failures (Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore))

    /// PERSIST-025-O3: the same tests run against the real GitHub test repository.
    [<Fact>]
    member _.``PERSIST-025-O3``() =
        let live = fixture.Run
        let probes = Conformance.run (Conformance.overLive live) live.Created
        Assert.Empty(Conformance.failures probes)

    /// PERSIST-025-O4: a fake-only pass is never reported as live verification: a passing fake report is not live
    /// verification, and without the live secret the live fixture refuses with BLOCKED instead of passing.
    [<Fact>]
    member _.``PERSIST-025-O4``() =
        let fakeReport : Conformance.Report =
            { Mode = "fake"; Probes = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore; CreatedCommits = [ CommitOid "x" ] }
        Assert.True(fakeReport.Passed)
        Assert.False(fakeReport.IsLiveVerification)
        let saved = Environment.GetEnvironmentVariable "CAUSA_WRITE_TOKEN"
        try
            Environment.SetEnvironmentVariable("CAUSA_WRITE_TOKEN", null)
            let blocked = Assert.Throws<InvalidOperationException>(fun () -> (new LiveRun("PERSIST025Blocked") :> IDisposable).Dispose())
            Assert.StartsWith("BLOCKED: no credential", blocked.Message)
        finally
            Environment.SetEnvironmentVariable("CAUSA_WRITE_TOKEN", saved)
        // And the live run itself is live verification only from real commits.
        let live = fixture.Run
        let probes = Conformance.run (Conformance.overLive live) live.Created
        let liveReport : Conformance.Report = { Mode = "live-github"; Probes = probes; CreatedCommits = live.CreatedCommits }
        Assert.True(liveReport.IsLiveVerification, sprintf "%A" (Conformance.failures probes))

    /// PERSIST-025-O5 (PAT-02..08 in both modes): the probe script runs over the fake and live, and the live run's
    /// evidence names the real commits it created, each of which GitHub lists after the run's base head.
    [<Fact>]
    member _.``PERSIST-025-O5``() =
        let fakeProbes = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
        let live = fixture.Run
        let liveProbes = Conformance.run (Conformance.overLive live) live.Created
        Assert.Empty(Conformance.divergence fakeProbes liveProbes)
        let created = live.CreatedCommits
        Assert.NotEmpty(created)
        let head = live.Reader.ReadHead() |> ok "head"
        let listed = live.Reader.Compare live.BaseHead head 1 |> ok "compare"
        for c in created do
            Assert.Contains(c, listed.Commits |> List.map (fun x -> x.Oid))
        let evidence = File.ReadAllText(live.WriteEvidence())
        for (CommitOid oid) in created do
            Assert.Contains(oid, evidence)
        Assert.Contains("\"providerMode\":\"live-github\"", evidence)
        for secret in LiveEnv.secrets () do
            Assert.DoesNotContain(secret, evidence)
