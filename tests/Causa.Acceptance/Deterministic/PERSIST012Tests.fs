// Owner: persistence. PERSIST-012 (deterministic): a conflict is resolved only by the user's explicit choice.
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveScenarios

module PERSIST012Tests =
    let private conflict name =
        let a, s, other, _, _ = ConflictScenario.run name
        let remote = match s.State with SaveState.Conflict(Reviewable v) -> v | x -> failwithf "%A" x
        a, s, other, remote

    let private stepC (a: SaveRig) view input = a.Machine.StepConflict (a.Context()) view input |> Result.defaultWith (failwithf "%A")

    /// O1: the conflict stays until the user chooses: edits, network changes and stray replies keep it.
    [<Fact>]
    let ``PERSIST-012-O1`` () =
        let a, s, _, _ = conflict "persist012-o1"
        let s1 = a.Step s SaveInput.WentOffline
        let s2 = a.Step s1 SaveInput.WentOnline
        let s3 = a.Observe s2 "another unsaved change"
        Assert.Equal("Conflict", stateName s3)
        Assert.True(Result.isError (a.Machine.Step (a.Context()) s3 SaveInput.SaveRequested))

    /// O2: the three actions are offered: review, reapply, discard.
    [<Fact>]
    let ``PERSIST-012-O2`` () =
        let a, _, _, remote = conflict "persist012-o2"
        let view = Reviewable remote
        Assert.Equal(view, fst (stepC a view ConflictInput.ReviewChosen))
        Assert.Equal(Reapplying remote, fst (stepC a view ConflictInput.ReapplyChosen))
        Assert.Equal(ConfirmingDiscard remote, fst (stepC a view ConflictInput.DiscardChosen))

    /// O3: reapply re-decides each unsaved command in its original order through the engine at the GitHub version.
    [<Fact>]
    let ``PERSIST-012-O3`` () =
        let a, s, _, remote = conflict "persist012-o3"
        let accepted, refused = Reapply.redecide a.Actor remote s.Unsaved
        Assert.Empty(refused)
        Assert.Equal<System.Guid list>(s.Unsaved |> List.map (fun c -> c.CommandId), accepted |> List.map (fun c -> c.CommandId))

    /// O4: accepted commands become unsaved on the new base head; refused ones are listed with the engine's reason.
    [<Fact>]
    let ``PERSIST-012-O4`` () =
        let a, s, other, remote = conflict "persist012-o4"
        let duplicateStart =
            a.Command s.Id (Causa.Domain.StartInvestigation { Title = Causa.Domain.NonEmptyText.create "again" |> Option.get; ObservationId = System.Guid.NewGuid(); Original = Causa.Domain.NonEmptyText.create "again" |> Option.get })
        let accepted, refused = Reapply.redecide a.Actor remote (s.Unsaved @ [ duplicateStart ])
        Assert.Equal(1, refused.Length)
        Assert.False(System.String.IsNullOrWhiteSpace refused.Head.Reason)
        let view, _ = stepC a (Reapplying remote) (ConflictInput.ReapplyDone(accepted, refused))
        let s' = a.Step { s with State = SaveState.Conflict view } (SaveInput.ConflictResolvedReapplied accepted)
        Assert.Equal("Unsaved", stateName s')
        Assert.Equal(other, s'.BaseHead)
        Assert.Equal<Causa.Domain.CommandEnvelope list>(accepted, s'.Unsaved)

    /// O5: reapply does not save.
    [<Fact>]
    let ``PERSIST-012-O5`` () =
        let a, s, _, remote = conflict "persist012-o5"
        let before = a.Mutations
        let accepted, refused = Reapply.redecide a.Actor remote s.Unsaved
        let view, effects = stepC a (Reapplying remote) (ConflictInput.ReapplyDone(accepted, refused))
        let s' = a.Step { s with State = SaveState.Conflict view } (SaveInput.ConflictResolvedReapplied accepted)
        Assert.Empty(effects |> List.filter (function Send _ -> true | _ -> false))
        Assert.Equal(before, a.Mutations)
        Assert.Equal("Unsaved", stateName s')

    /// O6: discard asks for confirmation first, and cancel returns to the review.
    [<Fact>]
    let ``PERSIST-012-O6`` () =
        let a, s, other, remote = conflict "persist012-o6"
        let confirming, _ = stepC a (Reviewable remote) ConflictInput.DiscardChosen
        Assert.Equal(ConfirmingDiscard remote, confirming)
        Assert.Equal(Reviewable remote, fst (stepC a confirming ConflictInput.DiscardCancelled))
        let discarded, _ = stepC a confirming ConflictInput.DiscardConfirmed
        let s' = a.Step { s with State = SaveState.Conflict discarded } (SaveInput.ConflictResolvedDiscarded remote)
        Assert.Equal("Saved", stateName s')
        Assert.Equal(other, s'.BaseHead)
        Assert.Empty(s'.Unsaved)

    /// O7: no action replaces the GitHub version with the tab's: no conflict input sends a mutation.
    [<Fact>]
    let ``PERSIST-012-O7`` () =
        let a, _, other, remote = conflict "persist012-o7"
        let views = [ Reviewable remote; Reapplying remote; ConfirmingDiscard remote; Discarded remote; Unresolvable "x"; RemoteUnavailable ]
        let inputs =
            [ ConflictInput.ReadAgain; ConflictInput.ReviewChosen; ConflictInput.ReapplyChosen; ConflictInput.ReapplyDone([], []); ConflictInput.DiscardChosen
              ConflictInput.DiscardConfirmed; ConflictInput.DiscardCancelled; ConflictInput.ExportChosen ]
        for v in views do
            for i in inputs do
                match a.Machine.StepConflict (a.Context()) v i with
                | Ok(_, effects) ->
                    for e in effects do
                        match e with
                        | Send r -> Assert.False(GitHubRequests.isMutation r, sprintf "%A/%A sent a mutation" v i)
                        | _ -> ()
                | Error _ -> ()
        Assert.Equal(other, a.Fake.Head())

    /// O8 (PAT-05): after each action the other writer's commit is still on the branch.
    [<Fact>]
    let ``PERSIST-012-O8`` () =
        let a, s, other, remote = conflict "persist012-o8"
        let accepted, refused = Reapply.redecide a.Actor remote s.Unsaved
        let view, _ = stepC a (Reapplying remote) (ConflictInput.ReapplyDone(accepted, refused))
        let s' = a.Step { s with State = SaveState.Conflict view } (SaveInput.ConflictResolvedReapplied accepted)
        a.Rebase s' |> ignore
        let saved = a.Step s' SaveInput.SaveRequested
        Assert.Equal("Saved", stateName saved)
        Assert.True(a.Fake.IsAncestor(other, a.Fake.Head()))
        Assert.Contains("Tab B's observation", eventsText a saved)
        Assert.Contains("Tab A's unsaved observation", eventsText a saved)
