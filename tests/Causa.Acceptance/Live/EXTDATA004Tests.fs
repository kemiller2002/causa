// Owner: integration (T20, reassigned). EXT-DATA-004: credentials. The only bundled credential is the deployed test
// build's injected key (EXT-DATA-004-O5, ODR-COMP-P13 A; tested in Causa.App.Tests DeployConnectionTests).
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

module private Secrets =
    let tokenPattern = Regex("gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}")

    /// Every committed text file that could ship or be logged: source, web, build, workflows, docs.
    let committedTexts () =
        [ for dir in [ "src"; "web"; "build"; ".github"; "docs" ] do
              let full = Repo.path dir
              if Directory.Exists full then
                  for f in Directory.GetFiles(full, "*", SearchOption.AllDirectories) do
                      let rel = Path.GetRelativePath(Repo.root (), f).Replace('\\', '/')
                      if not (rel.Contains "/bin/" || rel.Contains "/obj/") && FileInfo(f).Length < 2_000_000L then
                          yield rel, File.ReadAllText f ]

type EXTDATA004Tests(fixture: LiveFixture<EXTDATA004Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA004Tests>>

    /// No credential is committed, bundled, logged, put in a URL, error text, browser storage or an event file.
    [<Fact>]
    member _.``EXT-DATA-004-O1``() =
        Assert.Empty([ for path, text in Secrets.committedTexts () do if Secrets.tokenPattern.IsMatch text then yield path ])
        Assert.Equal("<credential>", string (Credential.create "ghp_not_a_real_token_aaaaaaaaaaaaaaaaaaaa"))
        Assert.DoesNotContain("localStorage", Repo.read "web/boundary.js")
        Assert.DoesNotContain("sessionStorage", Repo.read "web/boundary.js")
        let live = fixture.Run
        let token = match LiveEnv.token LiveEnv.Read with Ok t -> t | Error e -> failwith e
        let s, sent = AppDrive.connectThroughForm live.Execute live.ReadConnection token
        let viewText = sprintf "%A" (AppDrive.view s)
        Assert.DoesNotContain(token, viewText)
        Assert.All(sent, fun r -> Assert.DoesNotContain(token, r.Url))
        Assert.All(live.Log, fun o -> Assert.DoesNotContain(token, o.Url + o.Body))

    /// The credential is least privilege: it goes only to api.github.com, only for the connected repository.
    [<Fact>]
    member _.``EXT-DATA-004-O2``() =
        Assert.Equal("https://api.github.com", GitHubRequests.ApiBase)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let allowed = sprintf "%s/repos/%s/%s" GitHubRequests.ApiBase live.ReadConnection.Repository.Owner live.ReadConnection.Repository.Name
        Assert.All(run.Requests, fun r -> Assert.True(r.Url.StartsWith allowed || r.Url = GitHubRequests.GraphQlUrl, r.Url))

    /// An in-memory credential needs reauthorization after a reload: a new tab starts disconnected, with no context.
    [<Fact>]
    member _.``EXT-DATA-004-O3``() =
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.True(run.Model.Context.IsSome)
        let reloaded = AppDrive.fresh ()
        Assert.Equal(Disconnected, reloaded.Model.Connection)
        Assert.True(reloaded.Model.Context.IsNone)
        let s, _ = AppDrive.ui.Init InvestigationsRoute
        Assert.False(s.Local.Form.CredentialEntered)

    /// No unrestricted token by default: the app ships none, and write access comes only from GitHub's own answer.
    [<Fact>]
    member _.``EXT-DATA-004-O4``() =
        let s, _ = AppDrive.ui.Init InvestigationsRoute
        Assert.True(s.Local.Credential.IsNone)
        Assert.Equal(ConnectAccepted false, GitHubConnect.interpret (RequestId 1L) (Answered(RequestId 1L, 200, [], """{"permissions":{"push":false,"pull":true}}""")))
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        match run.Model.Connection with
        | Connected _ -> ()
        | other -> Assert.Fail(sprintf "the read token did not connect: %A %A" other run.Announcements)
