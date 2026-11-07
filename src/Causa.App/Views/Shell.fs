// Owner: ui. The application shell's projection (UI-014, UI-016, UI-020, UI-029): the page title, which screen is
// mounted, the persistent primary destinations with the current one marked, the current investigation context (a
// frozen demonstration or a writable copy), and the live-region announcements. Pure: AppView in, Bindings out. Every
// other screen module reads this one read-only (AGENT-RULES: Views/Shell.fs is ui's).
namespace Causa.App.Views

open System
open Causa.Domain
open Causa.Persistence
open Causa.Ui.Contracts

/// Building a Bindings map. The binding names are the data-text / data-if / data-bind-* / data-each names of the
/// authored HTML (Limen 0.9.1 grammar); a screen module's whole output is one of these maps.
[<RequireQualifiedAccess>]
module Bind =
    let text (key: string) (value: string) : string * BindingValue = key, BindingValue.Text value
    let flag (key: string) (value: bool) : string * BindingValue = key, BindingValue.Flag value
    let number (key: string) (value: float) : string * BindingValue = key, BindingValue.Number value

    let items (key: string) (rows: (string * BindingPrimitive) list list) : string * BindingValue =
        key, BindingValue.Items(rows |> List.map Map.ofList)

    let cellText (key: string) (value: string) : string * BindingPrimitive = key, BindingPrimitive.Text value
    let cellFlag (key: string) (value: bool) : string * BindingPrimitive = key, BindingPrimitive.Flag value

    let ofList (entries: (string * BindingValue) list) : Bindings = Map.ofList entries

    /// Right-biased union: a later map's entry wins. Screens own disjoint names, so a collision is a defect the
    /// cross-view tests look for, not a feature.
    let merge (maps: Bindings list) : Bindings =
        maps |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m) Map.empty

    /// aria-current as the primary and section navigation render it (J2-08, J3-01): "page" or "false".
    let current (isCurrent: bool) = if isCurrent then "page" else "false"

/// Plain-language copy shared by every screen module that compiles after the shell (UI-010, UI-014, UI-022).
[<RequireQualifiedAccess>]
module Copy =
    let appName = "Causa"

    /// The page title: "<screen> · Causa" (UI-014-O1 consistent page titles).
    let pageTitle (screen: string) = if String.IsNullOrWhiteSpace screen then appName else screen + " · " + appName

    /// UI-014-O3: frozen demonstrations are told apart from writable copies, in words, never by colour alone.
    let copyKind kind =
        match kind with
        | FrozenDemonstration -> "Frozen demonstration (read-only)"
        | WorkingCopy -> "Your working copy (editable)"
        | CopyKindUnknown -> "Copy type not known"

    /// A short tag for the same distinction, paired with copyKind's sentence where space allows.
    let copyTag kind =
        match kind with
        | FrozenDemonstration -> "Demonstration"
        | WorkingCopy -> "Working copy"
        | CopyKindUnknown -> "Unknown copy"

    /// Unknown is never rendered as zero (UI-022, UI-001 "where supported").
    let count c =
        match c with
        | Known n -> string n
        | UnknownCount -> "Not known"
        | NotSupported -> "Not available"

    let date (d: DateTimeOffset) = d.ToUniversalTime().ToString("d MMM yyyy, HH:mm 'UTC'", Globalization.CultureInfo.InvariantCulture)

    let dateOption (d: DateTimeOffset option) = d |> Option.map date |> Option.defaultValue "Not recorded"

    /// What a failure tells the user: what happened, what was preserved and a safe next step (UI-010). Retry is offered
    /// only where it performs a real action.
    let failure (f: UiFailure) : FailureCopy =
        let preservedNothingChanged = "Nothing in your data repository was changed."
        match f with
        | UiFailure.InvestigationNotFound _ ->
            { What = "This investigation could not be found in the connected repository."
              Preserved = preservedNothingChanged
              NextStep = "Go back to Investigations and choose another investigation."
              Retry = false }
        | UiFailure.InvalidData _ ->
            { What = "The stored investigation files could not be read safely, so nothing from them is shown."
              Preserved = preservedNothingChanged
              NextStep = "Go back to Investigations, or check the data repository in Administration."
              Retry = false }
        | UiFailure.NotAuthorized ->
            { What = "The connected account is not allowed to read this repository."
              Preserved = preservedNothingChanged
              NextStep = "Connect with an access token that can read the repository."
              Retry = false }
        | UiFailure.CredentialRefused ->
            { What = "GitHub refused the access token."
              Preserved = preservedNothingChanged
              NextStep = "Enter a valid access token and connect again."
              Retry = false }
        | UiFailure.ProviderUnreachable ->
            { What = "GitHub could not be reached."
              Preserved = preservedNothingChanged
              NextStep = "Check your network connection, then try again."
              Retry = true }
        | UiFailure.ProviderRateLimited ->
            { What = "GitHub is limiting requests from this account for now."
              Preserved = preservedNothingChanged
              NextStep = "Wait a few minutes, then try again."
              Retry = true }
        | UiFailure.ProviderError _ ->
            { What = "GitHub answered in a way Causa does not recognise."
              Preserved = preservedNothingChanged
              NextStep = "Try again. If it keeps happening, see Administration."
              Retry = true }
        | UiFailure.CapabilityAbsent(_, because) ->
            { What = "This view is not available yet: " + because
              Preserved = preservedNothingChanged
              NextStep = "Use the other sections of this investigation, or go back to Investigations."
              Retry = false }

