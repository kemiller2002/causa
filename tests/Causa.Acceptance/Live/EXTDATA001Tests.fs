// Owner: integration (T20, reassigned). EXT-DATA-001 (no bundled investigation content). This file also holds the
// driver the EXT-DATA live tests share (AppDrive): the application's own engine and lister (Program.backend's provider
// seam) run against an Execute, so a live test exercises exactly the requests and classification the browser uses.
// Live parts read with CAUSA_GITHUB_TOKEN and write with CAUSA_WRITE_TOKEN on a fresh branch per test class
// (LiveFixture); without them they fail closed as BLOCKED, never pass.
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.TestSupport

/// Driving the application engine to quiescence over an Execute.
[<RequireQualifiedAccess>]
module AppDrive =
    let backend: Backend =
        { Kernel = Kernel.kernel
          Policies = Policies.coreDefault
          Actor = { ActorId = "ext-data-tests" }
          Author = Actor.Human(NonEmptyText.create "ext-data-tests" |> Option.get)
          Now = fun () -> DateTimeOffset.UtcNow
          StorageVersion = "1.1.0"
          Persistence = None
          Provider = Some { Connector = Connect.connector; Lister = GitHubLister.lister } }

    let engine = Update.engine backend

    type Run =
        { Model: Model
          Requests: HttpRequest list
          Announcements: string list }

    /// Applies a message, then performs every HTTP effect through `execute` and feeds its answer back, until none is left.
    let rec settle (execute: Execute) (run: Run) (effects: HostEffect list) : Run =
        match effects with
        | [] -> run
        | Http request :: rest ->
            let model, more = engine.Update (Msg.Browser(HttpCompleted(execute request))) run.Model
            settle execute { run with Model = model; Requests = run.Requests @ [ request ] } (rest @ more)
        | LiveAnnounce(_, text) :: rest -> settle execute { run with Announcements = run.Announcements @ [ text ] } rest
        | _ :: rest -> settle execute run rest

    let send (execute: Execute) (msg: Msg) (run: Run) : Run =
        let model, effects = engine.Update msg run.Model
        settle execute { run with Model = model } effects

    let fresh () : Run =
        let model, _ = engine.Init(Route "#/")
        { Model = model; Requests = []; Announcements = [] }

    /// An explicit Connect to a repository with a credential, settled (EXT-DATA-003).
    let connect (execute: Execute) (connection: ConnectionContext) : Run =
        fresh () |> send execute (Msg.Connection(ConnectionInput.ConnectSubmitted(connection.Repository, connection.Credential)))

    let catalogListing (run: Run) =
        run.Model.Listings |> Map.toList |> List.map snd |> List.tryFind (fun l -> l.Address.Role = WorkspaceRole.Catalog)

    /// The application's UI adapter over the same engine, with its own state (no shared tab).
    let config: Causa.App.Ui.AdapterConfig =
        { NewIdentifier = Guid.NewGuid
          Now = fun () -> DateTimeOffset.UtcNow
          Run = "ext-data"
          SessionLocal = false
          ValidateStart = Causa.App.Views.StartScreen.validate
          InvestigationOf = Update.investigationOf backend
          CanSave = false }

    let ui = Causa.App.Ui.UiAdapter.backend config engine

    /// The view state (Limen bindings) of an adapter state.
    let view (s: Causa.App.Ui.AdapterState) : Causa.Ui.Contracts.Bindings = View.bindings (ui.Project s)

    let text (key: string) (b: Causa.Ui.Contracts.Bindings) =
        match b.TryFind key with
        | Some(Causa.Ui.Contracts.BindingValue.Text t) -> t
        | _ -> ""

    let flag (key: string) (b: Causa.Ui.Contracts.Bindings) =
        match b.TryFind key with
        | Some(Causa.Ui.Contracts.BindingValue.Flag f) -> f
        | _ -> false

    /// Drives the adapter (as the browser does) and performs its HTTP effects through `execute`, settled.
    let rec adapterSettle (execute: Execute) (s: Causa.App.Ui.AdapterState) (effects: HostEffect list) (sent: HttpRequest list) =
        match effects with
        | [] -> s, sent
        | Http r :: rest ->
            let s2, more = Causa.App.Ui.UiAdapter.browser engine (HttpCompleted(execute r)) s
            adapterSettle execute s2 (rest @ more) (sent @ [ r ])
        | _ :: rest -> adapterSettle execute s rest sent

    let intent (execute: Execute) (i: Causa.Ui.Contracts.UiIntent) (s: Causa.App.Ui.AdapterState, sent: HttpRequest list) =
        let s2, fx = ui.Dispatch i s
        adapterSettle execute s2 fx sent

    /// The Connect form filled and submitted, as a user does it.
    let connectThroughForm (execute: Execute) (connection: ConnectionContext) (token: string) =
        let input f v = Causa.Ui.Contracts.UiIntent.Input(f, v, None)
        (fst (ui.Init Causa.Ui.Contracts.InvestigationsRoute), [])
        |> intent execute (input Causa.Ui.Contracts.Fields.repositoryOwner connection.Repository.Owner)
        |> intent execute (input Causa.Ui.Contracts.Fields.repositoryName connection.Repository.Name)
        |> intent execute (input Causa.Ui.Contracts.Fields.branch connection.Repository.Branch)
        |> intent execute (Causa.Ui.Contracts.UiIntent.Secret(Causa.Ui.Contracts.Fields.token, Credential.create token))
        |> intent execute (Causa.Ui.Contracts.UiIntent.Activate(Causa.Ui.Contracts.Actions.connect, None))

    /// Every text file of the shipped application: source, web entrypoints and the build script.
    let shippedTexts () =
        [ for dir in [ "src"; "web"; "build" ] do
              let full = Repo.path dir
              if Directory.Exists full then
                  for f in Directory.GetFiles(full, "*", SearchOption.AllDirectories) do
                      let rel = Path.GetRelativePath(Repo.root (), f).Replace('\\', '/')
                      let ext = Path.GetExtension f |> Option.ofObj |> Option.defaultValue ""
                      if not (rel.Contains "/bin/" || rel.Contains "/obj/") && [ ".fs"; ".cs"; ".fsproj"; ".csproj"; ".html"; ".css"; ".js"; ".json"; ".svg" ] |> List.contains ext then
                          yield rel, File.ReadAllText f
          yield "build.fsx", Repo.read "build.fsx" ]

    /// What must never be bundled: the catalog's investigation ids and titles, and event-history records.
    let bundledInvestigationContent () =
        let markers =
            [ for c in PinnedCatalog.cases -> c.Id.ToString "D"
              yield "\"type\":\"InvestigationStarted\""
              yield "Command Module fire"
              yield "STS-51-L"
              yield "Burned-out ceiling lightbulb" ]
        [ for path, text in shippedTexts () do
              for m in markers do
                  if text.Contains(m, StringComparison.OrdinalIgnoreCase) then yield sprintf "%s contains %s" path m ]

