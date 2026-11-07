// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-020 deployment gate: release acceptance needs every level green plus exact-SHA deployed browser verification;
// an unavailable provider or credential is BLOCKED and the offline evidence is kept.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF020Tests(fixture: LiveFixture<PF020Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf020/investigations/00000000-0000-0000-0000-000000000020"
    let fakeProbes () = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore

    let withoutCredentials (f: unit -> 'a) : 'a =
        let saved = [ "CAUSA_GITHUB_TOKEN"; "CAUSA_WRITE_TOKEN" ] |> List.map (fun v -> v, Environment.GetEnvironmentVariable v)
        try
            for (v, _) in saved do
                Environment.SetEnvironmentVariable(v, null)
            f ()
        finally
            for (v, value) in saved do
                Environment.SetEnvironmentVariable(v, value)

    interface IClassFixture<LiveFixture<PF020Tests>>

    /// PF-020-O1: release acceptance needs green domain, fixture UI, provider conformance and live checks plus exact-SHA
    /// deployed browser verification: the gate has a release completion gate separate from a slice pass and serves the
    /// candidate at its exact SHA to the journeys, and provider conformance is green on the fake and on real GitHub
    /// together, as live verification with real commits.
    [<Fact>]
    member _.``PF-020-O1``() =
        let gate = Repo.read ".github/workflows/verification-gate.yml"
        Assert.Contains("a slice PASS is never a release PASS", gate)
        Assert.Contains("CAUSA_CANDIDATE", gate)
        for suite in [ "SU-OWN-DOTNET"; "SU-DOTNET-DETERMINISTIC"; "SU-BROWSER-CHROMIUM"; "SU-DOTNET-LIVE" ] do
            Assert.Contains(suite, gate)
        Assert.Empty(Conformance.failures (fakeProbes ()))
        let live = fixture.Run
        let report : Conformance.Report = { Mode = "live-github"; Probes = Conformance.run (Conformance.overLive live) live.Created; CreatedCommits = live.CreatedCommits }
        Assert.True(report.IsLiveVerification, sprintf "live failures: %A" (Conformance.failures report.Probes))

    /// PF-020-O2: an unavailable credential is BLOCKED, never a pass, and the offline evidence is preserved: with the
    /// secrets unset the live fixture refuses with BLOCKED while the offline transcript is still produced and written;
    /// with them present the live run proceeds and the offline record is still there.
    [<Fact>]
    member _.``PF-020-O2``() =
        let dir =
            match Environment.GetEnvironmentVariable "CAUSA_EVIDENCE_DIR" with
            | null -> IO.Path.Combine(Environment.CurrentDirectory, "live-evidence")
            | d -> d
        IO.Directory.CreateDirectory dir |> ignore
        let offlineRecord = IO.Path.Combine(dir, "PF020-offline-conformance.json")
        withoutCredentials (fun () ->
            let e = Assert.Throws<InvalidOperationException>(fun () -> new LiveRun("PF020Probe") |> ignore)
            Assert.StartsWith("BLOCKED: no credential", e.Message)
            let offline = fakeProbes ()
            Assert.Empty(Conformance.failures offline)
            let doc = Json.Object [ "providerMode", Json.String "fake"; "probes", Json.Array [ for p in offline -> Json.Object [ "name", Json.String p.Name; "actual", Json.String p.Actual ] ] ]
            IO.File.WriteAllText(offlineRecord, Json.serialize doc))
        Assert.True(IO.File.Exists offlineRecord)
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
        Assert.True(IO.File.Exists offlineRecord, "the live run removed the offline evidence")
        Assert.Contains("\"providerMode\":\"fake\"", IO.File.ReadAllText offlineRecord)
