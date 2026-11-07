// Owner: integration (T20, reassigned from ui/persistence by the coordinator). Connecting to the data repository:
// the deploy connection the deployed test build injects (EXT-DATA-004-O5, ODR-COMP-P13 A), and the GitHub workspace
// lister (IF41-SAVE WorkspaceLister, v4.1.5 L1) over the T18 provider requests and classifier. Pure: requests are data,
// answers are classified by Classify (IF41-ERRORS); nothing here performs I/O.
namespace Causa.App

open System
open System.Text
open System.Text.Json
open Causa.Domain
open Causa.Persistence

/// The connection the deployed test build carries in `deploy-connection.json` (TEST-DEPLOY-KEY section 2.4).
type DeployConnection = { Repository: Repository; Credential: Credential.T }

[<RequireQualifiedAccess>]
module DeployConnection =
    [<Literal>]
    let Schema = "causa.deploy-connection/1"

    /// The file the app reads after its build record: same origin, no credential sent.
    [<Literal>]
    let FileName = "deploy-connection.json"

    let private str (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
            | _ -> None
        | _ -> None

    /// Decodes the file. Ok None: the file holds no connection (every build without the deploy secrets). Errors never
    /// carry a value from the file, only what is wrong with it.
    let decode (body: string) : Result<DeployConnection option, string> =
        try
            use doc = JsonDocument.Parse body
            let root = doc.RootElement
            if str "schema" root <> Some Schema then Error(sprintf "%s does not declare schema %s" FileName Schema)
            else
                match root.TryGetProperty "connection" with
                | false, _ -> Ok None
                | true, c when c.ValueKind = JsonValueKind.Null -> Ok None
                | true, c ->
                    match str "owner" c, str "repository" c, str "branch" c, str "key" c with
                    | Some owner, Some repository, Some branch, Some key ->
                        Ok(Some { Repository = { Owner = owner; Name = repository; Branch = branch }; Credential = Credential.create key })
                    | _ -> Error(sprintf "the connection in %s lacks owner, repository, branch or key" FileName)
        with :? JsonException -> Error(sprintf "%s is not JSON" FileName)

/// The investigation listing of a workspace, read at one commit (v4.1.5 L1; EXT-DATA-003, EXT-DATA-013).
[<RequireQualifiedAccess>]
module GitHubLister =
    let private activeCatalogPath = RepoPath "catalog/active.json"

    let private parse (bytes: byte[]) : JsonElement option =
        try
            use doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
            Some(doc.RootElement.Clone())
        with :? JsonException -> None

    let private str (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
            | _ -> None
        | _ -> None

    /// The manifest of a catalog workspace: `catalog/<last segment of the workspace>/manifest.json` (CONTRACT section 9;
    /// `catalog/active.json` names both, and they agree in every published catalog).
    let manifestPath (Workspace workspace) =
        RepoPath(sprintf "catalog/%s/manifest.json" (workspace.TrimEnd('/').Split('/') |> Array.last))

    let private investigationsDir (Workspace workspace) = RepoPath(workspace.TrimEnd('/') + "/investigations")

    let private fileBytes (answer: Result<ProviderAnswer, ProviderError>) : Result<byte[], LoadFailure> =
        answer
        |> Result.bind Decode.file
        |> Result.mapError ProviderFailed
        |> Result.bind (function
            | FileBytes(_, bytes) -> Ok bytes
            | TooLargeForContents _ -> Error(ProviderFailed(Malformed "a catalog file over 1 MB")))

    let private request (ctx: RequestContext) =
        function
        | ListHead -> GitHubRequests.readHead ctx.NextId ctx.Connection
        | ListActiveCatalogAt commit -> GitHubRequests.readFile ctx.NextId ctx.Connection commit activeCatalogPath
        | ListManifestAt(commit, workspace) -> GitHubRequests.readFile ctx.NextId ctx.Connection commit (manifestPath workspace)
        | ListDirectoryAt(commit, workspace) -> GitHubRequests.readDirectory ctx.NextId ctx.Connection commit (investigationsDir workspace)

    let private next (ctx: RequestContext) (step: ListingStep) = ListingNextRequest(step, request ctx step)

    /// The investigations a catalog manifest lists, with their titles and expected state digests (EXT-DATA-013).
    let catalogListing (address: WorkspaceAddress.T) (commit: CommitOid) (manifest: JsonElement) : Result<Listing, LoadFailure> =
        match manifest.TryGetProperty "cases" with
        | true, cases when cases.ValueKind = JsonValueKind.Array ->
            let listed =
                [ for c in cases.EnumerateArray() do
                      match str "investigationId" c |> Option.map Guid.TryParse, str "path" c with
                      | Some(true, id), Some path ->
                          yield
                              { Ref = { Address = address; Id = InvestigationId id }
                                Path = RepoPath path
                                Title = str "title" c
                                ExpectedStateSha256 = str "expectedStateSha256" c }
                      | _ -> () ]
            Ok { Address = address; ReadAt = commit; Investigations = listed }
        | _ -> Error(Unverifiable [])

    /// The investigations a working copy holds: one directory per investigation id. Nothing else is read (EXT-DATA-006).
    let workingCopyListing (address: WorkspaceAddress.T) (commit: CommitOid) (entries: (string * string) list) : Listing =
        let (Workspace workspace) = address.Workspace
        { Address = address
          ReadAt = commit
          Investigations =
            [ for name, kind in entries do
                  match kind, Guid.TryParse name with
                  | "dir", (true, id) ->
                      yield
                          { Ref = { Address = address; Id = InvestigationId id }
                            Path = RepoPath(sprintf "%s/investigations/%s" workspace name)
                            Title = None
                            ExpectedStateSha256 = None }
                  | _ -> () ] }

    let private interpret (ctx: RequestContext) (target: ListingTarget) (step: ListingStep) (outcome: HttpOutcome) : ListingInterpretation =
        match step, target with
        | ListHead, _ ->
            match Classify.classify Operation.ReadHead outcome |> Result.bind Decode.head, target with
            | Error e, _ -> ListingFailed(ProviderFailed e)
            | Ok commit, ActiveCatalog -> next ctx (ListActiveCatalogAt commit)
            | Ok commit, WorkingCopyOf address -> next ctx (ListDirectoryAt(commit, address.Workspace))
        | ListActiveCatalogAt commit, _ ->
            match fileBytes (Classify.classify (Operation.ReadFile activeCatalogPath) outcome) |> Result.map parse with
            | Ok(Some active) ->
                match str "workspace" active with
                | Some ws -> next ctx (ListManifestAt(commit, Workspace ws))
                | None -> ListingFailed(ProviderFailed(Malformed "catalog/active.json names no workspace"))
            | Ok None -> ListingFailed(ProviderFailed(Malformed "catalog/active.json is not JSON"))
            | Error f -> ListingFailed f
        | ListManifestAt(commit, workspace), _ ->
            match fileBytes (Classify.classify (Operation.ReadFile(manifestPath workspace)) outcome) |> Result.map parse with
            | Ok(Some manifest) ->
                match catalogListing (WorkspaceAddress.catalog workspace) commit manifest with
                | Ok listing -> ListingComplete listing
                | Error f -> ListingFailed f
            | Ok None -> ListingFailed(ProviderFailed(Malformed "the catalog manifest is not JSON"))
            | Error f -> ListingFailed f
        | ListDirectoryAt(commit, workspace), _ ->
            let address =
                match target with
                | WorkingCopyOf a -> a
                | ActiveCatalog -> WorkspaceAddress.catalog workspace
            match Classify.classify (Operation.ReadFile(investigationsDir workspace)) outcome |> Result.bind Decode.directory with
            | Ok entries -> ListingComplete(workingCopyListing address commit entries)
            // A working copy that does not exist yet lists as empty at the head read (EXT-DATA-009: a clean start).
            | Error(NotFound _) -> ListingComplete { Address = address; ReadAt = commit; Investigations = [] }
            | Error e -> ListingFailed(ProviderFailed e)

    let lister : WorkspaceLister =
        { List = fun ctx _ -> ListHead, request ctx ListHead
          InterpretList = interpret }
