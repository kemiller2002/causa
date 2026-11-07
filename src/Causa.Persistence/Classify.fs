// Owner: persistence. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Persistence

/// IF41-ERRORS: the classification of github-error-matrix.json. Every provider answer maps to exactly one case.
type RateLimitKind =
    | Primary of resetAtEpochSeconds: int64 option
    | Secondary of retryAfterSeconds: int option

type ProviderError =
    | AuthenticationFailed
    | PermissionDenied of operation: string
    | RateLimited of RateLimitKind
    | StaleHead of expected: CommitOid * actual: CommitOid option
    | ValidationFailed of messages: string list
    | NotFound of what: string
    | HistoryRewritten of expected: CommitOid
    | NetworkUnavailable of TransportFailure
    | Malformed of detail: string
    | ReadBackMismatch of commit: CommitOid
    | Unrecognised of status: int

/// A 2xx answer that decoded: the request it answers and its body, for the caller to decode further.
type ProviderAnswer = { Request: RequestId; Status: int; Body: string }

type Classifier = HttpOutcome -> Result<ProviderAnswer, ProviderError>

/// The answer to Connect (EXT-DATA-005): write access comes only from the permissions GitHub states for the
/// repository, never from a successful read.
type ConnectResult =
    | ConnectAccepted of canWrite: bool
    | ConnectRejected of ProviderError
    | ConnectSuperseded

/// Persistence builds the Connect request and decodes its answer; the App maps ConnectResult to SM-CONNECTION's events.
type Connector =
    { ConnectRequest: RequestId -> ConnectionContext -> HttpRequest
      InterpretConnect: RequestId -> HttpOutcome -> ConnectResult }

// ---- T18: the classifier (github-error-matrix.json, v4.1.6) ---------------------------------------------------------

/// What a request was, so an answer whose HTTP status has several meanings is read with the right evidence. The
/// classifier never decides by status alone where GitHub gives a status several meanings (PERSIST-008-O2).
[<RequireQualifiedAccess>]
type Operation =
    /// createCommitOnBranch conditional on this head.
    | Mutation of expectedHead: CommitOid
    /// The repository read of Connect: a 404 there is GitHub hiding a repository the token cannot read.
    | Connect
    | ReadHead
    /// A file or directory at a named commit (Connect already confirmed access, so a 404 is a missing file).
    | ReadFile of RepoPath
    | ReadBlob
    | ReadCommit
    /// compare expectedBase...head during reconciliation: diverged, behind or 404 is rewritten history.
    | Compare of expectedBase: CommitOid

/// The matrix class an outcome falls in, by its id (github-error-matrix.json `classes[].id`).
type MatrixClass =
    | GhAuth
    | GhRatePrimary
    | GhRateSecondary
    | GhStaleHead
    | GhLargeFile
    | GhPermission
    | GhValidation
    | GhNotFound
    | GhHistoryRewritten
    | GhNetworkBefore
    | GhNetworkAfter
    | GhUnknownOutcome
    | GhMalformed
    | GhTruncatedList
    | Success

module MatrixClass =
    let id =
        function
        | GhAuth -> "GH-AUTH"
        | GhRatePrimary -> "GH-RATE-PRIMARY"
        | GhRateSecondary -> "GH-RATE-SECONDARY"
        | GhStaleHead -> "GH-STALE-HEAD"
        | GhLargeFile -> "GH-LARGE-FILE"
        | GhPermission -> "GH-PERMISSION"
        | GhValidation -> "GH-VALIDATION"
        | GhNotFound -> "GH-NOT-FOUND"
        | GhHistoryRewritten -> "GH-HISTORY-REWRITTEN"
        | GhNetworkBefore -> "GH-NETWORK-BEFORE"
        | GhNetworkAfter -> "GH-NETWORK-AFTER"
        | GhUnknownOutcome -> "GH-UNKNOWN-OUTCOME"
        | GhMalformed -> "GH-MALFORMED"
        | GhTruncatedList -> "GH-TRUNCATED-LIST"
        | Success -> "SUCCESS"

    /// The automatic retry bound of each class (github-error-matrix.json `classes[].maxAttempts`); no caller retries
    /// beyond it.
    let maxAttempts =
        function
        | GhStaleHead -> 3
        | GhRatePrimary
        | GhNetworkAfter
        | GhUnknownOutcome
        | GhMalformed
        | GhTruncatedList -> 3
        | GhLargeFile -> 1
        | GhAuth
        | GhRateSecondary
        | GhPermission
        | GhValidation
        | GhNotFound
        | GhHistoryRewritten
        | GhNetworkBefore
        | Success -> 0

