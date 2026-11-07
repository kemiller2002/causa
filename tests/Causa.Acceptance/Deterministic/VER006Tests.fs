// Owner: integration (T11). VER-006 (cross-system contract validation): the application is exercised against the
// installed Echelon artifacts themselves (Limen's protocol codec, Forma's published stylesheet, the Praxis and Percepta
// records, the Folio and Forma workflow packages), and the local build path is run.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open ReleaseRecord

module CrossSystem =
    let formaCss () =
        Tooling.requireNodeModules ()
        Repo.text "node_modules/@echelon-foundry/design-system/dist/all.css"

    /// Every ef-* class an HTML document uses.
    let formaClasses (html: string) =
        Regex.Matches(html, "class=\"([^\"]*)\"")
        |> Seq.collect (fun m -> m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        |> Seq.filter (fun c -> c.StartsWith "ef-")
        |> Seq.distinct
        |> List.ofSeq

    /// Every --ef-* property a stylesheet reads.
    let formaTokensRead (css: string) =
        Regex.Matches(css, @"var\((--ef-[a-z0-9-]+)") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.distinct |> List.ofSeq

    let installedPackage (name: string) =
        Tooling.requireNodeModules ()
        Repo.json (sprintf "node_modules/%s/package.json" name)

    /// A Limen BrowserLocation for a hash.
    let location (hash: string) =
        sprintf "{\"origin\":\"http://127.0.0.1:4620\",\"path\":\"/\",\"query\":\"\",\"hash\":\"%s\"}" hash

    let initialize (hash: string) =
        sprintf "{\"kind\":\"Initialize\",\"protocolVersion\":1,\"capabilities\":[],\"location\":%s}" (location hash)

    let locationChanged (hash: string) = sprintf "{\"kind\":\"LocationChanged\",\"location\":%s}" (location hash)

    /// Decodes each line (one JSON message) with Limen's own generated codec; prints one verdict per line.
    let limenDecode (decoder: string) (messages: string list) : string list =
        let payload = JsonSerializer.Serialize(messages)
        Tooling.node (
            sprintf
                """import * as codec from "%s";
const messages = %s;
for (const m of messages) {
  const decoded = codec.%s(JSON.parse(m));
  console.log(decoded.ok ? "ok" : "rejected " + JSON.stringify(decoded.error ?? decoded));
}"""
                (Uri(Repo.path "node_modules/@echelon-foundry/limen/dist/generated/core.codec.js").AbsoluteUri)
                payload
                decoder
        )
        |> fun out -> out.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

open CrossSystem

type VER006Tests() =
    let locked = lockedSystems (lock ())
    let version id = locked |> List.tryFind (fun s -> s.Id = id) |> Option.map (fun s -> s.Version) |> Option.defaultValue ""

    /// API and schema compatibility across the applicable systems, checked against their installed artifacts.
    [<Fact>]
    member _.``VER-006-O1``() =
        let css = formaCss ()
        // Forma publishes its classes in its stylesheet and its pattern markup (some pattern classes carry no rule).
        let patterns =
            Directory.GetFiles(Repo.path "node_modules/@echelon-foundry/design-system/dist/patterns", "*.html")
            |> Array.map File.ReadAllText
            |> String.concat "\n"
        Gate.noDefects
            "Forma"
            [ for c in formaClasses (Repo.text "web/index.html") do
                  let inCss = Regex.IsMatch(css, @"\." + Regex.Escape c + @"(?![A-Za-z0-9_-])")
                  let inPatterns = formaClasses patterns |> List.contains c
                  if not (inCss || inPatterns) then
                      yield sprintf "web/index.html uses .%s, which Forma %s does not publish" c (version "forma")
              for t in formaTokensRead (Repo.text "web/causa.css") do
                  if not (css.Contains(t + ":")) then yield sprintf "web/causa.css reads %s, which Forma %s does not define" t (version "forma")
              for f in [ "web/causa.css"; "web/causa-theme.css" ] do
                  if Regex.IsMatch(Repo.text f, @"(^|[;{\s])--ef-[a-z0-9-]+\s*:") then yield sprintf "%s declares a Forma token of its own" f ]
        Gate.noDefects
            "installed Echelon packages"
            [ for id in [ "forma"; "forma-workflow"; "limen"; "folio" ] do
                  match locked |> List.tryFind (fun s -> s.Id = id) with
                  | Some s ->
                      let p = installedPackage s.NpmPackage
                      if Json.strOr "name" p <> s.NpmPackage then yield sprintf "%s installs package %s" id (Json.strOr "name" p)
                      if Json.strOr "version" p <> s.Version then yield sprintf "%s installed %s, locked %s" id (Json.strOr "version" p) s.Version
                      let entries =
                          [ yield! Json.str "main" p |> Option.toList
                            match Json.prop "exports" p with
                            | Some e when e.ValueKind = JsonValueKind.Object ->
                                for _, v in Json.entries e do
                                    if v.ValueKind = JsonValueKind.String then yield v.GetString() |> Option.ofObj |> Option.defaultValue ""
                                    else yield! Json.str "import" v |> Option.toList
                            | Some e when e.ValueKind = JsonValueKind.String -> yield e.GetString() |> Option.ofObj |> Option.defaultValue ""
                            | _ -> () ]
                      for entry in entries |> List.filter (fun e -> e <> "" && not (e.Contains "*")) do
                          if not (Repo.exists (Path.Combine("node_modules", s.NpmPackage, entry))) then
                              yield sprintf "%s %s declares entry %s, which is not installed" id s.Version entry
                  | None -> yield sprintf "%s is not locked" id ]
        Gate.noDefects
            "Praxis, Ordo and Percepta records"
            [ let toolchain = Repo.json ".echelon/toolchain.json"
              for id in [ "praxis"; "ordo" ] do
                  if Json.str id toolchain <> Some(version id) then yield sprintf ".echelon/toolchain.json %s is not the locked %s" id (version id)
              let installation = Repo.text ".ros/installation.json"
              if not (installation.Contains(version "praxis")) then yield sprintf ".ros/installation.json does not record Praxis %s" (version "praxis")
              let percepta = Repo.json ".percepta/application.json"
              if percepta.ValueKind <> JsonValueKind.Object then yield ".percepta/application.json is not a Percepta application record"
              for f in Directory.GetFiles(Repo.path ".percepta/contracts", "*.json") do
                  use d = JsonDocument.Parse(File.ReadAllText f)
                  if d.RootElement.ValueKind <> JsonValueKind.Object then yield sprintf "%s is not a Percepta contract" (Path.GetFileName f) ]

    /// Integration tests, not version strings: the engine's real answers go through Limen's own protocol codec.
    [<Fact>]
    member _.``VER-006-O2``() =
        let hashes = [ ""; "#/"; "#/administration"; "#/settings"; "#/investigations/new"; "#/no-such-page" ]
        let inbound = [ initialize ""; yield! hashes |> List.map locationChanged ]
        // What the test sends is a message Limen itself would send.
        Gate.noDefects
            "browser-to-engine messages"
            (List.zip inbound (limenDecode "decodeBrowserToEngineMessage" inbound) |> List.filter (snd >> (<>) "ok") |> List.map (fun (m, v) -> m + ": " + v))
        let answers = inbound |> List.map Causa.App.Program.dispatch
        Gate.noDefects
            "engine-to-browser answers"
            (List.zip answers (limenDecode "decodeEngineToBrowserMessage" answers) |> List.filter (snd >> (<>) "ok") |> List.map (fun (m, v) -> m + ": " + v))
        // Not vacuous: Limen's codec rejects an answer that breaks the protocol.
        match limenDecode "decodeEngineToBrowserMessage" [ "{\"view\":{\"nested\":{\"a\":1}},\"effects\":[]}" ] with
        | [ v ] when v.StartsWith "rejected" -> ()
        | other -> failwithf "Limen's codec accepted a malformed answer: %A" other
        // The kernel the boundary starts is the one the lock names.
        let exported =
            Tooling.node (
                sprintf "import * as limen from \"%s\"; console.log(typeof limen.BrowserKernel);" (Uri(Repo.path "node_modules/@echelon-foundry/limen/dist/index.js").AbsoluteUri)
            )
        if exported <> "function" then failwithf "Limen %s exports no BrowserKernel (%s)" (version "limen") exported

    /// The local bootstrap and build path runs, and the deploy path builds the site the browser loads.
    [<Fact>]
    member _.``VER-006-O3``() =
        let out = Tooling.tempDir ()
        try
            Tooling.build [ "--skip-engine"; "--out"; out ] |> ignore
            let index = File.ReadAllText(Path.Combine(out, "index.html"))
            Gate.noDefects
                "the built site"
                [ if index.Contains "<!-- causa:screens -->" || index.Contains "<!-- causa:screen-styles -->" then yield "index.html still holds a build marker"
                  for m in Regex.Matches(index, "\\s(?:href|src)=\"([^\"#:]+)\"") do
                      let target = m.Groups[1].Value
                      if not (File.Exists(Path.Combine(out, target))) then yield sprintf "index.html references %s, which the build did not write" target
                  let importMap = Regex.Match(index, "\"@echelon-foundry/limen\":\\s*\"\\./([^\"]+)\"")
                  if not importMap.Success then yield "index.html has no import map entry for Limen"
                  elif not (File.Exists(Path.Combine(out, importMap.Groups[1].Value))) then yield sprintf "the Limen import map target %s is missing" importMap.Groups[1].Value
                  if not (File.Exists(Path.Combine(out, "provenance.json"))) then yield "the build wrote no provenance.json" ]
        finally
            Directory.Delete(out, true)
        let packageJson = Repo.json "package.json"
        let deploy = Repo.text ".github/workflows/deploy-pages.yml"
        let playwright = Repo.text "playwright.config.ts"
        Gate.noDefects
            "the deploy and browser paths"
            [ if packageJson |> Json.prop "scripts" |> Option.bind (Json.str "build") <> Some "dotnet fsi build.fsx" then yield "npm run build is not build.fsx"
              if not (deploy.Contains "run: npm run build") then yield "the Pages workflow does not build with npm run build"
              if not (deploy.Contains "dist/index.html") then yield "the Pages workflow does not require dist/index.html"
              for project in [ "chromium"; "webkit" ] do
                  if not (playwright.Contains(sprintf "name: \"%s\"" project)) then yield sprintf "playwright.config.ts defines no %s project" project ]
