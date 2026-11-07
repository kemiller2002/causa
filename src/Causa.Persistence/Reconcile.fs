// Owner: persistence. The commit a save sends, its write marker, and the reads that verify or reconcile it.
namespace Causa.Persistence

open System
open System.Text
open Causa.Domain

/// The bytes a session writes: one file set from the session alone (deterministic: the same session gives the same
/// bytes, so a read-back or a reconciliation compares against exactly what was sent).
type SessionEncoder = InvestigationSession -> Result<FileSet, string>

/// The three files of one investigation read at one commit, with the commit's time.
type FilesAtCommit =
    { Commit: CommitOid
      CommittedAt: DateTimeOffset option
      Files: FileSet option
      Truncated: bool }

[<RequireQualifiedAccess>]
module Writes =

    let workspaceOf (address: WorkspaceAddress.T) = let (Workspace w) = address.Workspace in w

    let guidOf (InvestigationId g) = g

    /// `<workspace>/investigations/<id>` (CONTRACT section 3).
    let prefixOf (address: WorkspaceAddress.T) (id: InvestigationId) =
        RepoPath(sprintf "%s/investigations/%s" (workspaceOf address) ((guidOf id).ToString()))

    let sessionPrefix (s: InvestigationSession) = prefixOf (Writable.address s.Address) s.Id

    let fileNames = [ "manifest.json"; "events.ndjson"; "snapshot.json" ]

    let investigationRef (s: InvestigationSession) : InvestigationRef = { Address = Writable.address s.Address; Id = s.Id }

    /// The write marker of the session's current mutation: one per send, numbered by the session's mutation count
    /// (PERSIST-015). It is carried in the commit message, never in the investigation's bytes.
    let marker (s: InvestigationSession) : WriteMarker = { Session = guidOf s.Id; Sequence = int64 s.Mutations }

    let markerText (m: WriteMarker) = sprintf "causa-write-marker: %s/%d" (m.Session.ToString()) m.Sequence

    /// The provisional encoder: a deterministic file set of the session's base head and unsaved commands. It is the
    /// stand-in until the IF41-STORAGE codec (StorageCodec.Encode) is wired in through SaveMachines.create; it writes
    /// only inside the session's own working-copy prefix.
    let provisionalEncoder : SessionEncoder =
        fun s ->
            let (CommitOid b) = s.BaseHead
            let id = (guidOf s.Id).ToString()
            let manifest = Json.Object [ "schema", Json.String "causa-app-provisional/1"; "investigationId", Json.String id; "baseHead", Json.String b; "commands", Json.Number(decimal s.Unsaved.Length) ]
            let events =
                s.Unsaved
                |> List.map (fun c -> Json.serialize (Json.Object [ "commandId", Json.String(c.CommandId.ToString()); "command", Json.String(sprintf "%A" c.Command) ]) + "\n")
                |> String.concat ""
            let snapshot = Json.Object [ "investigationId", Json.String id; "unsavedCommands", Json.Number(decimal s.Unsaved.Length) ]
            Ok
                { Manifest = Encoding.UTF8.GetBytes(Json.serialize manifest + "\n")
                  Events = Encoding.UTF8.GetBytes events
                  Snapshot = Encoding.UTF8.GetBytes(Json.serialize snapshot + "\n") }

    /// The one conditional commit of a save: the three files under the prefix, conditional on `expected`.
    let commitRequest (ctx: RequestContext) (encode: SessionEncoder) (s: InvestigationSession) (expected: CommitOid) : Result<HttpRequest, string> =
        match encode s with
        | Error e -> Error e
        | Ok files ->
            let (RepoPath p) = sessionPrefix s
            let changes =
                [ PutFile(RepoPath(p + "/manifest.json"), files.Manifest)
                  PutFile(RepoPath(p + "/events.ndjson"), files.Events)
                  PutFile(RepoPath(p + "/snapshot.json"), files.Snapshot) ]
            GitHubRequests.commit ctx.NextId ctx.Connection expected (sessionPrefix s) (sprintf "causa: save investigation %s" ((guidOf s.Id).ToString())) (markerText (marker s)) changes
            |> Result.mapError (fun es -> String.Join("; ", es))

    let private gqlString (s: string) = Json.serialize (Json.String s)

    /// One GraphQL read of the three files and the commit time at a named commit: a read-back or a reconciliation file
    /// read is one request, so one Outstanding identity covers it.
    let filesAt (id: RequestId) (connection: ConnectionContext) (CommitOid commit) (RepoPath prefix) : HttpRequest =
        let blob alias name = sprintf "%s: object(expression: %s) { ... on Blob { text isTruncated } }" alias (gqlString (sprintf "%s:%s/%s" commit prefix name))
        let query =
            sprintf
                "query { repository(owner: %s, name: %s) { c: object(oid: %s) { oid ... on Commit { committedDate } } %s %s %s } }"
                (gqlString connection.Repository.Owner)
                (gqlString connection.Repository.Name)
                (gqlString commit)
                (blob "m" "manifest.json")
                (blob "e" "events.ndjson")
                (blob "s" "snapshot.json")
        { Id = id
          Method = Post
          Url = GitHubRequests.GraphQlUrl
          Headers = GitHubRequests.headers connection @ [ "Content-Type", "application/json" ]
          Body = Some(Json.serialize (Json.Object [ "query", Json.String query ])) }

    /// The files of a filesAt answer. Files is None when any of the three is absent at the commit.
    let decodeFilesAt (commit: CommitOid) (answer: ProviderAnswer) : Result<FilesAtCommit, ProviderError> =
        match Json.parse answer.Body with
        | None -> Error(Malformed "the file read is not JSON")
        | Some json ->
            let repo = Json.path [ "data"; "repository" ] (Some json)
            match repo with
            | None -> Error(Malformed "required field data.repository is missing")
            | Some _ ->
                let at =
                    Json.path [ "c"; "committedDate" ] repo
                    |> Json.text
                    |> Option.bind (fun d ->
                        match DateTimeOffset.TryParse(d, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
                        | true, v -> Some v
                        | _ -> None)
                let blob alias = Json.field alias repo
                let text alias = blob alias |> Json.field "text" |> Json.text
                let truncated = [ "m"; "e"; "s" ] |> List.exists (fun a -> blob a |> Json.field "isTruncated" |> Json.bool = Some true)
                let files =
                    match text "m", text "e", text "s" with
                    | Some m, Some e, Some s -> Some { Manifest = Encoding.UTF8.GetBytes m; Events = Encoding.UTF8.GetBytes e; Snapshot = Encoding.UTF8.GetBytes s }
                    | _ -> None
                Ok { Commit = commit; CommittedAt = at; Files = files; Truncated = truncated }

    /// The commit a filesAt answer was read at, or None when the answer is not a filesAt answer.
    let filesAtCommitOid (answer: ProviderAnswer) : CommitOid option =
        Json.parse answer.Body |> Json.path [ "data"; "repository"; "c"; "oid" ] |> Json.text |> Option.map CommitOid

    /// The files a compare page names as changed (`files[].filename`).
    let changedFiles (answer: ProviderAnswer) : string list =
        Json.parse answer.Body |> Json.field "files" |> Json.items |> List.choose (fun f -> Json.field "filename" (Some f) |> Json.text)

    let touchesPrefix (RepoPath prefix) (files: string list) = files |> List.exists (fun f -> f.StartsWith(prefix + "/"))

    /// Reconciliation reads sent in the 60 s before now (INV-04).
    let readsInWindow (now: DateTimeOffset) (sentAt: DateTimeOffset list) =
        sentAt |> List.filter (fun t -> t > now.AddSeconds -60.0 && t <= now)

    let private gql (s: string) = Json.serialize (Json.String s)

    /// One GraphQL read of the branch head and the investigation's three files at that head, so a load reads one
    /// commit and never a cached branch answer (T-L01, AR-026).
    let headAndFiles (id: RequestId) (connection: ConnectionContext) (target: InvestigationRef) : HttpRequest =
        let (RepoPath prefix) = prefixOf target.Address target.Id
        let file alias name =
            sprintf "%s: file(path: %s) { path object { ... on Blob { text isTruncated } } }" alias (gql (prefix + "/" + name))
        let query =
            sprintf
                "query { repository(owner: %s, name: %s) { ref(qualifiedName: %s) { target { oid ... on Commit { committedDate %s %s %s } } } } }"
                (gql connection.Repository.Owner)
                (gql connection.Repository.Name)
                (gql ("refs/heads/" + connection.Repository.Branch))
                (file "m" "manifest.json")
                (file "e" "events.ndjson")
                (file "s" "snapshot.json")
        { Id = id
          Method = Post
          Url = GitHubRequests.GraphQlUrl
          Headers = GitHubRequests.headers connection @ [ "Content-Type", "application/json" ]
          Body = Some(Json.serialize (Json.Object [ "query", Json.String query ])) }

    let addressOf (workspace: string) : WorkspaceAddress.T =
        let marker = "workspaces/demo/causa-app-"
        if workspace.StartsWith marker then
            match WorkspaceAddress.workingCopy [] (workspace.Substring marker.Length) with
            | Some a -> a
            | None -> WorkspaceAddress.catalog (Workspace workspace)
        else
            WorkspaceAddress.catalog (Workspace workspace)

    /// The investigation a headAndFiles answer holds, verified with `verify` at the head it was read at.
    let verifyHeadAndFiles
        (verify: WorkspaceAddress.T -> CommitOid -> FileSet -> Result<VerifiedInvestigation, VerificationFailure list>)
        (answer: ProviderAnswer)
        : Result<VerifiedInvestigation, LoadFailure> =
        let target = Json.parse answer.Body |> Json.path [ "data"; "repository"; "ref"; "target" ]
        match target |> Json.field "oid" |> Json.text with
        | None -> Error(ProviderFailed(NotFound "branch"))
        | Some oid ->
            let text alias = target |> Json.path [ alias; "object"; "text" ] |> Json.text
            match text "m", text "e", text "s", target |> Json.path [ "m"; "path" ] |> Json.text with
            | Some m, Some e, Some s, Some p ->
                let workspace = p.Substring(0, max 0 (p.IndexOf "/investigations/"))
                let files = { Manifest = Encoding.UTF8.GetBytes m; Events = Encoding.UTF8.GetBytes e; Snapshot = Encoding.UTF8.GetBytes s }
                verify (addressOf workspace) (CommitOid oid) files |> Result.mapError Unverifiable
            | _ -> Error(ProviderFailed(NotFound "investigation files"))
