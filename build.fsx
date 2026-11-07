// Owner: integration (T10). The application build: `dotnet fsi build.fsx` (npm run build). No FAKE, no secrets.
//
// It writes the complete deployable site to dist/ (DEPLOYMENT.md):
//   dist/index.html        web/index.html with every web/screens/*.html fragment inlined at the screens marker and
//                          every web/screens/*.css linked at the screen-styles marker (CF-08 A)
//   dist/causa.css, dist/causa-theme.css, dist/causa-icon.svg, dist/boundary.js, dist/screens/*.css
//   dist/vendor/forma/     the published Forma package's dist/ (@echelon-foundry/design-system, package-lock pinned)
//   dist/vendor/limen/     the published Limen package's dist/ (@echelon-foundry/limen, package-lock pinned)
//   dist/_framework/       the published .NET WebAssembly engine (src/Causa.Host)
//
//   dist/provenance.json   build provenance (AR-011): the commit (the deployed SHA), the CI run, tool versions, the
//                          digest of every build input, the pinned dependency set and the fixtures
//
// Options:
//   --skip-engine          leave out the engine publish (a shell-only build for quick local checks; never used by CI or
//                          the deploy workflow)
//   --out <dir>            write the site to <dir> instead of dist/
//   --provenance-only      write only <out>/provenance.json
//   --compare <a> <b>      compare two provenance files: prints whether a re-run differs reproducibly (its inputs
//                          changed), only by environment drift (tools, OS, runner), or not at all (AR-011-O2)
// Exit code 0 only when every step succeeded; a failed step names itself and stops the build.

#load "build/DeployConnection.fs"

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions

type Step = { Name: string; Run: unit -> Result<unit, string> }

let root = __SOURCE_DIRECTORY__
let path (parts: string list) = Path.Combine(Array.ofList (root :: parts))
let argv = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray
let args = Set.ofList argv
let skipEngine = args.Contains "--skip-engine"
let provenanceOnly = args.Contains "--provenance-only"

let rec optionValue (name: string) (rest: string list) =
    match rest with
    | n :: v :: _ when n = name -> Some v
    | _ :: tail -> optionValue name tail
    | [] -> None

let dist = optionValue "--out" argv |> Option.map Path.GetFullPath |> Option.defaultValue (path [ "dist" ])

let screensMarker = "<!-- causa:screens -->"
let screenStylesMarker = "<!-- causa:screen-styles -->"

/// Fragments in a stable (ordinal) order, so the same inputs always produce the same dist/index.html.
let screenFiles (extension: string) =
    let dir = path [ "web"; "screens" ]
    if Directory.Exists dir then
        Directory.GetFiles(dir, "*" + extension) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray
    else
        []

/// Pure: the shell plus its fragments and stylesheets gives the served index.html.
let composeIndex (shell: string) (fragments: (string * string) list) (stylesheets: string list) : Result<string, string> =
    let count (marker: string) = Regex.Matches(shell, Regex.Escape marker).Count
    match count screensMarker, count screenStylesMarker with
    | 1, 1 ->
        let body =
            fragments
            |> List.map (fun (name, html) -> sprintf "<!-- web/screens/%s -->\n%s" name (html.Trim()))
            |> String.concat "\n"
        let links =
            stylesheets
            |> List.map (fun name -> sprintf "<link rel=\"stylesheet\" href=\"screens/%s\">" name)
            |> String.concat "\n  "
        Ok(shell.Replace(screensMarker, body).Replace(screenStylesMarker, links))
    | s, c -> Error(sprintf "web/index.html must hold each marker exactly once (screens: %d, screen-styles: %d)" s c)

let rec copyDirectory (source: string) (target: string) =
    Directory.CreateDirectory target |> ignore
    for file in Directory.GetFiles source do
        File.Copy(file, Path.Combine(target, Path.GetFileName file), true)
    for dir in Directory.GetDirectories source do
        copyDirectory dir (Path.Combine(target, Path.GetFileName dir))

let requireDirectory (dir: string) (hint: string) =
    if Directory.Exists dir then Ok() else Error(sprintf "%s is missing (%s)" dir hint)

