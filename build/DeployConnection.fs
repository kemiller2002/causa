// Owner: integration (T20). The deployed test build's injected connection (EXT-DATA-004-O5, ODR-COMP-P13 A;
// Indy-init docs/v416-competition-decisions/TEST-DEPLOY-KEY.md sections 2, 4 and 5). Shared by build.fsx (#load) and
// Causa.App.Tests (DeployConnectionTests). Pure: the environment and the files come in as values.
//
// Only the deploy build (CAUSA_DEPLOY_CONNECTION=1, set only in deploy-pages.yml's build step) reads the four secrets.
// With none set, deploy-connection.json holds no connection; with all four, the one connection; with only some, the
// build fails naming the missing secrets. Every message names a secret or a file, never a value.
module Causa.Build.DeployConnection

open System
open System.Text
open System.Text.RegularExpressions

[<Literal>]
let Schema = "causa.deploy-connection/1"

[<Literal>]
let FileName = "deploy-connection.json"

let secretNames = [ "REPO_OWNER"; "DATA_SOURCE"; "CAUSA_BRANCH"; "DATA_ACCESS_KEY_CAUSA" ]

/// The injected connection. The key never prints: ToString shows only where it connects.
[<CustomEquality; NoComparison>]
type Settings =
    { Owner: string
      Repository: string
      Branch: string
      Key: string }
    override s.ToString() = sprintf "%s/%s@%s (key withheld)" s.Owner s.Repository s.Branch
    override s.Equals(o) =
        match o with
        | :? Settings as t -> s.Owner = t.Owner && s.Repository = t.Repository && s.Branch = t.Branch && s.Key = t.Key
        | _ -> false
    override s.GetHashCode() = hash (s.Owner, s.Repository, s.Branch)

/// A GitHub token literal (classic, fine-grained, OAuth, user-to-server, server-to-server, refresh).
let tokenPattern = Regex("gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled)

let private namePattern = Regex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,99})$")
let private branchPattern = Regex("^[A-Za-z0-9._/-]{1,250}$")

/// Is this the deploy build? Only `CAUSA_DEPLOY_CONNECTION=1` makes it one.
let enabled (env: string -> string option) = env "CAUSA_DEPLOY_CONNECTION" = Some "1"

/// The connection from the four secrets. Ok None: none is set. Error: some are missing, or one is malformed; the
/// message names the secret, never its value.
let settings (env: string -> string option) : Result<Settings option, string> =
    let value name = env name |> Option.map (fun v -> v.Trim()) |> Option.filter (String.IsNullOrEmpty >> not)
    let present = secretNames |> List.filter (value >> Option.isSome)
    match present with
    | [] -> Ok None
    | _ when present.Length < secretNames.Length ->
        let missing = secretNames |> List.filter (fun n -> not (List.contains n present))
        Error(sprintf "the deploy connection needs all four secrets; missing: %s" (String.Join(", ", missing)))
    | _ ->
        let get n = (value n).Value
        let malformed =
            [ if not (namePattern.IsMatch(get "REPO_OWNER")) then yield "REPO_OWNER"
              if not (namePattern.IsMatch(get "DATA_SOURCE")) then yield "DATA_SOURCE"
              let branch = get "CAUSA_BRANCH"
              if not (branchPattern.IsMatch branch) || branch.Contains ".." || branch.StartsWith "/" || branch.EndsWith "/" then yield "CAUSA_BRANCH"
              let key = get "DATA_ACCESS_KEY_CAUSA"
              if not (tokenPattern.IsMatch key) || tokenPattern.Match(key).Value <> key then yield "DATA_ACCESS_KEY_CAUSA" ]
        match malformed with
        | [] -> Ok(Some { Owner = get "REPO_OWNER"; Repository = get "DATA_SOURCE"; Branch = get "CAUSA_BRANCH"; Key = get "DATA_ACCESS_KEY_CAUSA" })
        | names -> Error(sprintf "malformed secret: %s" (String.Join(", ", names)))

let private quote (s: string) =
    let b = StringBuilder("\"")
    for c in s do
        match c with
        | '"' -> b.Append "\\\"" |> ignore
        | '\\' -> b.Append "\\\\" |> ignore
        | c when c < ' ' -> b.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> b.Append c |> ignore
    b.Append('"').ToString()

/// The file the application reads: the schema, and the connection only when there is one.
let render (s: Settings option) : string =
    match s with
    | None -> sprintf "{\"schema\":%s}\n" (quote Schema)
    | Some c ->
        sprintf
            "{\"schema\":%s,\"connection\":{\"owner\":%s,\"repository\":%s,\"branch\":%s,\"key\":%s}}\n"
            (quote Schema) (quote c.Owner) (quote c.Repository) (quote c.Branch) (quote c.Key)

/// One credential finding: the file and what is wrong, never the value.
type Finding = { File: string; Reason: string }

/// Scans a built site (artifact-relative paths with '/', and their bytes) for credentials. Without a deploy key no
/// credential may appear anywhere and no file may hold a connection. With one, it may appear exactly once, in the
/// root deploy-connection.json, for the configured repository, and nowhere else; no other token anywhere.
let scan (files: (string * byte[]) list) (deploy: Settings option) : Finding list =
    let text (bytes: byte[]) = Encoding.Latin1.GetString bytes
    [ for path, bytes in files do
          let content = text bytes
          let isConfig = path = FileName
          let named = path.EndsWith("/" + FileName) || path = FileName
          let tokens = [ for m in tokenPattern.Matches content -> m.Value ]
          match deploy with
          | None ->
              if not tokens.IsEmpty then yield { File = path; Reason = "a GitHub token literal is in the built site" }
              if named && content.Contains "\"connection\"" then
                  yield { File = path; Reason = "a connection in a build without the deploy secrets" }
          | Some s ->
              if named && not isConfig then yield { File = path; Reason = sprintf "%s is not at the artifact root" FileName }
              let others = tokens |> List.filter (fun t -> t <> s.Key)
              if not others.IsEmpty then yield { File = path; Reason = "a GitHub token literal other than the deploy key" }
              let keyCount = Regex.Matches(content, Regex.Escape s.Key).Count
              if keyCount > 0 && not isConfig then yield { File = path; Reason = "the deploy key is outside deploy-connection.json" }
              if isConfig then
                  if keyCount > 1 then yield { File = path; Reason = "the deploy key appears more than once" }
                  if content <> render (Some s) then yield { File = path; Reason = "the connection is not exactly the configured repository and key" } ]
