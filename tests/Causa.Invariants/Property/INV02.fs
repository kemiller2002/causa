// Owner: domain (v4 T12, invariant tests first). INV-02 property: a reply to a superseded request is inert.
namespace Causa.Invariants.Property

open System
open Xunit
open Causa.Persistence
open Causa.Invariants.Harness
open Causa.Invariants.Harness.Fixtures

module INV02 =
    let private outstandingId (s: InvestigationSession) what =
        match s.Outstanding with
        | Some(id, _) -> id
        | None -> failwithf "%s: no request outstanding" what

    let private loadingId (s: LoadState) =
        match s with
        | Loading(id, _) -> id
        | other -> failwithf "Open did not reach Loading: %A" other

    let private readingId (v: ConflictView) =
        match v with
        | ReadingRemote id -> id
        | other -> failwithf "Read again did not reach ReadingRemote: %A" other

    /// Permuting the delivery order of replies to superseded loads, refreshes, conflict re-reads, mutations and
    /// reconciliation reads leaves the final state equal to delivering only the current reply (PERSIST-051-O2).
    [<Fact>]
    let ReorderedRepliesAreInert () =
        let machine = Machine.require ()
        let problems = Collections.Generic.List<string>()
        let ctx = context 40L t0
        // SM-SAVE in Saving (the mutation outstanding) and in UnknownOutcome (a reconciliation read outstanding).
        let saving = Machine.saving machine (commit 0xb1)
        let unknown =
            let lost = Machine.step machine ctx saving SaveInput.AnswerLost
            match machine.ReconcileDue (context 41L (t0.AddSeconds 3.0)) lost with
            | Ok(s, _) -> s
            | Error fault -> failwithf "ReconcileDue refused in UnknownOutcome: %A" fault
        let sessions =
            [ "Saving", saving, NotAnswered(outstandingId saving "Saving", LostAfterSending)
              "UnknownOutcome", unknown, NotAnswered(outstandingId unknown "UnknownOutcome", TimedOut) ]
        for (label, start, current) in sessions do
            let currentId = requestOf current
            let expected = Delivery.session machine start (set [ currentId ]) [ current ]
            for seed in 1..40 do
                let rng = Random seed
                let stale = Delivery.staleReplies rng (set [ currentId ]) 6
                for o in stale do
                    match machine.Interpret ctx start o with
                    | MachineEvent(SaveInput.SupersededReply id) when id = requestOf o -> ()
                    | other -> problems.Add(sprintf "%s: a reply to superseded %A was interpreted as %A" label (requestOf o) other)
                let actual = Delivery.session machine start (set [ currentId ]) (Delivery.shuffleWith rng current stale)
                if actual <> expected then
                    problems.Add(sprintf "%s seed %d: stale replies changed the outcome (%s, base %A) versus current only (%s, base %A)" label seed (stateName actual.State) actual.BaseHead (stateName expected.State) expected.BaseHead)
        // SM-LOAD: replies to superseded loads and refreshes.
        let loading =
            match machine.StepLoad ctx NotLoaded (LoadInput.OpenRequested reference) with
            | Ok(s, _) -> s
            | Error fault -> failwithf "OpenRequested refused: %A" fault
        let loadCurrent = NotAnswered(loadingId loading, LostAfterSending)
        let loadExpected = Delivery.load machine loading (set [ requestOf loadCurrent ]) [ loadCurrent ]
        // SM-CONFLICT: replies to superseded conflict re-reads.
        let reading =
            match machine.StepConflict ctx (Reviewable(verified (commit 0xc1))) ConflictInput.ReadAgain with
            | Ok(v, _) -> v
            | Error fault -> failwithf "ReadAgain refused in Reviewable: %A" fault
        let conflictCurrent = NotAnswered(readingId reading, LostAfterSending)
        let conflictExpected = Delivery.conflict machine reading (set [ requestOf conflictCurrent ]) [ conflictCurrent ]
        for seed in 1..40 do
            let rng = Random(1000 + seed)
            let staleLoads = Delivery.staleReplies rng (set [ requestOf loadCurrent ]) 6
            let loadActual = Delivery.load machine loading (set [ requestOf loadCurrent ]) (Delivery.shuffleWith rng loadCurrent staleLoads)
            if loadActual <> loadExpected then
                problems.Add(sprintf "SM-LOAD seed %d: stale replies gave %A, current only %A" seed loadActual loadExpected)
            let staleReads = Delivery.staleReplies rng (set [ requestOf conflictCurrent ]) 6
            let conflictActual = Delivery.conflict machine reading (set [ requestOf conflictCurrent ]) (Delivery.shuffleWith rng conflictCurrent staleReads)
            if conflictActual <> conflictExpected then
                problems.Add(sprintf "SM-CONFLICT seed %d: stale replies gave %A, current only %A" seed conflictActual conflictExpected)
        if problems.Count > 0 then failwith (String.Join(Environment.NewLine, problems |> Seq.truncate 20))
