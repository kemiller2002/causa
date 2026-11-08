// Owner: integration (T20, reassigned). EXT-DATA-012: a configurable data repository, isolated from the application's;
// its content untrusted; no data through public Pages assets.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA012Tests(fixture: LiveFixture<EXTDATA012Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA012Tests>>

    /// The data repository is whatever the user connects to; no repository is built in.
    [<Fact>]
    member _.``EXT-DATA-012-O1``() =
        // No repository is preset: the Connect form starts empty and a fresh engine has no connection context. (The
        // contract's own references to causa-data are documentation, not a connection target.)
        let s, _ = AppDrive.ui.Init InvestigationsRoute
        Assert.Equal(Connection.emptyForm, s.Local.Form)
        Assert.True((AppDrive.fresh ()).Model.Context.IsNone)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.Equal(Some live.ReadConnection.Repository, run.Model.Context |> Option.map (fun c -> c.Repository))
        match Environment.GetEnvironmentVariable "GITHUB_REPOSITORY" with
        | null | "" -> ()
        | app -> Assert.NotEqual<string>(app, live.ReadConnection.Repository.Owner + "/" + live.ReadConnection.Repository.Name)

    /// Repository content is untrusted: forged entries are dropped and only verified files are loaded.
    [<Fact>]
    member _.``EXT-DATA-012-O2``() =
        let forged =
            System.Text.Json.JsonDocument.Parse("""{"cases":[{"investigationId":"../../etc","path":"x"},{"investigationId":"6b1c6f0e-2f7a-4a52-9a8e-0d8d6f1e3c11"}]}""").RootElement
        match GitHubLister.catalogListing (WorkspaceAddress.catalog (Workspace "w")) (CommitOid "c") forged with
        | Ok l -> Assert.Empty(l.Investigations)
        | Error _ -> ()
        let entries = [ "../evil", "dir"; "not-a-guid", "dir"; "6b1c6f0e-2f7a-4a52-9a8e-0d8d6f1e3c11", "file" ]
        Assert.Empty((GitHubLister.workingCopyListing (WorkspaceAddress.workingCopy [] "x").Value (CommitOid "c") entries).Investigations)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.All((AppDrive.catalogListing run).Value.Investigations, fun i -> Assert.NotEqual(Guid.Empty, (let (InvestigationId g) = i.Ref.Id in g)))

    /// No data is reachable through the public Pages assets: the site carries no investigation file and the app reads
    /// data only from api.github.com.
    [<Fact>]
    member _.``EXT-DATA-012-O3``() =
        let out = Causa.Acceptance.Deterministic.Tooling.tempDir ()
        try
            Causa.Acceptance.Deterministic.Tooling.build [ "--skip-engine"; "--out"; out ] |> ignore
            Assert.DoesNotContain(Directory.GetFiles(out, "*", SearchOption.AllDirectories), fun f -> f.EndsWith "events.ndjson" || f.EndsWith "snapshot.json")
        finally
            Directory.Delete(out, true)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.All(run.Requests, fun r -> Assert.StartsWith(GitHubRequests.ApiBase + "/", r.Url))
