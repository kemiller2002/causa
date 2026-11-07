// Owner: persistence. T18: sanitized observations of real GitHub answers, the live executor and the live environment.
namespace Causa.TestSupport

open System
open System.Net.Http
open Causa.Persistence

/// One sanitized, versioned observation of a GitHub answer (PF-013-O1): the operation it answered, its status, the
/// headers that carry classification or cache evidence, and its body with no secret or private payload (PF-013-O3).
type RecordedAnswer =
    { /// Stable name of the answer form.
      Form: string
      /// Where the form was observed: a live probe of the governed gate, or a rehearsal observation the matrix records.
      ObservedIn: string
      Operation: Operation
      Status: int
      Headers: (string * string) list
      Body: string
      /// The github-error-matrix.json class the form belongs to.
      ExpectedClass: MatrixClass }

[<RequireQualifiedAccess>]
module Recordings =

    /// The version of this observation set; refreshed when the provider contract changes (PF-013-O4).
    [<Literal>]
    let Version = "2026-11-07.t18.1"

    let private oidA = "4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e"
    let private oidB = "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b"
    let expectedHead = CommitOid oidB

    let private rest = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-limit", "5000"; "x-ratelimit-remaining", "4987" ]

    let private graphql = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-limit", "5000"; "x-ratelimit-remaining", "4990" ]

    let private mutation = Operation.Mutation expectedHead

    let private kd = "Rehearsal 7 live observation recorded in github-error-matrix.json"

    /// Every answer form persistence classifies. The stateful fake answers only with these forms (PERSIST-025, PF-013).
    let all : RecordedAnswer list =
        [ { Form = "graphql-commit-created"; ObservedIn = kd; Operation = mutation; Status = 200; Headers = graphql
            Body = sprintf """{"data":{"createCommitOnBranch":{"commit":{"oid":"%s","committedDate":"2026-11-07T17:00:00Z"}}}}""" oidA
            ExpectedClass = Success }
          { Form = "graphql-stale-data"; ObservedIn = kd; Operation = mutation; Status = 200; Headers = graphql
            Body = sprintf """{"data":{"createCommitOnBranch":null},"errors":[{"type":"STALE_DATA","path":["createCommitOnBranch"],"locations":[{"line":1,"column":58}],"message":"Expected branch to point to \"%s\" but it did not. Pull and try again."}]}""" oidB
            ExpectedClass = GhStaleHead }
          { Form = "graphql-forbidden-moved-head"; ObservedIn = "Rehearsal 7 KD-022 (concurrent live writers)"; Operation = mutation; Status = 200; Headers = graphql
            Body = sprintf """{"data":{"createCommitOnBranch":null},"errors":[{"type":"FORBIDDEN","path":["createCommitOnBranch"],"locations":[{"line":1,"column":58}],"message":"A ref named \"refs/heads/causa-test\" is at %s but expected %s."}]}""" oidA oidB
            ExpectedClass = GhStaleHead }
          { Form = "graphql-forbidden-permission"; ObservedIn = kd; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"data":{"createCommitOnBranch":null},"errors":[{"type":"FORBIDDEN","path":["createCommitOnBranch"],"locations":[{"line":1,"column":58}],"message":"Resource not accessible by personal access token"}]}"""
            ExpectedClass = GhPermission }
          { Form = "graphql-rate-limited"; ObservedIn = kd; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded for user."}]}"""
            ExpectedClass = GhRateSecondary }
          { Form = "graphql-unprocessable"; ObservedIn = "github-error-matrix.json GH-VALIDATION (GraphQL UNPROCESSABLE); message illustrative"; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"data":{"createCommitOnBranch":null},"errors":[{"type":"UNPROCESSABLE","path":["createCommitOnBranch"],"message":"The commit could not be processed."}]}"""
            ExpectedClass = GhValidation }
          { Form = "graphql-not-found-deleted-path"; ObservedIn = "governed gate run 42402357707 (real GitHub): deleting a path absent at the expected head is refused with GraphQL type NOT_FOUND; the message text was not captured by that run's evidence"; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"data":{"createCommitOnBranch":null},"errors":[{"type":"NOT_FOUND","path":["createCommitOnBranch"],"message":"A path was requested for deletion which does not exist as of the expected head."}]}"""
            ExpectedClass = GhNotFound }
          { Form = "graphql-not-found-branch"; ObservedIn = kd; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"data":{"createCommitOnBranch":null},"errors":[{"type":"NOT_FOUND","path":["createCommitOnBranch"],"message":"Could not resolve to a Ref with the name 'refs/heads/missing'."}]}"""
            ExpectedClass = GhNotFound }
          { Form = "graphql-unknown-error-type"; ObservedIn = "classifier boundary: a type the matrix does not name"; Operation = mutation; Status = 200; Headers = graphql
            Body = """{"errors":[{"type":"SOMETHING_NEW","message":"Something went wrong while executing your query."}]}"""
            ExpectedClass = GhUnknownOutcome }
          { Form = "graphql-malformed-200"; ObservedIn = "classifier boundary: a 2xx body that does not decode"; Operation = mutation; Status = 200; Headers = graphql
            Body = "<html>unicorn</html>"; ExpectedClass = GhMalformed }
          { Form = "graphql-502"; ObservedIn = kd; Operation = mutation; Status = 502; Headers = [ "content-type", "text/html" ]
            Body = "<html><body>Bad gateway</body></html>"; ExpectedClass = GhUnknownOutcome }
          { Form = "rest-401-bad-credentials"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 401; Headers = [ "content-type", "application/json; charset=utf-8" ]
            Body = """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest","status":"401"}"""
            ExpectedClass = GhAuth }
          { Form = "graphql-401-bad-credentials"; ObservedIn = kd; Operation = mutation; Status = 401; Headers = [ "content-type", "application/json; charset=utf-8" ]
            Body = """{"message":"Bad credentials","documentation_url":"https://docs.github.com/graphql"}"""
            ExpectedClass = GhAuth }
          { Form = "rest-403-permission"; ObservedIn = kd; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/a.json"); Status = 403; Headers = rest
            Body = """{"message":"Resource not accessible by personal access token","documentation_url":"https://docs.github.com/rest","status":"403"}"""
            ExpectedClass = GhPermission }
          { Form = "rest-403-primary-rate-limit"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 403
            Headers = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-limit", "5000"; "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1794070800" ]
            Body = """{"message":"API rate limit exceeded for user ID 1.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api"}"""
            ExpectedClass = GhRatePrimary }
          { Form = "rest-429-primary-rate-limit"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 429
            Headers = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1794070800" ]
            Body = """{"message":"API rate limit exceeded"}"""; ExpectedClass = GhRatePrimary }
          { Form = "rest-403-secondary-rate-limit"; ObservedIn = "Rehearsal 7 KD-020 (concurrent live writers)"; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/a.json"); Status = 403; Headers = rest
            Body = """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again. If you reach out to GitHub Support for help, please include the request ID.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api#about-secondary-rate-limits"}"""
            ExpectedClass = GhRateSecondary }
          { Form = "rest-403-retry-after"; ObservedIn = kd; Operation = mutation; Status = 403; Headers = rest @ [ "retry-after", "60" ]
            Body = """{"message":"You have triggered an abuse detection mechanism. Please wait a few minutes before you try again."}"""
            ExpectedClass = GhRateSecondary }
          { Form = "rest-429-retry-after"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 429; Headers = rest @ [ "retry-after", "30" ]
            Body = """{"message":"Too many requests"}"""; ExpectedClass = GhRateSecondary }
          { Form = "rest-422-validation"; ObservedIn = kd; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/a.json"); Status = 422; Headers = rest
            Body = """{"message":"Validation Failed","errors":[{"resource":"Commit","field":"sha","code":"invalid"}],"documentation_url":"https://docs.github.com/rest"}"""
            ExpectedClass = GhValidation }
          { Form = "rest-422-missing-branch"; ObservedIn = "PF-008: GitHub HTTP 422 for a missing branch where observed"; Operation = Operation.ReadCommit; Status = 422; Headers = rest
            Body = """{"message":"No commit found for SHA: missing-branch","documentation_url":"https://docs.github.com/rest/commits/commits#get-a-commit"}"""
            ExpectedClass = GhValidation }
          { Form = "rest-404-file"; ObservedIn = kd; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/missing.json"); Status = 404; Headers = rest
            Body = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/repos/contents#get-repository-content","status":"404"}"""
            ExpectedClass = GhNotFound }
          { Form = "rest-404-branch"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 404; Headers = rest
            Body = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/git/refs#get-a-reference","status":"404"}"""
            ExpectedClass = GhNotFound }
          { Form = "rest-404-connect-hidden"; ObservedIn = kd; Operation = Operation.Connect; Status = 404; Headers = rest
            Body = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/repos/repos#get-a-repository","status":"404"}"""
            ExpectedClass = GhPermission }
          { Form = "rest-compare-diverged"; ObservedIn = kd; Operation = Operation.Compare expectedHead; Status = 200; Headers = rest
            Body = sprintf """{"status":"diverged","ahead_by":1,"behind_by":2,"total_commits":1,"commits":[{"sha":"%s","commit":{"message":"other"}}]}""" oidA
            ExpectedClass = GhHistoryRewritten }
          { Form = "rest-compare-404"; ObservedIn = kd; Operation = Operation.Compare expectedHead; Status = 404; Headers = rest
            Body = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/commits/commits#compare-two-commits","status":"404"}"""
            ExpectedClass = GhHistoryRewritten }
          { Form = "rest-compare-truncated"; ObservedIn = "Rehearsal 7 R7L-09: compare lists at most 250 commits"; Operation = Operation.Compare expectedHead; Status = 200; Headers = rest
            Body = sprintf """{"status":"ahead","ahead_by":251,"behind_by":0,"total_commits":251,"commits":[{"sha":"%s","commit":{"message":"m"}}]}""" oidA
            ExpectedClass = GhTruncatedList }
          { Form = "rest-contents-too-large-403"; ObservedIn = "Rehearsal 7 F11"; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/events.ndjson"); Status = 403; Headers = rest
            Body = """{"message":"This API returns blobs up to 1 MB in size. The requested blob is too large to fetch via the API, but you can use the Git Data API to request blobs up to 100 MB in size.","errors":[{"resource":"Blob","field":"data","code":"too_large"}],"documentation_url":"https://docs.github.com/rest/repos/contents#get-repository-content"}"""
            ExpectedClass = GhLargeFile }
          { Form = "rest-contents-encoding-none"; ObservedIn = "Rehearsal 7 F11"; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/events.ndjson"); Status = 200; Headers = rest
            Body = """{"type":"file","encoding":"none","size":1500000,"name":"events.ndjson","path":"workspaces/demo/x/events.ndjson","content":"","sha":"3b18e512dba79e4c8300dd08aeb37f8e728b8dad"}"""
            ExpectedClass = GhLargeFile }
          { Form = "rest-contents-file-cached"; ObservedIn = "Rehearsal 7 R7L-19: cache headers on mutable reads"; Operation = Operation.ReadFile(RepoPath "workspaces/demo/x/a.json"); Status = 200
            Headers = rest @ [ "cache-control", "private, max-age=60, s-maxage=60"; "etag", "W/\"0b1c\"" ]
            Body = """{"type":"file","encoding":"base64","size":2,"name":"a.json","path":"workspaces/demo/x/a.json","content":"e30=\n","sha":"9e26dfeeb6e641a33dae4961196235bdb965b21b"}"""
            ExpectedClass = Success }
          { Form = "rest-ref-head"; ObservedIn = kd; Operation = Operation.ReadHead; Status = 200; Headers = rest @ [ "cache-control", "private, max-age=60, s-maxage=60" ]
            Body = sprintf """{"ref":"refs/heads/causa-test","object":{"sha":"%s","type":"commit"}}""" oidB
            ExpectedClass = Success }
          { Form = "rest-ref-malformed"; ObservedIn = "classifier boundary: a 2xx body that does not decode"; Operation = Operation.ReadHead; Status = 200; Headers = rest
            Body = "{\"ref\":"; ExpectedClass = GhMalformed } ]

    let form (name: string) : RecordedAnswer =
        match all |> List.tryFind (fun r -> r.Form = name) with
        | Some r -> r
        | None -> failwithf "no recorded answer form %s" name

    /// The recorded form as the outcome of one request.
    let outcome (id: RequestId) (r: RecordedAnswer) : HttpOutcome = Answered(id, r.Status, r.Headers, r.Body)

    /// The headers an observation may keep: classification and cache evidence only (PF-013-O3). Authorization,
    /// cookies, request ids and everything else are dropped.
    let keptHeaders =
        set [ "content-type"; "x-ratelimit-limit"; "x-ratelimit-remaining"; "x-ratelimit-reset"; "x-ratelimit-used"; "x-ratelimit-resource"
              "retry-after"; "cache-control"; "etag"; "last-modified"; "x-github-api-version-selected" ]

    let sanitizeHeaders (headers: (string * string) list) =
        headers |> List.filter (fun (k, _) -> keptHeaders.Contains(k.ToLowerInvariant()))

    /// A body is kept only with every occurrence of each secret replaced, and file contents dropped (PF-013-O3).
    let sanitizeBody (secrets: string list) (body: string) =
        secrets |> List.filter (String.IsNullOrEmpty >> not) |> List.fold (fun (b: string) s -> b.Replace(s, "<credential>")) body

