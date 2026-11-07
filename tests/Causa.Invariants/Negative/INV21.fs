// Owner: domain (written by the T18 completion lane under the coordinator's ownership exception).
// INV-21 negative: the credential is sent only in the Authorization header over HTTPS to the GitHub API, never to a
// plaintext or other host (Rehearsal 7 F14), and never appears in a URL, body, printout or evidence.
namespace Causa.Invariants.Negative

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type INV21() =
    let token = "inv21-credential-value"

    let connection : ConnectionContext =
        { Repository = { Owner = "causa-test-owner"; Name = "causa-test-data"; Branch = "causa-test" }; Credential = Credential.create token }

    /// Every request persistence builds for the application.
    let requests () : HttpRequest list =
        let head = CommitOid "4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e"
        let prefix = RepoPath "workspaces/demo/causa-app-inv21/investigations/00000000-0000-4000-8000-000000000021"
        let file = let (RepoPath p) = prefix in RepoPath(p + "/manifest.json")
        let target = { Address = (WorkspaceAddress.workingCopy [] "inv21").Value; Id = Causa.Domain.InvestigationId(Guid "00000000-0000-4000-8000-000000000021") }
        [ GitHubRequests.connect (RequestId 1L) connection
          GitHubRequests.readHead (RequestId 2L) connection
          GitHubRequests.readFile (RequestId 3L) connection head file
          GitHubRequests.readDirectory (RequestId 4L) connection head prefix
          GitHubRequests.readBlob (RequestId 5L) connection "0b1c"
          GitHubRequests.compare (RequestId 6L) connection head head 1
          Writes.headAndFiles (RequestId 7L) connection target
          GitHubRequests.commit (RequestId 8L) connection head prefix "inv21" "" [ PutFile(file, [| 1uy |]) ] |> Result.defaultWith (fun e -> failwithf "%A" e) ]

    /// Destinations the credential must never reach: plaintext, another host, a look-alike host, a userinfo trick,
    /// another port and the site's raw-content host.
    let forbidden =
        [ "http://api.github.com/repos/o/r"
          "https://example.com/repos/o/r"
          "https://api.github.com.example.com/repos/o/r"
          "https://api.github.com@example.com/repos/o/r"
          "https://api.github.com:8443/repos/o/r"
          "https://raw.githubusercontent.com/o/r/main/a.json"
          "ftp://api.github.com/repos/o/r" ]

    [<Fact>]
    member _.CredentialDestinations() =
        // Every request goes to https://api.github.com with the credential in the Authorization header alone.
        for r in requests () do
            let uri = Uri r.Url
            Assert.Equal("https", uri.Scheme)
            Assert.Equal("api.github.com", uri.Host)
            Assert.Equal(443, uri.Port)
            Assert.DoesNotContain(token, r.Url)
            Assert.DoesNotContain(token, defaultArg r.Body "")
            let carrying = r.Headers |> List.filter (fun (_, v) -> v.Contains token)
            Assert.Equal<(string * string) list>([ "Authorization", "Bearer " + token ], carrying)
            Assert.True(LiveHttp.allowedHost r.Url, r.Url)
        // A request to any other destination is refused before it is sent, and the refusal records no credential.
        let log = ResizeArray<Observation>()
        let execute = LiveHttp.executor (TimeSpan.FromSeconds 1.0) log
        let template = List.head (requests ())
        for url in forbidden do
            Assert.False(LiveHttp.allowedHost url, url)
            match execute { template with Url = url } with
            | NotAnswered(_, TransportFailure.FailedBeforeSending) -> ()
            | other -> failwithf "a request to %s was not refused before sending: %A" url other
        Assert.Equal(forbidden.Length, log.Count)
        Assert.All(log, fun o -> Assert.True(o.Status.IsNone && not (o.Url.Contains token) && not (o.Body.Contains token), o.Url))
        // The credential never prints: not in %A of the connection, not in ToString.
        Assert.DoesNotContain(token, sprintf "%A" connection)
        Assert.DoesNotContain(token, string connection.Credential)
        Assert.DoesNotContain(token, sprintf "%A" (requests () |> List.map (fun r -> r.Url, r.Body)))
