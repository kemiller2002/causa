// Owner: integration (T20, reassigned). EXT-DATA-008: a new investigation is command-created, schema-valid, replayable
// and round-trip verified, never copied from a demonstration.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA008Tests(fixture: LiveFixture<EXTDATA008Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA008Tests>>

    /// New causa-storage file sets come from the schema and the engine, not from a copied demonstration.
    [<Fact>]
    member _.``EXT-DATA-008-O1``() =
        let commands = AppDrive.startCommands "EXT-DATA-008 start" "Created from commands." "Why?"
        let investigation =
            commands
            |> List.fold (fun acc c -> acc |> Result.bind (fun st -> Update.apply AppDrive.backend st c |> Result.map Some)) (Ok None)
        match investigation with
        | Ok(Some i) ->
            match StorageCodecs.codec.EncodeNew i.Id i.Timeline with
            | Ok files ->
                let text = Text.Encoding.UTF8.GetString files.Events
                Assert.All(PinnedCatalog.cases, fun c -> Assert.DoesNotContain(c.Id.ToString "D", text))
            | Error f -> Assert.Fail(sprintf "EncodeNew refused a command-created history: %A" f)
        | other -> Assert.Fail(sprintf "the commands did not replay: %A" other)
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))

    /// The new investigation is created by commands, validated, saved, read back and replayed to the same state.
    [<Fact>]
    member _.``EXT-DATA-008-O2``() =
        let live = fixture.Run
        let runName = "ext-data-008-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let backend = AppDrive.persistent runName
        let engine = Update.engine backend
        let writable = (WorkspaceAddress.workingCopy [] runName |> Option.bind Writable.ofAddress).Value
        let commands = AppDrive.startCommands "EXT-DATA-008 round trip" "Saved and read back." "What failed?"
        let connected = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.WriteConnection.Repository, live.WriteConnection.Credential))) (AppDrive.fresh ())
        let created = AppDrive.sendWith engine live.Execute (Msg.CreateRequested(writable, commands.Head)) connected
        let withQuestion = commands.Tail |> List.fold (fun r c -> AppDrive.sendWith engine live.Execute (Msg.DomainCommand c) r) created
        let ref = { Address = Writable.address writable; Id = commands.Head.Investigation }
        let before = Update.investigationOf backend withQuestion.Model ref
        let saved = AppDrive.sendWith engine live.Execute (Msg.SessionInput(ref, SaveInput.SaveRequested)) withQuestion
        match saved.Model.Sessions[ref].State with
        | SaveState.Saved _ -> ()
        | other -> Assert.Fail(sprintf "the save did not confirm: %A %A" other saved.Announcements)
        // A fresh tab reads it back through the contract and replays it to the same state.
        let reopened = AppDrive.sendWith engine live.Execute (Msg.LoadStep(ref, LoadInput.OpenRequested ref)) (AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))) (AppDrive.fresh ()))
        match reopened.Model.Loads |> Map.tryFind ref, before with
        | Some(Loaded v), Ok(Some b) ->
            Assert.Equal(b.Version, v.Investigation.Version)
            Assert.Equal(NonEmptyText.value b.Title, NonEmptyText.value v.Investigation.Title)
            Assert.Equal(Some "What failed?", PrimaryQuestion.ofInvestigation v.Investigation)
        | other -> Assert.Fail(sprintf "the saved investigation did not read back: %A" (fst other))
