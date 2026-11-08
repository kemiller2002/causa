// Owner: persistence. PERSIST-011 (deterministic): the same investigation changed remotely is a genuine conflict.
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveScenarios

module ConflictScenario =
    /// Tab A saves an investigation; tab B (another tab of the same user) opens and saves it; tab A then saves its own
    /// unsaved change, which meets B's commit.
    let runOver (fake: StatefulFake) (name: string) =
        let a = SaveRig(fake, name)
        let b = SaveRig(fake, name)
        let saved = savedInvestigation a
        let target : InvestigationRef = { Address = a.Address; Id = saved.Id }
        let vB = b.Load target |> Result.defaultWith (failwithf "%A")
        let sB = b.Observe (b.Machine.Initial b.Writable vB) "Tab B's observation"
        let sB' = b.Step sB SaveInput.SaveRequested
        if stateName sB' <> "Saved" then failwithf "tab B did not save: %A" sB'.State
        let otherCommit = fake.Head()
        let sA = a.Observe saved "Tab A's unsaved observation"
        let mutationsBefore = a.Mutations
        let conflicted = a.Step sA SaveInput.SaveRequested
        a, conflicted, otherCommit, mutationsBefore, sA

    let run (name: string) = runOver (StatefulFake()) name

module PERSIST011Tests =
    open ConflictScenario

    /// O1: the investigation's own files differ between base and new head: a conflict.
    [<Fact>]
    let ``PERSIST-011-O1`` () =
        let _, s, _, _, _ = run "persist011-o1"
        Assert.Equal("Conflict", stateName s)

    /// O2: another tab of the same user is such a writer.
    [<Fact>]
    let ``PERSIST-011-O2`` () =
        let a, s, other, _, _ = run "persist011-o2"
        Assert.Equal("Conflict", stateName s)
        Assert.Contains("Tab B's observation", eventsText a s)
        Assert.Equal(other, a.Fake.Head())

    /// O3: no automatic rebase, merge or overwrite: one mutation was refused, none resent. The investigation's own
    /// files are compared by blob id, so it holds even when a compare answer lists no files (as a provider may).
    [<Fact>]
    let ``PERSIST-011-O3`` () =
        let a, s, other, before, _ = run "persist011-o3"
        Assert.Equal(before + 1, a.Mutations)
        Assert.Equal(0, s.RebaseAttempts)
        Assert.Equal(other, a.Fake.Head())
        let quiet = StatefulFake()
        quiet.CompareWithoutFiles()
        let a2, s2, other2, before2, _ = runOver quiet "persist011-o3b"
        Assert.Equal("Conflict", stateName s2)
        Assert.Equal(before2 + 1, a2.Mutations)
        Assert.Equal(other2, a2.Fake.Head())

    /// O4: Conflict keeps every unsaved change, unchanged and visible.
    [<Fact>]
    let ``PERSIST-011-O4`` () =
        let _, s, _, _, beforeSave = run "persist011-o4"
        Assert.Equal<Causa.Domain.CommandEnvelope list>(beforeSave.Unsaved, s.Unsaved)
        match s.State with
        | SaveState.Conflict(Reviewable remote) -> Assert.Equal("Pump trips at noon", remote.Investigation.Title.Value)
        | other -> failwithf "%A" other

    /// O5: an investigation deleted on the branch is also a conflict.
    [<Fact>]
    let ``PERSIST-011-O5`` () =
        let r = rig "persist011-o5"
        let saved = savedInvestigation r
        let (RepoPath p) = Writes.sessionPrefix saved
        r.Fake.CommitAsOtherWriter("delete it", [ for f in Writes.fileNames -> p + "/" + f, None ]) |> ignore
        let s = r.Step (r.Observe saved "unsaved") SaveInput.SaveRequested
        match s.State with
        | SaveState.Conflict(Unresolvable _) -> Assert.NotEmpty(s.Unsaved)
        | other -> failwithf "a deleted investigation was %A" other

    /// O6 (PAT-05): the unsaved change is kept, nothing more is sent, the other writer's commit stays on the branch.
    [<Fact>]
    let ``PERSIST-011-O6`` () =
        let a, s, other, before, _ = run "persist011-o6"
        Assert.NotEmpty(s.Unsaved)
        Assert.Equal(before + 1, a.Mutations)
        Assert.True(a.Fake.IsAncestor(other, a.Fake.Head()))