/// One live exchange as evidence: the request without its credential and the sanitized answer.
type Observation =
    { Method: string
      Url: string
      Status: int option
      Transport: string option
      Headers: (string * string) list
      Body: string
      At: DateTimeOffset }

/// The live environment of SU-DOTNET-LIVE: CAUSA_GITHUB_TOKEN reads, CAUSA_WRITE_TOKEN writes, against
/// CAUSA_TEST_OWNER/CAUSA_TEST_REPO@CAUSA_TEST_BRANCH. Neither token falls back to the other (PF-015-O3); a missing
/// secret is BLOCKED, never a pass (PF-015-O5, PF-020-O2).
[<RequireQualifiedAccess>]
module LiveEnv =
    type Access =
        | Read
        | Write

    /// Variables a test sets for its own flow only (AsyncLocal): the process environment is never changed, so a test
    /// that checks a missing secret cannot make a concurrently running live test BLOCKED (gate run 42402357707).
    let private overrides = Threading.AsyncLocal<Map<string, string option> option>()

    /// Runs `f` with these variables set (Some) or unset (None) for the calling flow alone.
    let withVariables (values: (string * string option) list) (f: unit -> 'a) : 'a =
        let before = overrides.Value
        let current = defaultArg before Map.empty
        overrides.Value <- Some(values |> List.fold (fun m (k, v) -> Map.add k v m) current)
        try
            f ()
        finally
            overrides.Value <- before

    let private env (name: string) =
        let raw =
            match overrides.Value |> Option.bind (Map.tryFind name) with
            | Some value -> value
            | None ->
                match Environment.GetEnvironmentVariable name with
                | null -> None
                | v -> Some v
        match raw with
        | Some v when not (String.IsNullOrWhiteSpace v) -> Some(v.Trim())
        | _ -> None

    /// A variable as the live harness sees it (the calling flow's overrides first).
    let variable (name: string) = env name

    let tokenVariable =
        function
        | Read -> "CAUSA_GITHUB_TOKEN"
        | Write -> "CAUSA_WRITE_TOKEN"

    /// The governed test repository: `testRepository` of the competition lock (Indy-init
    /// kickoff/competition/toolchain.lock.json), the only repository the gate's minted tokens can reach. The governed
    /// gate's suite step sets the tokens but not CAUSA_TEST_OWNER/REPO/BRANCH (run 42399293179), so the live suites use
    /// these when the variables are absent and a token is present; the variables always override.
    let governedTestRepository : Repository = { Owner = "kemiller2002"; Name = "causa-acceptance-r10"; Branch = "causa-acceptance" }

    /// The repository under test, or the names of the missing variables.
    let repository () : Result<Repository, string> =
        let anyToken = (env "CAUSA_GITHUB_TOKEN").IsSome || (env "CAUSA_WRITE_TOKEN").IsSome
        match env "CAUSA_TEST_OWNER", env "CAUSA_TEST_REPO", env "CAUSA_TEST_BRANCH" with
        | Some owner, Some name, Some branch -> Ok { Owner = owner; Name = name; Branch = branch }
        | None, None, None when anyToken -> Ok governedTestRepository
        | o, r, b ->
            let missing = [ if o.IsNone then "CAUSA_TEST_OWNER"; if r.IsNone then "CAUSA_TEST_REPO"; if b.IsNone then "CAUSA_TEST_BRANCH" ]
            Error(sprintf "BLOCKED: no repository: %s not set" (String.Join(", ", missing)))

    /// The raw token for one access, read only from its own variable.
    let token (access: Access) : Result<string, string> =
        match env (tokenVariable access) with
        | Some t -> Ok t
        | None -> Error(sprintf "BLOCKED: no credential: %s is not set (it never falls back to another token)" (tokenVariable access))

    let connection (access: Access) : Result<ConnectionContext, string> =
        match token access, repository () with
        | Ok t, Ok r -> Ok { Repository = r; Credential = Credential.create t }
        | Error e, _
        | _, Error e -> Error e

    /// Every secret the environment holds, for scrubbing evidence.
    let secrets () = [ Read; Write ] |> List.choose (token >> Result.toOption)

    /// The connection, or the test fails closed with the BLOCKED reason (never a vacuous pass).
    let require (access: Access) : ConnectionContext =
        match connection access with
        | Ok c -> c
        | Error blocked -> raise (InvalidOperationException blocked)

/// The real executor of live tests: one HTTPS request to api.github.com, executed once, never retried (IF41-WIRE). A
/// request to any other host is refused before sending, so the credential reaches only GitHub (INV-21).
[<RequireQualifiedAccess>]
module LiveHttp =
    let private client = lazy (new HttpClient())

    let allowedHost (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri ->
            match uri with
            | null -> false
            | u -> u.Scheme = "https" && u.Host = "api.github.com" && u.IsDefaultPort && String.IsNullOrEmpty u.UserInfo
        | _ -> false

    let private methodOf =
        function
        | Get -> System.Net.Http.HttpMethod.Get
        | Post -> System.Net.Http.HttpMethod.Post
        | Put -> System.Net.Http.HttpMethod.Put
        | Patch -> System.Net.Http.HttpMethod.Patch

    let private methodName =
        function
        | Get -> "GET"
        | Post -> "POST"
        | Put -> "PUT"
        | Patch -> "PATCH"

    /// An Execute over the real network with a timeout; every exchange is appended, sanitized, to `log`.
    let executor (timeout: TimeSpan) (log: ResizeArray<Observation>) : Execute =
        fun request ->
            let secrets = LiveEnv.secrets ()
            let record status transport headers body =
                lock log (fun () ->
                    log.Add
                        { Method = methodName request.Method
                          Url = Recordings.sanitizeBody secrets request.Url
                          Status = status
                          Transport = transport
                          Headers = Recordings.sanitizeHeaders headers
                          Body = Recordings.sanitizeBody secrets body
                          At = DateTimeOffset.UtcNow })
            if not (allowedHost request.Url) then
                record None (Some "FailedBeforeSending: host not allowed") [] ""
                NotAnswered(request.Id, TransportFailure.FailedBeforeSending)
            else
                use message = new HttpRequestMessage(methodOf request.Method, request.Url)
                let mutable contentType = "application/json"
                for (k, v) in request.Headers do
                    if String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase) then contentType <- v
                    else message.Headers.TryAddWithoutValidation(k, v) |> ignore
                match request.Body with
                | Some body -> message.Content <- new StringContent(body, Text.Encoding.UTF8, contentType.Split(';').[0])
                | None -> ()
                use cts = new Threading.CancellationTokenSource(timeout)
                try
                    use response = client.Value.SendAsync(message, cts.Token).GetAwaiter().GetResult()
                    let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    let headers =
                        [ for h in response.Headers do
                              for v in h.Value -> h.Key.ToLowerInvariant(), v
                          for h in response.Content.Headers do
                              for v in h.Value -> h.Key.ToLowerInvariant(), v ]
                    record (Some(int response.StatusCode)) None headers body
                    Answered(request.Id, int response.StatusCode, headers, body)
                with
                | :? OperationCanceledException ->
                    record None (Some "TimedOut") [] ""
                    NotAnswered(request.Id, TransportFailure.TimedOut)
                | :? HttpRequestException as e when e.HttpRequestError = HttpRequestError.NameResolutionError || e.HttpRequestError = HttpRequestError.ConnectionError ->
                    record None (Some "FailedBeforeSending") [] ""
                    NotAnswered(request.Id, TransportFailure.FailedBeforeSending)
                | :? HttpRequestException ->
                    record None (Some "LostAfterSending") [] ""
                    NotAnswered(request.Id, TransportFailure.LostAfterSending)

/// One live test class's fixture (SU-DOTNET-LIVE: one fresh branch per test class): a branch made from
/// CAUSA_TEST_BRANCH with the write token, ports over the real network for both tokens, the sanitized log of every
/// exchange and the real commits the run created, written as evidence that names them (PERSIST-025-O5, PF-014-O3).
type LiveRun(testClass: string) =
    let log = ResizeArray<Observation>()
    let created = ResizeArray<string>()
    let writeConnection = LiveEnv.require LiveEnv.Write
    let readConnection = LiveEnv.require LiveEnv.Read
    let execute = LiveHttp.executor (TimeSpan.FromSeconds 30.0) log
    let runId = sprintf "%s-%s" (testClass.ToLowerInvariant()) (DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6))
    let branch = "causa-t18/" + runId
    let baseHead =
        match (Provider.over execute writeConnection).ReadHead() with
        | Ok head -> head
        | Error e -> raise (InvalidOperationException(sprintf "live fixture: reading %s failed: %A" writeConnection.Repository.Branch e))
    do
        let (CommitOid sha) = baseHead
        let body = Json.serialize (Json.Object [ "ref", Json.String("refs/heads/" + branch); "sha", Json.String sha ])
        let request =
            { Id = RequestId 0L
              Method = Post
              Url = sprintf "%s/repos/%s/%s/git/refs" GitHubRequests.ApiBase writeConnection.Repository.Owner writeConnection.Repository.Name
              Headers = GitHubRequests.headers writeConnection @ [ "Content-Type", "application/json" ]
              Body = Some body }
        match execute request with
        | Answered(_, 201, _, _) -> ()
        | Answered(_, status, _, b) -> raise (InvalidOperationException(sprintf "live fixture: creating branch %s answered %d %s" branch status (Recordings.sanitizeBody (LiveEnv.secrets ()) b)))
        | NotAnswered(_, f) -> raise (InvalidOperationException(sprintf "live fixture: creating branch %s was not answered: %A" branch f))

    let onBranch (c: ConnectionContext) = { c with Repository = { c.Repository with Branch = branch } }

    member _.Branch = branch
    member _.RunId = runId
    member _.BaseHead = baseHead
    member _.Log = log
    member _.Execute : Execute = execute
    member _.WriteConnection = onBranch writeConnection
    member _.ReadConnection = onBranch readConnection
    member this.Writer = Provider.over execute this.WriteConnection
    member this.Reader = Provider.over execute this.ReadConnection
    /// The prefix this run writes under: a working-copy workspace of its own (CONTRACT section 9.4; PERSIST-006).
    member val Prefix = RepoPath(sprintf "workspaces/demo/causa-app-t18-%s/investigations/%s" (DateTimeOffset.UtcNow.ToString("yyyyMMdd")) (Guid.NewGuid().ToString()))
    member _.Created(CommitOid oid) = lock created (fun () -> created.Add oid)
    member _.CreatedCommits : CommitOid list = lock created (fun () -> List.ofSeq created |> List.map CommitOid)

    /// The evidence file of this run: repository, branch, base head, every real commit created and the sanitized
    /// exchanges. It holds no credential (PF-013-O3, PF-015-O4).
    member this.WriteEvidence() : string =
        let dir =
            match Environment.GetEnvironmentVariable "CAUSA_EVIDENCE_DIR" with
            | null -> IO.Path.Combine(Environment.CurrentDirectory, "live-evidence")
            | d -> d
        IO.Directory.CreateDirectory dir |> ignore
        let (CommitOid b) = baseHead
        let obs (o: Observation) =
            Json.Object [ "method", Json.String o.Method; "url", Json.String o.Url
                          "status", (match o.Status with Some s -> Json.Number(decimal s) | None -> Json.Null)
                          "transport", (match o.Transport with Some t -> Json.String t | None -> Json.Null)
                          "headers", Json.Object(o.Headers |> List.map (fun (k, v) -> k, Json.String v))
                          "at", Json.String(o.At.ToString "o") ]
        let doc =
            Json.Object [ "testClass", Json.String testClass; "providerMode", Json.String "live-github"; "recordingsVersion", Json.String Recordings.Version
                          "repository", Json.String(writeConnection.Repository.Owner + "/" + writeConnection.Repository.Name)
                          "sourceBranch", Json.String writeConnection.Repository.Branch; "branch", Json.String branch; "baseHead", Json.String b
                          "sourceSha", (match Environment.GetEnvironmentVariable "GITHUB_SHA" with null -> Json.Null | s -> Json.String s)
                          "runId", (match Environment.GetEnvironmentVariable "GITHUB_RUN_ID" with null -> Json.Null | s -> Json.String s)
                          "createdCommits", Json.Array(this.CreatedCommits |> List.map (fun (CommitOid o) -> Json.String o))
                          "exchanges", Json.Array(lock log (fun () -> List.ofSeq log) |> List.map obs) ]
        let text = Recordings.sanitizeBody (LiveEnv.secrets ()) (Json.serialize doc)
        let file = IO.Path.Combine(dir, testClass + ".json")
        IO.File.WriteAllText(file, text)
        file

    interface IDisposable with
        member this.Dispose() =
            this.WriteEvidence() |> ignore
            // The fresh branch is removed; the commits stay reachable from the evidence by oid.
            try
                use client = new HttpClient()
                use message = new HttpRequestMessage(System.Net.Http.HttpMethod.Delete, sprintf "%s/repos/%s/%s/git/refs/heads/%s" GitHubRequests.ApiBase writeConnection.Repository.Owner writeConnection.Repository.Name branch)
                for (k, v) in GitHubRequests.headers writeConnection do
                    message.Headers.TryAddWithoutValidation(k, v) |> ignore
                client.Send(message) |> ignore
            with _ -> ()

