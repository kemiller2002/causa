// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-009 concurrency fixtures: delayed, reordered, superseded, cancelled and retried requests, scheduled
// deterministically, with request identity, provider, credential generation and freshness honoured.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF009Tests(fixture: LiveFixture<PF009Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf009/investigations/00000000-0000-0000-0000-000000000009"
    let file name = let (RepoPath p) = prefix in RepoPath(p + "/" + name)
    let address = (WorkspaceAddress.workingCopy [] "pf009").Value
    let target = { Address = address; Id = Causa.Domain.InvestigationId(Guid "00000000-0000-4000-8000-000000000009") }
    let m = SaveMachines.machine

    /// One SM-LOAD schedule: request 1 is sent, then superseded by request 2 (a refresh after a failure); request 2's
    /// answer arrives first (reordered), then request 1's stale answer (delayed), then a cancelled request 3 that
    /// times out. Returns every state the machine passes through.
    let schedule (execute: Execute) (connection: ConnectionContext) =
        let ctx n = { AppLoad.context connection with NextId = RequestId n; Now = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero) }
        let ok r = match r with Ok(s, effects) -> s, effects | Error f -> failwithf "SM-LOAD refused: %A" f
        let sent effects = effects |> List.pick (function Send r -> Some r | _ -> None)
        let s1, e1 = ok (m.StepLoad (ctx 1L) NotLoaded (LoadInput.OpenRequested target))
        let first = execute (sent e1)
        let s1f, _ = ok (m.StepLoad (ctx 1L) s1 (LoadInput.LoadRejected(ProviderFailed(NetworkUnavailable TransportFailure.TimedOut))))
        let s2, e2 = ok (m.StepLoad (ctx 2L) s1f (LoadInput.OpenRequested target))
        let second = execute (sent e2)
        let interpret n s outcome =
            match m.InterpretLoad (ctx n) s outcome with
            | LoadEvent input ->
                // An illegal (state, input) pair fails closed: the state is unchanged (SM-LOAD).
                match m.StepLoad (ctx n) s input with
                | Ok(next, _) -> next
                | Error _ -> s
            | LoadNextRequest(next, _) -> next
        let afterStale = interpret 3L s2 first
        let afterSecond = interpret 3L afterStale second
        let afterLate = interpret 3L afterSecond first
        let cancelled = interpret 3L (Loading(RequestId 3L, LoadHead)) (NotAnswered(RequestId 3L, TransportFailure.TimedOut))
        [ s1; s1f; s2; afterStale; afterSecond; afterLate; cancelled ]

    interface IClassFixture<LiveFixture<PF009Tests>>

    /// PF-009-O1: delayed, reordered, superseded, cancelled and retried requests are scheduled deterministically: the
    /// same schedule over the fake gives the same states every time, a stale answer leaves the newer request waiting,
    /// a cancelled request fails as a network cause, and a lost commit retried at the same head is written once; the
    /// same schedule over real GitHub passes through the same machine states.
    [<Fact>]
    member _.``PF-009-O1``() =
        let run () =
            let fake = StatefulFake()
            schedule fake.Execute fake.ReadConnection
        let states = run ()
        Assert.Equal<LoadState list>(states, run ())
        Assert.Equal(Loading(RequestId 2L, LoadHead), states[3])
        Assert.Equal(LoadFailed(ProviderFailed(NotFound "branch")) |> LoadMachine.stateName, LoadMachine.stateName states[4])
        Assert.Equal(states[4], states[5])
        Assert.Equal(LoadFailed(ProviderFailed(NetworkUnavailable TransportFailure.TimedOut)), states[6])
        let fake = StatefulFake()
        let w = Provider.over fake.Execute fake.WriteConnection
        let h0 = fake.Head()
        fake.Inject LoseBeforeApplying
        Assert.Equal("NetworkUnavailable.LostAfterSending", Conformance.kind (w.Commit h0 prefix "a" [ PutFile(file "a.json", [| 1uy |]) ]))
        Assert.Equal("ok", Conformance.kind (w.Commit h0 prefix "a" [ PutFile(file "a.json", [| 1uy |]) ]))
        Assert.Equal(Some h0, fake.Commit(fake.Head()) |> Option.bind (fun c -> c.Parent) |> Option.map CommitOid)
        let live = fixture.Run
        let real = schedule live.Execute live.ReadConnection
        Assert.Equal<string list>(states |> List.map LoadMachine.stateName, real |> List.map LoadMachine.stateName)

    /// PF-009-O2: a stale response never overwrites a newer request's state, and a cached success never masks a new
    /// authentication failure: on the fake and on real GitHub an earlier success does not stop a revoked or refused
    /// credential from being reported as AuthenticationFailed, and an answer naming another request is refused.
    [<Fact>]
    member _.``PF-009-O2``() =
        let fake = StatefulFake()
        let w = Provider.over fake.Execute fake.WriteConnection
        Assert.Equal("ok", Conformance.kind (w.ReadHead()))
        fake.RevokeToken fake.WriteToken
        Assert.Equal("AuthenticationFailed", Conformance.kind (w.ReadHead()))
        let otherRequest : Execute = fun r -> fake.Execute { r with Id = RequestId 999L }
        Assert.Equal("Malformed", Conformance.kind ((Provider.over otherRequest fake.ReadConnection).ReadHead()))
        let newer = Loaded { Investigation = TestValues.investigation target.Id (Causa.Domain.AggregateVersion 2L); History = []; ReadAt = CommitOid "b"; Address = address; Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }
        Assert.Equal(newer, fst (m.StepLoad (AppLoad.context fake.ReadConnection) newer (LoadInput.SupersededReply(RequestId 1L)) |> Result.defaultWith (fun f -> failwithf "%A" f)))
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
        let refused = Provider.over live.Execute { live.ReadConnection with Credential = Credential.create "causa-pf009-not-a-token" }
        Assert.Equal("AuthenticationFailed", Conformance.kind (refused.ReadHead()))
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))

    /// PF-009-O3: request identity, active provider, credential generation and freshness are honoured: every live
    /// outcome names the request it answers, every port names the repository it reads, a new credential generation is
    /// used from its first request, and each read returns the head as it is now, not as it was.
    [<Fact>]
    member _.``PF-009-O3``() =
        let fake = StatefulFake()
        let before = Provider.over fake.Execute fake.ReadConnection
        let h0 = before.ReadHead() |> PinnedCatalog.ok "fake head"
        let moved = fake.CommitAsOtherWriter("other writer", [ "workspaces/demo/causa-app-pf009/x.json", Some [| 1uy |] ])
        Assert.Equal(Ok moved, before.ReadHead())
        Assert.NotEqual(h0, moved)
        let live = fixture.Run
        for n in [ 77L; 78L ] do
            match live.Execute(GitHubRequests.readHead (RequestId n) live.ReadConnection) with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> Assert.Equal(RequestId n, id)
        Assert.Equal(live.ReadConnection.Repository, live.Reader.Repository)
        Assert.Equal(live.Branch, live.Reader.Repository.Branch)
        let regenerated = Provider.over live.Execute { live.ReadConnection with Credential = Credential.create "causa-pf009-new-generation" }
        Assert.Equal("AuthenticationFailed", Conformance.kind (regenerated.ReadHead()))
        let h1 = live.Reader.ReadHead() |> PinnedCatalog.ok "live head"
        let c = live.Writer.Commit h1 live.Prefix "causa PF-009-O3: freshness" [ PutFile(RepoPath((let (RepoPath p) = live.Prefix in p) + "/fresh.json"), [| 1uy |]) ] |> PinnedCatalog.ok "live commit"
        live.Created c.Oid
        Assert.Equal(Ok c.Oid, live.Reader.ReadHead())
