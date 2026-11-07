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
// Options: --skip-engine leaves out the engine publish (a shell-only build for quick local checks; never used by CI or
// the deploy workflow). Exit code 0 only when every step succeeded; a failed step names itself and stops the build.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

type Step = { Name: string; Run: unit -> Result<unit, string> }

let root = __SOURCE_DIRECTORY__
let path (parts: string list) = Path.Combine(Array.ofList (root :: parts))
let dist = path [ "dist" ]
let args = fsi.CommandLineArgs |> Array.skip 1 |> Set.ofArray
let skipEngine = args.Contains "--skip-engine"

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

let engine () =
    let publishDir = path [ "src"; "Causa.Host"; "bin"; "publish" ]
    if Directory.Exists publishDir then Directory.Delete(publishDir, true)
    run "dotnet" [ "publish"; path [ "src"; "Causa.Host"; "Causa.Host.csproj" ]; "-c"; "Release"; "-o"; publishDir; "--nologo" ]
    |> Result.bind (fun () ->
        let framework = Path.Combine(publishDir, "wwwroot", "_framework")
        requireDirectory framework "the engine publish wrote no _framework"
        |> Result.map (fun () -> copyDirectory framework (Path.Combine(dist, "_framework"))))

let requireSite () =
    [ "index.html"; "causa.css"; "causa-theme.css"; "causa-icon.svg"; "boundary.js"; "vendor/forma/all.css"; "vendor/limen/index.js" ]
    @ (if skipEngine then [] else [ "_framework/dotnet.js" ])
    |> List.filter (fun f -> not (File.Exists(Path.Combine(dist, f))))
    |> function
        | [] -> Ok()
        | missing -> Error(sprintf "dist/ lacks %s" (String.Join(", ", missing)))

let steps =
    [ { Name = "clean dist/"; Run = clean }
      { Name = "compose index.html"; Run = index }
      { Name = "copy web assets"; Run = webAssets }
      { Name = "vendor Forma"; Run = vendor "design-system" "forma" }
      { Name = "vendor Limen"; Run = vendor "limen" "limen" }
      if not skipEngine then { Name = "publish the engine"; Run = engine }
      { Name = "check the site"; Run = requireSite } ]

let result =
    steps
    |> List.fold
        (fun state step ->
            state
            |> Result.bind (fun () ->
                printfn "build: %s" step.Name
                step.Run() |> Result.mapError (fun e -> sprintf "%s: %s" step.Name e)))
        (Ok())

match result with
| Ok() -> printfn "build: dist/ written"; exit 0
| Error e -> eprintfn "build FAILED at %s" e; exit 1
