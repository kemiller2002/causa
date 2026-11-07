// Owner: domain (v4 T12, invariant tests first). INV-01 property; the driver and oracle are in Mutation/INV01.fs.
namespace Causa.Invariants.Property

open System
open Xunit
open Causa.Invariants.Harness

module INV01 =
    /// For random sequences of edits, saves and fake replies (forged commit oids, mismatched read-backs, replies in the
    /// wrong state), Saved occurs only immediately after a matching read-back or marker reconciliation (PERSIST-023-O2).
    [<Fact>]
    let NoSavedWithoutMatchingReadBack () =
        let machine = Machine.require ()
        let report = Inv01.check machine Inv01.production 300 40
        let problems =
            [ yield! report.Violations |> List.truncate 20
              // Not vacuous (AR-009-O2): real saves were confirmed and mismatched read-backs were delivered.
              if report.ConfirmedSaves = 0 then sprintf "no save was ever confirmed in %d steps: the property was not exercised" report.Steps
              if report.MismatchesDelivered = 0 then "no mismatched read-back was delivered while a save was in flight" ]
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))
