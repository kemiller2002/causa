// Owner: persistence. T18: INV-22 live: three simultaneous commits; every refusal GitHub returns is a stale head.
namespace Causa.Invariants.Live

open System.Text
open System.Threading.Tasks
open Xunit
open Causa.Persistence
open Causa.TestSupport

type INV22(fixture: LiveFixture<INV22>) =

    /// Three writers commit at the same expected head at once: exactly one commit lands, and every refusal is
    /// classified as a stale head (never a permission failure or a rate limit), whatever form GitHub sends it in
    /// (STALE_DATA or FORBIDDEN 'is at <oid> but expected <oid>', Rehearsal 7 KD-022). The answers are kept,
    /// sanitized, in the run's evidence.
    let threeAtOnce (writer: ProviderPort) (reader: ProviderPort) (prefix: RepoPath) (created: CommitOid -> unit) =
        let (RepoPath p) = prefix
        let head = reader.ReadHead() |> Result.defaultWith (fun e -> failwithf "head read failed: %A" e)
        let results =
            [| 1..3 |]
            |> Array.map (fun i ->
                Task.Run(fun () -> writer.Commit head prefix (sprintf "causa INV-22 writer %d" i) [ PutFile(RepoPath(sprintf "%s/writer-%d.json" p i), Encoding.UTF8.GetBytes(sprintf "{\"writer\":%d}" i)) ]))
            |> Task.WhenAll
            |> fun t -> t.GetAwaiter().GetResult()
            |> List.ofArray
        results |> List.iter (Result.iter (fun c -> created c.Oid))
        let landed = results |> List.choose Result.toOption
        let refused = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        Assert.True(List.length landed = 1, sprintf "expected exactly one commit to land: %A" results)
        for e in refused do
            match e with
            | StaleHead(expected, _) -> Assert.Equal(head, expected)
            | other -> failwithf "a refusal of a simultaneous commit was classified as %A, not a stale head" other
        Assert.Equal(Ok (List.head landed).Oid, reader.ReadHead())

    interface IClassFixture<LiveFixture<INV22>>

    [<Fact>]
    member _.ThreeSimultaneousCommits() =
        let fake = StatefulFake()
        threeAtOnce (Provider.over fake.Execute fake.WriteConnection) (Provider.over fake.Execute fake.ReadConnection) (RepoPath "workspaces/demo/causa-app-inv22/investigations/i") ignore
        let live = fixture.Run
        threeAtOnce live.Writer live.Reader live.Prefix live.Created
