// Owner: integration (T20, reassigned). EXT-DATA-013: the active demonstration catalog: exactly the four cases
// catalog/active.json names, loaded through the contract and replay to their recorded states, never bundled.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA013Tests(fixture: LiveFixture<EXTDATA013Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA013Tests>>

    /// The active catalog is exactly the four investigations catalog/active.json names.
    [<Fact>]
    member _.``EXT-DATA-013-O1``() =
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let listing = (AppDrive.catalogListing run).Value
        Assert.Equal(PinnedCatalog.workspace, listing.Address.Workspace)
        Assert.Equal<Set<Guid>>(PinnedCatalog.cases |> List.map (fun c -> c.Id) |> Set.ofList, listing.Investigations |> List.map (fun i -> let (InvestigationId g) = i.Ref.Id in g) |> Set.ofList)

    /// After an explicit Connect all four load through the contract and F# replay to the states the manifest records;
    /// none of their records is bundled.
    [<Fact>]
    member _.``EXT-DATA-013-O2``() =
        Assert.Empty(AppDrive.bundledInvestigationContent ())
        let live = fixture.Run
        let engine = Update.engine (AppDrive.persistent "ext-data-013")
        let connected = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))) (AppDrive.fresh ())
        for c in PinnedCatalog.cases do
            let ref = PinnedCatalog.target c
            let opened = AppDrive.sendWith engine live.Execute (Msg.LoadStep(ref, LoadInput.OpenRequested ref)) connected
            match opened.Model.Loads |> Map.tryFind ref with
            | Some(Loaded v) ->
                let (AggregateVersion version) = v.Investigation.Version
                Assert.Equal(int64 c.ExpectedVersion, version)
            | other -> Assert.Fail(sprintf "%s did not load: %A" c.Key other)

    /// The synthetic v1.0.0 fixtures stay the storage contract's test corpus: none is listed as a catalog case.
    [<Fact>]
    member _.``EXT-DATA-013-O3``() =
        let fixtures = typeof<StatefulFake>.Assembly.GetName().Name
        Assert.DoesNotContain(fixtures, typeof<Model>.Assembly.GetReferencedAssemblies() |> Array.map (fun a -> string a.Name))
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.All((AppDrive.catalogListing run).Value.Investigations, fun i -> Assert.StartsWith("workspaces/demo/demonstration-v1/", (let (RepoPath p) = i.Path in p)))
