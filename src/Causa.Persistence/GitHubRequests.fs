// Owner: persistence. T18: the GitHub requests persistence builds (IF41-WIRE: HTTP as data).
namespace Causa.Persistence

open System
open System.Text
open System.Text.Json

/// One file of a commit: a path under the investigation prefix and its bytes (PERSIST-006).
type FileChange =
    | PutFile of RepoPath * byte[]
    | DeleteFile of RepoPath

/// JSON as immutable data: what persistence reads from and writes to GitHub (no nullable reference in sight).
[<RequireQualifiedAccess>]
type Json =
    | Null
    | Bool of bool
    | Number of decimal
    | String of string
    | Array of Json list
    | Object of (string * Json) list

[<RequireQualifiedAccess>]
module Json =
    let rec private ofElement (e: JsonElement) : Json =
        match e.ValueKind with
        | JsonValueKind.Object -> Json.Object [ for p in e.EnumerateObject() -> p.Name, ofElement p.Value ]
        | JsonValueKind.Array -> Json.Array [ for i in e.EnumerateArray() -> ofElement i ]
        | JsonValueKind.String -> Json.String(e.GetString() |> Option.ofObj |> Option.defaultValue "")
        | JsonValueKind.Number ->
            match e.TryGetDecimal() with
            | true, d -> Json.Number d
            | _ -> Json.Number(decimal (e.GetDouble()))
        | JsonValueKind.True -> Json.Bool true
        | JsonValueKind.False -> Json.Bool false
        | _ -> Json.Null

    /// The parsed JSON of a body, or None when it does not parse.
    let parse (body: string) : Json option =
        try
            use doc = JsonDocument.Parse body
            Some(ofElement doc.RootElement)
        with :? JsonException ->
            None

    let rec private write (w: Utf8JsonWriter) (j: Json) =
        match j with
        | Json.Null -> w.WriteNullValue()
        | Json.Bool b -> w.WriteBooleanValue b
        | Json.Number n -> w.WriteNumberValue n
        | Json.String s -> w.WriteStringValue s
        | Json.Array items ->
            w.WriteStartArray()
            items |> List.iter (write w)
            w.WriteEndArray()
        | Json.Object props ->
            w.WriteStartObject()
            for (k, v) in props do
                w.WritePropertyName k
                write w v
            w.WriteEndObject()

    let serialize (j: Json) : string =
        use stream = new System.IO.MemoryStream()
        (use w = new Utf8JsonWriter(stream)
         write w j)
        Encoding.UTF8.GetString(stream.ToArray())

    let field (name: string) (j: Json option) : Json option =
        match j with
        | Some(Json.Object props) -> props |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
        | _ -> None

    let path (names: string list) (j: Json option) : Json option = names |> List.fold (fun acc n -> field n acc) j

    let text (j: Json option) =
        match j with
        | Some(Json.String s) -> Some s
        | _ -> None

    let int (j: Json option) =
        match j with
        | Some(Json.Number n) when n = System.Math.Floor n -> Some(int n)
        | _ -> None

    let bool (j: Json option) =
        match j with
        | Some(Json.Bool b) -> Some b
        | _ -> None

    let items (j: Json option) =
        match j with
        | Some(Json.Array xs) -> xs
        | _ -> []

