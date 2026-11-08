// Owner: integration (T20, reassigned). EXT-DATA-007: the connection and data states are distinct and honest.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts
open Causa.TestSupport

type EXTDATA007Tests(fixture: LiveFixture<EXTDATA007Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA007Tests>>

    /// Disconnected, connecting, authorized-empty, loading, incompatible, access-denied, offline and conflict are
    /// distinct states with their own words.
    [<Fact>]
    member _.``EXT-DATA-007-O1``() =
        let words =
            [ Disconnected; Connecting(RequestId 1L); Connected true; Connected false; CredentialRefused; Reauthenticating ]
            |> List.map Connection.describe
        Assert.Equal(words.Length, (List.distinct words).Length)
        let failures =
            [ UiFailure.InvalidData "x"; UiFailure.NotAuthorized; UiFailure.CredentialRefused; UiFailure.ProviderUnreachable ]
            |> List.map (fun f -> (PresentationLanguage.failure f).What)
        Assert.Equal(failures.Length, (List.distinct failures).Length)
        let kinds = [ SavedKind; UnsavedKind; SavingKind; OfflineKind; ConflictKind; UnknownOutcomeKind; SaveFailedKind ] |> List.map PresentationLanguage.saveState
        Assert.Equal(7, (List.distinct kinds).Length)
        let live = fixture.Run
        let connecting = AppDrive.engine.Update (Msg.Connection(ConnectionInput.ConnectSubmitted(live.ReadConnection.Repository, live.ReadConnection.Credential))) (AppDrive.fresh ()).Model |> fst
        Assert.True((match connecting.Connection with Connecting _ -> true | _ -> false))
        let run = AppDrive.connect live.Execute live.ReadConnection
        Assert.True((match run.Model.Connection with Connected _ -> true | _ -> false))

    /// A supplied token never implies a connection: only GitHub's answer does.
    [<Fact>]
    member _.``EXT-DATA-007-O2``() =
        let s, sent =
            AppDrive.connectThroughForm (fun r -> NotAnswered(r.Id, TransportFailure.FailedBeforeSending)) { Repository = { Owner = "o"; Name = "r"; Branch = "b" }; Credential = Credential.create "t" } "t"
        Assert.NotEmpty(sent)
        Assert.False((match s.Model.Connection with Connected _ -> true | _ -> false), "a token with no answer from GitHub showed as connected")
        let live = fixture.Run
        let refused = AppDrive.connect live.Execute { live.ReadConnection with Credential = Credential.create "causa-ext-data-007-not-a-token" }
        Assert.Equal(CredentialRefused, refused.Model.Connection)
