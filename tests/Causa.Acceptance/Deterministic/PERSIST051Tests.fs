// Owner: domain (v4 T12, invariant tests first). PERSIST-051: the PAT-12 save-state machine model test. The tables,
// fixtures and PAT-12 driver are in PERSIST023Tests.fs (compiled first in this project).
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open Causa.Domain
open Causa.Persistence

/// PERSIST-051-O3: the compile-time negative check. A script outside the persistence module that forges a
/// confirmation (and from it Saved) must be refused by the F# compiler with an accessibility error; the same script
/// using the one legal constructor must compile (positive control, so the refusal is for the intended reason).
module CompileCheck =
    type Outcome = { ExitCode: int; Output: string }

    let private dotnet () =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null
        | "" -> "dotnet"
        | path -> path

    let run (body: string) : Outcome =
        let dir = Path.Combine(Path.GetTempPath(), "causa-pat12-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let reference (t: Type) = sprintf "#r @\"%s\"" t.Assembly.Location
            let script =
                String.Join(
                    "\n",
                    [ reference typeof<InvestigationId>
                      reference typeof<SaveState>
                      "open Causa.Domain"
                      "open Causa.Persistence"
                      body
                      "printfn \"compiled\"" ]
                )
            let path = Path.Combine(dir, "Check.fsx")
            File.WriteAllText(path, script)
            let info = ProcessStartInfo(dotnet (), sprintf "fsi --nologo \"%s\"" path)
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.UseShellExecute <- false
            info.WorkingDirectory <- dir
            use p = match Process.Start info with null -> failwith "dotnet fsi could not be started" | p -> p
            let stdout = p.StandardOutput.ReadToEndAsync()
            let stderr = p.StandardError.ReadToEndAsync()
            if not (p.WaitForExit 180000) then
                p.Kill true
                failwith "dotnet fsi did not finish within 180 s"
            { ExitCode = p.ExitCode; Output = stdout.Result + stderr.Result }
        finally
            try Directory.Delete(dir, true) with _ -> ()

    let forgedSaved =
        """
let forged : Confirmation.T =
    { Investigation = InvestigationId System.Guid.Empty
      Commit = CommitOid "forged"
      CommittedAt = System.DateTimeOffset.MinValue }
let saved = SaveState.Saved(Confirmed forged)
"""

    let legalSaved =
        """
let bytes = { Manifest = [| 1uy |]; Events = [| 2uy |]; Snapshot = [| 3uy |] }
let confirmation = Confirmation.ofReadBack (InvestigationId System.Guid.Empty) (CommitOid "c") System.DateTimeOffset.MinValue bytes bytes
let saved = confirmation |> Option.map (fun c -> SaveState.Saved(Confirmed c))
"""

    let refusedForAccessibility (o: Outcome) =
        o.ExitCode <> 0 && (o.Output.Contains "FS1093" || o.Output.Contains "FS0410" || o.Output.Contains "not accessible")

module PERSIST051Tests =
    open Xunit
    open Fixtures

    let private fail (problems: string list) =
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))

    /// The enumeration is complete: the states and inputs driven are exactly the machine's unions and the table's lists.
    let private enumerationProblems (results: Pat12.PairResult list) =
        [ if unionCaseNames typeof<SaveState> <> MachineTables.saveStates then
              sprintf "SaveState cases %A differ from the SM-SAVE states" (unionCaseNames typeof<SaveState>)
          if unionCaseNames typeof<SaveInput> <> MachineTables.saveEvents then
              sprintf "SaveInput cases %A differ from the SM-SAVE events" (unionCaseNames typeof<SaveInput>)
          if (saveInputs |> List.map inputName) <> MachineTables.saveEvents then "the driven inputs are not every SM-SAVE event"
          if (saveStateValues |> List.map stateName |> List.distinct) <> MachineTables.saveStates then
              "the driven states are not every SM-SAVE state"
          let driven = results |> List.map (fun r -> r.State, r.Event) |> Set.ofList
          let expected = Set.ofList [ for s in MachineTables.saveStates do for e in MachineTables.saveEvents -> s, e ]
          if driven <> expected then sprintf "driven %d pairs; missing %A" driven.Count (Set.difference expected driven) ]

    /// O1: an F# test that enumerates every save state and every input of the save-state machine, and runs it.
    [<Fact>]
    let ``PERSIST-051-O1`` () =
        let machine = Machine.require ()
        let results = Pat12.saveMachinePairs machine
        fail (enumerationProblems results @ Pat12.report results)

    /// O2: each legal transition reaches the state PERSIST-031 defines, every illegal input fails closed with a typed
    /// fault, and Saved is reached only from a read-back confirmation.
    [<Fact>]
    let ``PERSIST-051-O2`` () =
        let machine = Machine.require ()
        let results = Pat12.saveMachinePairs machine
        let legal = results |> List.filter (fun r -> r.Legal)
        fail
            [ if legal.Length <> 50 then sprintf "%d legal pairs driven; the table has 50" legal.Length
              yield! Pat12.report results
              yield! Pat12.report (Pat12.conflictMachinePairs machine) ]

    /// O3: a compile-time negative check: code outside the persistence module cannot construct Saved.
    [<Fact>]
    let ``PERSIST-051-O3`` () =
        let forged = CompileCheck.run CompileCheck.forgedSaved
        let legal = CompileCheck.run CompileCheck.legalSaved
        fail
            [ if not (CompileCheck.refusedForAccessibility forged) then
                  sprintf "forging a confirmation outside Causa.Persistence was not refused for accessibility (exit %d):\n%s" forged.ExitCode forged.Output
              if legal.ExitCode <> 0 || not (legal.Output.Contains "compiled") then
                  sprintf "positive control: the legal construction did not compile (exit %d):\n%s" legal.ExitCode legal.Output ]

    /// O4: the PAT-12 test passes on the candidate SHA (the whole model test: every pair of both machines and the
    /// construction scan).
    [<Fact>]
    let ``PERSIST-051-O4`` () =
        let machine = Machine.require ()
        let save = Pat12.saveMachinePairs machine
        fail
            [ yield! enumerationProblems save
              yield! Pat12.report save
              yield! Pat12.report (Pat12.conflictMachinePairs machine)
              yield! Construction.publicConstructionPaths () ]

    /// O5: it covers PERSIST-007 (read-back), PERSIST-023 (structural truth) and PERSIST-031 (the seven states).
    [<Fact>]
    let ``PERSIST-051-O5`` () =
        let machine = Machine.require ()
        let readBackAt c =
            { session SaveState.Saving with Outstanding = Some(RequestId 1L, ReadBackAt c) }
        let ctx = context false
        let matched = machine.Step ctx (readBackAt c2) (SaveInput.ReadBackMatched(confirmed c2))
        let mismatched = machine.Step ctx (readBackAt c2) (SaveInput.ReadBackMismatched c2)
        let duringSave = machine.Step ctx { readBackAt c2 with AfterInFlight = [ command 2 ] } (SaveInput.ReadBackMatched(confirmed c2))
        fail
            [ // PERSIST-031: exactly seven states.
              if unionCaseNames typeof<SaveState> <> MachineTables.saveStates then "SaveState is not exactly the seven PERSIST-031 states"
              // PERSIST-007: Saved only from an equal read-back, with the base head advanced to the confirmed commit.
              match matched with
              | Ok(s, _) ->
                  match s.State with
                  | SaveState.Saved(Confirmed c) when c.CommitOid = c2 && s.BaseHead = c2 -> ()
                  | other -> sprintf "PERSIST-007: a matching read-back reached %s (base %A), expected Saved at the commit" (stateName other) s.BaseHead
              | Error f -> sprintf "PERSIST-007: a matching read-back was refused: %A" f
              match mismatched with
              | Ok(s, _) ->
                  match s.State with
                  | SaveState.SaveFailed _ when s.Unsaved = (readBackAt c2).Unsaved -> ()
                  | other -> sprintf "PERSIST-007: a mismatched read-back reached %s or dropped changes, expected SaveFailed keeping them" (stateName other)
              | Error f -> sprintf "PERSIST-007: a mismatched read-back was refused: %A" f
              match duringSave with
              | Ok(s, _) when stateName s.State = "Unsaved" && s.BaseHead = c2 -> ()
              | Ok(s, _) -> sprintf "T-S12: changes made during the save left %s (base %A), expected Unsaved at the confirmed commit" (stateName s.State) s.BaseHead
              | Error f -> sprintf "T-S12: refused: %A" f
              // PERSIST-023: no public construction path.
              yield! Construction.publicConstructionPaths () ]
