// Owner: integration (T20, reassigned). EXT-DATA-011: the causa-storage contract as a reader/writer specification,
// versioned and failing closed.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA011Tests(fixture: LiveFixture<EXTDATA011Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA011Tests>>

    /// The storage contract is implemented (codec), its demonstration records are not vendored.
    [<Fact>]
    member _.``EXT-DATA-011-O1``() =
        Assert.Empty(AppDrive.bundledInvestigationContent ())
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let listing = (AppDrive.catalogListing run).Value
        Assert.All(listing.Investigations, fun i -> Assert.True(i.ExpectedStateSha256.IsSome, "the catalog manifest's expected digest was not read"))

    /// The contract is versioned and incompatible data fails closed.
    [<Fact>]
    member _.``EXT-DATA-011-O2``() =
        Assert.True(DeployConnection.decode """{"schema":"causa.deploy-connection/2"}""" |> Result.isError)
        let future : FileSet = { Manifest = """{"formatVersion":"9.0.0","schemaVersion":"9.0.0"}"""B; Events = Array.empty; Snapshot = "{}"B }
        Assert.True(StorageCodecs.codec.Verify (WorkspaceAddress.catalog (Workspace "w")) (CommitOid "x") future |> Result.isError, "an unsupported version verified")
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
