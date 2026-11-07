// Owner: persistence. PERSIST-015 (deterministic, PAT-06): an unknown commit outcome is reconciled before any resend.
namespace Causa.Acceptance.Deterministic

open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveScenarios

module UnknownScenario =
    /// A new investigation whose save meets `fault` on the mutation.
    let lost (name: string) (fault: Fault) =
        let r = rig name
        let s0 = r.Create "Title" "Problem statement"
        r.Fake.Inject fault
        r, r.Step s0 SaveInput.SaveRequested

    let headReads (r: SaveRig) = r.Fake.State.Received |> List.filter (fun (_, u) -> u.Contains "/git/ref/heads/") |> List.length

module PERSIST015Tests =
    open UnknownScenario

    /// O1: a mutation whose answer was lost after sending has an unknown outcome.
    [<Fact>]
    let ``PERSIST-015-O1`` () =
        let _, s = lost "persist015-o1" LoseAfterApplying
        Assert.Equal("UnknownOutcome", stateName s)

    /// O2: a timeout, a lost response and an unrecognised answer are all unknown outcomes.
    [<Fact>]
    let ``PERSIST-015-O2`` () =
        for fault in [ TimeOutAfterApplying; LoseAfterApplying; LoseBeforeApplying; AnswerWith "graphql-502"; AnswerWith "graphql-unknown-error-type"; AnswerWith "graphql-malformed-200" ] do
            let _, s = lost "persist015-o2" fault
            Assert.True((stateName s = "UnknownOutcome"), sprintf "%A gave %A" fault s.State)

    /// O3: no further mutation until reconciliation decides.
    [<Fact>]
    let ``PERSIST-015-O3`` () =
        let r, s = lost "persist015-o3" LoseAfterApplying
        Assert.Equal(1, r.Mutations)
        Assert.True(Result.isError (r.Machine.Step (r.Context()) s SaveInput.SaveRequested))
        Assert.Equal(1, r.Mutations)

    /// O4: reconciliation reads the branch head and the commits after the expected head.
    [<Fact>]
    let ``PERSIST-015-O4`` () =
        let r, s = lost "persist015-o4" LoseAfterApplying
        let before = headReads r
        r.Due s |> ignore
        Assert.Equal(before + 1, headReads r)
        Assert.Contains(r.Fake.State.Received, fun (_, u) -> u.Contains "/compare/")

    /// O5: a commit with this save's marker and the written bytes confirms the save.
    [<Fact>]
    let ``PERSIST-015-O5`` () =
        let r, s = lost "persist015-o5" LoseAfterApplying
        let s' = r.Due s
        Assert.Equal("Saved", stateName s')
        Assert.Equal(r.Fake.Head(), s'.BaseHead)
        Assert.Equal(1, r.Mutations)

    /// O6: a commit with the marker whose bytes differ is Save failed, never Saved.
    [<Fact>]
    let ``PERSIST-015-O6`` () =
        let r, s = lost "persist015-o6" LoseBeforeApplying
        let (RepoPath p) = Writes.sessionPrefix s
        let forged = r.Fake.CommitAsOtherWriter("forged\n\n" + Writes.markerText (Writes.marker s), [ for f in Writes.fileNames -> p + "/" + f, Some(Encoding.UTF8.GetBytes "{}\n") ])
        let s' = r.Due s
        Assert.Equal(SaveState.SaveFailed(BytesDiffer forged), s'.State)

    /// O7: head unchanged and no marker: nothing was written; the save is sent again within the limit.
    [<Fact>]
    let ``PERSIST-015-O7`` () =
        let r, s = lost "persist015-o7" LoseBeforeApplying
        let s' = r.Due s
        Assert.Equal("Saved", stateName s')
        Assert.Equal(2, r.Mutations)

    /// O8: head moved and no marker: the stale-head rules apply (an unrelated move rebases).
    [<Fact>]
    let ``PERSIST-015-O8`` () =
        let r, s = lost "persist015-o8" LoseBeforeApplying
        let other = r.Fake.CommitAsOtherWriter("unrelated", [ "workspaces/demo/other/x.json", Some(Encoding.UTF8.GetBytes "{}") ])
        let s' = r.Due s
        Assert.Equal("Saved", stateName s')
        Assert.Equal(1, s'.RebaseAttempts)
        Assert.True(r.Fake.IsAncestor(other, r.Fake.Head()))

    /// O9: the expected head is no longer an ancestor: Save failed, the branch history was rewritten.
    [<Fact>]
    let ``PERSIST-015-O9`` () =
        let r, s = lost "persist015-o9" LoseBeforeApplying
        r.Fake.RewriteHistory() |> ignore
        Assert.Equal(SaveState.SaveFailed HistoryWasRewritten, (r.Due s).State)

    /// O10: a failed reconciliation read leaves the outcome unknown.
    [<Fact>]
    let ``PERSIST-015-O10`` () =
        let r, s = lost "persist015-o10" LoseBeforeApplying
        r.Fake.Inject(LoseBeforeApplying, false)
        let s' = r.Due s
        Assert.Equal("UnknownOutcome", stateName s')
        Assert.Equal(1, r.Mutations)

    /// O11 (PAT-06): every branch with injected lost answers ends where the rule says.
    [<Fact>]
    let ``PERSIST-015-O11`` () =
        let outcome fault prepare =
            let r, s = lost "persist015-o11" fault
            prepare r s
            stateName (r.Due s)
        Assert.Equal("Saved", outcome LoseAfterApplying (fun _ _ -> ()))
        Assert.Equal("Saved", outcome LoseBeforeApplying (fun _ _ -> ()))
        Assert.Equal("SaveFailed", outcome LoseBeforeApplying (fun r _ -> r.Fake.RewriteHistory() |> ignore))
        Assert.Equal("UnknownOutcome", outcome LoseBeforeApplying (fun r _ -> r.Fake.Inject(FailBeforeSending, false)))

    /// Reconciliation pages through more commits than one compare lists (GH-TRUNCATED-LIST).
    [<Fact>]
    let ReconcileBeyond250Commits () =
        let r, s = lost "persist015-many" LoseBeforeApplying
        for i in 1..260 do
            r.Fake.CommitAsOtherWriter(sprintf "unrelated %d" i, [ sprintf "workspaces/demo/other/%d.json" i, Some(Encoding.UTF8.GetBytes "{}") ]) |> ignore
        let s' = r.Due s
        Assert.Contains(r.Fake.State.Received, fun (_, u) -> u.Contains "/compare/" && u.Contains "page=3")
        Assert.Equal("Saved", stateName s')
