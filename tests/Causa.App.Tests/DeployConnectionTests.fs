// Owner: integration (T20). EXT-DATA-004-O5 (ODR-COMP-P13 A; TEST-DEPLOY-KEY sections 4 and 5): generating the deploy
// connection, scanning the built site, and the application reading it.
namespace Causa.App.Tests

open System
open System.Text
open System.Text.Json
open Xunit
open Causa.Build
open Causa.Build.DeployConnection

module private Deploy =
    // Synthetic key shapes only: they match the token pattern and are no one's credential.
    let key = "github_pat_" + String('A', 22) + "_" + String('b', 59)
    let otherKey = "ghp_" + String('Z', 36)

    let env (pairs: (string * string) list) : string -> string option =
        fun name -> pairs |> List.tryFind (fst >> (=) name) |> Option.map snd

    let all =
        [ "CAUSA_DEPLOY_CONNECTION", "1"; "REPO_OWNER", "causa-tests"; "DATA_SOURCE", "causa-data-test"; "CAUSA_BRANCH", "competition"
          "DATA_ACCESS_KEY_CAUSA", key ]

    let settings = { Owner = "causa-tests"; Repository = "causa-data-test"; Branch = "competition"; Key = key }

    let bytes (s: string) = Encoding.UTF8.GetBytes s

    let site (extra: (string * string) list) =
        [ "index.html", "<!doctype html><title>Causa</title>"; "boundary.js", "export {}" ] @ extra
        |> List.map (fun (p, c) -> p, bytes c)

    let location = "{\"origin\":\"https://example.test\",\"path\":\"/causa/\",\"query\":\"\",\"hash\":\"\"}"
    let initialize = sprintf "{\"kind\":\"Initialize\",\"protocolVersion\":1,\"capabilities\":[],\"location\":%s}" location

    let effects (answer: string) = JsonDocument.Parse(answer).RootElement.GetProperty("effects").EnumerateArray() |> List.ofSeq
    let str (name: string) (e: JsonElement) : string = match e.TryGetProperty name with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""

    let httpResult (correlation: string) (status: int) (body: string) =
        sprintf
            "{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"HttpResult\",\"correlationId\":\"%s\",\"outcome\":{\"kind\":\"Success\",\"status\":%d,\"body\":%s}}}"
            correlation status (JsonSerializer.Serialize body)

