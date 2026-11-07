// Owner: persistence. SM-CONFLICT transitions (state-machine-contracts.json) and the fixtures of the machine tests.
namespace Causa.Persistence.Tests.StateMachines

open System
open Xunit
open Causa.Domain
open Causa.Persistence

/// Values of the frozen contract types shared by the SM-CONFLICT, SM-LOAD and SM-SAVE tests.
module MachineFixtures =
    let machine = SaveMachines.machine
    let investigation = InvestigationId(Guid "0b5e2c1d-7a4f-4e3b-9c28-5d6e7f809a1b")
    let address = WorkspaceAddress.workingCopy [] "t18-machines" |> Option.get
    let writable = Writable.ofAddress address |> Option.get
    let oid (n: int) = CommitOid(sprintf "%040x" n)
    let b1, c2, c3 = oid 0xb1, oid 0xc2, oid 0xc3
    let t0 = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero)

    let files (tag: string) : FileSet =
        { Manifest = Text.Encoding.UTF8.GetBytes("m" + tag); Events = Text.Encoding.UTF8.GetBytes("e" + tag); Snapshot = Text.Encoding.UTF8.GetBytes("s" + tag) }

    let verified (at: CommitOid) : VerifiedInvestigation =
        { Investigation = Causa.TestSupport.TestValues.investigation investigation (AggregateVersion 1L); History = []; ReadAt = at; Address = address; Bytes = files "v" }

    let confirmation (at: CommitOid) = Confirmation.ofReadBack investigation at t0 (files "w") (files "w") |> Option.get

    let command (n: int) : CommandEnvelope =
        { CommandId = Guid(n, 0s, 0s, Array.zeroCreate 8); Investigation = investigation; ExpectedVersion = Some(AggregateVersion 1L); Command = Core ActivateInvestigation }

    let ctxAt (now: DateTimeOffset) (elsewhere: bool) : RequestContext =
        { Connection = { Repository = { Owner = "causa-test-owner"; Name = "causa-test-data"; Branch = "causa-test" }; Credential = Credential.create "fake-write-token" }
          NextId = RequestId 42L
          MutationInFlightElsewhere = elsewhere
          Now = now }

    let ctx = ctxAt t0 false

    let session (state: SaveState) : InvestigationSession =
        { Id = investigation; Address = writable; BaseHead = b1; State = state
          Unsaved = (match state with SaveState.Saved _ -> [] | _ -> [ command 1 ])
          AfterInFlight = []; Outstanding = None; Mutations = 0; RebaseAttempts = 0; ReconcileReads = 0; ReconcileReadsSentAt = [] }

    let awaiting (a: Awaiting) (s: InvestigationSession) = { s with Outstanding = Some(RequestId 7L, a) }

    let sends (effects: SaveEffect list) = effects |> List.choose (function Send r -> Some r | _ -> None)
    let schedules (effects: SaveEffect list) = effects |> List.choose (function ScheduleReconcileRead(_, ms) -> Some ms | _ -> None)

    let stepOk (c: RequestContext) (s: InvestigationSession) (input: SaveInput) =
        match machine.Step c s input with
        | Ok r -> r
        | Error f -> failwithf "Step refused: %A" f

    let conflictOk (view: ConflictView) (input: ConflictInput) =
        match machine.StepConflict ctx view input with
        | Ok r -> r
        | Error f -> failwithf "StepConflict refused: %A" f