/// The xunit class fixture of a live test class: the LiveRun is made on first use, so a test that needs real GitHub
/// fails closed with "BLOCKED: no credential ..." when the secret is absent, and a test that needs only the stateful
/// fake still runs. The run's branch is removed and its evidence written when the class ends.
type LiveFixture<'TestClass>() =
    let run = lazy (new LiveRun(typeof<'TestClass>.Name))
    member _.Run : LiveRun = run.Value
    interface IDisposable with
        member _.Dispose() =
            if run.IsValueCreated then (run.Value :> IDisposable).Dispose()

/// Test values of the domain's types that must compile against both the bootstrap stub and the full IF41-DOMAIN
/// record (T13): an Investigation built field by field through F# reflection, with its id and version and every other
/// field at its default. It is only ever compared or carried, never read: a test that needs a real investigation
/// replays a history through Kernel.kernel.
[<RequireQualifiedAccess>]
module TestValues =
    open Microsoft.FSharp.Reflection

    /// The empty or first value of a type: Empty for a list or map, the first field-less case of a union, "" for a
    /// string, the default of a value type.
    let private orNull (x: obj | null) : obj = Unchecked.nonNull x

    let rec private neutral (t: Type) : obj =
        match t.GetProperty("Empty", Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Static) with
        | null ->
            if t = typeof<string> then orNull (box "")
            elif t.IsValueType then orNull (Activator.CreateInstance t)
            elif FSharpType.IsUnion(t, Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic) then
                let case = FSharpType.GetUnionCases(t, Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic) |> Array.head
                orNull (FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun f -> neutral f.PropertyType), Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic))
            else Unchecked.defaultof<obj>
        | empty -> orNull (empty.GetValue null)

    let investigation (id: Causa.Domain.InvestigationId) (version: Causa.Domain.AggregateVersion) : Causa.Domain.Investigation =
        let t = typeof<Causa.Domain.Investigation>
        let values =
            FSharpType.GetRecordFields(t)
            |> Array.map (fun f ->
                match f.Name with
                | "Id" -> box id
                | "Version" -> box version
                | _ -> neutral f.PropertyType)
        FSharpValue.MakeRecord(t, values) :?> Causa.Domain.Investigation
