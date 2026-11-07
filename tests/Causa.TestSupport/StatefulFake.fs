// Owner: persistence. T18: the stateful GitHub fake, built from the recorded answer forms (PERSIST-025-O2, PF-013).
namespace Causa.TestSupport

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Causa.Persistence

/// One commit of the fake: its parent (real ancestry), the whole tree and the message (where the write marker lives).
type FakeCommit =
    { Oid: string
      Parent: string option
      Tree: Map<string, byte[]>
      Message: string
      CommittedAt: DateTimeOffset }

/// What the next matching request suffers instead of (or after) being answered normally.
type Fault =
    /// The request never leaves the tab: nothing is applied (GH-NETWORK-BEFORE).
    | FailBeforeSending
    /// The request is lost before GitHub applies it: nothing is written, the caller sees LostAfterSending.
    | LoseBeforeApplying
    /// GitHub applies the mutation and the answer is lost: the commit exists, the caller sees LostAfterSending.
    | LoseAfterApplying
    /// GitHub applies the mutation and the caller times out.
    | TimeOutAfterApplying
    /// GitHub answers with a recorded form instead (a secondary rate limit, a 502, ...), applying nothing.
    | AnswerWith of form: string
    /// Another writer commits an unrelated file just before this request is handled (the branch advances).
    | AdvanceFirst

/// Which recorded stale-head form the fake answers a moved head with: GitHub sends both (INV-22).
type StaleForm =
    | StaleData
    | ForbiddenMovedHead

type FakeState =
    { Commits: Map<string, FakeCommit>
      Branches: Map<string, string>
      /// Token -> may write. A token absent here is a bad credential (401).
      Tokens: Map<string, bool>
      /// Faults waiting for the next request they match: (mutationsOnly, fault), first first.
      Faults: (bool * Fault) list
      StaleForm: StaleForm
      Clock: DateTimeOffset
      /// Every request the fake received, in order (method, url; "Mutation" for createCommitOnBranch), with no credential.
      Received: (string * string) list
      Sequence: int }