module SMCONFLICT =
    open MachineFixtures

    let private remote = verified c2
    let private name = ConflictMachine.stateName

    [<Fact>]
    let ``T-C01`` () = Assert.Equal(Reviewable remote, fst (conflictOk (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteVerified remote)))

    [<Fact>]
    let ``T-C02`` () = Assert.Equal(RemoteUnavailable, fst (conflictOk (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteReadFailed TransportFailure.LostAfterSending)))

    [<Fact>]
    let ``T-C03`` () = Assert.Equal("Unresolvable", name (fst (conflictOk (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteMissingOrInvalid None))))

    [<Fact>]
    let ``T-C04`` () =
        let view, effects = conflictOk RemoteUnavailable ConflictInput.ReadAgain
        Assert.Equal(ReadingRemote ctx.NextId, view)
        Assert.Equal(ctx.NextId, (List.exactlyOne (sends effects)).Id)

    [<Fact>]
    let ``T-C05`` () = Assert.Equal(Reviewable remote, fst (conflictOk (Reviewable remote) ConflictInput.ReviewChosen))

    [<Fact>]
    let ``T-C06`` () = Assert.Equal(Reapplying remote, fst (conflictOk (Reviewable remote) ConflictInput.ReapplyChosen))

    [<Fact>]
    let ``T-C07`` () =
        let refused = [ { Command = command 2; Reason = "refused by the engine" } ]
        Assert.Equal(Reapplied(remote, refused), fst (conflictOk (Reapplying remote) (ConflictInput.ReapplyDone([ command 1 ], refused))))

    [<Fact>]
    let ``T-C08`` () = Assert.Equal(ConfirmingDiscard remote, fst (conflictOk (Reviewable remote) ConflictInput.DiscardChosen))

    [<Fact>]
    let ``T-C09`` () = Assert.Equal(Discarded remote, fst (conflictOk (ConfirmingDiscard remote) ConflictInput.DiscardConfirmed))

    [<Fact>]
    let ``T-C10`` () = Assert.Equal(Reviewable remote, fst (conflictOk (ConfirmingDiscard remote) ConflictInput.DiscardCancelled))

    [<Fact>]
    let ``T-C11`` () =
        let view, effects = conflictOk (Reviewable remote) ConflictInput.ReadAgain
        Assert.Equal(ReadingRemote ctx.NextId, view)
        Assert.Single(sends effects) |> ignore

    [<Fact>]
    let ``T-C12`` () =
        let view, effects = conflictOk (Unresolvable "x") ConflictInput.ExportChosen
        Assert.Equal(Unresolvable "x", view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C13`` () = Assert.Equal(ReadingRemote ctx.NextId, fst (conflictOk (Unresolvable "x") ConflictInput.ReadAgain))

    [<Fact>]
    let ``T-C14`` () = Assert.Equal(ReadingRemote(RequestId 1L), fst (conflictOk (ReadingRemote(RequestId 1L)) (ConflictInput.SupersededReply(RequestId 2L))))

    [<Fact>]
    let ``T-C15`` () = Assert.Equal(Reviewable remote, fst (conflictOk (Reviewable remote) (ConflictInput.SupersededReply(RequestId 2L))))

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        let legal =
            set [ "ReadingRemote", "RemoteVerified"; "ReadingRemote", "RemoteReadFailed"; "ReadingRemote", "RemoteMissingOrInvalid"; "ReadingRemote", "SupersededReply"
                  "Reviewable", "ReadAgain"; "Reviewable", "ReviewChosen"; "Reviewable", "ReapplyChosen"; "Reviewable", "DiscardChosen"; "Reviewable", "SupersededReply"
                  "Reapplying", "ReapplyDone"; "ConfirmingDiscard", "DiscardConfirmed"; "ConfirmingDiscard", "DiscardCancelled"
                  "RemoteUnavailable", "ReadAgain"; "Unresolvable", "ReadAgain"; "Unresolvable", "ExportChosen" ]
        let views = [ ReadingRemote(RequestId 1L); Reviewable remote; Reapplying remote; Reapplied(remote, []); ConfirmingDiscard remote; Discarded remote; RemoteUnavailable; Unresolvable "x" ]
        let inputs =
            [ ConflictInput.RemoteVerified remote; ConflictInput.RemoteReadFailed TransportFailure.TimedOut; ConflictInput.RemoteMissingOrInvalid None; ConflictInput.ReadAgain
              ConflictInput.ReviewChosen; ConflictInput.ReapplyChosen; ConflictInput.ReapplyDone([], []); ConflictInput.DiscardChosen; ConflictInput.DiscardConfirmed
              ConflictInput.DiscardCancelled; ConflictInput.ExportChosen; ConflictInput.SupersededReply(RequestId 1L) ]
        let mutable illegal = 0
        for v in views do
            for i in inputs do
                if not (legal.Contains(name v, ConflictMachine.inputName i)) then
                    illegal <- illegal + 1
                    match machine.StepConflict ctx v i with
                    | Error f ->
                        Assert.Equal("SM-CONFLICT", f.Machine)
                        Assert.Equal(name v, f.State)
                        Assert.Equal(ConflictMachine.inputName i, f.Input)
                    | Ok(v', _) -> failwithf "illegal (%s, %s) reached %s" (name v) (ConflictMachine.inputName i) (name v')
        Assert.Equal(81, illegal)