let run (file: string) (arguments: string list) : Result<unit, string> =
    let info = ProcessStartInfo(file, arguments, WorkingDirectory = root, UseShellExecute = false)
    use p = Process.Start info
    p.WaitForExit()
    if p.ExitCode = 0 then Ok() else Error(sprintf "%s %s exited with %d" file (String.Join(" ", arguments)) p.ExitCode)

let clean () =
    if Directory.Exists dist then Directory.Delete(dist, true)
    Directory.CreateDirectory dist |> ignore
    Ok()

let index () =
    let shell = File.ReadAllText(path [ "web"; "index.html" ])
    let fragments = screenFiles ".html" |> List.map (fun f -> Path.GetFileName f, File.ReadAllText f)
    let stylesheets = screenFiles ".css" |> List.map Path.GetFileName
    composeIndex shell fragments stylesheets
    |> Result.map (fun html -> File.WriteAllText(Path.Combine(dist, "index.html"), html))

let webAssets () =
    [ "causa.css"; "causa-theme.css"; "causa-icon.svg"; "boundary.js" ]
    |> List.filter (fun f -> File.Exists(path [ "web"; f ]))
    |> List.iter (fun f -> File.Copy(path [ "web"; f ], Path.Combine(dist, f), true))
    let styles = screenFiles ".css"
    if not styles.IsEmpty then
        let target = Path.Combine(dist, "screens")
        Directory.CreateDirectory target |> ignore
        styles |> List.iter (fun f -> File.Copy(f, Path.Combine(target, Path.GetFileName f), true))
    Ok()

let vendor (package: string) (name: string) () =
    let source = path [ "node_modules"; "@echelon-foundry"; package; "dist" ]
    requireDirectory source "run npm ci first"
    |> Result.map (fun () -> copyDirectory source (Path.Combine(dist, "vendor", name)))

/// The Limen capability packs (`capabilities/<name>/`) the application registers. web/boundary.js imports only the
/// kernel (`@echelon-foundry/limen` -> vendor/limen/index.js), whose static imports reach no pack, so none ships.
/// Unregistered packs are left out because some reference browser APIs the application must not contain:
/// coordination (navigator.locks, BroadcastChannel; PERSIST-004) and store (indexedDB; PERSIST-003). A pack added
/// here must not reference any API in `forbiddenBrowserApis`; the "check the site" step enforces it.
let limenPacks : string list = []

/// Browser APIs no shipped script may reference (PAT-13: PERSIST-003-O5, PERSIST-004-O4). The engine's _framework
/// (the .NET runtime) is outside this check, as in the PERSIST-003/004 browser specs.
let forbiddenBrowserApis = [ "navigator.locks"; "BroadcastChannel"; "indexedDB"; "caches.open"; "storage.getDirectory" ]

let vendorLimen () =
    let source = path [ "node_modules"; "@echelon-foundry"; "limen"; "dist" ]
    requireDirectory source "run npm ci first"
    |> Result.map (fun () ->
        let target = Path.Combine(dist, "vendor", "limen")
        Directory.CreateDirectory target |> ignore
        for file in Directory.GetFiles source do
            File.Copy(file, Path.Combine(target, Path.GetFileName file), true)
        for dir in Directory.GetDirectories source do
            let name = Path.GetFileName dir
            if name = "capabilities" then
                for pack in limenPacks do
                    copyDirectory (Path.Combine(dir, pack)) (Path.Combine(target, name, pack))
            else
                copyDirectory dir (Path.Combine(target, name)))

/// Every shipped script and page outside _framework, checked for the forbidden browser APIs.
let forbiddenApiUses () =
    [ for f in Directory.GetFiles(dist, "*", SearchOption.AllDirectories) do
          let rel = Path.GetRelativePath(dist, f).Replace('\\', '/')
          if not (rel.StartsWith "_framework/") && Regex.IsMatch(rel, @"\.(js|mjs|html)$") then
              let text = File.ReadAllText f
              for api in forbiddenBrowserApis do
                  if text.Contains api then yield sprintf "%s references %s" rel api ]

