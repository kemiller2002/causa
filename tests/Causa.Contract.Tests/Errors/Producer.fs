// Owner: persistence. T18: IF41-ERRORS producer contract tests (github-error-matrix.json).
namespace Causa.Contract.Tests.Errors

open Xunit
open Causa.Persistence
open Causa.TestSupport

module Producer =

    let private classOf (r: RecordedAnswer) = fst (Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 9L) r))

    /// Every matrix row has at least one recorded answer form, and each form classifies into its row.
    [<Fact>]
    let EveryMatrixRowClassifiesItsRecordedAnswer () =
        for r in Recordings.all do
            Assert.True((classOf r = r.ExpectedClass), sprintf "%s: %s, expected %s" r.Form (MatrixClass.id (classOf r)) (MatrixClass.id r.ExpectedClass))
        let answeredRows =
            [ GhAuth; GhRatePrimary; GhRateSecondary; GhStaleHead; GhLargeFile; GhPermission; GhValidation; GhNotFound; GhHistoryRewritten; GhUnknownOutcome; GhMalformed; GhTruncatedList ]
        for row in answeredRows do
            Assert.True(Recordings.all |> List.exists (fun r -> r.ExpectedClass = row), sprintf "no recorded form for %s" (MatrixClass.id row))
        Assert.Equal(GhNetworkBefore, fst (Classify.classifyWithClass Operation.ReadHead (NotAnswered(RequestId 1L, TransportFailure.FailedBeforeSending))))
        Assert.Equal(GhNetworkAfter, fst (Classify.classifyWithClass Operation.ReadHead (NotAnswered(RequestId 1L, TransportFailure.LostAfterSending))))
        Assert.Equal(GhNetworkAfter, fst (Classify.classifyWithClass Operation.ReadHead (NotAnswered(RequestId 1L, TransportFailure.TimedOut))))

    /// A status GitHub gives several meanings is never classified by status alone: 403 is a permission failure, a
    /// primary or secondary rate limit, or a large file by its other evidence; 404 is a hidden repository at Connect,
    /// a missing file, or rewritten history at compare; GraphQL 200 is success, a stale head in two forms, or a refusal.
    [<Fact>]
    let SharedStatusesNeedOtherEvidence () =
        let classify op status headers body = fst (Classify.classifyWithClass op (Answered(RequestId 1L, status, headers, body)))
        let file = Operation.ReadFile(RepoPath "w/i/a.json")
        let msg m = sprintf """{"message":"%s"}""" m
        Assert.Equal(GhPermission, classify file 403 [ "x-ratelimit-remaining", "10" ] (msg "Resource not accessible by personal access token"))
        Assert.Equal(GhRatePrimary, classify file 403 [ "x-ratelimit-remaining", "0" ] (msg "Resource not accessible by personal access token"))
        Assert.Equal(GhRateSecondary, classify file 403 [ "x-ratelimit-remaining", "10"; "retry-after", "5" ] (msg "Forbidden"))
        Assert.Equal(GhRateSecondary, classify file 403 [] (msg "You have exceeded a secondary rate limit"))
        Assert.Equal(GhLargeFile, (Recordings.form "rest-contents-too-large-403" |> classOf))
        Assert.Equal(GhPermission, classify Operation.Connect 404 [] (msg "Not Found"))
        Assert.Equal(GhNotFound, classify file 404 [] (msg "Not Found"))
        Assert.Equal(GhHistoryRewritten, classify (Operation.Compare(CommitOid "a")) 404 [] (msg "Not Found"))
        let mutation = Operation.Mutation(CommitOid "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b")
        Assert.Equal(Success, classify mutation 200 [] (Recordings.form "graphql-commit-created").Body)
        Assert.Equal(GhStaleHead, classify mutation 200 [] (Recordings.form "graphql-stale-data").Body)
        Assert.Equal(GhStaleHead, classify mutation 200 [] (Recordings.form "graphql-forbidden-moved-head").Body)
        Assert.Equal(GhPermission, classify mutation 200 [] (Recordings.form "graphql-forbidden-permission").Body)
        // The moved-head form names both oids.
        match Classify.classify mutation (Recordings.outcome (RequestId 1L) (Recordings.form "graphql-forbidden-moved-head")) with
        | Error(StaleHead(CommitOid expected, Some(CommitOid actual))) ->
            Assert.Equal("9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b", expected)
            Assert.Equal("4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e", actual)
        | other -> failwithf "%A" other
        // No class retries beyond its bound.
        Assert.Equal(3, MatrixClass.maxAttempts GhStaleHead)
        Assert.Equal(0, MatrixClass.maxAttempts GhRateSecondary)
        Assert.Equal(1, MatrixClass.maxAttempts GhLargeFile)

    /// Write access comes only from the permissions GitHub states, never from a successful read.
    [<Fact>]
    let WriteAccessOnlyFromPermissions () =
        let connect body = Connect.connector.InterpretConnect (RequestId 3L) (Answered(RequestId 3L, 200, [], body))
        Assert.Equal(ConnectAccepted true, connect """{"permissions":{"admin":false,"push":true,"pull":true}}""")
        Assert.Equal(ConnectAccepted false, connect """{"permissions":{"admin":false,"push":false,"pull":true}}""")
        match connect """{"full_name":"o/r"}""" with
        | ConnectRejected(Malformed _) -> ()
        | other -> failwithf "a read without stated permissions granted %A" other
        Assert.Equal(ConnectSuperseded, Connect.connector.InterpretConnect (RequestId 4L) (Answered(RequestId 3L, 200, [], "{}")))
        let fake = StatefulFake()
        Assert.Equal(ConnectAccepted false, (Provider.over fake.Execute fake.ReadConnection).Connect())
        Assert.Equal(ConnectAccepted true, (Provider.over fake.Execute fake.WriteConnection).Connect())
