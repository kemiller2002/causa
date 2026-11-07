// Owner: persistence. PERSIST-010 (deterministic): at most three automatic rebases, four mutations per save.
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveScenarios

module PERSIST010Tests =

    /// A save whose branch advances before every attempt.
    let private alwaysAdvancing (run: string) =
        let r = rig run
        let s0 = r.Create "Title" "Problem statement"
        for _ in 1..10 do
            r.Fake.Inject AdvanceFirst
        r, r.Step s0 SaveInput.SaveRequested

    [<Fact>]
    let ``PERSIST-010-O1`` () =
        let r, s = alwaysAdvancing "persist010-o1"
        Assert.Equal(4, r.Mutations)
        Assert.Equal(3, s.RebaseAttempts)

    /// O2: every attempt follows a fresh read of the branch head.
    [<Fact>]
    let ``PERSIST-010-O2`` () =
        let r, _ = alwaysAdvancing "persist010-o2"
        let received = r.Fake.State.Received
        let isMutation (m, _) = m = "Mutation"
        let isHeadRead (_, u: string) = u.Contains "/git/ref/heads/"
        let indexes = received |> List.indexed |> List.filter (snd >> isMutation) |> List.map fst
        for (a, b) in List.pairwise indexes do
            Assert.True(received[a + 1 .. b - 1] |> List.exists isHeadRead, "an attempt was sent without a fresh head read")

    /// O3: at the limit, Save failed (the repository kept changing) with the change kept unsaved.
    [<Fact>]
    let ``PERSIST-010-O3`` () =
        let _, s = alwaysAdvancing "persist010-o3"
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)
        Assert.NotEmpty(s.Unsaved)

    /// O4 (PAT-08): no more than four mutations for one save request.
    [<Fact>]
    let ``PERSIST-010-O4`` () =
        let r, _ = alwaysAdvancing "persist010-o4"
        Assert.True(r.Mutations <= 4)
