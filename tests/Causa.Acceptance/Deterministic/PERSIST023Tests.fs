// Owner: domain (v4 T12, invariant tests first). PERSIST-023: truthful save state is structural.
// The tests drive the frozen IF41-SAVE contract (src/Causa.Persistence/SaveMachine.fs) black-box. The machine itself is
// the persistence role's (T19); these tests find its one exported SaveMachine value and fail with a BLOCKED message
// while none exists, so they never pass vacuously.
namespace Causa.Acceptance.Deterministic

open System
open System.Reflection
open Causa.Domain
open Causa.Persistence

/// The SM-SAVE and SM-CONFLICT tables of requirements-compiled/v4.1.6/state-machine-contracts.json: each machine's
/// states, events and, for every legal (state, event) pair, the next states its transitions allow. Every other pair is
/// illegal and fails closed with SaveFault (invalidTransition rule).
module MachineTables =
    let saveStates = [ "Saved"; "Unsaved"; "Saving"; "Offline"; "Conflict"; "UnknownOutcome"; "SaveFailed" ]

    let saveEvents =
        [ "Edit"; "SaveRequested"; "MutationSent"; "ReadBackMatched"; "ReadBackMismatched"; "StaleHeadUnrelated"
          "StaleHeadSameInvestigation"; "ProviderRefused"; "CredentialRefused"; "AnswerLost"; "FailedBeforeSending"
          "ReconcileMarkerBytesMatch"; "ReconcileMarkerBytesDiffer"; "ReconcileNothingWrittenHeadUnchanged"
          "ReconcileNothingWrittenHeadMoved"; "ReconcileHistoryRewritten"; "ReconcileReadFailed"; "ReconcileReadsSpent"
          "CheckAgain"; "WentOffline"; "WentOnline"; "ConflictResolvedReapplied"; "ConflictResolvedDiscarded"
          "SupersededReply"; "QueuedSaveStarted"; "RebaseReadFailed"; "ReadBackReadFailed" ]

    let saveLegal : Map<string * string, string list> =
        [ "Saved", "Edit", [ "Offline"; "Unsaved" ]
          "Saved", "WentOffline", [ "Saved" ]
          "Saved", "WentOnline", [ "Saved" ]
          "Unsaved", "Edit", [ "Unsaved" ]
          "Unsaved", "SaveRequested", [ "Offline"; "Saving"; "Unsaved" ]
          "Unsaved", "WentOffline", [ "Offline" ]
          "Unsaved", "WentOnline", [ "Unsaved" ]
          "Unsaved", "QueuedSaveStarted", [ "Offline"; "Saving" ]
          "Saving", "Edit", [ "Saving" ]
          "Saving", "MutationSent", [ "Saving" ]
          "Saving", "ReadBackMatched", [ "Saved"; "Unsaved" ]
          "Saving", "ReadBackMismatched", [ "SaveFailed" ]
          "Saving", "StaleHeadUnrelated", [ "SaveFailed"; "Saving" ]
          "Saving", "StaleHeadSameInvestigation", [ "Conflict" ]
          "Saving", "ProviderRefused", [ "SaveFailed" ]
          "Saving", "CredentialRefused", [ "SaveFailed" ]
          "Saving", "AnswerLost", [ "UnknownOutcome" ]
          "Saving", "FailedBeforeSending", [ "Offline" ]
          "Saving", "WentOffline", [ "Saving" ]
          "Saving", "WentOnline", [ "Saving" ]
          "Saving", "SupersededReply", [ "Saving" ]
          "Saving", "RebaseReadFailed", [ "Offline"; "UnknownOutcome" ]
          "Saving", "ReadBackReadFailed", [ "UnknownOutcome" ]
          "Offline", "Edit", [ "Offline" ]
          "Offline", "SaveRequested", [ "Offline" ]
          "Offline", "WentOffline", [ "Offline" ]
          "Offline", "WentOnline", [ "Unsaved" ]
          "Offline", "QueuedSaveStarted", [ "Offline" ]
          "Conflict", "Edit", [ "Conflict" ]
          "Conflict", "WentOffline", [ "Conflict" ]
          "Conflict", "WentOnline", [ "Conflict" ]
          "Conflict", "ConflictResolvedReapplied", [ "Unsaved" ]
          "Conflict", "ConflictResolvedDiscarded", [ "Saved" ]
          "UnknownOutcome", "Edit", [ "UnknownOutcome" ]
          "UnknownOutcome", "StaleHeadSameInvestigation", [ "Conflict" ]
          "UnknownOutcome", "ReconcileMarkerBytesMatch", [ "Saved"; "Unsaved" ]
          "UnknownOutcome", "ReconcileMarkerBytesDiffer", [ "SaveFailed" ]
          "UnknownOutcome", "ReconcileNothingWrittenHeadUnchanged", [ "SaveFailed"; "Saving" ]
          "UnknownOutcome", "ReconcileNothingWrittenHeadMoved", [ "SaveFailed"; "Saving" ]
          "UnknownOutcome", "ReconcileHistoryRewritten", [ "SaveFailed" ]
          "UnknownOutcome", "ReconcileReadFailed", [ "UnknownOutcome" ]
          "UnknownOutcome", "ReconcileReadsSpent", [ "UnknownOutcome" ]
          "UnknownOutcome", "CheckAgain", [ "UnknownOutcome" ]
          "UnknownOutcome", "WentOffline", [ "UnknownOutcome" ]
          "UnknownOutcome", "WentOnline", [ "UnknownOutcome" ]
          "UnknownOutcome", "SupersededReply", [ "UnknownOutcome" ]
          "SaveFailed", "Edit", [ "SaveFailed" ]
          "SaveFailed", "SaveRequested", [ "Offline"; "Saving" ]
          "SaveFailed", "WentOffline", [ "SaveFailed" ]
          "SaveFailed", "WentOnline", [ "SaveFailed" ] ]
        |> List.map (fun (s, e, next) -> (s, e), next)
        |> Map.ofList

    let conflictStates =
        [ "ReadingRemote"; "Reviewable"; "Reapplying"; "Reapplied"; "ConfirmingDiscard"; "Discarded"; "RemoteUnavailable"
          "Unresolvable" ]

    let conflictEvents =
        [ "RemoteVerified"; "RemoteReadFailed"; "RemoteMissingOrInvalid"; "ReadAgain"; "ReviewChosen"; "ReapplyChosen"
          "ReapplyDone"; "DiscardChosen"; "DiscardConfirmed"; "DiscardCancelled"; "ExportChosen"; "SupersededReply" ]

    let conflictLegal : Map<string * string, string list> =
        [ "ReadingRemote", "RemoteVerified", [ "Reviewable" ]
          "ReadingRemote", "RemoteReadFailed", [ "RemoteUnavailable" ]
          "ReadingRemote", "RemoteMissingOrInvalid", [ "Unresolvable" ]
          "ReadingRemote", "SupersededReply", [ "ReadingRemote" ]
          "Reviewable", "ReadAgain", [ "ReadingRemote" ]
          "Reviewable", "ReviewChosen", [ "Reviewable" ]
          "Reviewable", "ReapplyChosen", [ "Reapplying" ]
          "Reviewable", "DiscardChosen", [ "ConfirmingDiscard" ]
          "Reviewable", "SupersededReply", [ "Reviewable" ]
          "Reapplying", "ReapplyDone", [ "Reapplied" ]
          "ConfirmingDiscard", "DiscardConfirmed", [ "Discarded" ]
          "ConfirmingDiscard", "DiscardCancelled", [ "Reviewable" ]
          "RemoteUnavailable", "ReadAgain", [ "ReadingRemote" ]
          "Unresolvable", "ReadAgain", [ "ReadingRemote" ]
          "Unresolvable", "ExportChosen", [ "Unresolvable" ] ]
        |> List.map (fun (s, e, next) -> (s, e), next)
        |> Map.ofList

