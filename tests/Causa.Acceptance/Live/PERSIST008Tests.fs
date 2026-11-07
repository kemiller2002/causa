// Owner: persistence. T18: PERSIST-008 detect a stale head, against the stateful fake and the live test repository.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PERSIST008Tests(fixture: LiveFixture<PERSIST008Tests>) =

    let prefix = RepoPath "workspaces/demo/causa-app-persist008/investigations/00000000-0000-0000-0000-000000000008"
    let file (RepoPath p) name = RepoPath(p + "/" + name)
    let put p name (text: string) = PutFile(file p name, Encoding.UTF8.GetBytes text)

    /// A moved head through one port: another commit lands, then a commit conditional on the old head is refused as a
    /// stale head, nothing is written and the head stays the other writer's.
    let movedHead (writer: ProviderPort) (reader: ProviderPort) (p: RepoPath) (created: CommitOid -> unit) =
        let h0 = reader.ReadHead() |> Result.defaultWith (fun e -> failwithf "head read failed: %A" e)
        let other = writer.Commit h0 p "causa PERSIST-008: other writer" [ put p "other.json" "{\"other\":1}" ] |> Result.defaultWith (fun e -> failwithf "other writer failed: %A" e)
        created other.Oid
        let refused = writer.Commit h0 p "causa PERSIST-008: stale" [ put p "mine.json" "{\"mine\":1}" ]
        refused |> Result.iter (fun c -> created c.Oid)
        let h1 = reader.ReadHead() |> Result.defaultWith (fun e -> failwithf "head read failed: %A" e)
        refused, h0, other.Oid, h1, reader.ReadFile h1 (file p "mine.json")

    let assertStale (refused: Result<CreatedCommit, ProviderError>, h0: CommitOid, other: CommitOid, h1: CommitOid, mine: Result<byte[], ProviderError>) =
        match refused with
        | Error(StaleHead(expected, _)) -> Assert.Equal(h0, expected)
        | r -> failwithf "a moved head was classified as %A" r
        Assert.Equal(other, h1)
        Assert.Equal("NotFound", Conformance.kind mine)

    interface IClassFixture<LiveFixture<PERSIST008Tests>>

    /// PERSIST-008-O1: a mutation refused because the head moved is a stale head; nothing was written. Both recorded
    /// forms on the fake (STALE_DATA and FORBIDDEN 'is at <oid> but expected <oid>'), then real GitHub.
    [<Fact>]
    member _.``PERSIST-008-O1``() =
        for form in [ StaleData; ForbiddenMovedHead ] do
            let fake = StatefulFake()
            fake.UseStaleForm form
            assertStale (movedHead (Provider.over fake.Execute fake.WriteConnection) (Provider.over fake.Execute fake.ReadConnection) prefix ignore)
        let live = fixture.Run
        assertStale (movedHead live.Writer live.Reader live.Prefix live.Created)

    /// PERSIST-008-O2: each answer is classified from its typed content into distinct causes: every recorded form
    /// lands in its matrix class, and the live answers for a bad credential, a read-only credential, a moved head and
    /// an invalid change are four different causes.
    [<Fact>]
    member _.``PERSIST-008-O2``() =
        for r in Recordings.all do
            let cls, _ = Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 1L) r)
            Assert.True((cls = r.ExpectedClass), sprintf "%s classified as %s, expected %s" r.Form (MatrixClass.id cls) (MatrixClass.id r.ExpectedClass))
        let live = fixture.Run
        let adapter = Conformance.overLive live
        let probes = Conformance.run adapter live.Created
        let byName n = (probes |> List.find (fun p -> p.Name = n)).Actual
        let causes =
            [ byName "read head with a bad credential"; byName "commit with the read credential"; byName "commit at a moved head"; byName "delete a file that does not exist" ]
        Assert.Equal<string list>([ "AuthenticationFailed"; "PermissionDenied"; "StaleHead"; "ValidationFailed" ], causes)
        Assert.Equal(4, causes |> List.distinct |> List.length)

    /// PERSIST-008-O3: a network failure is never a stale head or a conflict: lost before applying, lost after
    /// applying, failed before sending and timed out are network causes (a lost mutation answer is an unknown
    /// outcome), on the fake and on the real network (a request that times out against api.github.com).
    [<Fact>]
    member _.``PERSIST-008-O3``() =
        for fault in [ LoseBeforeApplying; LoseAfterApplying; FailBeforeSending; TimeOutAfterApplying ] do
            let fake = StatefulFake()
            fake.Inject fault
            let writer = Provider.over fake.Execute fake.WriteConnection
            match writer.Commit (fake.Head()) prefix "lost" [ put prefix "a.json" "{}" ] with
            | Error(NetworkUnavailable f as e) ->
                Assert.Equal((fault = FailBeforeSending), (f = TransportFailure.FailedBeforeSending))
                Assert.Equal((fault <> FailBeforeSending), Classify.isUnknownMutationOutcome e)
            | other -> failwithf "%A was classified as %A" fault other
        let live = fixture.Run
        let impatient = LiveHttp.executor (TimeSpan.FromMilliseconds 1.0) live.Log
        let port = Provider.over impatient live.WriteConnection
        let head = live.Reader.ReadHead() |> Result.defaultWith (fun e -> failwithf "%A" e)
        match port.Commit head live.Prefix "causa PERSIST-008-O3: impatient" [ put live.Prefix "impatient.json" "{}" ] with
        | Error(NetworkUnavailable _) -> ()
        | Ok c ->
            live.Created c.Oid
            failwith "a 1 ms request was answered; the network failure path was not exercised"
        | other -> failwithf "a network failure was classified as %A" other
        match port.ReadHead() with
        | Error(NetworkUnavailable _) -> ()
        | other -> failwithf "a timed-out read was classified as %A" other

    /// PERSIST-008-O4 (PAT-03): every classification of the probe script holds with the stateful fake and against the
    /// live test repository, and the two agree probe by probe.
    [<Fact>]
    member _.``PERSIST-008-O4``() =
        let fakeRun = Conformance.run (Conformance.overFake (StatefulFake()) prefix) ignore
        Assert.Empty(Conformance.failures fakeRun)
        let live = fixture.Run
        let liveRun = Conformance.run (Conformance.overLive live) live.Created
        Assert.Empty(Conformance.failures liveRun)
        Assert.Empty(Conformance.divergence fakeRun liveRun)
