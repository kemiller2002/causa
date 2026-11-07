// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-012 fast local feedback: documented commands, offline CI without secrets, and measured timings by layer.
namespace Causa.Acceptance.Live

open System
open System.Diagnostics
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF012Tests(fixture: LiveFixture<PF012Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf012/investigations/00000000-0000-0000-0000-000000000012"
    let docs = [ "README.md"; "DEPLOYMENT.md" ]
    let documentedLines () =
        docs |> List.filter Repo.exists |> List.collect (fun d -> Repo.read d |> fun t -> t.Split('\n') |> List.ofArray)

    /// The run steps of the Causa CI workflow that execute dotnet test.
    let ciTestCommands () =
        Repo.read ".github/workflows/ci.yml" |> fun t -> t.Split('\n') |> Array.filter (fun l -> l.Contains "dotnet test") |> List.ofArray

    interface IClassFixture<LiveFixture<PF012Tests>>

    /// PF-012-O1: one documented command builds and starts a populated fixture-backed UI (a CAUSA_PROVIDER fixture mode), and
    /// a separate documented command runs the fast offline domain, UI and provider-contract checks (a dotnet test that
    /// does not select the live suites).
    [<Fact>]
    member _.``PF-012-O1``() =
        let lines = documentedLines ()
        Assert.True(lines |> List.exists (fun l -> l.Contains "CAUSA_PROVIDER=" && l.ToLowerInvariant().Contains "fixture"), sprintf "no documented command builds and starts the fixture-backed UI (CAUSA_PROVIDER=<fixture mode>) in %A" docs)
        Assert.True(
            lines |> List.exists (fun l -> l.Contains "dotnet test" && l.Contains "!~" && l.Contains "Live"),
            sprintf "no documented offline check command (dotnet test excluding the Live suites) in %A" docs)
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))

    /// PF-012-O2: CI runs the offline deterministic checks without secrets: the Causa CI workflow names no secret and
    /// its dotnet test excludes the live suites, so a missing credential cannot fail it.
    [<Fact>]
    member _.``PF-012-O2``() =
        let ci = Repo.read ".github/workflows/ci.yml"
        for marker in [ "secrets."; "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] do
            Assert.False(ci.Contains marker, sprintf "ci.yml uses %s" marker)
        let commands = ciTestCommands ()
        Assert.NotEmpty(commands)
        let runsLive = commands |> Seq.filter (fun c -> not (c.Contains "!~Causa.Acceptance.Live" && c.Contains "!~Causa.Invariants.Live")) |> Seq.map (fun c -> c.Trim()) |> List.ofSeq
        Assert.True(List.isEmpty runsLive, sprintf "the offline CI job (.github/workflows/ci.yml) runs the live suites with no secret, so a missing credential or a provider outage fails it: %A" runsLive)
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))

    /// PF-012-O3: execution time and failures are recorded by layer, with no invented budget: the offline provider
    /// contract layer and the live layer are each timed and their failures counted, and the record is written as
    /// evidence next to the live run's.
    [<Fact>]
    member _.``PF-012-O3``() =
        let timed (f: unit -> Conformance.Probe list) =
            let watch = Stopwatch.StartNew()
            let probes = f ()
            watch.Stop()
            watch.Elapsed.TotalMilliseconds, Conformance.failures probes |> List.length
        let offlineMs, offlineFailures = timed (fun () -> Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore)
        let live = fixture.Run
        let liveMs, liveFailures = timed (fun () -> Conformance.run (Conformance.overLive live) live.Created)
        let layer name ms failures = Json.Object [ "layer", Json.String name; "milliseconds", Json.Number(decimal ms); "failures", Json.Number(decimal failures) ]
        let dir = IO.Path.GetDirectoryName(live.WriteEvidence()) |> Option.ofObj |> Option.defaultValue "."
        let file = IO.Path.Combine(dir, "PF012-layer-timings.json")
        IO.File.WriteAllText(file, Json.serialize (Json.Array [ layer "provider-contract-offline" offlineMs offlineFailures; layer "live-integration" liveMs liveFailures ]))
        let recorded = Json.parse (IO.File.ReadAllText file) |> Json.items
        Assert.Equal(2, recorded.Length)
        Assert.Equal(0, offlineFailures)
        Assert.Equal(0, liveFailures)
        Assert.True(liveMs > 0.0 && offlineMs > 0.0)