type DeployConnectionTests() =
    // ---- Generation ---------------------------------------------------------------------------------------------------

    [<Fact>]
    member _.``no secret set: no connection, nothing bundled``() =
        Assert.Equal(Ok None, settings (Deploy.env [ "CAUSA_DEPLOY_CONNECTION", "1" ]))
        Assert.False(enabled (Deploy.env [])) // a local or CI build is not the deploy build and reads no secret
        let file = render None
        Assert.DoesNotContain("\"connection\"", file)
        Assert.Contains(Schema, file)

    [<Fact>]
    member _.``all four secrets: the one connection, rendered for the application``() =
        let s = settings (Deploy.env Deploy.all)
        Assert.Equal(Ok(Some Deploy.settings), s)
        let file = render (Some Deploy.settings)
        // The application's decoder reads exactly this file.
        match Causa.App.DeployConnection.decode file with
        | Ok(Some c) ->
            Assert.Equal("causa-tests", c.Repository.Owner)
            Assert.Equal("causa-data-test", c.Repository.Name)
            Assert.Equal("competition", c.Repository.Branch)
            Assert.Equal(("Authorization", "Bearer " + Deploy.key), Causa.Persistence.Credential.authorization c.Credential)
        | other -> Assert.Fail(sprintf "the application did not read the rendered connection: %A" (other |> Result.map (Option.map (fun c -> c.Repository))))

    [<Fact>]
    member _.``some secrets: the build fails naming the missing secrets, never a value``() =
        let partial = Deploy.all |> List.filter (fun (k, _) -> k <> "CAUSA_BRANCH" && k <> "DATA_SOURCE")
        match settings (Deploy.env partial) with
        | Error message ->
            Assert.Contains("CAUSA_BRANCH", message)
            Assert.Contains("DATA_SOURCE", message)
            Assert.DoesNotContain(Deploy.key, message)
            Assert.DoesNotContain("causa-tests", message)
        | Ok _ -> Assert.Fail "a partial set of secrets was accepted"

    [<Fact>]
    member _.``a malformed secret fails naming the secret, never the value``() =
        let bad = Deploy.all |> List.map (fun (k, v) -> if k = "DATA_ACCESS_KEY_CAUSA" then k, "not a token " + Deploy.key else k, v)
        match settings (Deploy.env bad) with
        | Error message ->
            Assert.Contains("DATA_ACCESS_KEY_CAUSA", message)
            Assert.DoesNotContain(Deploy.key, message)
        | Ok _ -> Assert.Fail "a malformed key was accepted"
        let badOwner = Deploy.all |> List.map (fun (k, v) -> if k = "REPO_OWNER" then k, "owner/../x" else k, v)
        match settings (Deploy.env badOwner) with
        | Error message -> Assert.Contains("REPO_OWNER", message); Assert.DoesNotContain("owner/../x", message)
        | Ok _ -> Assert.Fail "a malformed owner was accepted"

    [<Fact>]
    member _.``the settings never print the key``() =
        Assert.DoesNotContain(Deploy.key, string Deploy.settings)
        Assert.DoesNotContain(Deploy.key, sprintf "%O" Deploy.settings)
        Assert.Contains("causa-tests/causa-data-test@competition", string Deploy.settings)

    // ---- Scan ---------------------------------------------------------------------------------------------------------

    [<Fact>]
    member _.``scan passes: a build without secrets ships no credential``() =
        Assert.Empty(scan (Deploy.site []) None)
        Assert.Empty(scan (Deploy.site [ FileName, render None ]) None)

    [<Fact>]
    member _.``scan passes: the deploy build ships the one key, in deploy-connection.json only``() =
        Assert.Empty(scan (Deploy.site [ FileName, render (Some Deploy.settings) ]) (Some Deploy.settings))

    [<Fact>]
    member _.``scan fails: any other token literal, in any build``() =
        let leaked = Deploy.site [ "boundary.js", "const t = '" + Deploy.otherKey + "';" ]
        Assert.Contains(scan leaked None, fun f -> f.File = "boundary.js")
        Assert.Contains(scan (leaked @ [ FileName, Deploy.bytes (render (Some Deploy.settings)) ]) (Some Deploy.settings), fun f -> f.File = "boundary.js")

    [<Fact>]
    member _.``scan fails: the injected key outside deploy-connection.json``() =
        let site = Deploy.site [ FileName, render (Some Deploy.settings); "_framework/app.js", "x" + Deploy.key ]
        Assert.Contains(scan site (Some Deploy.settings), fun f -> f.File = "_framework/app.js")

    [<Fact>]
    member _.``scan fails: a deploy-connection.json not at the artifact root``() =
        let site = Deploy.site [ "vendor/" + FileName, render (Some Deploy.settings) ]
        Assert.Contains(scan site (Some Deploy.settings), fun f -> f.File = "vendor/" + FileName)

    [<Fact>]
    member _.``scan fails: a connection in a build that has no deploy secrets``() =
        let site = Deploy.site [ FileName, render (Some { Deploy.settings with Key = "redacted" }) ]
        Assert.Contains(scan site None, fun f -> f.File = FileName)

    [<Fact>]
    member _.``scan fails: another token or another repository in deploy-connection.json``() =
        let otherRepo = Deploy.site [ FileName, render (Some { Deploy.settings with Repository = "elsewhere" }) ]
        Assert.Contains(scan otherRepo (Some Deploy.settings), fun f -> f.File = FileName)
        let otherToken = Deploy.site [ FileName, render (Some { Deploy.settings with Key = Deploy.otherKey }) ]
        Assert.Contains(scan otherToken (Some Deploy.settings), fun f -> f.File = FileName)

    [<Fact>]
    member _.``scan findings never carry a credential value``() =
        let site =
            Deploy.site [ FileName, render (Some { Deploy.settings with Key = Deploy.otherKey }); "a.js", Deploy.key; "b.js", Deploy.otherKey ]
        let findings = scan site (Some Deploy.settings) @ scan site None
        Assert.NotEmpty(findings)
        Assert.All(findings, fun f -> Assert.DoesNotContain(Deploy.key, f.Reason + f.File); Assert.DoesNotContain(Deploy.otherKey, f.Reason + f.File))

    // ---- The application ----------------------------------------------------------------------------------------------

    [<Fact>]
    member _.``the read is a same-origin GET without credentials, and its answer is decoded``() =
        Causa.App.Program.reset ()
        let read = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize) |> List.filter (fun e -> Deploy.str "kind" e = "Http")
        Assert.Single(read) |> ignore
        let e = read.Head
        Assert.Equal("GET", Deploy.str "method" e)
        Assert.Equal("https://example.test/causa/" + FileName, Deploy.str "url" e)
        Assert.Equal("omit", Deploy.str "credentials" e)
        Assert.False(e.TryGetProperty("headers") |> fst, "the deploy read sends no header, so no credential")
        Assert.True(Causa.App.DeployConnection.decode (render None) = Ok None)
        Assert.True(Causa.App.DeployConnection.decode "{\"schema\":\"other/1\"}" |> Result.isError)

    [<Fact>]
    member _.``the deploy connection is read once, after the build record``() =
        Causa.App.Program.reset ()
        let first = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize)
        Assert.Equal(1, first |> List.filter (fun e -> (Deploy.str "url" e |> string).EndsWith FileName) |> List.length)
        // Later messages (navigation, another Initialize) never read it again.
        let later =
            [ "{\"kind\":\"LocationChanged\",\"location\":{\"origin\":\"https://example.test\",\"path\":\"/causa/\",\"query\":\"\",\"hash\":\"#/settings\"}}"
              Deploy.initialize ]
            |> List.collect (Causa.App.Program.dispatch >> Deploy.effects)
        Assert.DoesNotContain(later, fun e -> (Deploy.str "url" e |> string).EndsWith FileName)

    [<Fact>]
    member _.``without a connection in the file the app is exactly as before``() =
        Causa.App.Program.reset ()
        let read = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize) |> List.find (fun e -> Deploy.str "kind" e = "Http")
        let answer = Causa.App.Program.dispatch (Deploy.httpResult (Deploy.str "correlationId" read) 200 (render None))
        Assert.Empty(Deploy.effects answer)
        let view = JsonDocument.Parse(answer).RootElement.GetProperty "view"
        Assert.True(view.GetProperty("connection.visible").GetBoolean())
        Assert.Equal("Not connected", view.GetProperty("connection.state").GetString())
        // A missing file (404, every build but the deploy build) is the same.
        Causa.App.Program.reset ()
        let read = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize) |> List.find (fun e -> Deploy.str "kind" e = "Http")
        Assert.Empty(Deploy.effects (Causa.App.Program.dispatch (Deploy.httpResult (Deploy.str "correlationId" read) 404 "not found")))

    [<Fact>]
    member _.``with the injected connection the app connects to that repository with that key, untyped``() =
        Causa.App.Program.reset ()
        let read = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize) |> List.find (fun e -> Deploy.str "kind" e = "Http")
        let answer = Causa.App.Program.dispatch (Deploy.httpResult (Deploy.str "correlationId" read) 200 (render (Some Deploy.settings)))
        let connect = Deploy.effects answer |> List.filter (fun e -> Deploy.str "kind" e = "Http")
        Assert.Single(connect) |> ignore
        let c = connect.Head
        Assert.Equal("https://api.github.com/repos/causa-tests/causa-data-test", Deploy.str "url" c)
        Assert.Equal("Bearer " + Deploy.key, c.GetProperty("headers").GetProperty("Authorization").GetString())
        let view = JsonDocument.Parse(answer).RootElement.GetProperty "view"
        Assert.Equal("Connecting", view.GetProperty("connection.state").GetString())
        // The key is never in the view.
        Assert.DoesNotContain(Deploy.key, view.GetRawText())

    [<Fact>]
    member _.``a connection the user already made is not replaced``() =
        Causa.App.Program.reset ()
        let read = Deploy.effects (Causa.App.Program.dispatch Deploy.initialize) |> List.find (fun e -> Deploy.str "kind" e = "Http")
        // The user connects first (typed), then the deploy file arrives.
        let ev name value = sprintf "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":\"%s\",\"value\":\"%s\"}}" name value
        for name, value in [ "UIF-CONN-OWNER", "someone"; "UIF-CONN-REPOSITORY", "their-data"; "UIF-CONN-BRANCH", "main"; "UIF-CONN-TOKEN", Deploy.otherKey ] do
            Causa.App.Program.dispatch (ev name value) |> ignore
        let typed = Deploy.effects (Causa.App.Program.dispatch "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":\"UIA-CONNECT\"}}")
        Assert.Contains(typed, fun e -> (Deploy.str "url" e |> string).EndsWith "/repos/someone/their-data")
        let answer = Causa.App.Program.dispatch (Deploy.httpResult (Deploy.str "correlationId" read) 200 (render (Some Deploy.settings)))
        Assert.DoesNotContain(Deploy.effects answer, fun e -> Deploy.str "kind" e = "Http")
