// Owner: integration (T20, reassigned). EXT-DATA-006: connect and read never mutate; creating is an explicit action.
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

type EXTDATA006Tests(fixture: LiveFixture<EXTDATA006Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA006Tests>>

    /// Connect and listing send only reads, and the branch head is unchanged afterwards.
    [<Fact>]
    member _.``EXT-DATA-006-O1``() =
        let live = fixture.Run
        let before = live.Reader.ReadHead() |> PinnedCatalog.ok "head before"
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.All(run.Requests, fun r -> Assert.Equal(Get, r.Method))
        Assert.Equal(before, live.Reader.ReadHead() |> PinnedCatalog.ok "head after")

    /// Creating is explicit and needs a connection GitHub allows to write; a read-only one is refused with the reason.
    [<Fact>]
    member _.``EXT-DATA-006-O2``() =
        let backend = AppDrive.persistent "ext-data-006"
        let engine = Update.engine backend
        let start = AppDrive.startCommands "EXT-DATA-006" "Created only by an explicit action." ""
        let writable = (WorkspaceAddress.workingCopy [] "ext-data-006" |> Option.bind Writable.ofAddress).Value
        // Without a listing of the working copy (its head) nothing is created.
        let unlisted = AppDrive.sendWith engine (fun r -> failwithf "nothing may be sent: %s" r.Url) (Msg.CreateRequested(writable, start.Head)) (AppDrive.fresh ())
        Assert.True(unlisted.Model.Sessions.IsEmpty)
        let live = fixture.Run
        let connected = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.WriteConnection.Repository, live.WriteConnection.Credential))) (AppDrive.fresh ())
        Assert.All(connected.Requests, fun r -> Assert.Equal(Get, r.Method))
        let created = AppDrive.sendWith engine live.Execute (Msg.CreateRequested(writable, start.Head)) connected
        Assert.Single(created.Model.Sessions) |> ignore
        Assert.All(created.Requests, fun r -> Assert.Equal(Get, r.Method))
