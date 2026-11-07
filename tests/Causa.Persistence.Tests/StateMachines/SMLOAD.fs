// Owner: persistence. SM-LOAD transitions (state-machine-contracts.json).
namespace Causa.Persistence.Tests.StateMachines

open Xunit
open Causa.Persistence

module SMLOAD =
    open MachineFixtures

    let private target : InvestigationRef = { Address = address; Id = investigation }
    let private loadOk state input =
        match machine.StepLoad ctx state input with
        | Ok r -> r
        | Error f -> failwithf "StepLoad refused: %A" f

    [<Fact>]
    let ``T-L01`` () =
        let state, effects = loadOk NotLoaded (LoadInput.OpenRequested target)
        Assert.Equal(Loading(ctx.NextId, LoadHead), state)
        let r = List.exactlyOne (sends effects)
        Assert.Equal(GitHubRequests.GraphQlUrl, r.Url)
        Assert.Contains("refs/heads/causa-test", defaultArg r.Body "")

    [<Fact>]
    let ``T-L03`` () = Assert.Equal(Loaded(verified c2), fst (loadOk (Loading(RequestId 1L, LoadHead)) (LoadInput.LoadVerified(verified c2))))

    [<Fact>]
    let ``T-L04`` () =
        let failure = ProviderFailed(NotFound "investigation files")
        Assert.Equal(LoadFailed failure, fst (loadOk (Loading(RequestId 1L, LoadHead)) (LoadInput.LoadRejected failure)))

    [<Fact>]
    let ``T-L05`` () = Assert.Equal(Loaded(verified c2), fst (loadOk (Loaded(verified c2)) LoadInput.WentOffline))

    [<Fact>]
    let ``T-L06`` () = Assert.Equal(NotLoaded, fst (loadOk UnavailableOffline LoadInput.WentOnline))

    [<Fact>]
    let ``T-L07`` () = Assert.Equal(Loading(ctx.NextId, LoadHead), fst (loadOk (Loaded(verified c2)) LoadInput.RefreshRequested))

    [<Fact>]
    let ``T-L08`` () = Assert.Equal(Loading(ctx.NextId, LoadHead), fst (loadOk (LoadFailed(ProviderFailed AuthenticationFailed)) (LoadInput.OpenRequested target)))

    [<Fact>]
    let ``T-L09`` () = Assert.Equal(Loading(RequestId 1L, LoadHead), fst (loadOk (Loading(RequestId 1L, LoadHead)) (LoadInput.SupersededReply(RequestId 2L))))

    [<Fact>]
    let ``T-L10`` () = Assert.Equal(Loaded(verified c2), fst (loadOk (Loaded(verified c2)) (LoadInput.SupersededReply(RequestId 2L))))

    /// An answer to another request is SupersededReply; a provider refusal is LoadRejected with the typed cause.
    [<Fact>]
    let InterpretClassifiesTheAnswer () =
        let loading = Loading(RequestId 5L, LoadHead)
        Assert.Equal(LoadEvent(LoadInput.SupersededReply(RequestId 6L)), machine.InterpretLoad ctx loading (Answered(RequestId 6L, 200, [], "{}")))
        Assert.Equal(LoadEvent(LoadInput.LoadRejected(ProviderFailed AuthenticationFailed)), machine.InterpretLoad ctx loading (Answered(RequestId 5L, 401, [], """{"message":"Bad credentials"}""")))