let engine () =
    let publishDir = path [ "src"; "Causa.Host"; "bin"; "publish" ]
    if Directory.Exists publishDir then Directory.Delete(publishDir, true)
    run "dotnet" [ "publish"; path [ "src"; "Causa.Host"; "Causa.Host.csproj" ]; "-c"; "Release"; "-o"; publishDir; "--nologo" ]
    |> Result.bind (fun () ->
        let framework = Path.Combine(publishDir, "wwwroot", "_framework")
        requireDirectory framework "the engine publish wrote no _framework"
        |> Result.map (fun () -> copyDirectory framework (Path.Combine(dist, "_framework"))))


// ---- Provenance (AR-011) ------------------------------------------------------------------------------------------
// Two parts. "reproducible" holds only what the repository determines: the commit, the digest of every tracked build
// input, the pinned dependency set and the fixtures; the same commit always gives the same part. "environment" holds
// what the machine determines: tool versions, OS, CI run, time. A re-run whose reproducible part differs changed its
// inputs; one whose only differences are environmental is environment drift.

let capture (file: string) (arguments: string list) : string option =
    try
        let info = ProcessStartInfo(file, arguments, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
        use p = Process.Start info
        let out = p.StandardOutput.ReadToEnd().Trim()
        p.WaitForExit()
        if p.ExitCode = 0 && out <> "" then Some out else None
    with _ -> None

let sha256File (file: string) =
    use stream = File.OpenRead file
    SHA256.HashData(stream) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

let sha256Text (text: string) =
    SHA256.HashData(Text.Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

let env (name: string) = Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.filter ((<>) "")

/// The tracked files a build reads (git ls-files: untracked local files never enter the provenance).
let buildInputs () =
    let pathspecs =
        [ "build.fsx"; "global.json"; "Directory.Build.props"; "Directory.Packages.props"; "Causa.slnx"; "package.json"
          "package-lock.json"; ".echelon/dependency-lock.json"; ".echelon/release-selection.json"; "web"; "src/Causa.Domain"
          "src/Causa.Persistence"; "src/Causa.App"; "src/Causa.Host" ]
    capture "git" ("ls-files" :: "--" :: pathspecs)
    |> Option.map (fun out -> out.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim()) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray)
    |> Option.defaultValue []
    |> List.filter (fun f -> File.Exists(path [ f ]))
    |> List.map (fun f -> f, sha256File (path [ f ]))

let fixtureFiles () =
    capture "git" [ "ls-files"; "--"; "tests/Causa.TestSupport"; "tests/fixtures" ]
    |> Option.map (fun out -> out.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim()) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray)
    |> Option.defaultValue []
    |> List.filter (fun f -> File.Exists(path [ f ]) && not (f.EndsWith ".fsproj"))
    |> List.map (fun f -> f, sha256File (path [ f ]))

/// The locked dependency set as id -> "version tag commit digest" (from .echelon/dependency-lock.json).
let pinnedDependencies () =
    let file = path [ ".echelon"; "dependency-lock.json" ]
    if not (File.Exists file) then []
    else
        use doc = JsonDocument.Parse(File.ReadAllText file)
        let str (e: JsonElement) (name: string) =
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
            | _ -> ""
        let digest (e: JsonElement) =
            match e.TryGetProperty "packageDigest" with
            | true, d when d.ValueKind = JsonValueKind.Object -> str d "sha256"
            | _ -> str e "sha256"
        [ for s in doc.RootElement.GetProperty("systems").EnumerateArray() ->
              str s "id", String.Join(" ", [ str s "version"; str s "tag"; str s "commit"; digest s ] |> List.filter ((<>) "")) ]

let commit () = env "GITHUB_SHA" |> Option.orElse (capture "git" [ "rev-parse"; "HEAD" ]) |> Option.defaultValue "unknown"

