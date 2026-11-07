// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-017 failure isolation: failures are reported by layer; a provider outage never fails the offline checks and an
// offline pass never waives the integration gate.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF017Tests(fixture: LiveFixture<PF017Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf017/investigations/00000000-0000-0000-0000-000000000017"

    /// The layer each dotnet suite reports.
    let layerOf =
        Map.ofList
            [ "SU-OWN-DOTNET", "domain/provider-contract"; "SU-DOTNET-DETERMINISTIC", "domain/provider-contract"; "SU-DOTNET-INVARIANTS", "domain"
              "SU-DOTNET-LIVE", "live-integration" ]

    /// A provider that is down: every request fails before it is sent.
    let outage : Execute = fun r -> NotAnswered(r.Id, TransportFailure.FailedBeforeSending)

    interface IClassFixture<LiveFixture<PF017Tests>>

    /// PF-017-O1: failures are reported by layer: every acceptance test of this assembly is bound in the plan to exactly
    /// one suite, that suite's command selects it, and each suite belongs to one layer; a live failure names its layer
    /// in the live run's evidence.
    [<Fact>]
    member _.``PF-017-O1``() =
        let bound = Plan.tests ()
        let suites = Plan.suites () |> List.map (fun s -> s.Id, s.Command) |> Map.ofList
        for (id, _) in Plan.identities typeof<PF017Tests>.Assembly do
            let entries = bound |> List.filter (fun t -> t.Id = id)
            Assert.True(entries.Length = 1, sprintf "%s is bound to %d suites" id entries.Length)
            let suite = entries.Head.Suite
            Assert.True(layerOf.ContainsKey suite, sprintf "%s runs in %s, which reports no layer" id suite)
            let ns = id.Substring("dotnet:".Length)
            let selected = ns.Substring(0, ns.LastIndexOf('.', ns.LastIndexOf('.') - 1) + 1)
            Assert.True(suites[suite].Contains selected, sprintf "%s does not select %s" suite selected)
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created |> ignore
        let evidence = IO.File.ReadAllText(live.WriteEvidence())
        Assert.Contains("\"testClass\":\"PF017Tests\"", evidence)
        Assert.Contains("\"providerMode\":\"live-github\"", evidence)

    /// PF-017-O2: a provider outage does not fail the offline checks, and an offline pass does not waive a failing
    /// integration gate: with the provider down the offline transcript still passes while the live transcript fails
    /// and is not live verification; the offline CI job does not run the live suites; and with the provider up the
    /// live gate passes on its own evidence.
    [<Fact>]
    member _.``PF-017-O2``() =
        let offline = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
        Assert.Empty(Conformance.failures offline)
        let fake = StatefulFake()
        let down : Conformance.Adapter =
            { Conformance.overFake fake prefix with
                Mode = "live-github"
                Writer = Provider.over outage fake.WriteConnection
                Reader = Provider.over outage fake.ReadConnection
                BadCredential = Provider.over outage (fake.Connection "x")
                MissingBranch = Provider.over outage fake.ReadConnection }
        let duringOutage = Conformance.run down ignore
        Assert.NotEmpty(Conformance.failures duringOutage)
        Assert.False(({ Mode = "live-github"; Probes = duringOutage; CreatedCommits = [] } : Conformance.Report).IsLiveVerification)
        Assert.Empty(Conformance.failures offline)
        let ci = Repo.read ".github/workflows/ci.yml"
        let commands = ci.Split('\n') |> Array.filter (fun l -> l.Contains "dotnet test")
        Assert.All(commands, fun c -> Assert.True(c.Contains "!~Causa.Acceptance.Live" && c.Contains "!~Causa.Invariants.Live", sprintf "the offline CI job runs the live suites, so a provider outage fails it: %s" (c.Trim())))
        let live = fixture.Run
        let real = Conformance.run (Conformance.overLive live) live.Created
        Assert.True(({ Mode = "live-github"; Probes = real; CreatedCommits = live.CreatedCommits } : Conformance.Report).IsLiveVerification)
