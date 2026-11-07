// Owner: integration (T20, reassigned). EXT-DATA-003: the user names the repository and branch; a token alone is not
// a target.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA003Tests(fixture: LiveFixture<EXTDATA003Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA003Tests>>

    /// The repository and branch are specified explicitly and the credential goes through the Connect flow.
    [<Fact>]
    member _.``EXT-DATA-003-O1``() =
        let token = "not-a-real-token"
        let noBranch, sent = AppDrive.connectThroughForm (fun r -> failwithf "nothing may be sent: %s" r.Url) { Repository = { Owner = "o"; Name = "r"; Branch = "" }; Credential = Credential.create token } token
        Assert.Empty(sent)
        Assert.Contains(noBranch.Local.Errors, fun e -> e.Field = Fields.branch)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let first = run.Requests.Head
        Assert.Equal(sprintf "%s/repos/%s/%s" GitHubRequests.ApiBase live.ReadConnection.Repository.Owner live.ReadConnection.Repository.Name, first.Url)
        Assert.True(run.Requests |> List.exists (fun r -> r.Url.Contains("/git/ref/heads/")), "the branch the user named is read")

    /// A token alone never selects a repository.
    [<Fact>]
    member _.``EXT-DATA-003-O2``() =
        let token = "not-a-real-token"
        let onlyToken, sent =
            (fst (AppDrive.ui.Init InvestigationsRoute), [])
            |> AppDrive.intent (fun r -> failwithf "nothing may be sent: %s" r.Url) (UiIntent.Secret(Fields.token, Credential.create token))
            |> AppDrive.intent (fun r -> failwithf "nothing may be sent: %s" r.Url) (UiIntent.Activate(Actions.connect, None))
        Assert.Empty(sent)
        Assert.NotEmpty(onlyToken.Local.Errors)
        Assert.Equal(Disconnected, onlyToken.Model.Connection)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let repoPrefix = sprintf "%s/repos/%s/%s" GitHubRequests.ApiBase live.ReadConnection.Repository.Owner live.ReadConnection.Repository.Name
        Assert.All(run.Requests, fun r -> Assert.StartsWith(repoPrefix, r.Url))