let provenanceJson () : string =
    let inputs = buildInputs ()
    let fixtures = fixtureFiles ()
    let treeDigest (files: (string * string) list) = files |> List.map (fun (f, h) -> f + " " + h) |> String.concat "\n" |> sha256Text
    let sha = commit ()
    let options = JsonWriterOptions(Indented = true)
    use stream = new MemoryStream()
    (
        use w = new Utf8JsonWriter(stream, options)
        let obj (name: string) (entries: (string * string) list) =
            w.WriteStartObject name
            for k, v in entries do w.WriteString(k, v)
            w.WriteEndObject()
        w.WriteStartObject()
        w.WriteString("schema", "causa.build-provenance/1")
        w.WriteStartObject "reproducible"
        w.WriteString("commit", sha)
        w.WriteString("deployedSha", sha)
        w.WriteString("inputsDigest", treeDigest inputs)
        obj "inputs" inputs
        obj "dependencies" (pinnedDependencies ())
        w.WriteStartObject "fixtures"
        w.WriteString("digest", treeDigest fixtures)
        obj "files" fixtures
        w.WriteStartArray "seeds"
        w.WriteEndArray()
        w.WriteString("seedPolicy", "no randomized fixture exists yet; a property or generated fixture records its seed here")
        w.WriteEndObject()
        w.WriteEndObject()
        w.WriteStartObject "environment"
        obj
            "tools"
            [ "dotnet", capture "dotnet" [ "--version" ] |> Option.defaultValue "unavailable"
              "node", capture "node" [ "--version" ] |> Option.defaultValue "unavailable"
              "npm", capture "npm" [ "--version" ] |> Option.defaultValue "unavailable"
              "git", capture "git" [ "--version" ] |> Option.defaultValue "unavailable" ]
        obj
            "machine"
            [ "os", RuntimeInformation.OSDescription
              "architecture", RuntimeInformation.OSArchitecture.ToString()
              "runtime", RuntimeInformation.FrameworkDescription ]
        obj
            "ci"
            [ "runId", env "GITHUB_RUN_ID" |> Option.defaultValue "local"
              "runAttempt", env "GITHUB_RUN_ATTEMPT" |> Option.defaultValue "local"
              "workflow", env "GITHUB_WORKFLOW" |> Option.defaultValue "local"
              "repository", env "GITHUB_REPOSITORY" |> Option.defaultValue "local" ]
        w.WriteString("builtAt", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
        w.WriteEndObject()
        w.WriteEndObject()
    )
    Text.Encoding.UTF8.GetString(stream.ToArray())

let provenance () =
    Directory.CreateDirectory dist |> ignore
    File.WriteAllText(Path.Combine(dist, "provenance.json"), provenanceJson ())
    Ok()

/// Pure over two parsed provenance documents: the leaf paths that differ in each part.
let rec leaves (prefix: string) (e: JsonElement) : (string * string) list =
    match e.ValueKind with
    | JsonValueKind.Object -> [ for p in e.EnumerateObject() do yield! leaves (prefix + "/" + p.Name) p.Value ]
    | JsonValueKind.Array -> [ for i, v in Seq.indexed (e.EnumerateArray()) do yield! leaves (sprintf "%s/%d" prefix i) v ]
    | _ -> [ (prefix, e.ToString()) ]

let differences (part: string) (a: JsonElement) (b: JsonElement) =
    let get (e: JsonElement) =
        match e.TryGetProperty part with
        | true, v -> leaves "" v |> Map.ofList
        | _ -> Map.empty
    let ma, mb = get a, get b
    Set.union (ma |> Map.keys |> Set.ofSeq) (mb |> Map.keys |> Set.ofSeq)
    |> Set.filter (fun k -> ma.TryFind k <> mb.TryFind k)
    |> Set.toList

let compare (fileA: string) (fileB: string) =
    use a = JsonDocument.Parse(File.ReadAllText fileA)
    use b = JsonDocument.Parse(File.ReadAllText fileB)
    let inputs = differences "reproducible" a.RootElement b.RootElement
    let environment = differences "environment" a.RootElement b.RootElement |> List.filter ((<>) "/builtAt")
    let verdict =
        match inputs, environment with
        | [], [] -> "identical"
        | [], _ -> "environment-drift"
        | _ -> "reproducible-difference"
    let quoted (xs: string list) = xs |> List.map (fun x -> "\"" + x + "\"") |> String.concat ", "
    printfn "{\"verdict\": \"%s\", \"reproducible\": [%s], \"environment\": [%s]}" verdict (quoted inputs) (quoted environment)

// ---- Deploy connection and credential scan (EXT-DATA-004-O5, TEST-DEPLOY-KEY sections 2 and 5) ---------------------

let private envValue (name: string) = Environment.GetEnvironmentVariable name |> Option.ofObj

/// The deploy build's connection; every other build has none and reads no secret.
let deploySettings () : Result<Causa.Build.DeployConnection.Settings option, string> =
    if Causa.Build.DeployConnection.enabled envValue then Causa.Build.DeployConnection.settings envValue else Ok None

/// Only the deploy build writes deploy-connection.json: the connection when the four secrets are set, else the schema
/// alone. Other builds write nothing, and the app shows the Connect form.
let deployConnection () =
    if Causa.Build.DeployConnection.enabled envValue then
        deploySettings ()
        |> Result.map (fun s ->
            File.WriteAllText(Path.Combine(dist, Causa.Build.DeployConnection.FileName), Causa.Build.DeployConnection.render s)
            printfn "build: %s written (%s)" Causa.Build.DeployConnection.FileName (match s with Some c -> string c | None -> "no connection"))
    else Ok()

/// The build scans its own output: any GitHub token literal fails it, except the one deploy key in the root
/// deploy-connection.json of the deploy build. Findings name the file, never the value.
let scanSite () =
    deploySettings ()
    |> Result.bind (fun settings ->
        let files =
            Directory.GetFiles(dist, "*", SearchOption.AllDirectories)
            |> Array.map (fun f -> Path.GetRelativePath(dist, f).Replace('\\', '/'), File.ReadAllBytes f)
            |> List.ofArray
        match Causa.Build.DeployConnection.scan files settings with
        | [] -> Ok()
        | findings -> Error(findings |> List.map (fun f -> sprintf "%s: %s" f.File f.Reason) |> String.concat "; "))

let requireSite () =
    [ "index.html"; "causa.css"; "causa-theme.css"; "causa-icon.svg"; "boundary.js"; "vendor/forma/all.css"; "vendor/limen/index.js"; "provenance.json" ]
    @ (if skipEngine then [] else [ "_framework/dotnet.js" ])
    |> List.filter (fun f -> not (File.Exists(Path.Combine(dist, f))))
    |> function
        | [] ->
            match forbiddenApiUses () with
            | [] -> Ok()
            | uses -> Error(sprintf "dist/ references forbidden browser APIs (PERSIST-003/004): %s" (String.Join("; ", uses)))
        | missing -> Error(sprintf "dist/ lacks %s" (String.Join(", ", missing)))

let steps =
  if provenanceOnly then
    [ { Name = "write provenance"; Run = provenance } ]
  else
    [ { Name = "clean dist/"; Run = clean }
      { Name = "compose index.html"; Run = index }
      { Name = "copy web assets"; Run = webAssets }
      { Name = "vendor Forma"; Run = vendor "design-system" "forma" }
      { Name = "vendor Limen (registered packs only)"; Run = vendorLimen }
      if not skipEngine then { Name = "publish the engine"; Run = engine }
      { Name = "write provenance"; Run = provenance }
      { Name = "deploy connection"; Run = deployConnection }
      { Name = "scan the site for credentials"; Run = scanSite }
      { Name = "check the site"; Run = requireSite } ]

let result =
  match argv with
  | "--compare" :: a :: b :: _ -> compare a b; Ok()
  | _ ->
    steps
    |> List.fold
        (fun state step ->
            state
            |> Result.bind (fun () ->
                printfn "build: %s" step.Name
                step.Run() |> Result.mapError (fun e -> sprintf "%s: %s" step.Name e)))
        (Ok())

match result with
| Ok() -> (if not (args.Contains "--compare") then printfn "build: %s written" dist); exit 0
| Error e -> eprintfn "build FAILED at %s" e; exit 1
