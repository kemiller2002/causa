// Owner: integration (T20, reassigned). EXT-DATA-010: R001 stays a baseline; startup, authorization, read, write and
// recovery are measured separately; no provider PASS from mocked requests.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA010Tests(fixture: LiveFixture<EXTDATA010Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA010Tests>>

    /// R001 remains an immutable baseline: nothing of it is in this application.
    [<Fact>]
    member _.``EXT-DATA-010-O1``() =
        Assert.Empty(AppDrive.bundledInvestigationContent ())
        Assert.DoesNotContain("R001", Repo.read "build.fsx")
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))

    /// Startup, authorization, read, write and recovery are measured independently and recorded.
    [<Fact>]
    member _.``EXT-DATA-010-O2``() =
        let time f =
            let w = Diagnostics.Stopwatch.StartNew()
            let r = f ()
            w.Stop()
            r, w.Elapsed.TotalMilliseconds
        let _, startup = time (fun () -> AppDrive.ui.Init InvestigationsRoute)
        let live = fixture.Run
        let authorized, authorization = time (fun () -> AppDrive.fresh () |> AppDrive.send live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))))
        let _, read = time (fun () -> AppDrive.send live.Execute (Msg.ListRequested ActiveCatalog) authorized)
        let runName = "ext-data-010-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let engine = Update.engine (AppDrive.persistent runName)
        let writable = (WorkspaceAddress.workingCopy [] runName |> Option.bind Writable.ofAddress).Value
        let commands = AppDrive.startCommands "EXT-DATA-010" "Measured." ""
        let ref = { Address = Writable.address writable; Id = commands.Head.Investigation }
        let connected = AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.WriteConnection.Repository, live.WriteConnection.Credential))) (AppDrive.fresh ())
        let created = AppDrive.sendWith engine live.Execute (Msg.CreateRequested(writable, commands.Head)) connected
        let saved, write = time (fun () -> AppDrive.sendWith engine live.Execute (Msg.SessionInput(ref, SaveInput.SaveRequested)) created)
        let _, recovery = time (fun () -> AppDrive.sendWith engine live.Execute (Msg.LoadStep(ref, LoadInput.OpenRequested ref)) (AppDrive.sendWith engine live.Execute (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))) (AppDrive.fresh ())))
        Assert.True((match saved.Model.Sessions[ref].State with SaveState.Saved _ -> true | _ -> false))
        let dir = Path.GetDirectoryName(live.WriteEvidence()) |> Option.ofObj |> Option.defaultValue "."
        let measures = [ "startup", startup; "authorization", authorization; "read", read; "write", write; "recovery", recovery ]
        File.WriteAllText(Path.Combine(dir, "EXTDATA010-measures.json"), "{" + String.Join(",", measures |> List.map (fun (k, v) -> sprintf "\"%s\":%.1f" k v)) + "}")
        Assert.All(measures, fun (_, v) -> Assert.True(v >= 0.0))

    /// No external-provider PASS on mocked requests alone: the live answers came from api.github.com.
    [<Fact>]
    member _.``EXT-DATA-010-O3``() =
        let live = fixture.Run
        AppDrive.connect live.Execute live.ReadConnection |> ignore
        Assert.Contains(live.Log, fun o -> o.Url.StartsWith(GitHubRequests.ApiBase + "/") && o.Status = Some 200)
        Assert.All(live.Log, fun o -> Assert.True(LiveHttp.allowedHost o.Url || o.Status.IsNone, o.Url))