type EXTDATA001Tests(fixture: LiveFixture<EXTDATA001Tests>) =
    interface IClassFixture<LiveFixture<EXTDATA001Tests>>

    /// No investigation dataset, event history or golden state is in the application; the investigations a connected
    /// app shows come from the data repository.
    [<Fact>]
    member _.``EXT-DATA-001-O1``() =
        Assert.Empty(AppDrive.bundledInvestigationContent ())
        let fixtures = typeof<StatefulFake>.Assembly.GetName().Name
        for shipped in [ typeof<Model>.Assembly; typeof<ProviderPort>.Assembly; typeof<Investigation>.Assembly ] do
            Assert.DoesNotContain(fixtures, shipped.GetReferencedAssemblies() |> Array.map (fun a -> string a.Name))
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let listing = AppDrive.catalogListing run
        Assert.True(listing.IsSome, sprintf "the connected app listed no catalog: %A" run.Announcements)
        Assert.NotEmpty(listing.Value.Investigations)
        Assert.All(run.Requests, fun r -> Assert.StartsWith(GitHubRequests.ApiBase + "/", r.Url))

    /// The contract, schema and type declarations may ship; synthetic generators and fixtures never reach the shipped
    /// assemblies or the site.
    [<Fact>]
    member _.``EXT-DATA-001-O2``() =
        Assert.True(typeof<StorageCodec>.Assembly = typeof<ProviderPort>.Assembly, "the storage contract types ship in Causa.Persistence")
        // The built site holds no investigation file and no catalog record (the shell build: what the site adds).
        let out = Causa.Acceptance.Deterministic.Tooling.tempDir ()
        try
            Causa.Acceptance.Deterministic.Tooling.build [ "--skip-engine"; "--out"; out ] |> ignore
            let files = Directory.GetFiles(out, "*", SearchOption.AllDirectories)
            Assert.DoesNotContain(files, fun f -> f.Replace('\\', '/').Contains "/investigations/" || Path.GetFileName f = "events.ndjson")
            let ids = PinnedCatalog.cases |> List.map (fun c -> c.Id.ToString "D")
            Assert.All(files, fun f -> let t = File.ReadAllText f in Assert.False(ids |> List.exists t.Contains, sprintf "%s holds a catalog record id" f))
        finally
            Directory.Delete(out, true)
        let live = fixture.Run
        let run = AppDrive.connect live.Execute live.ReadConnection
        let titles = AppDrive.catalogListing run |> Option.map (fun l -> l.Investigations |> List.choose (fun i -> i.Title)) |> Option.defaultValue []
        Assert.NotEmpty(titles)
        let shipped = AppDrive.shippedTexts () |> List.map snd |> String.concat "\n"
        Assert.All(titles, fun t -> Assert.False(shipped.Contains t, sprintf "the shipped application contains the record title %s" t))