/// The shell itself.
module Shell =
    /// The binding name of the flag that mounts a screen's fragment: <template data-if="screen.<name>">.
    let screenName (screen: ScreenView) =
        match screen with
        | ListScreen _ -> "investigation-list"
        | StartScreen _ -> "investigation-start"
        | WorkspaceScreen _ -> "investigation-workspace"
        | AdministrationScreen _ -> "administration"
        | SettingsScreen _ -> "settings"
        | NotFoundScreen _ -> "not-found"

    let screenNames =
        [ "investigation-list"; "investigation-start"; "investigation-workspace"; "administration"; "settings"; "not-found" ]

    let destinationSlug d =
        match d with
        | Investigations -> "investigations"
        | Administration -> "administration"
        | Settings -> "settings"

    let destinationLabel d =
        match d with
        | Investigations -> "Investigations"
        | Administration -> "Administration"
        | Settings -> "Settings"

    let destinationHref d =
        match d with
        | Investigations -> "#/"
        | Administration -> "#/administration"
        | Settings -> "#/settings"

    let destinations = [ Investigations; Administration; Settings ]

    /// The primary destination a route belongs to. Every investigation route (list, start, workspace) sits under
    /// Investigations; a route nobody defines belongs to none (UI-020-O3 identifiable active page).
    let destinationOf route =
        match route with
        | InvestigationsRoute
        | StartRoute
        | WorkspaceRoute _ -> Some Investigations
        | AdministrationRoute -> Some Administration
        | SettingsRoute -> Some Settings
        | NotFoundRoute _ -> None

    /// The persistent primary navigation for a route (UI-020-O1, UI-029-O1): always the same three destinations, in
    /// the same order, whatever the screen.
    let primaryLinks route : NavLink<PrimaryDestination> list =
        let current = destinationOf route
        [ for d in destinations ->
              { Destination = d; Label = destinationLabel d; Href = destinationHref d; Current = (current = Some d) } ]

    let sectionSlug s =
        match s with
        | Overview -> "overview"
        | CausalAnalysis -> "causal-analysis"
        | EvidenceSection -> "evidence"
        | HypothesesSection -> "hypotheses"
        | TimelineSection -> "timeline"
        | CorrectiveActions -> "corrective-actions"

    let sectionLabel s =
        match s with
        | Overview -> "Overview"
        | CausalAnalysis -> "Causal Analysis"
        | EvidenceSection -> "Evidence"
        | HypothesesSection -> "Hypotheses"
        | TimelineSection -> "Timeline"
        | CorrectiveActions -> "Corrective Actions"

    let private roleSlug role =
        match role with
        | WorkspaceRole.Catalog -> "catalog"
        | WorkspaceRole.WorkingCopy -> "working-copy"

    let private guid (InvestigationId g) = g.ToString("D")

    /// The canonical hash of a route (SCREEN-CONTRACTS URL contract: declared parameters only, in order, defaults
    /// omitted, %20 with upper-case hex).
    let href route =
        let enc (s: string) = Uri.EscapeDataString s
        match route with
        | InvestigationsRoute -> "#/"
        | StartRoute -> "#/investigations/new"
        | WorkspaceRoute(t, section) ->
            let tab = if section = Overview then "" else "&tab=" + sectionSlug section
            sprintf "#/investigations/%s?ws=%s&role=%s%s" (guid t.Id) (enc t.Workspace) (roleSlug t.Role) tab
        | AdministrationRoute -> "#/administration"
        | SettingsRoute -> "#/settings"
        | NotFoundRoute requested -> "#" + requested

    /// The investigation sections in their fixed order (UI-029-O2). Corrective Actions appears only where supported.
    let sectionLinks (target: RouteTarget) (current: Section) (correctiveActionsSupported: bool) : NavLink<Section> list =
        [ Overview; CausalAnalysis; EvidenceSection; HypothesesSection; TimelineSection; CorrectiveActions ]
        |> List.filter (fun s -> s <> CorrectiveActions || correctiveActionsSupported)
        |> List.map (fun s ->
            { Destination = s; Label = sectionLabel s; Href = href (WorkspaceRoute(target, s)); Current = (s = current) })

    /// Section navigation as data-each rows (key, label, href, current).
    let sectionItems (links: NavLink<Section> list) =
        Bind.items
            "sections"
            [ for l in links ->
                  [ Bind.cellText "key" (sectionSlug l.Destination)
                    Bind.cellText "label" l.Label
                    Bind.cellText "href" l.Href
                    Bind.cellText "current" (Bind.current l.Current) ] ]

    /// The screen's own title (also its h1): what the user is looking at.
    let screenTitle (view: AppView) =
        match view.Screen with
        | ListScreen _ -> "Investigations"
        | StartScreen _ -> "New investigation"
        | WorkspaceScreen w ->
            match w.Frame with
            | Ready f when not (String.IsNullOrWhiteSpace f.Title) -> f.Title
            | _ -> "Investigation"
        | AdministrationScreen _ -> "Administration"
        | SettingsScreen _ -> "Settings"
        | NotFoundScreen _ -> "Page not found"

    /// The current investigation context the shell shows beside the navigation (UI-014-O3): which investigation is
    /// open and whether it is a frozen demonstration or a writable copy.
    let investigationContext (view: AppView) : (WorkspaceFrame) option =
        match view.Screen with
        | WorkspaceScreen { Frame = Ready f } -> Some f
        | _ -> None

    /// The links the view carries, or the route's own when a backend supplies none: the three destinations are
    /// persistent either way, and exactly one (or, on not-found, none) is current.
    let effectivePrimary (view: AppView) =
        if List.isEmpty view.Primary then primaryLinks view.Route else view.Primary

    let private latest politeness (view: AppView) =
        view.Announcements
        |> List.filter (fun (p, _) -> p = politeness)
        |> List.tryLast
        |> Option.map snd
        |> Option.defaultValue ""

    /// Every binding the shell owns. Names are prefixed (screen., nav., context., announce., origin., shell.) so no
    /// screen module can collide with them.
    let bindings (view: AppView) : Bindings =
        let current = screenName view.Screen
        let primary = effectivePrimary view
        let isCurrent d = primary |> List.exists (fun l -> l.Destination = d && l.Current)
        let context = investigationContext view
        Bind.ofList
            [ yield Bind.text "page-title" (Copy.pageTitle (screenTitle view))
              yield Bind.text "shell.heading" (screenTitle view)
              yield Bind.flag "shell.ready" true
              for name in screenNames do
                  yield Bind.flag ("screen." + name) (name = current)
              for d in destinations do
                  let slug = destinationSlug d
                  yield Bind.text ("nav." + slug + ".current") (Bind.current (isCurrent d))
                  yield Bind.flag ("nav." + slug + ".active") (isCurrent d)
              yield Bind.flag "context.present" context.IsSome
              yield Bind.text "context.title" (context |> Option.map (fun f -> f.Title) |> Option.defaultValue "")
              yield Bind.text "context.identifier" (context |> Option.map (fun f -> f.Identifier) |> Option.defaultValue "")
              yield
                  Bind.text
                      "context.copy"
                      (context |> Option.map (fun f -> Copy.copyKind f.Copy) |> Option.defaultValue "")
              yield Bind.text "context.tag" (context |> Option.map (fun f -> Copy.copyTag f.Copy) |> Option.defaultValue "")
              yield Bind.flag "context.frozen" (context |> Option.exists (fun f -> f.Copy = FrozenDemonstration))
              yield Bind.flag "context.writable" (context |> Option.exists (fun f -> f.Copy = WorkingCopy))
              yield
                  Bind.text
                      "context.href"
                      (match view.Route with
                       | WorkspaceRoute(t, _) -> href (WorkspaceRoute(t, Overview))
                       | _ -> "#/")
              yield Bind.text "announce.polite" (latest Polite view)
              yield Bind.text "announce.assertive" (latest Assertive view)
              match view.Origin with
              | FixtureBackend label ->
                  yield Bind.flag "origin.fixture" true
                  yield Bind.text "origin.label" (FixtureLabel.value label)
              | IntegratedBackend ->
                  yield Bind.flag "origin.fixture" false
                  yield Bind.text "origin.label" "" ]