/// The GitHub requests persistence sends. Every builder is pure: it returns the request as data and the App runtime
/// executes it through the host's only network operation (Wire.Execute). The credential appears only in the
/// Authorization header (PF-015-O4, INV-21): never in the URL, the body or any other header.
[<RequireQualifiedAccess>]
module GitHubRequests =

    /// The only destination of a request carrying the credential (INV-21, Rehearsal 7 F14).
    [<Literal>]
    let ApiBase = "https://api.github.com"

    [<Literal>]
    let GraphQlUrl = "https://api.github.com/graphql"

    [<Literal>]
    let ApiVersion = "2022-11-28"

    let private escapeSegment (s: string) = Uri.EscapeDataString s

    /// A repository path is escaped segment by segment, so '/' keeps its meaning and nothing else can.
    let escapePath (RepoPath path) =
        path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.map escapeSegment |> String.concat "/"

    let private repoUrl (repository: Repository) =
        sprintf "%s/repos/%s/%s" ApiBase (escapeSegment repository.Owner) (escapeSegment repository.Name)

    let headers (connection: ConnectionContext) : (string * string) list =
        [ Credential.authorization connection.Credential
          "Accept", "application/vnd.github+json"
          "X-GitHub-Api-Version", ApiVersion
          "User-Agent", "causa" ]

    let private get (connection: ConnectionContext) (id: RequestId) (url: string) : HttpRequest =
        { Id = id; Method = Get; Url = url; Headers = headers connection; Body = None }

    let private send (connection: ConnectionContext) (id: RequestId) (method: HttpMethod) (url: string) (body: Json) : HttpRequest =
        { Id = id
          Method = method
          Url = url
          Headers = headers connection @ [ "Content-Type", "application/json" ]
          Body = Some(Json.serialize body) }

    /// Connect (EXT-DATA-005): the repository and the permissions GitHub states for this credential.
    let connect (id: RequestId) (connection: ConnectionContext) : HttpRequest =
        get connection id (repoUrl connection.Repository)

    /// The branch head: GET /repos/{owner}/{repo}/git/ref/heads/{branch}.
    let readHead (id: RequestId) (connection: ConnectionContext) : HttpRequest =
        get connection id (sprintf "%s/git/ref/heads/%s" (repoUrl connection.Repository) (escapePath (RepoPath connection.Repository.Branch)))

    /// One file at a named commit: GET /repos/{owner}/{repo}/contents/{path}?ref={commit}. A read is always at a commit,
    /// never at the moving branch name, so no cached branch answer can stand in for an authoritative read (AR-026).
    let readFile (id: RequestId) (connection: ConnectionContext) (CommitOid commit) (path: RepoPath) : HttpRequest =
        get connection id (sprintf "%s/contents/%s?ref=%s" (repoUrl connection.Repository) (escapePath path) (escapeSegment commit))

    /// A directory listing at a named commit (the same contents endpoint; the answer is an array).
    let readDirectory (id: RequestId) (connection: ConnectionContext) (commit: CommitOid) (path: RepoPath) : HttpRequest =
        readFile id connection commit path

    /// The single re-read of a file over 1 MB through the Git blobs API (GH-LARGE-FILE).
    let readBlob (id: RequestId) (connection: ConnectionContext) (blobSha: string) : HttpRequest =
        get connection id (sprintf "%s/git/blobs/%s" (repoUrl connection.Repository) (escapeSegment blobSha))

    /// One commit: GET /repos/{owner}/{repo}/commits/{oid}.
    let readCommit (id: RequestId) (connection: ConnectionContext) (CommitOid commit) : HttpRequest =
        get connection id (sprintf "%s/commits/%s" (repoUrl connection.Repository) (escapeSegment commit))

    /// The commits after the expected head: GET /repos/{owner}/{repo}/compare/{base}...{head}, one page (GH-TRUNCATED-LIST).
    let compare (id: RequestId) (connection: ConnectionContext) (CommitOid baseOid) (CommitOid head) (page: int) : HttpRequest =
        get connection id (sprintf "%s/compare/%s...%s?per_page=100&page=%d" (repoUrl connection.Repository) (escapeSegment baseOid) (escapeSegment head) (max 1 page))

    let private createCommitMutation =
        "mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid committedDate } } }"

    /// The prefix every file of a save must lie under (PERSIST-006): `<prefix>/...`, with no '.', '..' or empty segment.
    let isUnder (RepoPath prefix) (RepoPath path) =
        let segments (p: string) = p.Split('/') |> List.ofArray
        let prefixSegments = segments (prefix.TrimEnd('/'))
        let pathSegments = segments path
        let safe = pathSegments |> List.forall (fun s -> s <> "" && s <> "." && s <> "..")
        safe && List.length pathSegments > List.length prefixSegments && List.take (List.length prefixSegments) pathSegments = prefixSegments

    let private pathOf =
        function
        | PutFile(RepoPath p, _) -> p
        | DeleteFile(RepoPath p) -> p

    /// One atomic commit on the branch, conditional on the expected head (createCommitOnBranch with expectedHeadOid):
    /// GitHub applies it only while the branch still points at `expectedHead`, so a moved head writes nothing
    /// (PERSIST-008). A change outside `prefix` is refused before anything is sent (PERSIST-006).
    let commit
        (id: RequestId)
        (connection: ConnectionContext)
        (CommitOid expectedHead)
        (prefix: RepoPath)
        (headline: string)
        (body: string)
        (changes: FileChange list)
        : Result<HttpRequest, string list> =
        let outside = changes |> List.map pathOf |> List.filter (RepoPath >> isUnder prefix >> not)
        if List.isEmpty changes then
            Error [ "a commit must change at least one file" ]
        elif not (List.isEmpty outside) then
            Error(outside |> List.map (fun p -> sprintf "path %s is outside the investigation prefix" p))
        else
            let additions =
                changes |> List.choose (function PutFile(RepoPath p, bytes) -> Some(Json.Object [ "path", Json.String p; "contents", Json.String(Convert.ToBase64String bytes) ]) | DeleteFile _ -> None)
            let deletions =
                changes |> List.choose (function DeleteFile(RepoPath p) -> Some(Json.Object [ "path", Json.String p ]) | PutFile _ -> None)
            let message =
                Json.Object [ yield "headline", Json.String headline
                              if body <> "" then yield "body", Json.String body ]
            let input =
                Json.Object
                    [ "branch", Json.Object [ "repositoryNameWithOwner", Json.String(connection.Repository.Owner + "/" + connection.Repository.Name); "branchName", Json.String connection.Repository.Branch ]
                      "expectedHeadOid", Json.String expectedHead
                      "message", message
                      "fileChanges", Json.Object [ "additions", Json.Array additions; "deletions", Json.Array deletions ] ]
            let request = Json.Object [ "query", Json.String createCommitMutation; "variables", Json.Object [ "input", input ] ]
            Ok(send connection id Post GraphQlUrl request)

    /// Whether a request is the mutation (createCommitOnBranch); every other request persistence builds is a read.
    let isMutation (request: HttpRequest) =
        request.Method = Post && request.Url = GraphQlUrl && (defaultArg request.Body "").Contains "createCommitOnBranch"

    /// Base64 as GitHub's contents API sends it (wrapped at 60 columns with '\n').
    let decodeBase64 (text: string) : byte[] option =
        try
            Some(Convert.FromBase64String(text.Replace("\n", "").Replace("\r", "")))
        with _ ->
            None

    let utf8 (s: string) = Encoding.UTF8.GetBytes s
