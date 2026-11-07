// Owner: persistence. T18: IF41-ERRORS as persistence consumes it.
namespace Causa.Contract.Tests.Errors

open Xunit
open Causa.Persistence

module ConsumerPersistence =

    /// Every typed error has a user message; a rate limit never reads as a permission problem.
    [<Fact>]
    let EveryProviderErrorHasAMessage () =
        let errors =
            [ AuthenticationFailed; PermissionDenied "write"; RateLimited(Primary(Some 1794070800L)); RateLimited(Primary None)
              RateLimited(Secondary(Some 60)); RateLimited(Secondary None); StaleHead(CommitOid "a", None); ValidationFailed [ "bad" ]
              NotFound "x"; HistoryRewritten(CommitOid "a"); NetworkUnavailable TransportFailure.FailedBeforeSending
              NetworkUnavailable TransportFailure.LostAfterSending; NetworkUnavailable TransportFailure.TimedOut; Malformed "m"
              ReadBackMismatch(CommitOid "a"); Unrecognised 502 ]
        for e in errors do
            Assert.False(System.String.IsNullOrWhiteSpace(Classify.message e), sprintf "%A" e)
        for e in [ RateLimited(Primary None); RateLimited(Secondary None) ] do
            Assert.DoesNotContain("cannot write", Classify.message e)
            Assert.DoesNotContain("permission", Classify.message e)
        Assert.DoesNotContain("refused", Classify.message (StaleHead(CommitOid "a", None)))
