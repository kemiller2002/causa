// Owner: persistence. T18: AR-014 secure capability isolation (deterministic; the stateful fake, no network).
namespace Causa.Acceptance.Deterministic

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

module AR014Tests =

    let private prefix = RepoPath "workspaces/demo/causa-app-ar014/investigations/00000000-0000-0000-0000-000000000014"

    let private put name text = PutFile(RepoPath(sprintf "workspaces/demo/causa-app-ar014/investigations/00000000-0000-0000-0000-000000000014/%s" name), Text.Encoding.UTF8.GetBytes(text: string))

    /// AR-014-O1: a credential gets only what it was granted. The read credential reads but cannot write (GitHub's
    /// recorded FORBIDDEN form, classified as a permission failure, nothing written); write access comes only from the
    /// permissions GitHub states, never from a successful read; a revoked credential is refused even after a success
    /// (no cached success masks it, PF-009-O2); and the live environment never lends one token's access to the other.
    [<Fact>]
    let ``AR-014-O1`` () =
        let fake = StatefulFake()
        let reader = Provider.over fake.Execute fake.ReadConnection
        let writer = Provider.over fake.Execute fake.WriteConnection
        let head = fake.Head()
        Assert.Equal(Ok head, reader.ReadHead())
        match reader.Commit head prefix "read token write" [ put "a.json" "{}" ] with
        | Error(PermissionDenied _) -> ()
        | other -> failwithf "the read credential wrote or was misclassified: %A" other
        Assert.Equal(head, fake.Head())
        Assert.Equal(ConnectAccepted false, reader.Connect())
        Assert.Equal(ConnectAccepted true, writer.Connect())
        match writer.Commit head prefix "write token write" [ put "a.json" "{}" ] with
        | Ok created -> Assert.Equal(created.Oid, fake.Head())
        | Error e -> failwithf "the write credential could not write: %A" e
        fake.RevokeToken fake.WriteToken
        Assert.Equal(Error AuthenticationFailed, writer.ReadHead())
        // Neither live token falls back to the other: each is read only from its own variable.
        Assert.Equal("CAUSA_GITHUB_TOKEN", LiveEnv.tokenVariable LiveEnv.Read)
        Assert.Equal("CAUSA_WRITE_TOKEN", LiveEnv.tokenVariable LiveEnv.Write)
        let saved = Environment.GetEnvironmentVariable "CAUSA_WRITE_TOKEN"
        let savedRead = Environment.GetEnvironmentVariable "CAUSA_GITHUB_TOKEN"
        try
            Environment.SetEnvironmentVariable("CAUSA_WRITE_TOKEN", null)
            Environment.SetEnvironmentVariable("CAUSA_GITHUB_TOKEN", "ar014-read-only-token")
            match LiveEnv.token LiveEnv.Write with
            | Error blocked -> Assert.StartsWith("BLOCKED: no credential", blocked)
            | Ok _ -> failwith "the write token fell back to the read token"
        finally
            Environment.SetEnvironmentVariable("CAUSA_WRITE_TOKEN", saved)
            Environment.SetEnvironmentVariable("CAUSA_GITHUB_TOKEN", savedRead)

    /// AR-014-O2: a secret is never echoed. The credential prints as "<credential>", travels only in the Authorization
    /// header of requests to api.github.com (never the URL or body), and the sanitized evidence of an exchange holds
    /// neither the token nor the Authorization header.
    [<Fact>]
    let ``AR-014-O2`` () =
        let token = "ghp_ar014SecretThatMustNeverAppear0000000"
        let connection = { Repository = { Owner = "o"; Name = "r"; Branch = "b" }; Credential = Credential.create token }
        Assert.Equal("<credential>", string connection.Credential)
        Assert.DoesNotContain(token, sprintf "%A" connection.Credential)
        Assert.DoesNotContain(token, sprintf "%O" connection)
        let requests =
            [ GitHubRequests.connect (RequestId 1L) connection
              GitHubRequests.readHead (RequestId 2L) connection
              GitHubRequests.readFile (RequestId 3L) connection (CommitOid "abc") (RepoPath "workspaces/demo/causa-app-x/investigations/i/manifest.json")
              GitHubRequests.compare (RequestId 4L) connection (CommitOid "abc") (CommitOid "def") 1
              match GitHubRequests.commit (RequestId 5L) connection (CommitOid "abc") prefix "h" "" [ put "a.json" "{}" ] with
              | Ok r -> r
              | Error e -> failwithf "%A" e ]
        for r in requests do
            Assert.True(LiveHttp.allowedHost r.Url, r.Url)
            Assert.DoesNotContain(token, r.Url)
            Assert.DoesNotContain(token, defaultArg r.Body "")
            let carrying = r.Headers |> List.filter (fun (_, v) -> v.Contains token)
            Assert.Equal<(string * string) list>([ "Authorization", "Bearer " + token ], carrying)
        Assert.False(LiveHttp.allowedHost "http://api.github.com/repos/o/r")
        Assert.False(LiveHttp.allowedHost "https://example.com/repos/o/r")
        let kept = Recordings.sanitizeHeaders [ "Authorization", "Bearer " + token; "Set-Cookie", "s=1"; "x-ratelimit-remaining", "10"; "x-github-request-id", "A:B" ]
        Assert.Equal<(string * string) list>([ "x-ratelimit-remaining", "10" ], kept)
        Assert.Equal("echo <credential> back", Recordings.sanitizeBody [ token ] ("echo " + token + " back"))
        // A request to another host is refused before sending: the token never leaves for it.
        let log = ResizeArray<Observation>()
        let execute = LiveHttp.executor (TimeSpan.FromSeconds 1.0) log
        let outcome = execute { (List.head requests) with Url = "https://example.com/steal" }
        Assert.Equal(NotAnswered(RequestId 1L, TransportFailure.FailedBeforeSending), outcome)
        Assert.DoesNotContain(token, sprintf "%A" (List.ofSeq log))
        for r in Recordings.all do
            Assert.DoesNotContain("ghp_", r.Body)
            Assert.DoesNotContain("github_pat_", r.Body)
            Assert.True(r.Headers |> List.forall (fun (k, _) -> Recordings.keptHeaders.Contains(k.ToLowerInvariant())), r.Form)
