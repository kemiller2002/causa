// Owner: persistence. SM-SAVE transitions (state-machine-contracts.json): each test drives one transition and
// asserts the next state and its effects.
namespace Causa.Persistence.Tests.StateMachines

open Xunit
open Causa.Persistence
open Causa.TestSupport

module SMSAVE =
    open MachineFixtures

    let private name (s: InvestigationSession) = SaveSession.stateName s.State
    let private saving = session SaveState.Saving |> awaiting MutationAnswer
    let private unknown = session (SaveState.UnknownOutcome 3) |> awaiting ReconcileHead
    let private go s input = stepOk ctx s input

    let private isCommit (r: HttpRequest) = GitHubRequests.isMutation r

    [<Fact>]
    let ``T-S01`` () =
        let s, effects = go (session (SaveState.Saved(VerifiedAt(verified b1)))) (SaveInput.Edit(command 2))
        Assert.Equal("Unsaved", name s)
        Assert.Equal<_ list>([ command 2 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S03`` () = Assert.Equal(2, (fst (go (session SaveState.Unsaved) (SaveInput.Edit(command 2)))).Unsaved.Length)

    [<Fact>]
    let ``T-S04`` () =
        let s, effects = go (session SaveState.Unsaved) SaveInput.SaveRequested
        Assert.Equal("Saving", name s)
        Assert.Equal(Some(ctx.NextId, MutationAnswer), s.Outstanding)
        let r = List.exactlyOne (sends effects)
        Assert.True(isCommit r)
        Assert.Contains("\"expectedHeadOid\":\"" + (let (CommitOid o) = b1 in o) + "\"", defaultArg r.Body "")
        Assert.Contains("causa-write-marker", defaultArg r.Body "")

    [<Fact>]
    let ``T-S53`` () =
        let s, effects = stepOk (ctxAt t0 true) (session SaveState.Unsaved) SaveInput.SaveRequested
        Assert.Equal("Unsaved", name s)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S06`` () = Assert.Equal("Offline", name (fst (go (session SaveState.Unsaved) SaveInput.WentOffline)))

    [<Fact>]
    let ``T-S43`` () = Assert.Equal("Unsaved", name (fst (go (session SaveState.Unsaved) SaveInput.WentOnline)))

    [<Fact>]
    let ``T-S54`` () = Assert.Equal("Saving", name (fst (go (session SaveState.Unsaved) SaveInput.QueuedSaveStarted)))

    [<Fact>]
    let ``T-S10`` () = Assert.Equal<_ list>([ command 2 ], (fst (go saving (SaveInput.Edit(command 2)))).AfterInFlight)

    [<Fact>]
    let ``T-S61`` () =
        let s, effects = go (saving |> fun s -> { s with Outstanding = Some(RequestId 9L, MutationAnswer) }) (SaveInput.MutationSent(RequestId 9L))
        Assert.Equal(Some(RequestId 9L, MutationAnswer), s.Outstanding)
        Assert.Empty(effects)

    [<Fact>]
    let ``T-S11`` () =
        let s, _ = go (session SaveState.Saving |> awaiting (ReadBackAt c2)) (SaveInput.ReadBackMatched(confirmation c2))
        Assert.Equal("Saved", name s)
        Assert.Equal(c2, s.BaseHead)
        Assert.Empty(s.Unsaved)

    [<Fact>]
    let ``T-S12`` () =
        let s, _ = go ({ session SaveState.Saving with AfterInFlight = [ command 2 ] } |> awaiting (ReadBackAt c2)) (SaveInput.ReadBackMatched(confirmation c2))
        Assert.Equal("Unsaved", name s)
        Assert.Equal(c2, s.BaseHead)
        Assert.Equal<_ list>([ command 2 ], s.Unsaved)

    [<Fact>]
    let ``T-S13`` () =
        let s, _ = go (session SaveState.Saving |> awaiting (ReadBackAt c2)) (SaveInput.ReadBackMismatched c2)
        Assert.Equal(SaveState.SaveFailed(BytesDiffer c2), s.State)
        Assert.NotEmpty(s.Unsaved)

    [<Fact>]
    let ``T-S14`` () =
        let s, effects = go (saving |> awaiting (FileComparisonAt c3)) (SaveInput.StaleHeadUnrelated c3)
        Assert.Equal("Saving", name s)
        Assert.Equal(1, s.RebaseAttempts)
        Assert.Contains("\"expectedHeadOid\":\"" + (let (CommitOid o) = c3 in o) + "\"", defaultArg (List.exactlyOne (sends effects)).Body "")

    [<Fact>]
    let ``T-S15`` () =
        let s, _ = go ({ saving with RebaseAttempts = 3 } |> awaiting (FileComparisonAt c3)) (SaveInput.StaleHeadUnrelated c3)
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)

    [<Fact>]
    let ``T-S16`` () =
        let s, effects = go (saving |> awaiting (FileComparisonAt c3)) (SaveInput.StaleHeadSameInvestigation c3)
        Assert.Equal("Conflict", name s)
        Assert.True(sends effects |> List.forall (isCommit >> not))

    [<Fact>]
    let ``T-S17`` () =
        let e = RateLimited(Secondary(Some 60))
        Assert.Equal(SaveState.SaveFailed(Refused e), (fst (go saving (SaveInput.ProviderRefused e))).State)

    [<Fact>]
    let ``T-S18`` () = Assert.Equal(SaveState.SaveFailed CredentialNotAccepted, (fst (go saving SaveInput.CredentialRefused)).State)

    [<Fact>]
    let ``T-S19`` () =
        let s, effects = go saving SaveInput.AnswerLost
        Assert.Equal("UnknownOutcome", name s)
        Assert.Empty(sends effects)
        Assert.Equal<int list>([ 2000 ], schedules effects)

    [<Fact>]
    let ``T-S20`` () =
        let s, effects = go saving SaveInput.FailedBeforeSending
        Assert.Equal("Offline", name s)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S39`` () = Assert.Equal(saving, fst (go saving (SaveInput.SupersededReply(RequestId 999L))))

    [<Fact>]
    let ``T-S59`` () = Assert.Equal("UnknownOutcome", name (fst (go (saving |> awaiting FreshHead) SaveInput.RebaseReadFailed)))

    [<Fact>]
    let ``T-S58`` () = Assert.Equal("UnknownOutcome", name (fst (go (saving |> awaiting (ReadBackAt c2)) SaveInput.ReadBackReadFailed)))

    [<Fact>]
    let ``T-S09`` () =
        let s, effects = go (session SaveState.Offline) SaveInput.WentOnline
        Assert.Equal("Unsaved", name s)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S08`` () = Assert.Empty(sends (snd (go (session SaveState.Offline) SaveInput.SaveRequested)))

    [<Fact>]
    let ``T-S37`` () =
        let s, _ = go (session (SaveState.Conflict(Reapplied(verified c2, [])))) (SaveInput.ConflictResolvedReapplied [ command 3 ])
        Assert.Equal("Unsaved", name s)
        Assert.Equal(c2, s.BaseHead)

    [<Fact>]
    let ``T-S38`` () =
        let s, _ = go (session (SaveState.Conflict(Discarded(verified c2)))) (SaveInput.ConflictResolvedDiscarded(verified c2))
        Assert.Equal(SaveState.Saved(VerifiedAt(verified c2)), s.State)
        Assert.Equal(c2, s.BaseHead)

    [<Fact>]
    let ``T-S21`` () =
        let s, _ = go unknown (SaveInput.ReconcileMarkerBytesMatch(confirmation c2))
        Assert.Equal("Saved", name s)
        Assert.Equal(c2, s.BaseHead)

    [<Fact>]
    let ``T-S23`` () = Assert.Equal(SaveState.SaveFailed(BytesDiffer c2), (fst (go unknown (SaveInput.ReconcileMarkerBytesDiffer c2))).State)

    [<Fact>]
    let ``T-S24`` () =
        let s, effects = go { unknown with Mutations = 1 } SaveInput.ReconcileNothingWrittenHeadUnchanged
        Assert.Equal("Saving", name s)
        Assert.Equal(2, s.Mutations)
        Assert.Contains("causa-write-marker: " + (let (Causa.Domain.InvestigationId g) = investigation in g.ToString()) + "/2", defaultArg (List.exactlyOne (sends effects)).Body "")

    [<Fact>]
    let ``T-S25`` () = Assert.Equal("SaveFailed", name (fst (go { unknown with Mutations = 4 } SaveInput.ReconcileNothingWrittenHeadUnchanged)))

    [<Fact>]
    let ``T-S26`` () = Assert.Equal("Saving", name (fst (go { unknown with Mutations = 1 } (SaveInput.ReconcileNothingWrittenHeadMoved c3))))

    [<Fact>]
    let ``T-S28`` () = Assert.Equal(SaveState.SaveFailed HistoryWasRewritten, (fst (go unknown SaveInput.ReconcileHistoryRewritten)).State)

    [<Fact>]
    let ``T-S29`` () =
        let s, effects = go { unknown with ReconcileReads = 1 } SaveInput.ReconcileReadFailed
        Assert.Equal("UnknownOutcome", name s)
        Assert.Equal<int list>([ 4000 ], schedules effects)

    [<Fact>]
    let ``T-S64`` () =
        let s, effects = go { unknown with ReconcileReads = 3 } SaveInput.ReconcileReadFailed
        Assert.Equal(SaveState.UnknownOutcome 0, s.State)
        Assert.Empty(schedules effects)

    [<Fact>]
    let ``T-S31`` () =
        let s, effects = go { unknown with ReconcileReads = 3; Outstanding = None } SaveInput.CheckAgain
        Assert.Single(sends effects) |> ignore
        Assert.Equal<_ list>([ t0 ], s.ReconcileReadsSentAt)

    [<Fact>]
    let ``T-S62`` () =
        let full = [ for i in 1..4 -> t0.AddSeconds(float -i) ]
        let s, effects = go { unknown with ReconcileReads = 3; Outstanding = None; ReconcileReadsSentAt = full } SaveInput.CheckAgain
        Assert.Empty(sends effects)
        Assert.Equal<_ list>(full, s.ReconcileReadsSentAt)

    [<Fact>]
    let ``T-S65`` () =
        let _, effects = go { unknown with Outstanding = None } SaveInput.WentOnline
        Assert.Empty(sends effects)
        Assert.Equal<int list>([ 0 ], schedules effects)

    [<Fact>]
    let ``T-S35`` () =
        let s, effects = go { session (SaveState.SaveFailed RepositoryKeptChanging) with Mutations = 4; RebaseAttempts = 3 } SaveInput.SaveRequested
        Assert.Equal("Saving", name s)
        Assert.Equal(1, s.Mutations)
        Assert.Equal(0, s.RebaseAttempts)
        Assert.Single(sends effects) |> ignore

    /// ReconcileDue sends at most four reconciliation reads in any 60 s (INV-04, ODR-V412-01).
    [<Fact>]
    let ReconcileDueKeepsTheWindow () =
        let s0 = { unknown with Outstanding = None; ReconcileReadsSentAt = [ for i in 1..4 -> t0.AddSeconds(float -i * 10.0) ] }
        match machine.ReconcileDue ctx s0 with
        | Ok(s, effects) ->
            Assert.Empty(sends effects)
            Assert.Equal<int list>([ 20000 ], schedules effects)
            Assert.Equal(s0, s)
        | Error f -> failwithf "%A" f

    /// The whole save through the machine over the stateful fake: commit, read-back by one read of the three files,
    /// and Saved only from bytes equal to those written (PERSIST-007, PERSIST-023).
    [<Fact>]
    let SaveConfirmedByReadBackOverTheFake () =
        let fake = StatefulFake()
        let c (id: int64) : RequestContext = { ctx with Connection = fake.WriteConnection; NextId = RequestId id }
        let start = { session SaveState.Unsaved with BaseHead = fake.Head() }
        let rec drive (s: InvestigationSession) (request: HttpRequest) (n: int64) =
            match machine.Interpret (c n) s (fake.Execute request) with
            | MachineEvent input -> fst (stepOk (c n) s input)
            | NextRequest(s', next) -> drive s' next (n + 1L)
        let s1, effects = stepOk (c 1L) start SaveInput.SaveRequested
        let done' = drive s1 (List.exactlyOne (sends effects)) 2L
        Assert.Equal("Saved", name done')
        Assert.Equal(fake.Head(), done'.BaseHead)
