// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-004 contract fidelity: fixture data follows the frozen storage contract, is described by a machine-readable
// manifest and is never mutated.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF004Tests(fixture: LiveFixture<PF004Tests>) =
    let json (bytes: byte[]) = Json.parse (Encoding.UTF8.GetString bytes)

    interface IClassFixture<LiveFixture<PF004Tests>>

    /// PF-004-O1: fixture data follows the frozen storage contract and loads through the same path as real data: each
    /// pinned case read from real GitHub has the events digest the catalog records; the same bytes served by the
    /// stateful fake and by GitHub both verify through SaveMachines.machine to the catalog's expected version.
    [<Fact>]
    member _.``PF-004-O1``() =
        AppLoad.requireCodec ()
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        for case in PinnedCatalog.cases do
            let files = PinnedCatalog.ok ("read " + case.Key) (PinnedCatalog.readFiles live.Reader head case)
            Assert.Equal(case.EventsSha256, PinnedCatalog.sha256 files.Events)
            let fake = StatefulFake()
            PinnedCatalog.seed fake case files |> ignore
            let viaFixture = AppLoad.load fake.Execute fake.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified (case.Key + " via fixture")
            let viaGitHub = AppLoad.load live.Execute live.ReadConnection (PinnedCatalog.target case) |> AppLoad.requireVerified (case.Key + " via GitHub")
            Assert.Equal(viaGitHub.Investigation, viaFixture.Investigation)
            Assert.Equal(Causa.Domain.AggregateVersion(int64 case.ExpectedVersion), viaGitHub.Investigation.Version)

    /// PF-004-O2: a machine-readable fixture manifest names every fixture's id, schema version, expected version and
    /// digests, and every negative fixture's expected error outcome: the active catalog manifest and the retained
    /// compatibility manifest, read from real GitHub, carry them and agree with the pinned catalog.
    [<Fact>]
    member _.``PF-004-O2``() =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let read path = PinnedCatalog.ok ("read " + path) (live.Reader.ReadFile head (RepoPath path)) |> json
        let active = read PinnedCatalog.activePath
        Assert.Equal(Some PinnedCatalog.catalogId, active |> Json.field "catalogId" |> Json.text)
        Assert.Equal(Some PinnedCatalog.catalogVersion, active |> Json.field "catalogVersion" |> Json.text)
        Assert.Equal(Some PinnedCatalog.manifestPath, active |> Json.field "manifest" |> Json.text)
        let manifest = read PinnedCatalog.manifestPath
        Assert.True((manifest |> Json.field "formatVersion" |> Json.text).IsSome, "the catalog manifest names no schema version")
        let listed = manifest |> Json.field "cases" |> Json.items |> List.map Some
        Assert.Equal(PinnedCatalog.cases.Length, listed.Length)
        for case in PinnedCatalog.cases do
            let entry = listed |> List.find (fun e -> e |> Json.field "investigationId" |> Json.text = Some(case.Id.ToString()))
            Assert.Equal(Some case.EventsSha256, entry |> Json.field "eventsSha256" |> Json.text)
            Assert.Equal(Some case.ExpectedVersion, entry |> Json.field "expectedVersion" |> Json.int)
            Assert.Equal(Some case.ExpectedStateSha256, entry |> Json.field "expectedStateSha256" |> Json.text)
            for digest in [ "manifestSha256"; "snapshotSha256" ] do
                Assert.True((entry |> Json.field digest |> Json.text).IsSome, sprintf "%s has no %s" case.Key digest)
        let compatibility = read PinnedCatalog.compatibilityManifestPath
        let negatives = compatibility |> Json.field "negativeFixtures" |> Json.items |> List.map Some
        Assert.NotEmpty(negatives)
        for n in negatives do
            Assert.True((n |> Json.field "path" |> Json.text).IsSome, "a negative fixture has no path")
            Assert.StartsWith("CAUSA.", n |> Json.field "expectedErrorCode" |> Json.text |> Option.defaultValue "")

    /// PF-004-O3: the frozen historical dataset is never mutated: no catalog address is writable, a change to a catalog
    /// file is refused before sending, and after the run writes its own working copy every pinned case's bytes on real
    /// GitHub are unchanged.
    [<Fact>]
    member _.``PF-004-O3``() =
        Assert.Equal(None, Writable.ofAddress PinnedCatalog.address)
        Assert.Equal(None, WorkspaceAddress.workingCopy [ PinnedCatalog.workspace ] "../demonstration-v1")
        let live = fixture.Run
        let before = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let case = PinnedCatalog.cases.Head
        let original = PinnedCatalog.ok "read case" (PinnedCatalog.readFiles live.Reader before case)
        let (RepoPath events) = PinnedCatalog.filePath case "events.ndjson"
        match live.Writer.Commit before live.Prefix "causa PF-004-O3: catalog write" [ PutFile(RepoPath events, [| 1uy |]) ] with
        | Error(ValidationFailed _) -> ()
        | Ok c ->
            live.Created c.Oid
            failwith "a write to a catalog file was sent and accepted"
        | other -> failwithf "a write to a catalog file was not refused before sending: %A" other
        let mine = RepoPath((let (RepoPath p) = live.Prefix in p) + "/pf004.json")
        let created = PinnedCatalog.ok "working-copy commit" (live.Writer.Commit before live.Prefix "causa PF-004-O3: working copy" [ PutFile(mine, Encoding.UTF8.GetBytes "{}") ])
        live.Created created.Oid
        let after = PinnedCatalog.ok "read case after" (PinnedCatalog.readFiles live.Reader created.Oid case)
        Assert.Equal<byte[]>(original.Events, after.Events)
        Assert.Equal<byte[]>(original.Manifest, after.Manifest)
        Assert.Equal<byte[]>(original.Snapshot, after.Snapshot)
