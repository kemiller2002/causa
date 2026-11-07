// Owner: domain (written by the T18 completion lane under the coordinator's ownership exception).
// INV-22 negative: every recorded GitHub answer form is classified from its typed content into its own cause; a rate
// limit is never a missing permission and a moved head is never a refusal.
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.TestSupport

type INV22() =
    let classify (form: string) =
        let r = Recordings.form form
        Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 1L) r)

    [<Fact>]
    member _.RecordedGitHubAnswerForms() =
        // Every recorded form lands in its github-error-matrix class.
        for r in Recordings.all do
            let cls = fst (Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 1L) r))
            Assert.True((cls = r.ExpectedClass), sprintf "%s classified as %s, expected %s" r.Form (MatrixClass.id cls) (MatrixClass.id r.ExpectedClass))
        // A moved head in either form GitHub sends (STALE_DATA; FORBIDDEN 'is at <oid> but expected <oid>', KD-022) is a
        // stale head naming the expected head, never a permission refusal.
        for f in [ "graphql-stale-data"; "graphql-forbidden-moved-head" ] do
            match classify f with
            | GhStaleHead, Error(StaleHead(expected, _)) -> Assert.Equal(Recordings.expectedHead, expected)
            | other -> failwithf "%s classified as %A" f other
        // HTTP 403 secondary-limit and retry-after answers (KD-020) and the GraphQL rate limit are rate limits, never a
        // missing permission.
        for f in [ "rest-403-secondary-rate-limit"; "rest-403-retry-after"; "graphql-rate-limited" ] do
            match classify f with
            | GhRateSecondary, Error(RateLimited(Secondary _)) -> ()
            | other -> failwithf "%s classified as %A" f other
        for f in [ "rest-403-primary-rate-limit"; "rest-429-primary-rate-limit" ] do
            match classify f with
            | GhRatePrimary, Error(RateLimited(Primary _)) -> ()
            | other -> failwithf "%s classified as %A" f other
        // A real permission refusal stays a permission refusal; a bad credential is authentication; validation is
        // validation: the causes are distinct.
        let causes =
            [ "graphql-forbidden-permission"; "rest-401-bad-credentials"; "graphql-stale-data"; "rest-403-secondary-rate-limit"; "rest-422-validation" ]
            |> List.map (classify >> snd >> Conformance.kind)
        Assert.Equal<string list>([ "PermissionDenied"; "AuthenticationFailed"; "StaleHead"; "RateLimited.Secondary"; "ValidationFailed" ], causes)
        // A network failure is never a stale head or a conflict.
        for f in [ TransportFailure.FailedBeforeSending; TransportFailure.LostAfterSending; TransportFailure.TimedOut ] do
            match Classify.classify (Operation.Mutation Recordings.expectedHead) (NotAnswered(RequestId 1L, f)) with
            | Error(NetworkUnavailable g) -> Assert.Equal(f, g)
            | other -> failwithf "a %A transport failure classified as %A" f other
