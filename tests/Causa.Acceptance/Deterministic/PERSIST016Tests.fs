// Owner: persistence. PERSIST-016 (deterministic, PAT-08): bounded retries, no tight loops.
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveScenarios
open UnknownScenario

module PERSIST016Tests =

    /// Under a network that fails every request: the save, then the reconciliation reads the machine allows.
    let private totalFailure name =
        let r = rig name
        let s0 = r.Create "Title" "Problem statement"
        // what the tab read before the save (its base head) is not part of the save's network activity
        let before = r.Fake.State.Received.Length
        for _ in 1..50 do
            r.Fake.Inject(LoseBeforeApplying, false)
        let mutable s = r.Step s0 SaveInput.SaveRequested
        for _ in 1..6 do
            s <- r.Due s
        s <- r.Step s SaveInput.WentOffline
        s <- r.Step s SaveInput.WentOnline
        for _ in 1..5 do
            s <- r.Step s SaveInput.CheckAgain
        r, s, before

    [<Fact>]
    let ``PERSIST-016-O1`` () =
        let r, _, before = totalFailure "persist016-o1"
        Assert.True(r.Fake.State.Received.Length - before <= 1 + 4)

    [<Fact>]
    let ``PERSIST-016-O2`` () =
        let r, _ = UnknownScenario.lost "persist016-o2" LoseAfterApplying
        for _ in 1..10 do
            r.Fake.Inject AdvanceFirst
        let r2 = rig "persist016-o2b"
        let s0 = r2.Create "T" "P"
        for _ in 1..10 do
            r2.Fake.Inject AdvanceFirst
        r2.Step s0 SaveInput.SaveRequested |> ignore
        Assert.True(r2.Mutations <= 4)

    /// O3: three automatic reads at least 2, 4 and 8 s apart, one more on going online, then only Check again.
    [<Fact>]
    let ``PERSIST-016-O3`` () =
        let r, s = UnknownScenario.lost "persist016-o3" LoseBeforeApplying
        Assert.Equal<int list>([ 2000 ], r.Scheduled)
        for _ in 1..3 do
            r.Fake.Inject(LoseBeforeApplying, false)
        let mutable s = s
        for _ in 1..3 do
            s <- r.Due s
        Assert.Equal<int list>([ 2000; 4000; 8000 ], r.Scheduled)
        Assert.Equal(SaveState.UnknownOutcome 0, s.State)
        let readsBefore = headReads r
        r.Fake.Inject(LoseBeforeApplying, false)
        s <- r.Step s SaveInput.WentOnline
        Assert.Equal(readsBefore + 1, headReads r)

    /// O4: a save that fails before it is sent is not retried automatically.
    [<Fact>]
    let ``PERSIST-016-O4`` () =
        let r, s = UnknownScenario.lost "persist016-o4" FailBeforeSending
        Assert.Equal("Offline", stateName s)
        let s' = r.Step s SaveInput.WentOnline
        Assert.Equal("Unsaved", stateName s')
        Assert.Equal(0, r.Mutations)
        Assert.Empty(r.Scheduled)

    /// O5: total failure: at most four mutations and at most four reconciliation reads in 60 s.
    [<Fact>]
    let ``PERSIST-016-O5`` () =
        let r, _, before = totalFailure "persist016-o5"
        let reads = r.Fake.State.Received |> List.skip before |> List.filter (fun (_, u) -> u.Contains "/git/ref/heads/") |> List.length
        Assert.True(r.Mutations <= 4)
        Assert.True(reads <= 4, sprintf "%d reconciliation reads" reads)

    /// O6 (PAT-08): counted under total network failure and under constant branch advance.
    [<Fact>]
    let ``PERSIST-016-O6`` () =
        let r, _, before = totalFailure "persist016-o6"
        Assert.True(r.Fake.State.Received.Length - before <= 8)
        let r2 = rig "persist016-o6b"
        let s0 = r2.Create "T" "P"
        for _ in 1..10 do
            r2.Fake.Inject AdvanceFirst
        let s = r2.Step s0 SaveInput.SaveRequested
        Assert.Equal(4, r2.Mutations)
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)