/// Values of the frozen contract types for driving the machines.
module Fixtures =
    let investigation = InvestigationId(Guid "6c0d5a3e-2f1b-4c8e-9a77-1d2e3f405162")
    let address = WorkspaceAddress.workingCopy [] "t12-invariants" |> Option.get
    let writable = Writable.ofAddress address |> Option.get
    let commit (n: int) = CommitOid(sprintf "%040x" n)
    let b1 = commit 0xb1
    let c2 = commit 0xc2
    let c3 = commit 0xc3
    let t0 = DateTimeOffset(2026, 11, 7, 17, 0, 0, TimeSpan.Zero)

    let files (tag: string) : FileSet =
        { Manifest = Text.Encoding.UTF8.GetBytes("{\"manifest\":\"" + tag + "\"}\n")
          Events = Text.Encoding.UTF8.GetBytes("{\"events\":\"" + tag + "\"}\n")
          Snapshot = Text.Encoding.UTF8.GetBytes("{\"snapshot\":\"" + tag + "\"}\n") }

    let verified (at: CommitOid) : VerifiedInvestigation =
        { Investigation = { Id = investigation; Version = AggregateVersion 1L }
          History = []
          ReadAt = at
          Address = address
          Bytes = files "verified" }

    /// A confirmation built the only way there is: from a read-back equal to the written bytes.
    let confirmed (at: CommitOid) : Confirmation.T =
        Confirmation.ofReadBack investigation at t0 (files "written") (files "written") |> Option.get

    let command (n: int) : CommandEnvelope =
        { CommandId = Guid(n, 0s, 0s, Array.zeroCreate 8)
          Investigation = investigation
          ExpectedVersion = Some(AggregateVersion 1L)
          Command = Core ActivateInvestigation }

    let context (elsewhere: bool) : RequestContext =
        { Connection =
            { Repository = { Owner = "causa-test-owner"; Name = "causa-test-repo"; Branch = "main" }
              Credential = Credential.create "not-a-real-token" }
          NextId = RequestId 500L
          MutationInFlightElsewhere = elsewhere
          Now = t0 }

    let session (state: SaveState) : InvestigationSession =
        { Id = investigation
          Address = writable
          BaseHead = b1
          State = state
          Unsaved = (match state with SaveState.Saved _ -> [] | _ -> [ command 1 ])
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0
          ReconcileReadsSentAt = [] }

    let conflictName (view: ConflictView) =
        match view with
        | ReadingRemote _ -> "ReadingRemote"
        | Reviewable _ -> "Reviewable"
        | Reapplying _ -> "Reapplying"
        | Reapplied _ -> "Reapplied"
        | ConfirmingDiscard _ -> "ConfirmingDiscard"
        | Discarded _ -> "Discarded"
        | RemoteUnavailable -> "RemoteUnavailable"
        | Unresolvable _ -> "Unresolvable"

    let stateName (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

    let inputName (input: SaveInput) =
        match input with
        | SaveInput.Edit _ -> "Edit"
        | SaveInput.SaveRequested -> "SaveRequested"
        | SaveInput.MutationSent _ -> "MutationSent"
        | SaveInput.ReadBackMatched _ -> "ReadBackMatched"
        | SaveInput.ReadBackMismatched _ -> "ReadBackMismatched"
        | SaveInput.StaleHeadUnrelated _ -> "StaleHeadUnrelated"
        | SaveInput.StaleHeadSameInvestigation _ -> "StaleHeadSameInvestigation"
        | SaveInput.ProviderRefused _ -> "ProviderRefused"
        | SaveInput.CredentialRefused -> "CredentialRefused"
        | SaveInput.AnswerLost -> "AnswerLost"
        | SaveInput.FailedBeforeSending -> "FailedBeforeSending"
        | SaveInput.ReconcileMarkerBytesMatch _ -> "ReconcileMarkerBytesMatch"
        | SaveInput.ReconcileMarkerBytesDiffer _ -> "ReconcileMarkerBytesDiffer"
        | SaveInput.ReconcileNothingWrittenHeadUnchanged -> "ReconcileNothingWrittenHeadUnchanged"
        | SaveInput.ReconcileNothingWrittenHeadMoved _ -> "ReconcileNothingWrittenHeadMoved"
        | SaveInput.ReconcileHistoryRewritten -> "ReconcileHistoryRewritten"
        | SaveInput.ReconcileReadFailed -> "ReconcileReadFailed"
        | SaveInput.ReconcileReadsSpent -> "ReconcileReadsSpent"
        | SaveInput.CheckAgain -> "CheckAgain"
        | SaveInput.WentOffline -> "WentOffline"
        | SaveInput.WentOnline -> "WentOnline"
        | SaveInput.ConflictResolvedReapplied _ -> "ConflictResolvedReapplied"
        | SaveInput.ConflictResolvedDiscarded _ -> "ConflictResolvedDiscarded"
        | SaveInput.SupersededReply _ -> "SupersededReply"
        | SaveInput.QueuedSaveStarted -> "QueuedSaveStarted"
        | SaveInput.RebaseReadFailed -> "RebaseReadFailed"
        | SaveInput.ReadBackReadFailed -> "ReadBackReadFailed"

    let conflictInputName (input: ConflictInput) =
        match input with
        | ConflictInput.RemoteVerified _ -> "RemoteVerified"
        | ConflictInput.RemoteReadFailed _ -> "RemoteReadFailed"
        | ConflictInput.RemoteMissingOrInvalid _ -> "RemoteMissingOrInvalid"
        | ConflictInput.ReadAgain -> "ReadAgain"
        | ConflictInput.ReviewChosen -> "ReviewChosen"
        | ConflictInput.ReapplyChosen -> "ReapplyChosen"
        | ConflictInput.ReapplyDone _ -> "ReapplyDone"
        | ConflictInput.DiscardChosen -> "DiscardChosen"
        | ConflictInput.DiscardConfirmed -> "DiscardConfirmed"
        | ConflictInput.DiscardCancelled -> "DiscardCancelled"
        | ConflictInput.ExportChosen -> "ExportChosen"
        | ConflictInput.SupersededReply _ -> "SupersededReply"

    /// One value of every SM-CONFLICT state.
    let conflictViews =
        [ ReadingRemote(RequestId 1L)
          Reviewable(verified c2)
          Reapplying(verified c2)
          Reapplied(verified c2, [])
          ConfirmingDiscard(verified c2)
          Discarded(verified c2)
          RemoteUnavailable
          Unresolvable "the GitHub version did not verify" ]

    /// Every SM-SAVE state, with the payload variants its guards distinguish.
    let saveStateValues =
        [ SaveState.Saved(Confirmed(confirmed b1))
          SaveState.Saved(VerifiedAt(verified b1))
          SaveState.Unsaved
          SaveState.Saving
          SaveState.Offline ]
        @ (conflictViews |> List.map SaveState.Conflict)
        @ [ SaveState.UnknownOutcome 3
            SaveState.UnknownOutcome 0
            SaveState.SaveFailed(Refused(RateLimited(Secondary(Some 60))))
            SaveState.SaveFailed(Refused(NetworkUnavailable TimedOut))
            SaveState.SaveFailed RepositoryKeptChanging
            SaveState.SaveFailed(BytesDiffer c2)
            SaveState.SaveFailed HistoryWasRewritten
            SaveState.SaveFailed CredentialNotAccepted ]

    let awaitings =
        [ None
          Some MutationAnswer
          Some(ReadBackAt c2)
          Some FreshHead
          Some(FileComparisonAt c3)
          Some ReconcileHead
          Some(ReconcileCommits 1)
          Some(ReconcileFilesAt c2) ]

    /// Candidate sessions for one state: every awaited answer, changes made during a save or not, bounds unused or
    /// spent, the 60 s reconciliation window free or full. Each guard of state-machine-contracts.json holds for some.
    let candidates (state: SaveState) : InvestigationSession list =
        let recent = [ for s in 1..4 -> t0.AddSeconds(float -s) ]
        [ for awaiting in awaitings do
              for after in [ []; [ command 2 ] ] do
                  for (mutations, rebases, reads) in [ (0, 0, 0); (1, 1, 1); (10, 3, 3) ] do
                      for sentAt in [ []; recent ] ->
                          { session state with
                              Outstanding = awaiting |> Option.map (fun a -> RequestId 1L, a)
                              AfterInFlight = after
                              Mutations = mutations
                              RebaseAttempts = rebases
                              ReconcileReads = reads
                              ReconcileReadsSentAt = sentAt } ]

    /// One input of every SM-SAVE event (replies name the request Outstanding in the candidates, RequestId 1, except
    /// SupersededReply, which names a request that is not current).
    let saveInputs =
        [ SaveInput.Edit(command 3)
          SaveInput.SaveRequested
          SaveInput.MutationSent(RequestId 1L)
          SaveInput.ReadBackMatched(confirmed c2)
          SaveInput.ReadBackMismatched c2
          SaveInput.StaleHeadUnrelated c3
          SaveInput.StaleHeadSameInvestigation c3
          SaveInput.ProviderRefused(PermissionDenied "createCommitOnBranch")
          SaveInput.CredentialRefused
          SaveInput.AnswerLost
          SaveInput.FailedBeforeSending
          SaveInput.ReconcileMarkerBytesMatch(confirmed c2)
          SaveInput.ReconcileMarkerBytesDiffer c2
          SaveInput.ReconcileNothingWrittenHeadUnchanged
          SaveInput.ReconcileNothingWrittenHeadMoved c3
          SaveInput.ReconcileHistoryRewritten
          SaveInput.ReconcileReadFailed
          SaveInput.ReconcileReadsSpent
          SaveInput.CheckAgain
          SaveInput.WentOffline
          SaveInput.WentOnline
          SaveInput.ConflictResolvedReapplied [ command 1 ]
          SaveInput.ConflictResolvedDiscarded(verified c2)
          SaveInput.SupersededReply(RequestId 999L)
          SaveInput.QueuedSaveStarted
          SaveInput.RebaseReadFailed
          SaveInput.ReadBackReadFailed ]

    let conflictInputs =
        [ ConflictInput.RemoteVerified(verified c2)
          ConflictInput.RemoteReadFailed LostAfterSending
          ConflictInput.RemoteMissingOrInvalid None
          ConflictInput.ReadAgain
          ConflictInput.ReviewChosen
          ConflictInput.ReapplyChosen
          ConflictInput.ReapplyDone([ command 1 ], [])
          ConflictInput.DiscardChosen
          ConflictInput.DiscardConfirmed
          ConflictInput.DiscardCancelled
          ConflictInput.ExportChosen
          ConflictInput.SupersededReply(RequestId 999L) ]

    let unionCaseNames (t: Type) =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(t, BindingFlags.Public ||| BindingFlags.NonPublic)
        |> Array.map (fun c -> c.Name)
        |> List.ofArray

    let typeName (t: Type | null) =
        match t with
        | null -> "<none>"
        | t ->
            match t.FullName with
            | null -> t.Name
            | name -> name

/// Finds the persistence role's SaveMachine: the public module value of type SaveMachine in Causa.Persistence.
module Machine =
    let exported () =
        typeof<SaveState>.Assembly.GetExportedTypes()
        |> Seq.collect (fun t -> t.GetProperties(BindingFlags.Public ||| BindingFlags.Static))
        |> Seq.filter (fun p -> p.PropertyType = typeof<SaveMachine> && p.GetIndexParameters().Length = 0)
        |> Seq.sortBy (fun p -> Fixtures.typeName p.DeclaringType + "." + p.Name)
        |> List.ofSeq

    let require () : SaveMachine =
        match exported () with
        | p :: _ ->
            match p.GetValue null with
            | :? SaveMachine as machine -> machine
            | _ -> failwithf "BLOCKED: %s.%s is null" (Fixtures.typeName p.DeclaringType) p.Name
        | [] ->
            failwith
                "BLOCKED: Causa.Persistence exports no SaveMachine value yet (IF41-SAVE is implemented by the persistence role, T19). Expected a public module value of type Causa.Persistence.SaveMachine."

/// PAT-12 over the frozen tables: every (state, event) pair of SM-SAVE and SM-CONFLICT driven through Step.
module Pat12 =
    open Fixtures

    type PairResult =
        { State: string
          Event: string
          Legal: bool
          /// Next states reached by candidates that Step accepted.
          Reached: string list
          Problems: string list }

    let private faultNames (machine: string) (state: string) (event: string) (fault: SaveFault) =
        [ if fault.Machine <> machine then sprintf "fault names machine %s, expected %s" fault.Machine machine
          if fault.State <> state then sprintf "fault names state %s, expected %s" fault.State state
          if fault.Input <> event then sprintf "fault names input %s, expected %s" fault.Input event ]

    /// Saved is entered only from a read-back confirmation (or a marker reconciliation, which carries one), or from a
    /// verified discard (T-S38, ODR-V4-08); its base head is the confirmed commit. Saved to Saved keeps its basis.
    let savedOnlyFromConfirmation (before: InvestigationSession) (input: SaveInput) (after: InvestigationSession) =
        match after.State with
        | SaveState.Saved basis ->
            match before.State, input, basis with
            | SaveState.Saved earlier, _, _ ->
                if earlier = basis then [] else [ "Saved to Saved changed its basis" ]
            | (SaveState.Saving | SaveState.UnknownOutcome _),
              (SaveInput.ReadBackMatched c | SaveInput.ReconcileMarkerBytesMatch c),
              Confirmed c' ->
                [ if c' <> c then "Saved rests on a confirmation other than the one delivered"
                  if after.BaseHead <> c.CommitOid then "Saved did not advance the base head to the confirmed commit" ]
            | SaveState.Conflict _, SaveInput.ConflictResolvedDiscarded v, VerifiedAt v' ->
                [ if v'.ReadAt <> v.ReadAt then "Saved after discard rests on another verified read"
                  if after.BaseHead <> v.ReadAt then "Saved after discard did not take the verified commit as base head" ]
            | _ ->
                [ sprintf "Saved reached from %s by %s without a read-back confirmation" (stateName before.State) (inputName input) ]
        | _ -> []

    let saveMachinePairs (machine: SaveMachine) : PairResult list =
        let byState = saveStateValues |> List.groupBy stateName |> Map.ofList
        [ for stateLabel in MachineTables.saveStates do
              let sessions = byState.[stateLabel] |> List.collect candidates
              for input in saveInputs do
                  let event = inputName input
                  let allowed = MachineTables.saveLegal |> Map.tryFind (stateLabel, event)
                  let outcomes =
                      [ for elsewhere in [ false; true ] do
                            for s in sessions -> s, machine.Step (context elsewhere) s input ]
                  let reached =
                      outcomes |> List.choose (fun (_, r) -> match r with Ok(s', _) -> Some(stateName s'.State) | Error _ -> None) |> List.distinct
                  let problems =
                      [ for (before, result) in outcomes do
                            match result, allowed with
                            | Error fault, None -> yield! faultNames "SM-SAVE" stateLabel event fault
                            | Error fault, Some _ -> if fault.Machine <> "SM-SAVE" then yield sprintf "fault names machine %s" fault.Machine
                            | Ok(after, _), None -> yield sprintf "illegal pair accepted, reaching %s" (stateName after.State)
                            | Ok(after, _), Some nexts ->
                                if not (List.contains (stateName after.State) nexts) then
                                    yield sprintf "reached %s; the table allows %A" (stateName after.State) nexts
                                yield! savedOnlyFromConfirmation before input after
                        if allowed.IsSome && List.isEmpty reached then
                            yield "legal pair: no candidate session satisfied any guard (every Step failed)" ]
                  { State = stateLabel; Event = event; Legal = allowed.IsSome; Reached = reached; Problems = List.distinct problems } ]

    let conflictMachinePairs (machine: SaveMachine) : PairResult list =
        [ for view in conflictViews do
              let stateLabel = conflictName view
              for input in conflictInputs do
                  let event = conflictInputName input
                  let allowed = MachineTables.conflictLegal |> Map.tryFind (stateLabel, event)
                  let outcomes = [ for elsewhere in [ false; true ] -> machine.StepConflict (context elsewhere) view input ]
                  let reached = outcomes |> List.choose (function Ok(v, _) -> Some(conflictName v) | Error _ -> None) |> List.distinct
                  let problems =
                      [ for result in outcomes do
                            match result, allowed with
                            | Error fault, None -> yield! faultNames "SM-CONFLICT" stateLabel event fault
                            | Error fault, Some _ -> if fault.Machine <> "SM-CONFLICT" then yield sprintf "fault names machine %s" fault.Machine
                            | Ok(v, _), None -> yield sprintf "illegal pair accepted, reaching %s" (conflictName v)
                            | Ok(v, _), Some nexts ->
                                if not (List.contains (conflictName v) nexts) then
                                    yield sprintf "reached %s; the table allows %A" (conflictName v) nexts
                        if allowed.IsSome && List.isEmpty reached then
                            yield "legal pair: no candidate satisfied any guard (every StepConflict failed)" ]
                  { State = stateLabel; Event = event; Legal = allowed.IsSome; Reached = reached; Problems = List.distinct problems } ]

    let report (results: PairResult list) =
        results
        |> List.filter (fun r -> not r.Problems.IsEmpty)
        |> List.map (fun r -> sprintf "(%s, %s): %s" r.State r.Event (String.Join("; ", r.Problems)))

/// Who can build a Confirmation: the production assemblies are scanned for any public way to obtain one.
module Construction =
    let productionAssemblies =
        [ typeof<InvestigationId>.Assembly; typeof<SaveState>.Assembly; typeof<Causa.Ui.Contracts.SaveStateKind>.Assembly ]

    let confirmationType = typeof<Confirmation.T>

    let private yieldsConfirmation (t: Type) =
        t = confirmationType
        || (t.IsGenericType && t.GetGenericArguments() |> Array.contains confirmationType && not (t.Name.StartsWith "FSharpFunc"))

    /// Every public method or constructor, in the production assemblies, that returns a Confirmation.T (or an option or
    /// result of one), other than Confirmation.ofReadBack.
    let publicConstructionPaths () =
        [ for asm in productionAssemblies do
              for t in asm.GetExportedTypes() do
                  if t = confirmationType && t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance).Length > 0 then
                      yield sprintf "public constructor %s" (Fixtures.typeName t)
                  for m in t.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) do
                      if yieldsConfirmation m.ReturnType && not (m.Name.StartsWith "get_") then
                          let name = Fixtures.typeName t + "." + m.Name
                          if name <> "Causa.Persistence.ConfirmationModule.ofReadBack" && name <> "Causa.Persistence.Confirmation.ofReadBack" then
                              yield sprintf "public method %s" name ]

    /// Assemblies that may see Causa.Persistence's internals (none may).
    let internalsVisibleTo () =
        typeof<SaveState>.Assembly.GetCustomAttributes<Runtime.CompilerServices.InternalsVisibleToAttribute>()
        |> Seq.map (fun a -> a.AssemblyName)
        |> List.ofSeq

