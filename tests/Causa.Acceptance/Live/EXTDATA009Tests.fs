// Owner: integration (T20, reassigned). EXT-DATA-009: a clean install proves retrieval, denial, refusal and the round
// trip against a separately populated repository; the deployed exact SHA is the one built.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA009Tests(fixture: LiveFixture<EXTDATA009Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA009Tests>>

    /// Zero production fixtures; authorized retrieval; denial without authorization; malformed-data refusal; create,
    /// write and reload.
    [<Fact>]
    member _.``EXT-DATA-009-O1``() =
        Assert.Empty(AppDrive.bundledInvestigationContent ())
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.True((AppDrive.catalogListing run).IsSome, "authorized retrieval listed nothing")
        let denied = AppDrive.connect live.Execute { live.ReadConnection with Credential = Credential.create "causa-ext-data-009-not-a-token" }
        Assert.Equal(CredentialRefused, denied.Model.Connection)
        let malformed : FileSet = { Manifest = "{}"B; Events = "not json"B; Snapshot = "{}"B }
        Assert.True(StorageCodecs.codec.Verify (WorkspaceAddress.catalog (Workspace "w")) (CommitOid "x") malformed |> Result.isError, "malformed files verified")
        let runName = "ext-data-009-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let engine = Update.engine (AppDrive.persistent runName)
        let writable = (WorkspaceAddress.workingCopy [] runName |> Option.bind Writable.ofAddress).Value
        let commands = AppDrive.startCommands "EXT-DATA-009" "Create, write, reload." ""
        let ref = { Address = Writable.address writable; Id = commands.Head.Investigation }
        let connected = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.WriteConnection.Repository, live.WriteConnection.Credential))) (AppDrive.fresh ())
        let saved =
            connected
            |> AppDrive.sendWith engine live.Execute (Msg.CreateRequested(writable, commands.Head))
            |> AppDrive.sendWith engine live.Execute (Msg.SessionInput(ref, SaveInput.SaveRequested))
        Assert.True((match saved.Model.Sessions[ref].State with SaveState.Saved _ -> true | _ -> false), sprintf "%A" saved.Announcements)
        let reloaded = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))) (AppDrive.fresh ())
        Assert.True(reloaded.Model.Listings |> Map.exists (fun _ l -> l.Investigations |> List.exists (fun i -> i.Ref = ref)), "the reload did not list the new investigation")

    /// The deployed exact SHA is the commit the site was built from (dist/provenance.json, AR-011).
    [<Fact>]
    member _.``EXT-DATA-009-O2``() =
        let out = Causa.Acceptance.Deterministic.Tooling.tempDir ()
        try
            Causa.Acceptance.Deterministic.Tooling.build [ "--provenance-only"; "--out"; out ] |> ignore
            use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(out, "provenance.json")))
            let r = doc.RootElement.GetProperty "reproducible"
            let commit = r.GetProperty("commit").GetString()
            Assert.Equal(commit, r.GetProperty("deployedSha").GetString())
            Assert.Matches("^[0-9a-f]{40}$", commit)
        finally
            Directory.Delete(out, true)
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