/// The stateful GitHub fake: an Execute that answers the requests persistence builds, enforcing expectedHeadOid with
/// real commit ancestry, accepting commits from simulated other writers, and losing answers before or after applying
/// a mutation. Every refusal it sends is a recorded form of real GitHub (Recordings.all); it never invents one.
type StatefulFake(owner: string, name: string, initialBranch: string, readToken: string, writeToken: string) =
    let gate = obj ()

    static let gitBlobSha (bytes: byte[]) =
        let header = Encoding.ASCII.GetBytes(sprintf "blob %d\000" bytes.Length)
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    static let commitOid (parent: string option) (tree: Map<string, byte[]>) (message: string) (sequence: int) =
        let text =
            [ yield defaultArg parent ""
              for KeyValue(p, b) in tree -> p + ":" + gitBlobSha b
              yield message
              yield string sequence ]
            |> String.concat "\n"
        SHA1.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let root =
        let tree = Map.ofList [ "README.md", Encoding.UTF8.GetBytes "causa test repository\n" ]
        let oid = commitOid None tree "initial" 0
        { Oid = oid; Parent = None; Tree = tree; Message = "initial"; CommittedAt = DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero) }

    let mutable state =
        { Commits = Map.ofList [ root.Oid, root ]
          Branches = Map.ofList [ initialBranch, root.Oid ]
          Tokens = Map.ofList [ readToken, false; writeToken, true ]
          Faults = []
          StaleForm = ForbiddenMovedHead
          Clock = root.CommittedAt
          Received = []
          Sequence = 1 }

    let repoPrefix = sprintf "https://api.github.com/repos/%s/%s/" (Uri.EscapeDataString owner) (Uri.EscapeDataString name)

    let json = Json.serialize

    let answerJson (id: RequestId) (status: int) (body: Json) =
        Answered(id, status, [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-limit", "5000"; "x-ratelimit-remaining", "4999" ], json body)

    let recorded (id: RequestId) (form: string) = Recordings.outcome id (Recordings.form form)

    /// A recorded form with the oids of this exchange substituted (the body is otherwise exactly the recording).
    let recordedWith (id: RequestId) (form: string) (replacements: (string * string) list) =
        let r = Recordings.form form
        let body = replacements |> List.fold (fun (b: string) (a, c) -> b.Replace(a, c)) r.Body
        Answered(id, r.Status, r.Headers, body)

    let recordedOidA = "4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e"
    let recordedOidB = "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b"

    let rec ancestors (s: FakeState) (oid: string) : string list =
        match s.Commits.TryFind oid with
        | Some c -> oid :: (c.Parent |> Option.map (ancestors s) |> Option.defaultValue [])
        | None -> []

    let apply (s: FakeState) (branch: string) (message: string) (changes: (string * byte[] option) list) : FakeState * FakeCommit =
        let headOid = s.Branches[branch]
        let head = s.Commits[headOid]
        let tree =
            changes |> List.fold (fun t (p, b) -> match b with Some bytes -> Map.add p bytes t | None -> Map.remove p t) head.Tree
        let at = s.Clock.AddSeconds 1.0
        let oid = commitOid (Some headOid) tree message s.Sequence
        let c = { Oid = oid; Parent = Some headOid; Tree = tree; Message = message; CommittedAt = at }
        { s with Commits = Map.add oid c s.Commits; Branches = Map.add branch oid s.Branches; Clock = at; Sequence = s.Sequence + 1 }, c

    let tokenOf (request: HttpRequest) =
        request.Headers
        |> List.tryPick (fun (k, v) ->
            if String.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase) && v.StartsWith "Bearer " then Some(v.Substring 7) else None)

    let takeFault (isMutation: bool) (s: FakeState) =
        match s.Faults |> List.tryFindIndex (fun (mutationsOnly, _) -> isMutation || not mutationsOnly) with
        | Some i -> Some(snd s.Faults[i]), { s with Faults = List.removeAt i s.Faults }
        | None -> None, s

    let fileJson (path: string) (bytes: byte[]) =
        let sha = gitBlobSha bytes
        if bytes.Length > 1_000_000 then
            Json.Object [ "type", Json.String "file"; "encoding", Json.String "none"; "size", Json.Number(decimal bytes.Length); "name", Json.String(path.Split('/') |> Array.last); "path", Json.String path; "content", Json.String ""; "sha", Json.String sha ]
        else
            Json.Object [ "type", Json.String "file"; "encoding", Json.String "base64"; "size", Json.Number(decimal bytes.Length); "name", Json.String(path.Split('/') |> Array.last); "path", Json.String path; "content", Json.String(Convert.ToBase64String bytes); "sha", Json.String sha ]

    let query (url: string) (key: string) =
        let m = Regex.Match(url, "[?&]" + key + "=([^&]*)")
        if m.Success then Some(Uri.UnescapeDataString m.Groups[1].Value) else None

    let pathPart (url: string) = (url.Split('?').[0]).Substring(repoPrefix.Length)

    let read (s: FakeState) (request: HttpRequest) : HttpOutcome =
        let id = request.Id
        let url = request.Url
        if url.TrimEnd('/') + "/" = repoPrefix then
            let canWrite = tokenOf request |> Option.bind s.Tokens.TryFind |> Option.defaultValue false
            answerJson id 200 (Json.Object [ "full_name", Json.String(owner + "/" + name); "default_branch", Json.String initialBranch
                                             "permissions", Json.Object [ "admin", Json.Bool false; "maintain", Json.Bool false; "push", Json.Bool canWrite; "pull", Json.Bool true ] ])
        elif not (url.StartsWith repoPrefix) then recorded id "rest-404-connect-hidden"
        else
            let rest = pathPart url
            let segments = rest.Split('/') |> Array.map Uri.UnescapeDataString |> List.ofArray
            match segments with
            | "git" :: "ref" :: "heads" :: branchParts ->
                let branch = String.Join("/", branchParts)
                match s.Branches.TryFind branch with
                | Some oid ->
                    answerJson id 200 (Json.Object [ "ref", Json.String("refs/heads/" + branch); "object", Json.Object [ "sha", Json.String oid; "type", Json.String "commit" ] ])
                | None -> recorded id "rest-404-branch"
            | "contents" :: pathParts ->
                let path = String.Join("/", pathParts)
                let reference = query url "ref" |> Option.defaultValue s.Branches[initialBranch]
                let commit = s.Commits.TryFind reference |> Option.orElse (s.Branches.TryFind reference |> Option.bind s.Commits.TryFind)
                match commit with
                | None -> recorded id "rest-404-file"
                | Some c ->
                    match c.Tree.TryFind path with
                    | Some bytes -> answerJson id 200 (fileJson path bytes)
                    | None ->
                        let prefix = path.TrimEnd('/') + "/"
                        let children =
                            c.Tree
                            |> Map.toList
                            |> List.choose (fun (p, _) -> if p.StartsWith prefix then Some(p.Substring(prefix.Length).Split('/')) else None)
                            |> List.map (fun parts -> parts[0], (if parts.Length > 1 then "dir" else "file"))
                            |> List.distinct
                        if List.isEmpty children then recorded id "rest-404-file"
                        else answerJson id 200 (Json.Array(children |> List.map (fun (n, k) -> Json.Object [ "name", Json.String n; "type", Json.String k; "path", Json.String(prefix + n) ])))
            | [ "git"; "blobs"; sha ] ->
                let blob = s.Commits |> Map.toSeq |> Seq.collect (fun (_, c) -> c.Tree |> Map.toSeq |> Seq.map snd) |> Seq.tryFind (fun b -> gitBlobSha b = sha)
                match blob with
                | Some bytes -> answerJson id 200 (Json.Object [ "sha", Json.String sha; "size", Json.Number(decimal bytes.Length); "encoding", Json.String "base64"; "content", Json.String(Convert.ToBase64String bytes) ])
                | None -> recorded id "rest-404-file"
            | [ "commits"; oid ] ->
                match s.Commits.TryFind oid with
                | Some c -> answerJson id 200 (Json.Object [ "sha", Json.String c.Oid; "commit", Json.Object [ "message", Json.String c.Message ]; "parents", Json.Array(c.Parent |> Option.toList |> List.map (fun p -> Json.Object [ "sha", Json.String p ])) ])
                | None -> recorded id "rest-422-missing-branch"
            | [ "compare"; range ] ->
                let parts = range.Split("...")
                let baseOid, headRef = parts[0], parts[1]
                let headOid = s.Branches.TryFind headRef |> Option.defaultValue headRef
                if not (s.Commits.ContainsKey baseOid) || not (s.Commits.ContainsKey headOid) then recordedWith id "rest-compare-404" []
                else
                    let headLine = ancestors s headOid
                    match List.tryFindIndex ((=) baseOid) headLine with
                    | None ->
                        let ahead = headLine |> List.takeWhile (fun o -> not (List.contains o (ancestors s baseOid)))
                        answerJson id 200 (Json.Object [ "status", Json.String "diverged"; "ahead_by", Json.Number(decimal ahead.Length); "behind_by", Json.Number 1M; "total_commits", Json.Number(decimal ahead.Length); "commits", Json.Array [] ])
                    | Some i ->
                        let after = headLine |> List.take i |> List.rev
                        let baseTree, headTree = s.Commits[baseOid].Tree, s.Commits[headOid].Tree
                        let changed =
                            Seq.append (Map.keys baseTree) (Map.keys headTree)
                            |> Seq.distinct
                            |> Seq.filter (fun p -> baseTree.TryFind p <> headTree.TryFind p)
                            |> Seq.map (fun p -> Json.Object [ "filename", Json.String p ])
                            |> List.ofSeq
                        let perPage = query url "per_page" |> Option.map int |> Option.defaultValue 250 |> min 250
                        let page = query url "page" |> Option.map int |> Option.defaultValue 1 |> max 1
                        let listed = after |> List.skip (min after.Length ((page - 1) * perPage)) |> List.truncate perPage
                        answerJson id 200 (Json.Object [ "status", Json.String(if after.IsEmpty then "identical" else "ahead"); "ahead_by", Json.Number(decimal after.Length); "behind_by", Json.Number 0M
                                                         "total_commits", Json.Number(decimal after.Length)
                                                         "files", Json.Array changed
                                                         "commits", Json.Array(listed |> List.map (fun o -> Json.Object [ "sha", Json.String o; "commit", Json.Object [ "message", Json.String s.Commits[o].Message ] ])) ])
            | _ -> recorded id "rest-404-file"

    let mutate (s: FakeState) (request: HttpRequest) : FakeState * HttpOutcome * bool =
        let id = request.Id
        let input = request.Body |> Option.bind Json.parse |> Json.path [ "variables"; "input" ]
        let branch = input |> Json.path [ "branch"; "branchName" ] |> Json.text
        let expected = input |> Json.field "expectedHeadOid" |> Json.text
        let headline = input |> Json.path [ "message"; "headline" ] |> Json.text |> Option.defaultValue ""
        let body = input |> Json.path [ "message"; "body" ] |> Json.text
        let message = match body with Some b -> headline + "\n\n" + b | None -> headline
        let changes =
            [ for a in Json.items (Json.path [ "fileChanges"; "additions" ] input) do
                  match Json.text (Json.field "path" (Some a)), Json.text (Json.field "contents" (Some a)) with
                  | Some p, Some c -> yield p, Some(Convert.FromBase64String c)
                  | _ -> ()
              for d in Json.items (Json.path [ "fileChanges"; "deletions" ] input) do
                  match Json.text (Json.field "path" (Some d)) with
                  | Some p -> yield p, None
                  | None -> () ]
        match branch, expected with
        | None, _
        | _, None -> s, recorded id "graphql-unprocessable", false
        | Some b, Some e ->
            match s.Branches.TryFind b with
            | None -> s, recorded id "graphql-not-found-branch", false
            | Some head when head <> e ->
                let outcome =
                    match s.StaleForm with
                    | StaleData -> recordedWith id "graphql-stale-data" [ recordedOidB, e ]
                    | ForbiddenMovedHead -> recordedWith id "graphql-forbidden-moved-head" [ recordedOidA, head; recordedOidB, e; "causa-test", b ]
                s, outcome, false
            | Some _ ->
                let head = s.Commits[s.Branches[b]]
                let missing = changes |> List.exists (fun (p, bytes) -> bytes.IsNone && not (head.Tree.ContainsKey p))
                if missing || List.isEmpty changes then s, recorded id "graphql-unprocessable", false
                else
                    let s2, c = apply s b message changes
                    s2, recordedWith id "graphql-commit-created" [ recordedOidA, c.Oid; "2026-11-07T17:00:00Z", c.CommittedAt.ToString("yyyy-MM-ddTHH:mm:ssZ") ], true

    /// GraphQL reads persistence sends (files at a commit; head and files of a branch), answered from the fake's trees.
    let graphQlRead (s: FakeState) (request: HttpRequest) : HttpOutcome =
        let id = request.Id
        let query = request.Body |> Option.bind Json.parse |> Json.field "query" |> Json.text |> Option.defaultValue ""
        let blob (bytes: byte[] option) =
            match bytes with
            | Some b -> Json.Object [ "text", Json.String(Encoding.UTF8.GetString b); "isTruncated", Json.Bool false ]
            | None -> Json.Null
        let ref' = Regex.Match(query, "ref\\(qualifiedName: \"refs/heads/([^\"]+)\"\\)")
        if ref'.Success then
            match s.Branches.TryFind ref'.Groups[1].Value with
            | None -> answerJson id 200 (Json.Object [ "data", Json.Object [ "repository", Json.Object [ "ref", Json.Null ] ] ])
            | Some oid ->
                let c = s.Commits[oid]
                let files =
                    [ for m in Regex.Matches(query, "(\\w+): file\\(path: \"([^\"]+)\"\\)") ->
                        let path = m.Groups[2].Value
                        m.Groups[1].Value,
                        (match c.Tree.TryFind path with
                         | Some b -> Json.Object [ "path", Json.String path; "object", blob (Some b) ]
                         | None -> Json.Null) ]
                answerJson id 200 (Json.Object [ "data", Json.Object [ "repository", Json.Object [ "ref", Json.Object [ "target", Json.Object([ "oid", Json.String oid; "committedDate", Json.String(c.CommittedAt.ToString "yyyy-MM-ddTHH:mm:ssZ") ] @ files) ] ] ] ])
        else
            let commit = Regex.Match(query, "c: object\\(oid: \"([0-9a-f]+)\"\\)")
            let c = if commit.Success then s.Commits.TryFind commit.Groups[1].Value else None
            let blobs =
                [ for m in Regex.Matches(query, "(\\w+): object\\(expression: \"([0-9a-f]+):([^\"]+)\"\\)") ->
                    m.Groups[1].Value, blob (s.Commits.TryFind m.Groups[2].Value |> Option.bind (fun c -> c.Tree.TryFind m.Groups[3].Value)) ]
            let cJson =
                match c with
                | Some c -> Json.Object [ "oid", Json.String c.Oid; "committedDate", Json.String(c.CommittedAt.ToString "yyyy-MM-ddTHH:mm:ssZ") ]
                | None -> Json.Null
            answerJson id 200 (Json.Object [ "data", Json.Object [ "repository", Json.Object(("c", cJson) :: blobs) ] ])

    let execute (request: HttpRequest) : HttpOutcome =
        lock gate (fun () ->
            let isMutation = GitHubRequests.isMutation request
            let fault, s1' = takeFault isMutation state
            // A request that fails before sending never reaches GitHub, so GitHub never receives it.
            let s1 =
                match fault with
                | Some FailBeforeSending -> s1'
                | _ -> { s1' with Received = s1'.Received @ [ (if isMutation then "Mutation" else string request.Method), request.Url ] }
            let token = tokenOf request
            let outcome, s2 =
                match fault with
                | Some FailBeforeSending -> NotAnswered(request.Id, TransportFailure.FailedBeforeSending), s1
                | Some LoseBeforeApplying -> NotAnswered(request.Id, TransportFailure.LostAfterSending), s1
                | Some(AnswerWith form) -> recorded request.Id form, s1
                | Some AdvanceFirst when isMutation ->
                    let s2, _ = apply s1 initialBranch (sprintf "other writer %d" s1.Sequence) [ sprintf "workspaces/demo/other/f%d.json" s1.Sequence, Some(Encoding.UTF8.GetBytes "{}") ]
                    let s3, answered, _ = mutate s2 request
                    answered, s3
                | _ ->
                    match token |> Option.bind s1.Tokens.TryFind with
                    | None -> (if isMutation then recorded request.Id "graphql-401-bad-credentials" else recorded request.Id "rest-401-bad-credentials"), s1
                    | Some false when isMutation -> recorded request.Id "graphql-forbidden-permission", s1
                    | Some _ when isMutation ->
                        let s3, answered, _ = mutate s1 request
                        match fault with
                        | Some LoseAfterApplying -> NotAnswered(request.Id, TransportFailure.LostAfterSending), s3
                        | Some TimeOutAfterApplying -> NotAnswered(request.Id, TransportFailure.TimedOut), s3
                        | _ -> answered, s3
                    | Some _ when request.Url = GitHubRequests.GraphQlUrl -> graphQlRead s1 request, s1
                    | Some _ -> read s1 request, s1
            state <- s2
            outcome)

    /// A fake with one branch `causa-test`, a read token and a write token.
    new() = StatefulFake("causa-test-owner", "causa-test-data", "causa-test", "fake-read-token", "fake-write-token")

    member _.Owner = owner
    member _.Name = name
    member _.Branch = initialBranch
    member _.Repository : Repository = { Owner = owner; Name = name; Branch = initialBranch }
    member _.ReadToken = readToken
    member _.WriteToken = writeToken
    member this.Connection(token: string) : ConnectionContext = { Repository = this.Repository; Credential = Credential.create token }
    member this.ReadConnection = this.Connection readToken
    member this.WriteConnection = this.Connection writeToken

    /// The host operation of the fake (IF41-WIRE Execute).
    member _.Execute : Execute = execute

    member _.State = lock gate (fun () -> state)

    member _.Head(?branch: string) = lock gate (fun () -> CommitOid state.Branches[defaultArg branch initialBranch])

    member _.FileAt(CommitOid commit, RepoPath path) : byte[] option =
        lock gate (fun () -> state.Commits.TryFind commit |> Option.bind (fun c -> c.Tree.TryFind path))

    member _.Commit(CommitOid oid) = lock gate (fun () -> state.Commits.TryFind oid)

    /// Whether `ancestor` is reachable from `descendant` by parent links (real ancestry).
    member _.IsAncestor(CommitOid ancestor, CommitOid descendant) = lock gate (fun () -> List.contains ancestor (ancestors state descendant))

    /// Another writer commits to the branch directly (a simulated concurrent writer); returns its commit.
    member _.CommitAsOtherWriter(message: string, changes: (string * byte[] option) list, ?branch: string) : CommitOid =
        lock gate (fun () ->
            let s, c = apply state (defaultArg branch initialBranch) message changes
            state <- s
            CommitOid c.Oid)

    /// Rewrite history: the branch is reset to a new root, so the old head is no longer reachable.
    member _.RewriteHistory(?branch: string) : CommitOid =
        lock gate (fun () ->
            let tree = Map.ofList [ "README.md", Encoding.UTF8.GetBytes "rewritten\n" ]
            let oid = commitOid None tree "rewritten" state.Sequence
            let c = { Oid = oid; Parent = None; Tree = tree; Message = "rewritten"; CommittedAt = state.Clock.AddSeconds 1.0 }
            state <- { state with Commits = Map.add oid c state.Commits; Branches = Map.add (defaultArg branch initialBranch) oid state.Branches; Sequence = state.Sequence + 1 }
            CommitOid oid)

    member _.CreateBranch(newBranch: string, CommitOid fromOid) =
        lock gate (fun () -> state <- { state with Branches = Map.add newBranch fromOid state.Branches })

    /// The next request (or the next mutation, when mutationsOnly) suffers `fault`.
    member _.Inject(fault: Fault, ?mutationsOnly: bool) =
        lock gate (fun () -> state <- { state with Faults = state.Faults @ [ defaultArg mutationsOnly true, fault ] })

    member _.UseStaleForm(form: StaleForm) = lock gate (fun () -> state <- { state with StaleForm = form })

    member _.RevokeToken(token: string) = lock gate (fun () -> state <- { state with Tokens = Map.remove token state.Tokens })

    member _.GitBlobSha(bytes: byte[]) = gitBlobSha bytes

/// The provider-conformance probes (PF-011-O3, PF-013-O2, PERSIST-008-O4): one script of GitHub behaviours run through
/// the provider port over any adapter. Run over the stateful fake and over real GitHub, the two transcripts must be
/// equal; a difference is a fake/real divergence.
[<RequireQualifiedAccess>]
module Conformance =

    /// The cause a result names, by typed case (never by message text).
    let kind (result: Result<'a, ProviderError>) : string =
        match result with
        | Ok _ -> "ok"
        | Error AuthenticationFailed -> "AuthenticationFailed"
        | Error(PermissionDenied _) -> "PermissionDenied"
        | Error(RateLimited(Primary _)) -> "RateLimited.Primary"
        | Error(RateLimited(Secondary _)) -> "RateLimited.Secondary"
        | Error(StaleHead _) -> "StaleHead"
        | Error(ValidationFailed _) -> "ValidationFailed"
        | Error(NotFound _) -> "NotFound"
        | Error(HistoryRewritten _) -> "HistoryRewritten"
        | Error(NetworkUnavailable f) -> sprintf "NetworkUnavailable.%A" f
        | Error(Malformed _) -> "Malformed"
        | Error(ReadBackMismatch _) -> "ReadBackMismatch"
        | Error(Unrecognised s) -> sprintf "Unrecognised.%d" s

    /// The three ports a probe run needs over one adapter: the write credential, the read credential and a credential
    /// GitHub does not accept, all on the same branch; and the prefix the run may write under.
    type Adapter =
        { Mode: string
          Writer: ProviderPort
          Reader: ProviderPort
          BadCredential: ProviderPort
          MissingBranch: ProviderPort
          Prefix: RepoPath }

    type Probe = { Name: string; Expected: string; Actual: string }

    let private file (RepoPath prefix) (name: string) = RepoPath(prefix + "/" + name)

    /// The probe script. Every commit it makes is reported through `created`.
    let run (adapter: Adapter) (created: CommitOid -> unit) : Probe list =
        let probe name expected actual = { Name = name; Expected = expected; Actual = actual }
        let bytes (s: string) = Text.Encoding.UTF8.GetBytes s
        let head0 = adapter.Reader.ReadHead()
        let first =
            match head0 with
            | Ok h -> adapter.Writer.Commit h adapter.Prefix "causa T18 probe: first" [ PutFile(file adapter.Prefix "probe.json", bytes "{\"probe\":1}") ]
            | Error e -> Error e
        first |> Result.iter (fun c -> created c.Oid)
        let stale =
            match head0 with
            | Ok h -> adapter.Writer.Commit h adapter.Prefix "causa T18 probe: stale" [ PutFile(file adapter.Prefix "stale.json", bytes "{\"stale\":true}") ]
            | Error e -> Error e
        stale |> Result.iter (fun c -> created c.Oid)
        let head1 = adapter.Reader.ReadHead()
        let staleWroteNothing =
            match head1, first with
            | Ok h, Ok c when h = c.Oid -> adapter.Reader.ReadFile h (file adapter.Prefix "stale.json") |> kind
            | other -> sprintf "head moved unexpectedly: %A" (fst other |> kind)
        let readBack =
            match head1 with
            | Ok h -> adapter.Reader.ReadFile h (file adapter.Prefix "probe.json") |> Result.map (fun b -> Text.Encoding.UTF8.GetString b = "{\"probe\":1}")
            | Error e -> Error e
        let permission =
            match head1 with
            | Ok h -> adapter.Reader.Commit h adapter.Prefix "causa T18 probe: read token" [ PutFile(file adapter.Prefix "denied.json", bytes "{}") ]
            | Error e -> Error e
        permission |> Result.iter (fun c -> created c.Oid)
        let validation =
            match head1 with
            | Ok h -> adapter.Writer.Commit h adapter.Prefix "causa T18 probe: delete missing" [ DeleteFile(file adapter.Prefix "never-existed.json") ]
            | Error e -> Error e
        validation |> Result.iter (fun c -> created c.Oid)
        let outside =
            match head1 with
            | Ok h -> adapter.Writer.Commit h adapter.Prefix "causa T18 probe: outside" [ PutFile(RepoPath "README.md", bytes "x") ]
            | Error e -> Error e
        let compare =
            match head0, head1 with
            | Ok h0, Ok h1 -> adapter.Reader.Compare h0 h1 1 |> Result.map (fun p -> p.Status, p.Commits |> List.map (fun c -> c.Oid) |> List.contains h1)
            | Error e, _
            | _, Error e -> Error e
        [ probe "read head with the read credential" "ok" (kind head0)
          probe "read head with a bad credential" "AuthenticationFailed" (kind (adapter.BadCredential.ReadHead()))
          probe "read a missing branch" "NotFound" (kind (adapter.MissingBranch.ReadHead()))
          probe "commit at the current head" "ok" (kind first)
          probe "commit at a moved head" "StaleHead" (kind stale)
          probe "a stale commit wrote nothing" "NotFound" staleWroteNothing
          probe "read back the committed bytes" "ok:true" (match readBack with Ok b -> sprintf "ok:%b" b | e -> kind e)
          probe "read a missing file at a commit" "NotFound" (match head1 with Ok h -> kind (adapter.Reader.ReadFile h (file adapter.Prefix "missing.json")) | e -> kind e)
          probe "commit with the read credential" "PermissionDenied" (kind permission)
          probe "delete a file that does not exist" "ValidationFailed" (kind validation)
          probe "a path outside the prefix is refused before sending" "ValidationFailed" (kind outside)
          probe "compare the expected head with the new head" "ok:ahead:true" (match compare with Ok(s, has) -> sprintf "ok:%s:%b" s has | e -> kind e) ]

    /// The adapter over a stateful fake.
    let overFake (fake: StatefulFake) (prefix: RepoPath) : Adapter =
        let on (c: ConnectionContext) branch = { c with Repository = { c.Repository with Branch = branch } }
        { Mode = "fake"
          Writer = Provider.over fake.Execute fake.WriteConnection
          Reader = Provider.over fake.Execute fake.ReadConnection
          BadCredential = Provider.over fake.Execute (fake.Connection "not-a-token")
          MissingBranch = Provider.over fake.Execute (on fake.ReadConnection "causa-t18-no-such-branch")
          Prefix = prefix }

    /// The adapter over a live run (real GitHub on the run's fresh branch).
    let overLive (live: LiveRun) : Adapter =
        let on (c: ConnectionContext) branch = { c with Repository = { c.Repository with Branch = branch } }
        { Mode = "live-github"
          Writer = live.Writer
          Reader = live.Reader
          BadCredential = Provider.over live.Execute { live.ReadConnection with Credential = Credential.create "causa-t18-not-a-token" }
          MissingBranch = Provider.over live.Execute (on live.ReadConnection (live.Branch + "-no-such-branch"))
          Prefix = live.Prefix }

    /// Every probe whose result differs between the two transcripts (fake/real divergence, PF-013-O2).
    let divergence (fake: Probe list) (real: Probe list) : (string * string * string) list =
        List.zip fake real |> List.filter (fun (f, r) -> f.Actual <> r.Actual) |> List.map (fun (f, r) -> f.Name, f.Actual, r.Actual)

    /// A probe transcript that matched its expectations, or the probes that did not.
    let failures (probes: Probe list) = probes |> List.filter (fun p -> p.Actual <> p.Expected)

    /// A conformance result. Only a live-github run over real network evidence is live verification; a fake-only
    /// pass never is (PERSIST-025-O4, PF-014-O4, PF-020).
    type Report =
        { Mode: string
          Probes: Probe list
          CreatedCommits: CommitOid list }
        member r.Passed = List.isEmpty (failures r.Probes)
        member r.IsLiveVerification = r.Mode = "live-github" && r.Passed && not (List.isEmpty r.CreatedCommits)

/// A deterministic test rig for the causa-storage save machine over the stateful fake: it plays the engine's part
/// (request identities, the base each session rests on, executing Sends and feeding answers back) so a test drives
/// one save request end to end and inspects every request sent (PAT-02..PAT-10 against the fake).
type SaveRig(fake: StatefulFake, run: string) =
    let actor = Causa.Domain.Actor.Human(Causa.Domain.NonEmptyText.create "investigator-01" |> Option.get)
    let mutable nextId = 1000L
    let mutable bases : Map<Causa.Domain.InvestigationId, VerifiedInvestigation> = Map.empty
    let mutable now = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero)
    let mutable scheduled : int list = []
    let mutable announced : string list = []
    let random = Random(18)
    let address = WorkspaceAddress.workingCopy [] run |> Option.get

    let machine () = SaveMachines.withStorage actor (fun s -> bases.TryFind s.Id)

    let ctx () : RequestContext = { Connection = fake.WriteConnection; NextId = RequestId nextId; MutationInFlightElsewhere = false; Now = now }

    let text (s: string) = Causa.Domain.NonEmptyText.create s |> Option.get

    let rec settle (s: InvestigationSession) (effects: SaveEffect list) : InvestigationSession =
        let mutable session = s
        for e in effects do
            match e with
            | ScheduleReconcileRead(_, ms) -> scheduled <- scheduled @ [ ms ]
            | Announce(_, t) -> announced <- announced @ [ t ]
            | ExportCommands _ -> ()
            | Send request ->
                nextId <- nextId + 1L
                session <- answer session request
        session

    and answer (s: InvestigationSession) (request: HttpRequest) : InvestigationSession =
        let outcome = fake.Execute request
        match s.State with
        | SaveState.Conflict(ReadingRemote rid) when rid = request.Id ->
            match (machine ()).InterpretConflict (ctx ()) (ReadingRemote rid) outcome with
            | ConflictEvent input ->
                match (machine ()).StepConflict (ctx ()) (ReadingRemote rid) input with
                | Ok(view, effects) -> settle { s with State = SaveState.Conflict view } effects
                | Error _ -> s
            | ConflictNextRequest(view, next) ->
                nextId <- nextId + 1L
                answer { s with State = SaveState.Conflict view } next
        | _ ->
            match (machine ()).Interpret (ctx ()) s outcome with
            | MachineEvent input -> step s input
            | NextRequest(s', next) ->
                nextId <- nextId + 1L
                answer s' next

    and step (s: InvestigationSession) (input: SaveInput) : InvestigationSession =
        match (machine ()).Step (ctx ()) s input with
        | Ok(s', effects) -> settle s' effects
        | Error f -> failwithf "Step refused %s/%s: %s" f.State f.Input f.Reason

    member _.Fake = fake
    member _.Address = address
    member _.Writable = Writable.ofAddress address |> Option.get
    member _.Actor = actor
    member _.Machine = machine ()
    member _.Scheduled = scheduled
    member _.Announced = announced
    member _.Now with get () = now and set v = now <- v
    member _.Context() = ctx ()

    /// A command envelope with a UUIDv7 id carrying the current time.
    member _.Command (investigation: Causa.Domain.InvestigationId) (command: Causa.Domain.CoreCommand) : Causa.Domain.CommandEnvelope =
        now <- now.AddMilliseconds 7.0
        let bytes = Array.zeroCreate<byte> 16
        random.NextBytes bytes
        { CommandId = Writes.commandIdAt now bytes; Investigation = investigation; ExpectedVersion = None; Command = Causa.Domain.Core command }

    /// A new investigation's session at the fake's head, holding its StartInvestigation command.
    member this.Create (title: string) (problem: string) : InvestigationSession =
        let inv = Causa.Domain.InvestigationId(Guid(Array.init 16 (fun _ -> byte (random.Next 256))))
        let start = this.Command inv (Causa.Domain.StartInvestigation { Title = text title; ObservationId = Guid.NewGuid(); Original = text problem })
        (machine ()).InitialNew this.Writable inv (fake.Head()) start true

    member this.Observe (s: InvestigationSession) (observation: string) : InvestigationSession =
        let cmd = this.Command s.Id (Causa.Domain.RecordObservation { ObservationId = Guid.NewGuid(); Original = text observation; ObservedAt = None; Source = None })
        step s (SaveInput.Edit cmd)

    member _.Step (s: InvestigationSession) (input: SaveInput) = step s input

    /// Step without settling the effects (to inject a fault before the request is sent).
    member _.StepRaw (s: InvestigationSession) (input: SaveInput) = (machine ()).Step (ctx ()) s input

    member _.Settle (s: InvestigationSession) (effects: SaveEffect list) = settle s effects

    member _.Due(s: InvestigationSession) : InvestigationSession =
        match (machine ()).ReconcileDue (ctx ()) s with
        | Ok(s', effects) -> settle s' effects
        | Error f -> failwithf "ReconcileDue refused: %s" f.Reason

    /// Loads an investigation from the fake as a fresh tab would, and records it as the base its session rests on.
    member _.Load(target: InvestigationRef) : Result<VerifiedInvestigation, LoadFailure> =
        let m = machine ()
        match m.StepLoad (ctx ()) NotLoaded (LoadInput.OpenRequested target) with
        | Ok(loading, [ Send request ]) ->
            nextId <- nextId + 1L
            match m.InterpretLoad (ctx ()) loading (fake.Execute request) with
            | LoadEvent(LoadInput.LoadVerified v) ->
                bases <- Map.add v.Investigation.Id v bases
                Ok v
            | LoadEvent(LoadInput.LoadRejected f) -> Error f
            | other -> failwithf "unexpected load answer %A" other
        | other -> failwithf "unexpected load step %A" other

    /// After a confirmed save, the engine rests the session on what GitHub holds: reload it as the base.
    member this.Rebase(s: InvestigationSession) : VerifiedInvestigation =
        match this.Load { Address = address; Id = s.Id } with
        | Ok v -> v
        | Error f -> failwithf "reload failed: %A" f

    /// The mutations the fake received (createCommitOnBranch), in order.
    member _.Mutations = fake.State.Received |> List.filter (fun (m, _) -> m = "Mutation") |> List.length
