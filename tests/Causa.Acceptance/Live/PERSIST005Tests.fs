// Owner: persistence. PERSIST-005 (live): GitHub is the authoritative durable store. Also the shared live scenario
// helpers of the persistence live tests (SU-DOTNET-LIVE: CAUSA_WRITE_TOKEN/CAUSA_GITHUB_TOKEN on a fresh branch).
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module LiveScenarios =
    let stateName (s: InvestigationSession) = SaveSession.stateName s.State
    let step (r: ProviderRig) (s: InvestigationSession) (input: SaveInput) = r.Step s input
    let stepRaw (r: ProviderRig) (s: InvestigationSession) (input: SaveInput) = r.StepRaw s input
    let settle (r: ProviderRig) (s: InvestigationSession) (effects: SaveEffect list) = r.Settle s effects
    let observe (r: ProviderRig) (s: InvestigationSession) (text: string) = r.Observe s text
    let create (r: ProviderRig) (title: string) (problem: string) = r.Create title problem

    /// A new investigation saved on the live branch, and the rig resting on what GitHub holds.
    let saved (rig: ProviderRig) (title: string) (problem: string) =
        let s = rig.Step (rig.Create title problem) SaveInput.SaveRequested
        if stateName s <> "Saved" then failwithf "the live save did not confirm: %A" s.State
        rig.Rebase s |> ignore
        s

    let target (rig: ProviderRig) (s: InvestigationSession) : InvestigationRef = { Address = rig.Address; Id = s.Id }

    /// A fresh tab: a new rig on the same branch, sharing nothing in memory.
    let freshTab (live: LiveRun) (rig: ProviderRig) = ProviderRig(live.Execute, live.WriteConnection, (Writable.address rig.Writable).Workspace |> fun (Workspace w) -> w.Substring("workspaces/demo/causa-app-".Length))

    let compareFiles (live: LiveRun) (baseOid: CommitOid) (head: CommitOid) =
        let request = GitHubRequests.compare (RequestId 9999L) live.ReadConnection baseOid head 1
        match Classify.classify (Operation.Compare baseOid) (live.Execute request) with
        | Ok answer -> Writes.changedFiles answer, (Decode.compare answer |> Result.map (fun p -> p.Commits |> List.map (fun c -> c.Oid)) |> Result.defaultValue [])
        | Error e -> failwithf "compare failed: %A" e

    let head (live: LiveRun) = live.Reader.ReadHead() |> Result.defaultWith (failwithf "head read failed: %A")

type PERSIST005Tests(fixture: LiveFixture<PERSIST005Tests>) =
    interface IClassFixture<LiveFixture<PERSIST005Tests>>

    /// O1: the branch is the only durable store: what a fresh tab knows comes only from GitHub.
    [<Fact>]
    member _.``PERSIST-005-O1``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p005o1")
        let s = LiveScenarios.saved rig "PERSIST-005-O1" "Durable only on GitHub."
        let tab = LiveScenarios.freshTab live rig
        match tab.Load(LiveScenarios.target rig s) with
        | Ok v -> Assert.Equal("PERSIST-005-O1", v.Investigation.Title.Value)
        | Error f -> failwithf "%A" f

    /// O2: opened by reading at a branch head and verifying as a reader would.
    [<Fact>]
    member _.``PERSIST-005-O2``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p005o2")
        let s = LiveScenarios.saved rig "PERSIST-005-O2" "Read at a head and verified."
        let v = (LiveScenarios.freshTab live rig).Load(LiveScenarios.target rig s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(LiveScenarios.head live, v.ReadAt)
        Assert.True(Result.isOk (StorageCodecs.codec.Verify v.Address v.ReadAt v.Bytes))

    /// O3: the tab records the commit it read as the base head.
    [<Fact>]
    member _.``PERSIST-005-O3``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p005o3")
        let s = LiveScenarios.saved rig "PERSIST-005-O3" "Base head recorded."
        let v = rig.Load(LiveScenarios.target rig s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(v.ReadAt, (rig.Machine.Initial rig.Writable v).BaseHead)

    /// O4: the head read of a load cannot come from an HTTP cache (one GraphQL POST), and file reads name a commit.
    [<Fact>]
    member _.``PERSIST-005-O4``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p005o4")
        let s = LiveScenarios.saved rig "PERSIST-005-O4" "No cached head."
        let before = rig.Recorder.Requests.Length
        rig.Load(LiveScenarios.target rig s) |> ignore
        let loadRequests = rig.Recorder.Requests |> List.skip before
        Assert.True(loadRequests |> List.forall (fun r -> r.Method = Post && r.Url = GitHubRequests.GraphQlUrl))
        for r in rig.Recorder.Requests |> List.filter (fun r -> r.Url.Contains "/contents/") do
            Assert.Matches("[?&]ref=[0-9a-f]{40}", r.Url)

    /// O5 (PAT-10): an investigation opened in a new tab equals its last confirmed commit.
    [<Fact>]
    member _.``PERSIST-005-O5``() =
        let live = fixture.Run
        let rig = LiveSaveRig.Create(live, "p005o5")
        let s = LiveScenarios.saved rig "PERSIST-005-O5" "Equal to the last confirmed commit."
        let s2 = rig.Step (LiveScenarios.observe rig s "A second confirmed observation.") SaveInput.SaveRequested
        Assert.Equal("Saved", LiveScenarios.stateName s2)
        let v = (LiveScenarios.freshTab live rig).Load(LiveScenarios.target rig s) |> Result.defaultWith (failwithf "%A")
        Assert.Equal(s2.BaseHead, v.ReadAt)
        Assert.True(v.Investigation.Observations |> Map.exists (fun _ o -> o.Original.Value = "A second confirmed observation."))
