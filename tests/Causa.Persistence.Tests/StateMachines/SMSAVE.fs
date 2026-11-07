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
        Assert.Contains(Writes.markerText (Writes.marker s), defaultArg (List.exactlyOne (sends effects)).Body "")
        Assert.NotEqual(Writes.marker { s with Mutations = 1 }, Writes.marker s)
        Assert.Equal(Writes.tabSession, (Writes.marker s).Session)

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

    /// Every pair outside the 50 legal pairs fails closed with SaveFault naming the machine, state and input, and the
    /// session unchanged (invalidTransition rule; 139 illegal pairs).
    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        let legal =
            set [ "Saved", "Edit"; "Saved", "WentOffline"; "Saved", "WentOnline"; "Unsaved", "Edit"; "Unsaved", "SaveRequested"; "Unsaved", "WentOffline"
                  "Unsaved", "WentOnline"; "Unsaved", "QueuedSaveStarted"; "Saving", "Edit"; "Saving", "MutationSent"; "Saving", "ReadBackMatched"
                  "Saving", "ReadBackMismatched"; "Saving", "StaleHeadUnrelated"; "Saving", "StaleHeadSameInvestigation"; "Saving", "ProviderRefused"
                  "Saving", "CredentialRefused"; "Saving", "AnswerLost"; "Saving", "FailedBeforeSending"; "Saving", "WentOffline"; "Saving", "WentOnline"
                  "Saving", "SupersededReply"; "Saving", "RebaseReadFailed"; "Saving", "ReadBackReadFailed"; "Offline", "Edit"; "Offline", "SaveRequested"
                  "Offline", "WentOffline"; "Offline", "WentOnline"; "Offline", "QueuedSaveStarted"; "Conflict", "Edit"; "Conflict", "WentOffline"
                  "Conflict", "WentOnline"; "Conflict", "ConflictResolvedReapplied"; "Conflict", "ConflictResolvedDiscarded"; "UnknownOutcome", "Edit"
                  "UnknownOutcome", "StaleHeadSameInvestigation"; "UnknownOutcome", "ReconcileMarkerBytesMatch"; "UnknownOutcome", "ReconcileMarkerBytesDiffer"
                  "UnknownOutcome", "ReconcileNothingWrittenHeadUnchanged"; "UnknownOutcome", "ReconcileNothingWrittenHeadMoved"
                  "UnknownOutcome", "ReconcileHistoryRewritten"; "UnknownOutcome", "ReconcileReadFailed"; "UnknownOutcome", "ReconcileReadsSpent"
                  "UnknownOutcome", "CheckAgain"; "UnknownOutcome", "WentOffline"; "UnknownOutcome", "WentOnline"; "UnknownOutcome", "SupersededReply"
                  "SaveFailed", "Edit"; "SaveFailed", "SaveRequested"; "SaveFailed", "WentOffline"; "SaveFailed", "WentOnline" ]
        let states =
            [ SaveState.Saved(Confirmed(confirmation b1)); SaveState.Unsaved; SaveState.Saving; SaveState.Offline; SaveState.Conflict(Reviewable(verified c2))
              SaveState.UnknownOutcome 3; SaveState.SaveFailed RepositoryKeptChanging ]
        let inputs =
            [ SaveInput.Edit(command 3); SaveInput.SaveRequested; SaveInput.MutationSent(RequestId 7L); SaveInput.ReadBackMatched(confirmation c2)
              SaveInput.ReadBackMismatched c2; SaveInput.StaleHeadUnrelated c3; SaveInput.StaleHeadSameInvestigation c3; SaveInput.ProviderRefused(PermissionDenied "write")
              SaveInput.CredentialRefused; SaveInput.AnswerLost; SaveInput.FailedBeforeSending; SaveInput.ReconcileMarkerBytesMatch(confirmation c2)
              SaveInput.ReconcileMarkerBytesDiffer c2; SaveInput.ReconcileNothingWrittenHeadUnchanged; SaveInput.ReconcileNothingWrittenHeadMoved c3
              SaveInput.ReconcileHistoryRewritten; SaveInput.ReconcileReadFailed; SaveInput.ReconcileReadsSpent; SaveInput.CheckAgain; SaveInput.WentOffline
              SaveInput.WentOnline; SaveInput.ConflictResolvedReapplied []; SaveInput.ConflictResolvedDiscarded(verified c2); SaveInput.SupersededReply(RequestId 999L)
              SaveInput.QueuedSaveStarted; SaveInput.RebaseReadFailed; SaveInput.ReadBackReadFailed ]
        let mutable illegal = 0
        for st in states do
            for input in inputs do
                let s = session st |> awaiting MutationAnswer
                let pair = SaveSession.stateName st, SaveSession.inputName input
                if not (legal.Contains pair) then
                    illegal <- illegal + 1
                    match machine.Step ctx s input with
                    | Error f ->
                        Assert.Equal("SM-SAVE", f.Machine)
                        Assert.Equal(fst pair, f.State)
                        Assert.Equal(snd pair, f.Input)
                    | Ok(s', _) -> failwithf "illegal %A reached %s" pair (SaveSession.stateName s'.State)
        Assert.Equal(139, illegal)

    /// J1 over the stateful fake with the causa-storage machine: start an investigation, Save; Saved only after the
    /// commit exists and its re-read equals the bytes written; GitHub holds the title and the problem statement in
    /// causa-storage files; a fresh load verifies and reopens them.
    [<Fact>]
    let J1StartSaveReloadOverTheFake () =
        let fake = StatefulFake()
        let actor = Causa.Domain.Actor.Human(Causa.Domain.NonEmptyText.create "investigator-01" |> Option.get)
        let storage = SaveMachines.withStorage actor (fun _ -> None)
        let inv = Causa.Domain.InvestigationId(System.Guid "9b2f1c3d-4e5f-4a6b-8c7d-0e1f2a3b4c5d")
        let wc = WorkspaceAddress.workingCopy [] "j1-round-trip" |> Option.get
        let w = Writable.ofAddress wc |> Option.get
        let title, problem = "Pump trips at noon", "The cooling pump trips at noon every day since Monday."
        let start : Causa.Domain.CommandEnvelope =
            { CommandId = Writes.commandIdAt (t0.AddMilliseconds 123.0) (Array.init 16 byte)
              Investigation = inv
              ExpectedVersion = None
              Command =
                Causa.Domain.Core(
                    Causa.Domain.StartInvestigation
                        { Title = Causa.Domain.NonEmptyText.create title |> Option.get
                          ObservationId = System.Guid "0c1d2e3f-4a5b-4c6d-8e7f-8091a2b3c4d5"
                          Original = Causa.Domain.NonEmptyText.create problem |> Option.get }
                ) }
        let c (id: int64) : RequestContext = { ctx with Connection = fake.WriteConnection; NextId = RequestId id }
        let stepOk cx s input = match storage.Step cx s input with Ok r -> r | Error f -> failwithf "%A" f
        let rec drive (s: InvestigationSession) (request: HttpRequest) (n: int64) =
            match storage.Interpret (c n) s (fake.Execute request) with
            | MachineEvent input -> fst (stepOk (c n) s input)
            | NextRequest(s', next) -> drive s' next (n + 1L)
        let head0 = fake.Head()
        let s0 = storage.InitialNew w inv head0 start true
        let s1, effects = stepOk (c 1L) s0 SaveInput.SaveRequested
        Assert.Equal("Saving", name s1)
        Assert.Equal(head0, fake.Head())
        let saved = drive s1 (List.exactlyOne (sends effects)) 2L
        Assert.True((name saved = "Saved"), sprintf "%A" saved.State)
        Assert.NotEqual(head0, fake.Head())
        Assert.Equal(fake.Head(), saved.BaseHead)
        let (RepoPath prefix) = Writes.sessionPrefix saved
        let events = fake.FileAt(fake.Head(), RepoPath(prefix + "/events.ndjson")) |> Option.map System.Text.Encoding.UTF8.GetString |> Option.get
        Assert.Contains(title, events)
        Assert.Contains(problem, events)
        Assert.Contains("\"formatVersion\":\"1.1.0\"", events)
        // A fresh load reads and verifies what GitHub holds.
        let target : InvestigationRef = { Address = wc; Id = inv }
        match storage.StepLoad (c 10L) NotLoaded (LoadInput.OpenRequested target) with
        | Ok(loading, fx) ->
            match storage.InterpretLoad (c 10L) loading (fake.Execute (List.exactlyOne (sends fx))) with
            | LoadEvent(LoadInput.LoadVerified v) ->
                Assert.Equal(title, v.Investigation.Title.Value)
                Assert.Contains(v.Investigation.Observations |> Map.toList |> List.map (fun (_, o) -> o.Original.Value), fun o -> o = problem)
                Assert.Equal(fake.Head(), v.ReadAt)
                Assert.Equal(WorkspaceRole.WorkingCopy, v.Address.Role)
            | other -> failwithf "the reload did not verify: %A" other
        | Error f -> failwithf "%A" f

    /// INV-023 fallback (P19): the question saves as an informational unknown in the start's commands and reloads as
    /// the primary question.
    [<Fact>]
    let PrimaryQuestionSavesAndReloads () =
        let fake = StatefulFake()
        let actor = Causa.Domain.Actor.Human(Causa.Domain.NonEmptyText.create "investigator-01" |> Option.get)
        let storage = SaveMachines.withStorage actor (fun _ -> None)
        let inv = Causa.Domain.InvestigationId(System.Guid "5a4b3c2d-1e0f-4a9b-8c7d-6e5f4a3b2c1d")
        let wc = WorkspaceAddress.workingCopy [] "inv023" |> Option.get
        let w = Writable.ofAddress wc |> Option.get
        let start : Causa.Domain.CommandEnvelope =
            { CommandId = Writes.commandIdAt t0 (Array.init 16 byte); Investigation = inv; ExpectedVersion = None
              Command = Causa.Domain.Core(Causa.Domain.StartInvestigation { Title = Causa.Domain.NonEmptyText.create "T" |> Option.get; ObservationId = System.Guid.NewGuid(); Original = Causa.Domain.NonEmptyText.create "P" |> Option.get }) }
        let commands = PrimaryQuestion.startCommands start "Why does the pump trip?" (Writes.commandIdAt (t0.AddSeconds 1.0) (Array.init 16 (fun i -> byte (i + 1)))) (System.Guid.NewGuid())
        let s0 = { storage.InitialNew w inv (fake.Head()) start true with Unsaved = commands }
        let c (id: int64) : RequestContext = { ctx with Connection = fake.WriteConnection; NextId = RequestId id }
        let stepOk cx s input = match storage.Step cx s input with Ok r -> r | Error f -> failwithf "%A" f
        let rec drive (s: InvestigationSession) (request: HttpRequest) (n: int64) =
            match storage.Interpret (c n) s (fake.Execute request) with
            | MachineEvent input -> fst (stepOk (c n) s input)
            | NextRequest(s', next) -> drive s' next (n + 1L)
        let s1, fx = stepOk (c 1L) s0 SaveInput.SaveRequested
        Assert.Equal("Saved", name (drive s1 (List.exactlyOne (sends fx)) 2L))
        match storage.StepLoad (c 10L) NotLoaded (LoadInput.OpenRequested { Address = wc; Id = inv }) with
        | Ok(loading, fx) ->
            match storage.InterpretLoad (c 10L) loading (fake.Execute (List.exactlyOne (sends fx))) with
            | LoadEvent(LoadInput.LoadVerified v) ->
                Assert.Equal(Some "Why does the pump trip?", PrimaryQuestion.ofInvestigation v.Investigation)
                let u = v.Investigation.Unknowns |> Map.toList |> List.exactlyOne |> snd
                Assert.Equal(Causa.Domain.Materiality.Informational, u.Materiality)
            | other -> failwithf "%A" other
        | Error f -> failwithf "%A" f

    /// The known-offline branches (T-S02, T-S05, T-S52, T-S55, T-S57, T-L02): Offline, nothing sent.
    [<Fact>]
    let KnownOfflineBranches () =
        let check st input =
            match KnownOffline.step (session st |> awaiting MutationAnswer) input with
            | Some(s, effects) ->
                Assert.Equal(SaveState.Offline, s.State)
                Assert.Empty(sends effects)
            | None -> failwithf "%A + %A has an offline branch" st input
        check (SaveState.Saved(VerifiedAt(verified b1))) (SaveInput.Edit(command 2))
        check SaveState.Unsaved SaveInput.SaveRequested
        check SaveState.Unsaved SaveInput.QueuedSaveStarted
        check (SaveState.SaveFailed RepositoryKeptChanging) SaveInput.SaveRequested
        check SaveState.Saving SaveInput.RebaseReadFailed
        Assert.True((KnownOffline.step (session SaveState.Unsaved) (SaveInput.Edit(command 2))).IsNone)
        match KnownOffline.open' NotLoaded with
        | Some(UnavailableOffline, effects) -> Assert.Empty(sends effects)
        | other -> failwithf "%A" other
        Assert.True((KnownOffline.open' (Loaded(verified c2))).IsNone)