/// The shared loading/empty/failed bindings (UI-010); States.fs builds on these.
module ScreenStates =
    /// The four state flags and the failure copy under `<prefix>.`: loading, ready, empty, failed, failure-what,
    /// failure-preserved, failure-next, retry-offered.
    let bindings (prefix: string) (state: ScreenState) (loadingActivity: string) : Bindings =
        let failure =
            match state with
            | ScreenFailed f -> Some(Copy.failure f)
            | _ -> None
        Bind.ofList
            [ Bind.flag (prefix + ".loading") (state = ScreenLoading)
              Bind.text (prefix + ".loading-text") loadingActivity
              Bind.flag (prefix + ".ready") (state = ScreenReady)
              Bind.flag (prefix + ".empty") (state = ScreenEmpty)
              Bind.flag (prefix + ".failed") failure.IsSome
              Bind.text (prefix + ".failure-what") (failure |> Option.map (fun c -> c.What) |> Option.defaultValue "")
              Bind.text (prefix + ".failure-preserved") (failure |> Option.map (fun c -> c.Preserved) |> Option.defaultValue "")
              Bind.text (prefix + ".failure-next") (failure |> Option.map (fun c -> c.NextStep) |> Option.defaultValue "")
              Bind.flag (prefix + ".retry-offered") (failure |> Option.exists (fun c -> c.Retry)) ]

    /// The ScreenState a Remote value implies (Ready [] is empty, never failed).
    let ofRemote (isEmpty: 'a -> bool) (r: Remote<'a>) =
        match r with
        | NotRequested -> ScreenReady
        | Waiting _ -> ScreenLoading
        | Ready v when isEmpty v -> ScreenEmpty
        | Ready _ -> ScreenReady
        | Failed f -> ScreenFailed f

    let activity (r: Remote<'a>) =
        match r with
        | Waiting a -> a
        | _ -> ""
