// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-015 credentials and security: authenticated reads and writes only, two separate secrets, never exposed, and a
// missing secret is BLOCKED.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF015Tests(fixture: LiveFixture<PF015Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf015/investigations/00000000-0000-0000-0000-000000000015"
    let liveProbes () =
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created

    let withEnv (values: (string * string option) list) (f: unit -> 'a) : 'a =
        // flow-local: the process environment is never changed (a concurrent live test keeps its credentials)
        LiveEnv.withVariables values f

    /// Every request persistence can build, over one connection.
    let everyRequest (c: ConnectionContext) : HttpRequest list =
        let head = CommitOid "4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e"
        let file = RepoPath((let (RepoPath p) = prefix in p) + "/a.json")
        let target = { Address = (WorkspaceAddress.workingCopy [] "pf015").Value; Id = Causa.Domain.InvestigationId(Guid "00000000-0000-4000-8000-000000000015") }
        [ GitHubRequests.connect (RequestId 1L) c
          GitHubRequests.readHead (RequestId 2L) c
          GitHubRequests.readFile (RequestId 3L) c head file
          GitHubRequests.readDirectory (RequestId 4L) c head prefix
          GitHubRequests.readBlob (RequestId 5L) c "0b1c"
          GitHubRequests.compare (RequestId 6L) c head head 1
          Writes.headAndFiles (RequestId 7L) c target
          GitHubRequests.commit (RequestId 8L) c head prefix "h" "" [ PutFile(file, [| 1uy |]) ] |> Result.defaultWith (fun e -> failwithf "%A" e) ]

    interface IClassFixture<LiveFixture<PF015Tests>>

    /// PF-015-O1: every catalog read and every write carries an authenticated credential; there is no anonymous mode:
    /// every request persistence builds sends Authorization to api.github.com, and real GitHub refuses a credential it
    /// does not accept instead of answering anonymously.
    [<Fact>]
    member _.``PF-015-O1``() =
        let fake = StatefulFake()
        for r in everyRequest fake.ReadConnection do
            Assert.True(r.Headers |> List.exists (fun (k, v) -> k = "Authorization" && v = "Bearer fake-read-token"), sprintf "%s has no credential" r.Url)
            Assert.StartsWith(GitHubRequests.ApiBase + "/", r.Url)
        let run = fixture.Run
        Assert.Equal("AuthenticationFailed", Conformance.kind ((Provider.over run.Execute { run.ReadConnection with Credential = Credential.create "causa-pf015-not-a-token" }).ReadHead()))
        Assert.Equal("ok", Conformance.kind (run.Reader.ReadHead()))

    /// PF-015-O2: authenticated reads use CAUSA_GITHUB_TOKEN with read-only permission: it reads the head and is
    /// refused a write on real GitHub.
    [<Fact>]
    member _.``PF-015-O2``() =
        Assert.Equal("CAUSA_GITHUB_TOKEN", LiveEnv.tokenVariable LiveEnv.Read)
        let probes = liveProbes ()
        Assert.Equal("ok", (probes |> List.find (fun p -> p.Name = "read head with the read credential")).Actual)
        Assert.Equal("PermissionDenied", (probes |> List.find (fun p -> p.Name = "commit with the read credential")).Actual)

    /// PF-015-O3: live writes use the separate CAUSA_WRITE_TOKEN and neither secret falls back to the other: with
    /// either one unset the other access is BLOCKED, and on real GitHub the write token commits.
    [<Fact>]
    member _.``PF-015-O3``() =
        Assert.Equal("CAUSA_WRITE_TOKEN", LiveEnv.tokenVariable LiveEnv.Write)
        withEnv [ "CAUSA_WRITE_TOKEN", None; "CAUSA_GITHUB_TOKEN", Some "pf015-read" ] (fun () -> Assert.True(LiveEnv.token LiveEnv.Write |> Result.isError))
        withEnv [ "CAUSA_GITHUB_TOKEN", None; "CAUSA_WRITE_TOKEN", Some "pf015-write" ] (fun () -> Assert.True(LiveEnv.token LiveEnv.Read |> Result.isError))
        let probes = liveProbes ()
        Assert.Equal("ok", (probes |> List.find (fun p -> p.Name = "commit at the current head")).Actual)
        Assert.NotEmpty(fixture.Run.CreatedCommits)

    /// PF-015-O4: credentials are never embedded, printed, put in URLs or included in traces, logs or artifacts: a
    /// credential prints as <credential>, no request URL or body carries it, and the live run's log and evidence hold
    /// neither secret.
    [<Fact>]
    member _.``PF-015-O4``() =
        let token = "pf015-secret-value"
        let c = { (StatefulFake()).ReadConnection with Credential = Credential.create token }
        Assert.DoesNotContain(token, sprintf "%A" c)
        Assert.DoesNotContain(token, string c.Credential)
        for r in everyRequest c do
            Assert.DoesNotContain(token, r.Url)
            Assert.DoesNotContain(token, defaultArg r.Body "")
            Assert.All(r.Headers |> List.filter (fun (k, _) -> k <> "Authorization"), fun (_, v) -> Assert.DoesNotContain(token, v))
        let live = fixture.Run
        liveProbes () |> ignore
        let evidence = IO.File.ReadAllText(live.WriteEvidence())
        for s in LiveEnv.secrets () do
            Assert.DoesNotContain(s, evidence)
            Assert.All(live.Log, fun o -> Assert.False(o.Url.Contains s || o.Body.Contains s, o.Url))

    /// PF-015-O5: a missing secret yields BLOCKED, not PASS: with either secret unset the live fixture refuses to start
    /// with "BLOCKED: no credential", and with both present it starts.
    [<Fact>]
    member _.``PF-015-O5``() =
        for variable in [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] do
            withEnv [ variable, None ] (fun () ->
                let e = Assert.Throws<InvalidOperationException>(fun () -> new LiveRun("PF015Probe") |> ignore)
                Assert.StartsWith("BLOCKED: no credential", e.Message))
        Assert.Equal("ok", Conformance.kind (fixture.Run.Reader.ReadHead()))
