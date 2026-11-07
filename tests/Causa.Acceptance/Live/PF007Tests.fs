// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-007 edge-case fixtures: each edge case exists and its expected outcome, fail-closed included, is explicit and met.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF007Tests(fixture: LiveFixture<PF007Tests>) =
    let str name (e: Json option) = e |> Json.field name |> Json.text
    let readJson (live: LiveRun) head path =
        PinnedCatalog.ok ("read " + path) (live.Reader.ReadFile head (RepoPath path)) |> Encoding.UTF8.GetString |> Json.parse

    /// The negative fixtures of the retained compatibility dataset, with their expected error codes.
    let negatives (live: LiveRun) head =
        readJson live head PinnedCatalog.compatibilityManifestPath
        |> Json.field "negativeFixtures"
        |> Json.items
        |> List.choose (fun n -> match str "path" (Some n), str "expectedErrorCode" (Some n) with Some p, Some c -> Some(p, c) | _ -> None)

    let workingCopy = (WorkspaceAddress.workingCopy [ PinnedCatalog.workspace ] "pf007-edge-cases").Value

    interface IClassFixture<LiveFixture<PF007Tests>>

    /// PF-007-O1: the edge cases are supplied: a completed and verified case (resolved), an unresolved case with
    /// contradicting evidence (active), invalid event history and invalid schema fixtures, a missing investigation and a
    /// missing or malformed catalog, each observable through the application.
    [<Fact>]
    member _.``PF-007-O1``() =
        let fake = StatefulFake()
        let missing = { Address = workingCopy; Id = Causa.Domain.InvestigationId(Guid "00000000-0000-4000-8000-000000000007") }
        match AppLoad.load fake.Execute fake.ReadConnection missing with
        | LoadFailed(ProviderFailed(NotFound _)) -> ()
        | other -> failwithf "a missing investigation loaded as %A" other
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let lifecycles = PinnedCatalog.cases |> List.map (fun c -> c.ExpectedLifecycle) |> set
        Assert.True(Set.isSubset (set [ "resolved"; "active" ]) lifecycles, sprintf "lifecycles: %A" lifecycles)
        let codes = negatives live head |> List.map snd |> set
        for code in [ "CAUSA.SEQUENCE_INVALID"; "CAUSA.HISTORY_INADMISSIBLE"; "CAUSA.SCHEMA_VIOLATION" ] do
            Assert.Contains(code, codes)
        let noCatalog = StatefulFake()
        let catalogHead = noCatalog.Head()
        Assert.Equal("NotFound", Conformance.kind ((Provider.over noCatalog.Execute noCatalog.ReadConnection).ReadFile catalogHead (RepoPath PinnedCatalog.activePath)))
        match AppList.list noCatalog.Execute noCatalog.ReadConnection ActiveCatalog with
        | Error _ -> ()
        | Ok l -> failwithf "a repository with no catalog listed %A" l
        let malformed = StatefulFake()
        malformed.CommitAsOtherWriter("malformed catalog", [ PinnedCatalog.activePath, Some(Encoding.UTF8.GetBytes "{\"activeCatalog\":") ]) |> ignore
        match AppList.list malformed.Execute malformed.ReadConnection ActiveCatalog with
        | Error _ -> ()
        | Ok l -> failwithf "a malformed catalog listed %A" l

    /// PF-007-O2: every negative fixture states its expected outcome, and the application meets it by failing closed:
    /// each one, read from real GitHub and served at its directory, is refused by SaveMachines.machine with exactly the
    /// expected CAUSA error code and nothing is loaded.
    [<Fact>]
    member _.``PF-007-O2``() =
        AppLoad.requireCodec ()
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let all = negatives live head
        Assert.NotEmpty(all)
        let mismatches =
            [ for (path, expected) in all do
                let doc = readJson live head ("fixtures/v1.0.0/" + path)
                let dir = str "directoryId" doc |> Option.defaultWith (fun () -> failwithf "%s names no directoryId" path)
                let id = Causa.Domain.InvestigationId(Guid dir)
                let (RepoPath prefix) = Writes.prefixOf workingCopy id
                let baseDir = "fixtures/v1.0.0/investigations/" + dir
                let files =
                    match doc |> Json.field "files" with
                    | Some(Json.Object fs) -> fs
                    | _ -> []
                let content name =
                    match files |> List.tryFind (fun (k, _) -> k = name) with
                    | Some(_, Json.Null) -> None
                    | Some(_, f) -> str "base64" (Some f) |> Option.map Convert.FromBase64String
                    | None -> live.Reader.ReadFile head (RepoPath(baseDir + "/" + name)) |> Result.toOption
                let names = (Writes.fileNames @ (files |> List.map fst)) |> List.distinct
                let fake = StatefulFake()
                fake.CommitAsOtherWriter("negative " + path, [ for n in names do match content n with Some b -> prefix + "/" + n, Some b | None -> () ]) |> ignore
                match AppLoad.load fake.Execute fake.ReadConnection { Address = workingCopy; Id = id } with
                | LoadFailed(Unverifiable failures) when failures |> List.exists (function ReaderStage(c, _, _, _) -> c = expected | _ -> false) -> ()
                | other -> yield path, expected, sprintf "%A" other ]
        Assert.True(List.isEmpty mismatches, sprintf "negative fixtures not refused with their expected code: %A" mismatches)
