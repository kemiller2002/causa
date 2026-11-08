// Owner: persistence. T18: IF41-WIRE producer contract tests.
namespace Causa.Contract.Tests.Wire

open Xunit
open Causa.Persistence
open Causa.TestSupport

module Producer =

    let private connection token = { Repository = { Owner = "o"; Name = "r"; Branch = "b" }; Credential = Credential.create token }

    let private requests token =
        let c = connection token
        [ GitHubRequests.connect (RequestId 1L) c
          GitHubRequests.readHead (RequestId 2L) c
          GitHubRequests.readFile (RequestId 3L) c (CommitOid "abc") (RepoPath "w/i/manifest.json")
          GitHubRequests.readBlob (RequestId 4L) c "sha"
          GitHubRequests.compare (RequestId 5L) c (CommitOid "a") (CommitOid "b") 2
          GitHubRequests.readCommit (RequestId 6L) c (CommitOid "abc")
          GitHubRequests.commit (RequestId 7L) c (CommitOid "abc") (RepoPath "w/i") "h" "" [ PutFile(RepoPath "w/i/a.json", [| 1uy |]) ] |> Result.defaultWith (failwithf "%A") ]

    /// Every request carries the identity it was built with, and the identities are distinct.
    [<Fact>]
    let RequestsCarryIdentity () =
        let rs = requests "t"
        Assert.Equal<RequestId list>([ 1L .. 7L ] |> List.map RequestId, rs |> List.map (fun r -> r.Id))
        Assert.True(GitHubRequests.isMutation (List.last rs))
        Assert.True(rs |> List.take 6 |> List.forall (GitHubRequests.isMutation >> not))
        // A change outside the prefix is refused before a request exists (PERSIST-006).
        match GitHubRequests.commit (RequestId 8L) (connection "t") (CommitOid "abc") (RepoPath "w/i") "h" "" [ PutFile(RepoPath "w/i/../x.json", [| 1uy |]) ] with
        | Error _ -> ()
        | Ok r -> failwithf "a path outside the prefix was sent: %A" r.Url

    /// Every outcome the fake host returns names the request it answers; the port refuses an answer to another one.
    [<Fact>]
    let OutcomeNamesItsRequest () =
        let fake = StatefulFake()
        for r in requests fake.WriteToken |> List.map (fun r -> { r with Url = r.Url.Replace("/repos/o/r", "/repos/causa-test-owner/causa-test-data") }) do
            match fake.Execute r with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> Assert.Equal(r.Id, id)
        let wrong : Execute = fun _ -> Answered(RequestId 999L, 200, [], """{"object":{"sha":"x"}}""")
        match (Provider.over wrong fake.ReadConnection).ReadHead() with
        | Error(Malformed _) -> ()
        | other -> failwithf "an answer to another request was accepted: %A" other

    /// The credential appears only in the Authorization header, and only for https://api.github.com.
    [<Fact>]
    let CredentialOnlyInAuthorizationHeader () =
        let token = "ghp_wireProducerTokenValue"
        for r in requests token do
            Assert.StartsWith("https://api.github.com/", r.Url)
            Assert.DoesNotContain(token, r.Url)
            Assert.DoesNotContain(token, defaultArg r.Body "")
            Assert.Equal<(string * string) list>([ "Authorization", "Bearer " + token ], r.Headers |> List.filter (fun (_, v) -> v.Contains token))
