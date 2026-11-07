// Owner: integration (T20, reassigned). EXT-DATA-005: permissions, paths and manifests checked; bad data fails closed.
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

type EXTDATA005Tests(fixture: LiveFixture<EXTDATA005Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA005Tests>>

    /// The provider checks repository and branch access and validates paths and manifests before loading.
    [<Fact>]
    member _.``EXT-DATA-005-O1``() =
        Assert.False(GitHubRequests.isUnder (RepoPath "workspaces/demo/x") (RepoPath "workspaces/demo/x/../y/f.json"))
        Assert.True(GitHubRequests.isUnder (RepoPath "workspaces/demo/x") (RepoPath "workspaces/demo/x/investigations/a/manifest.json"))
        let noCases = System.Text.Json.JsonDocument.Parse("""{"catalogId":"x"}""").RootElement
        Assert.True(GitHubLister.catalogListing (WorkspaceAddress.catalog (Workspace "w")) (CommitOid "c") noCases |> Result.isError)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.True(run.Requests |> List.exists (fun r -> r.Url.Contains "/git/ref/heads/"), "the branch is read before anything else is listed")
        let listing = AppDrive.catalogListing run
        Assert.True(listing.IsSome)
        Assert.All(listing.Value.Investigations, fun i -> Assert.StartsWith("workspaces/", (let (RepoPath p) = i.Path in p)))

    /// Malformed, forged, unauthorized or incompatible data fails closed with a safe diagnostic.
    [<Fact>]
    member _.``EXT-DATA-005-O2``() =
        let ctx = { Connection = { Repository = { Owner = "o"; Name = "r"; Branch = "b" }; Credential = Credential.create "secret-token" }; NextId = RequestId 2L; MutationInFlightElsewhere = false; Now = DateTimeOffset.UtcNow }
        match GitHubLister.lister.InterpretList ctx ActiveCatalog ListHead (Answered(RequestId 1L, 200, [], "not json")) with
        | ListingFailed f -> Assert.DoesNotContain("secret-token", sprintf "%A" f)
        | other -> Assert.Fail(sprintf "a malformed head answer was accepted: %A" other)
        let live = fixture.Run
        let refused = AppDrive.connect live.Execute { live.ReadConnection with Credential = Credential.create "causa-ext-data-005-not-a-token" }
        Assert.Equal(CredentialRefused, refused.Model.Connection)
        Assert.All(refused.Announcements, fun a -> Assert.DoesNotContain("causa-ext-data-005-not-a-token", a))