module PERSIST023Tests =
    open Xunit
    open Fixtures

    let private fail (problems: string list) =
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))

    /// O1: the save state is computed by one F# state machine, and the UI renders only that machine's projection.
    [<Fact>]
    let ``PERSIST-023-O1`` () =
        let machine = Machine.require ()
        let saveIndicator = typeof<Causa.App.Projections>.GetProperty("SaveIndicator") |> Option.ofObj |> Option.map (fun p -> p.PropertyType)
        let presentationKinds = unionCaseNames typeof<Causa.Ui.Contracts.SaveStateKind>
        let otherMachines =
            Construction.productionAssemblies
            |> Seq.collect (fun a -> a.GetTypes())
            |> Seq.filter (fun t -> Microsoft.FSharp.Reflection.FSharpType.IsUnion(t, BindingFlags.Public ||| BindingFlags.NonPublic))
            |> Seq.filter (fun t -> t <> typeof<SaveState> && t.DeclaringType <> typeof<SaveState>)
            |> Seq.filter (fun t ->
                let cases = unionCaseNames t |> Set.ofList
                Set.isSubset (Set.ofList MachineTables.saveStates) cases)
            |> Seq.map Fixtures.typeName
            |> List.ofSeq
        fail
            [ if unionCaseNames typeof<SaveState> <> MachineTables.saveStates then
                  sprintf "SaveState cases %A are not exactly the SM-SAVE states" (unionCaseNames typeof<SaveState>)
              if not (List.isEmpty otherMachines) then
                  sprintf "a second save-state union exists outside IF41-SAVE: %A" otherMachines
              if presentationKinds <> (MachineTables.saveStates |> List.map (fun s -> s + "Kind")) then
                  sprintf "the presentation kinds %A do not map one to one onto the machine's states" presentationKinds
              if saveIndicator <> Some typeof<InvestigationSession -> Causa.App.SaveIndicatorView> then
                  sprintf "the save indicator projection is %A, not a projection of the machine's session" (saveIndicator |> Option.map Fixtures.typeName)
              // The one machine computes every state from its own session: Initial is Saved on a verified read.
              match (machine.Initial writable (verified b1)).State with
              | SaveState.Saved(VerifiedAt v) when v.ReadAt = b1 -> ()
              | other -> sprintf "Initial is %s, expected Saved on the verified read" (stateName other) ]

    /// O2: Saved is constructible only from a confirmation produced by the PERSIST-007 read-back.
    [<Fact>]
    let ``PERSIST-023-O2`` () =
        let machine = Machine.require ()
        let differing = { files "written" with Snapshot = Text.Encoding.UTF8.GetBytes "{\"snapshot\":\"tampered\"}\n" }
        let results = Pat12.saveMachinePairs machine
        let savedByReadBack =
            results |> List.exists (fun r -> r.State = "Saving" && r.Event = "ReadBackMatched" && List.contains "Saved" r.Reached)
        let savedByMismatch =
            results |> List.exists (fun r -> r.Event = "ReadBackMismatched" && List.contains "Saved" r.Reached)
        fail
            [ // Negative control (AR-009): differing bytes never yield a confirmation; equal bytes do.
              if (Confirmation.ofReadBack investigation c2 t0 (files "written") differing).IsSome then
                  "Confirmation.ofReadBack confirmed bytes that differ from the written bytes"
              if (Confirmation.ofReadBack investigation c2 t0 (files "written") (files "written")).IsNone then
                  "Confirmation.ofReadBack refused bytes equal to the written bytes"
              if not savedByReadBack then "a matching read-back in Saving never reached Saved (T-S11)"
              if savedByMismatch then "a mismatched read-back reached Saved"
              yield!
                  results
                  |> List.collect (fun r -> r.Problems |> List.filter (fun p -> p.StartsWith "Saved") |> List.map (fun p -> sprintf "(%s, %s): %s" r.State r.Event p)) ]

    /// O3: adapters, view code and test fixtures in the production build cannot construct a confirmation (and so Saved).
    [<Fact>]
    let ``PERSIST-023-O3`` () =
        let t = Construction.confirmationType
        let publicSetters =
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.filter (fun p ->
                match p.SetMethod with
                | null -> false
                | setter -> setter.IsPublic)
        let publicFields = t.GetFields(BindingFlags.Public ||| BindingFlags.Instance)
        let representation =
            Microsoft.FSharp.Reflection.FSharpType.IsRecord(t, BindingFlags.Public)
        fail
            [ yield! Construction.publicConstructionPaths ()
              if representation then "Confirmation.T's record representation is public"
              if t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance).Length > 0 then "Confirmation.T has a public constructor"
              if publicSetters.Length > 0 || publicFields.Length > 0 then "Confirmation.T exposes writable state"
              yield! Construction.internalsVisibleTo () |> List.map (sprintf "Causa.Persistence exposes its internals to %s") ]

    /// O4: every illegal input fails closed with a typed fault and leaves the state unchanged.
    [<Fact>]
    let ``PERSIST-023-O4`` () =
        let machine = Machine.require ()
        let save = Pat12.saveMachinePairs machine
        let conflict = Pat12.conflictMachinePairs machine
        let illegal = save @ conflict |> List.filter (fun r -> not r.Legal)
        fail
            [ if (save |> List.filter (fun r -> not r.Legal)).Length <> 139 then
                  sprintf "SM-SAVE has %d illegal pairs; the table has 139" (save |> List.filter (fun r -> not r.Legal)).Length
              if (conflict |> List.filter (fun r -> not r.Legal)).Length <> 81 then
                  sprintf "SM-CONFLICT has %d illegal pairs; the table has 81" (conflict |> List.filter (fun r -> not r.Legal)).Length
              yield! Pat12.report illegal ]

    /// O5: PAT-12 verifies every state and input pair and that application code cannot construct Saved.
    [<Fact>]
    let ``PERSIST-023-O5`` () =
        let machine = Machine.require ()
        let save = Pat12.saveMachinePairs machine
        let conflict = Pat12.conflictMachinePairs machine
        fail
            [ if save.Length <> 189 then sprintf "SM-SAVE: %d pairs driven, the table has 189" save.Length
              if conflict.Length <> 96 then sprintf "SM-CONFLICT: %d pairs driven, the table has 96" conflict.Length
              yield! Pat12.report (save @ conflict)
              yield! Construction.publicConstructionPaths ()
              yield! Construction.internalsVisibleTo () |> List.map (sprintf "Causa.Persistence exposes its internals to %s") ]