/// The evidence one answer carries besides its status: the headers and the typed content of the body.
type AnswerEvidence =
    { Status: int
      RateRemaining: string option
      RateReset: int64 option
      RetryAfter: int option
      /// REST `message`, or every GraphQL error message.
      Messages: string list
      /// GraphQL `errors[].type`.
      GraphQlTypes: string list
      /// REST `errors[].code` (for example too_large).
      ErrorCodes: string list
      Body: Json option }

[<RequireQualifiedAccess>]
module Classify =
    open System
    open System.Text.RegularExpressions

    let header (name: string) (headers: (string * string) list) : string option =
        headers |> List.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd

    let private prop name node = Json.field name node
    let private items node = Json.items node
    let private strProp name node = Json.field name node |> Json.text

    let evidence (status: int) (headers: (string * string) list) (body: string) : AnswerEvidence =
        let json = Json.parse body
        let errors = items (prop "errors" json)
        { Status = status
          RateRemaining = header "x-ratelimit-remaining" headers |> Option.map (fun s -> s.Trim())
          RateReset = header "x-ratelimit-reset" headers |> Option.bind (fun s -> match Int64.TryParse(s.Trim()) with | true, v -> Some v | _ -> None)
          RetryAfter = header "retry-after" headers |> Option.bind (fun s -> match Int32.TryParse(s.Trim()) with | true, v -> Some v | _ -> None)
          Messages =
            [ yield! strProp "message" json |> Option.toList
              for e in errors do
                  yield! strProp "message" (Some e) |> Option.toList ]
          GraphQlTypes = errors |> List.choose (fun e -> strProp "type" (Some e))
          ErrorCodes = errors |> List.choose (fun e -> strProp "code" (Some e))
          Body = json }

    let private mentions (needles: string list) (messages: string list) =
        messages |> List.exists (fun m -> needles |> List.exists (fun n -> m.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))

    /// "A ref named refs/heads/x is at <oid> but expected <oid>" (FORBIDDEN, Rehearsal 7 KD-022).
    let private movedHead = Regex(@"is at ([0-9a-fA-F]{7,64}) but expected ([0-9a-fA-F]{7,64})", RegexOptions.Compiled)

    /// "Expected branch to point to "<oid>" but it did not" (STALE_DATA).
    let private expectedBranch = Regex(@"Expected branch to point to ""?([0-9a-fA-F]{7,64})?", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    let private isPrimary (e: AnswerEvidence) = (e.Status = 403 || e.Status = 429) && e.RateRemaining = Some "0"

    let private isSecondary (e: AnswerEvidence) =
        e.RateRemaining <> Some "0"
        && (((e.Status = 403 || e.Status = 429)
             && (e.RetryAfter.IsSome || mentions [ "secondary rate limit"; "too quickly"; "abuse" ] e.Messages))
            || List.contains "RATE_LIMITED" e.GraphQlTypes)

    let private isStale (e: AnswerEvidence) =
        List.contains "STALE_DATA" e.GraphQlTypes
        || e.Messages |> List.exists (fun m -> movedHead.IsMatch m || expectedBranch.IsMatch m)

    let private isLargeFile (operation: Operation) (e: AnswerEvidence) =
        match operation with
        | Operation.ReadFile _ ->
            (e.Status = 403 && (List.contains "too_large" e.ErrorCodes || mentions [ "too_large"; "too large" ] e.Messages))
            || (e.Status = 200 && strProp "encoding" e.Body = Some "none")
        | _ -> false

    let private staleError (operation: Operation) (e: AnswerEvidence) : ProviderError =
        let fromMoved =
            e.Messages |> List.tryPick (fun m -> let x = movedHead.Match m in if x.Success then Some(CommitOid x.Groups[2].Value, Some(CommitOid x.Groups[1].Value)) else None)
        match fromMoved, operation with
        | Some(expected, actual), _ -> StaleHead(expected, actual)
        | None, Operation.Mutation expected -> StaleHead(expected, None)
        | None, _ ->
            let expected =
                e.Messages |> List.tryPick (fun m -> let x = expectedBranch.Match m in if x.Success && x.Groups[1].Success then Some(CommitOid x.Groups[1].Value) else None)
            StaleHead(defaultArg expected (CommitOid ""), None)

    let private operationName =
        function
        | Operation.Mutation _ -> "write"
        | Operation.Connect -> "read repository"
        | Operation.ReadHead -> "read branch"
        | Operation.ReadFile(RepoPath p) -> "read " + p
        | Operation.ReadBlob -> "read blob"
        | Operation.ReadCommit -> "read commit"
        | Operation.Compare _ -> "compare"

    let private notFoundWhat =
        function
        | Operation.ReadFile(RepoPath p) -> p
        | Operation.ReadHead -> "branch"
        | Operation.Mutation _ -> "branch"
        | op -> operationName op

    let private validationMessages (e: AnswerEvidence) =
        if List.isEmpty e.Messages then [ sprintf "HTTP %d" e.Status ] else e.Messages

    let private recognisedGraphQl = set [ "STALE_DATA"; "FORBIDDEN"; "RATE_LIMITED"; "UNPROCESSABLE"; "NOT_FOUND"; "INVALID"; "ARGUMENT_ERROR"; "BAD_REQUEST" ]

    /// The class and typed error of one answered request, by the matrix precedence: GH-AUTH, GH-RATE-PRIMARY,
    /// GH-RATE-SECONDARY, GH-STALE-HEAD, GH-LARGE-FILE, GH-PERMISSION, GH-VALIDATION, GH-NOT-FOUND, then the rest.
    let classifyAnswer (operation: Operation) (requestId: RequestId) (e: AnswerEvidence) (body: string) : MatrixClass * Result<ProviderAnswer, ProviderError> =
        let answer () = Ok { Request = requestId; Status = e.Status; Body = body }
        if e.Status = 401 then GhAuth, Error AuthenticationFailed
        elif isPrimary e then GhRatePrimary, Error(RateLimited(Primary e.RateReset))
        elif isSecondary e then GhRateSecondary, Error(RateLimited(Secondary e.RetryAfter))
        elif isStale e then GhStaleHead, Error(staleError operation e)
        elif isLargeFile operation e then GhLargeFile, answer ()
        else
            match operation, e.Status with
            | _, 403 -> GhPermission, Error(PermissionDenied(operationName operation))
            | Operation.Connect, 404 -> GhPermission, Error(PermissionDenied(operationName operation))
            | _, 422 -> GhValidation, Error(ValidationFailed(validationMessages e))
            | Operation.Compare expected, 404 -> GhHistoryRewritten, Error(HistoryRewritten expected)
            | _, 404 -> GhNotFound, Error(NotFound(notFoundWhat operation))
            | _, s when s >= 200 && s < 300 ->
                match e.Body with
                | None -> GhMalformed, Error(Malformed "the answer body is not JSON")
                | Some _ when not (List.isEmpty e.GraphQlTypes) || not (List.isEmpty (items (prop "errors" e.Body))) ->
                    let types = e.GraphQlTypes
                    if List.contains "FORBIDDEN" types then GhPermission, Error(PermissionDenied(operationName operation))
                    elif List.contains "NOT_FOUND" types then GhNotFound, Error(NotFound(notFoundWhat operation))
                    elif types |> List.exists (fun t -> t = "UNPROCESSABLE" || t = "INVALID" || t = "ARGUMENT_ERROR" || t = "BAD_REQUEST") then
                        GhValidation, Error(ValidationFailed(validationMessages e))
                    elif types |> List.exists (fun t -> not (recognisedGraphQl.Contains t)) || List.isEmpty types then
                        // An error form the matrix does not name: unknown for a mutation, a failed read otherwise.
                        GhUnknownOutcome, Error(Unrecognised e.Status)
                    else GhUnknownOutcome, Error(Unrecognised e.Status)
                | Some json ->
                    match operation with
                    | Operation.Compare expected ->
                        match strProp "status" (Some json) with
                        | Some "diverged"
                        | Some "behind" -> GhHistoryRewritten, Error(HistoryRewritten expected)
                        | Some _ ->
                            let total = Json.int (prop "total_commits" (Some json))
                            let listed = items (prop "commits" (Some json)) |> List.length
                            (match total with Some t when t > listed -> GhTruncatedList | _ -> Success), answer ()
                        | None -> GhMalformed, Error(Malformed "compare answer has no status")
                    | _ -> Success, answer ()
            | _, s when s >= 500 -> GhUnknownOutcome, Error(Unrecognised s)
            | _, s -> GhUnknownOutcome, Error(Unrecognised s)

    /// The matrix class and result of one outcome.
    let classifyWithClass (operation: Operation) (outcome: HttpOutcome) : MatrixClass * Result<ProviderAnswer, ProviderError> =
        match outcome with
        | NotAnswered(_, TransportFailure.FailedBeforeSending) -> GhNetworkBefore, Error(NetworkUnavailable TransportFailure.FailedBeforeSending)
        | NotAnswered(_, failure) -> GhNetworkAfter, Error(NetworkUnavailable failure)
        | Answered(id, status, headers, body) -> classifyAnswer operation id (evidence status headers body) body

    /// IF41-ERRORS Classifier for one operation.
    let classify (operation: Operation) : Classifier = fun outcome -> classifyWithClass operation outcome |> snd

    /// Whether a mutation's answer leaves its outcome unknown (GH-NETWORK-AFTER, GH-UNKNOWN-OUTCOME, GH-MALFORMED):
    /// SM-SAVE AnswerLost, never a stale head, a conflict or a failure (PERSIST-008-O3, INV-03).
    let isUnknownMutationOutcome =
        function
        | NetworkUnavailable TransportFailure.LostAfterSending
        | NetworkUnavailable TransportFailure.TimedOut
        | Unrecognised _
        | Malformed _ -> true
        | _ -> false

    /// What the user reads for each typed error (github-error-matrix.json `userVisible`). A rate limit is never worded
    /// as a permission problem and a moved head never as a refusal (INV-22).
    let message (error: ProviderError) : string =
        match error with
        | AuthenticationFailed -> "GitHub refused the credential; your changes are kept in this tab."
        | PermissionDenied _ -> "This credential cannot write to the repository; your changes are kept in this tab."
        | RateLimited(Primary reset) ->
            let at =
                reset |> Option.map (fun s -> DateTimeOffset.FromUnixTimeSeconds(s).ToString("u")) |> Option.defaultValue "the reset time"
            sprintf "GitHub's rate limit was reached; try again after %s. Your changes are kept in this tab." at
        | RateLimited(Secondary after) ->
            let seconds = after |> Option.map string |> Option.defaultValue "a few"
            sprintf "GitHub asked us to slow down; try again in %s seconds. Your changes are kept in this tab." seconds
        | StaleHead _ -> "The repository changed since this investigation was read; saving continues after a fresh read."
        | ValidationFailed messages -> sprintf "GitHub refused the change as invalid: %s. Your changes are kept in this tab." (String.Join("; ", messages))
        | NotFound what -> sprintf "%s was not found in the repository." what
        | HistoryRewritten _ -> "The branch history on GitHub was rewritten; nothing is reported as saved. Your changes are kept in this tab."
        | NetworkUnavailable TransportFailure.FailedBeforeSending -> "Your changes exist only in this tab and cannot be saved until the connection returns."
        | NetworkUnavailable _ -> "The save may or may not have reached GitHub; checking."
        | Malformed _ -> "GitHub sent an answer that could not be read."
        | ReadBackMismatch _ -> "GitHub holds different bytes than this save wrote; nothing is reported as saved. Your changes are kept in this tab."
        | Unrecognised status -> sprintf "GitHub sent an answer that was not recognised (HTTP %d)." status

// ---- T18: decoding the answers persistence reads ----------------------------------------------------------------------

/// A file read: its bytes and blob sha, or the contents API's large-file form, re-read once through the blobs API.
type FileRead =
    | FileBytes of sha: string * bytes: byte[]
    | TooLargeForContents of blobSha: string option

/// One commit of a compare page: its oid and message (the write marker lives in the message, PERSIST-015).
type ComparedCommit = { Oid: CommitOid; Message: string }

type ComparePage = { Status: string; TotalCommits: int; Commits: ComparedCommit list }

/// A commit createCommitOnBranch made.
type CreatedCommit = { Oid: CommitOid; CommittedAt: System.DateTimeOffset }

[<RequireQualifiedAccess>]
module Decode =
    open System

    let private node (answer: ProviderAnswer) = Json.parse answer.Body
    let private path = Json.path
    let private text = Json.text

    let private required what =
        function
        | Some v -> Ok v
        | None -> Error(Malformed(sprintf "required field %s is missing" what))

    /// The head commit of a ref answer (`object.sha`).
    let head (answer: ProviderAnswer) : Result<CommitOid, ProviderError> =
        node answer |> path [ "object"; "sha" ] |> text |> Option.map CommitOid |> required "object.sha"

    /// A contents answer for a file (GH-LARGE-FILE: encoding none, or the 403 too_large answer).
    let file (answer: ProviderAnswer) : Result<FileRead, ProviderError> =
        let json = node answer
        if answer.Status = 403 then Ok(TooLargeForContents None)
        else
            match text (path [ "encoding" ] json), text (path [ "sha" ] json) with
            | Some "none", sha -> Ok(TooLargeForContents sha)
            | Some "base64", Some sha ->
                match text (path [ "content" ] json) |> Option.bind GitHubRequests.decodeBase64 with
                | Some bytes -> Ok(FileBytes(sha, bytes))
                | None -> Error(Malformed "file content is not base64")
            | _ -> Error(Malformed "required field content is missing")

    /// A blobs API answer.
    let blob (answer: ProviderAnswer) : Result<byte[], ProviderError> =
        let json = node answer
        match text (path [ "encoding" ] json), text (path [ "content" ] json) with
        | Some "base64", Some content -> GitHubRequests.decodeBase64 content |> required "content"
        | Some "utf-8", Some content -> Ok(GitHubRequests.utf8 content)
        | _ -> Error(Malformed "required field content is missing")

    /// The (name, type) entries of a directory listing.
    let directory (answer: ProviderAnswer) : Result<(string * string) list, ProviderError> =
        match node answer with
        | Some(Json.Array entries) ->
            Ok(entries |> List.choose (fun e -> match text (path [ "name" ] (Some e)), text (path [ "type" ] (Some e)) with | Some n, Some k -> Some(n, k) | _ -> None))
        | _ -> Error(Malformed "a directory listing is an array")

    /// The commit createCommitOnBranch made (`data.createCommitOnBranch.commit`). A 2xx answer without its oid is
    /// GH-MALFORMED: for a mutation an unknown outcome.
    let created (answer: ProviderAnswer) : Result<CreatedCommit, ProviderError> =
        let c = node answer |> path [ "data"; "createCommitOnBranch"; "commit" ]
        match text (path [ "oid" ] c), text (path [ "committedDate" ] c) with
        | Some oid, Some date ->
            match DateTimeOffset.TryParse(date, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true, at -> Ok { Oid = CommitOid oid; CommittedAt = at }
            | _ -> Error(Malformed "committedDate does not parse")
        | Some _, None -> Error(Malformed "required field committedDate is missing")
        | None, _ -> Error(Malformed "required field data.createCommitOnBranch.commit.oid is missing")

    /// One compare page.
    let compare (answer: ProviderAnswer) : Result<ComparePage, ProviderError> =
        let json = node answer
        match text (path [ "status" ] json) with
        | None -> Error(Malformed "compare answer has no status")
        | Some status ->
            let commits =
                Json.items (path [ "commits" ] json)
                |> List.choose (fun c ->
                    text (path [ "sha" ] (Some c))
                    |> Option.map (fun sha -> { ComparedCommit.Oid = CommitOid sha; Message = text (path [ "commit"; "message" ] (Some c)) |> Option.defaultValue "" }))
            Ok { Status = status; TotalCommits = Json.int (path [ "total_commits" ] json) |> Option.defaultValue (List.length commits); Commits = commits }

    /// Write access from the permissions GitHub states (EXT-DATA-005, DEC-R7-001): push, maintain or admin. GitHub
    /// states no `permissions` for a fine-grained token (observed live, recorded as rest-repo-fine-grained-no-permissions):
    /// then write is not refused here and GitHub decides it at the first conditional commit, whose refusal is the
    /// matrix's GH-PERMISSION (Save failed, changes kept). A stated `permissions` object is honoured exactly.
    let canWrite (answer: ProviderAnswer) : Result<bool, ProviderError> =
        match node answer with
        | None -> Error(Malformed "the repository answer is not JSON")
        | Some json ->
            match path [ "permissions" ] (Some json) with
            | Some(Json.Object _) as permissions ->
                let flag name = Json.bool (path [ name ] permissions) |> Option.defaultValue false
                Ok(flag "push" || flag "maintain" || flag "admin")
            | Some _ -> Error(Malformed "permissions is not an object")
            | None when (path [ "full_name" ] (Some json)).IsSome -> Ok true
            | None -> Error(Malformed "required field full_name is missing")

/// IF41-ERRORS Connector: Connect reads the repository and its stated permissions.
[<RequireQualifiedAccess>]
module Connect =
    let connector : Connector =
        { ConnectRequest = GitHubRequests.connect
          InterpretConnect =
            fun expected outcome ->
                let answered =
                    match outcome with
                    | Answered(id, _, _, _)
                    | NotAnswered(id, _) -> id
                if answered <> expected then ConnectSuperseded
                else
                    match Classify.classify Operation.Connect outcome |> Result.bind Decode.canWrite with
                    | Ok canWrite -> ConnectAccepted canWrite
                    | Error e -> ConnectRejected e }

// ---- T18: the provider port (PERSIST-025-O1, PF-001) ------------------------------------------------------------------

/// The typed investigation-data port the application reads and writes through. Its only effect is the Execute it is
/// built over, so the stateful fake and real GitHub are two adapters of one port: the same requests, the same
/// classification and the same result, error and provenance semantics whichever answers (PF-001-O2, PF-001-O3).
type ProviderPort =
    { Connect: unit -> ConnectResult
      ReadHead: unit -> Result<CommitOid, ProviderError>
      /// A file at a named commit; a file over 1 MB is re-read once through the blobs API (GH-LARGE-FILE).
      ReadFile: CommitOid -> RepoPath -> Result<byte[], ProviderError>
      ReadDirectory: CommitOid -> RepoPath -> Result<(string * string) list, ProviderError>
      /// One conditional commit under the prefix (PERSIST-006, PERSIST-008). A change outside the prefix is refused as
      /// ValidationFailed before anything is sent.
      Commit: CommitOid -> RepoPath -> string -> FileChange list -> Result<CreatedCommit, ProviderError>
      Compare: CommitOid -> CommitOid -> int -> Result<ComparePage, ProviderError>
      /// The repository the port is connected to (provenance of every answer).
      Repository: Repository }

[<RequireQualifiedAccess>]
module Provider =
    /// The port over one Execute. Request identities come from one counter per port, so every outcome names the
    /// request it answers and an answer to another request is refused (PF-009-O3).
    let over (execute: Execute) (connection: ConnectionContext) : ProviderPort =
        let counter = ref 0L
        let next () = RequestId(System.Threading.Interlocked.Increment(&counter.contents))
        let run (operation: Operation) (request: HttpRequest) : Result<ProviderAnswer, ProviderError> =
            let outcome = execute request
            let answered =
                match outcome with
                | Answered(id, _, _, _)
                | NotAnswered(id, _) -> id
            if answered <> request.Id then Error(Malformed "the answer names another request")
            else Classify.classify operation outcome
        { Repository = connection.Repository
          Connect =
            fun () ->
                let id = next ()
                Connect.connector.InterpretConnect id (execute (Connect.connector.ConnectRequest id connection))
          ReadHead = fun () -> run Operation.ReadHead (GitHubRequests.readHead (next ()) connection) |> Result.bind Decode.head
          ReadFile =
            fun commit path ->
                run (Operation.ReadFile path) (GitHubRequests.readFile (next ()) connection commit path)
                |> Result.bind Decode.file
                |> Result.bind (function
                    | FileBytes(_, bytes) -> Ok bytes
                    | TooLargeForContents(Some sha) -> run Operation.ReadBlob (GitHubRequests.readBlob (next ()) connection sha) |> Result.bind Decode.blob
                    | TooLargeForContents None -> Error(Malformed "a file over 1 MB came without its blob sha"))
          ReadDirectory =
            fun commit path -> run (Operation.ReadFile path) (GitHubRequests.readDirectory (next ()) connection commit path) |> Result.bind Decode.directory
          Commit =
            fun expected prefix headline changes ->
                match GitHubRequests.commit (next ()) connection expected prefix headline "" changes with
                | Error messages -> Error(ValidationFailed messages)
                | Ok request -> run (Operation.Mutation expected) request |> Result.bind Decode.created
          Compare =
            fun baseOid head page ->
                run (Operation.Compare baseOid) (GitHubRequests.compare (next ()) connection baseOid head page) |> Result.bind Decode.compare }
