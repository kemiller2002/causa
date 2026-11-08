// Owner: integration (T20, reassigned). EXT-DATA-002: a fresh launch is disconnected, with an explicit Connect.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA002Tests(fixture: LiveFixture<EXTDATA002Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA002Tests>>

    /// A fresh launch shows an accessible disconnected workspace with an explicit Connect data repository action.
    [<Fact>]
    member _.``EXT-DATA-002-O1``() =
        let s, _ = AppDrive.ui.Init InvestigationsRoute
        let v = AppDrive.view s
        Assert.True(AppDrive.flag "connection.visible" v)
        Assert.Equal("Not connected", AppDrive.text "connection.state" v)
        Assert.True(AppDrive.flag "connection.connect-offered" v)
        Assert.Contains("data-event=\"UIA-CONNECT\"", Repo.read "web/screens/investigation-list.html")
        let live = fixture.Run
        let s2, sent = AppDrive.connectThroughForm live.Execute live.ReadConnection (match LiveEnv.token LiveEnv.Read with Ok t -> t | Error e -> failwith e)
        Assert.NotEmpty(sent)
        Assert.False(AppDrive.flag "connection.visible" (AppDrive.view s2), "the Connect region stays after an explicit, successful Connect")

    /// No implicit demo import and no fallback to bundled fixtures: before Connect nothing is listed or read.
    [<Fact>]
    member _.``EXT-DATA-002-O2``() =
        let run = AppDrive.fresh ()
        Assert.True(run.Model.Listings.IsEmpty)
        Assert.True(run.Model.Loads.IsEmpty)
        let s, fx = AppDrive.ui.Init InvestigationsRoute
        Assert.Empty(fx |> List.filter (function Http _ -> true | _ -> false))
        Assert.Equal(NotRequested, (AppDrive.ui.Project s).Screen |> function ListScreen l -> l.Investigations | _ -> NotRequested)
        let live = fixture.Run
        let connected = AppDrive.connect live.Execute live.ReadConnection
        Assert.All(connected.Requests, fun r -> Assert.True(r.Url.StartsWith(GitHubRequests.ApiBase + "/repos/" + live.ReadConnection.Repository.Owner + "/"), r.Url))
