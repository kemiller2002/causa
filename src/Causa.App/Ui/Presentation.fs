// Owner: integration (T17). IF41-VIEW producer functions, from the governed design the package names for this file
// (project-structure.json: proposals/r10-contract-first-ui/contracts/Presentation.fs in kemiller2002/Indy-init@dae2347).
/// IF-UI producer implementation (integration-owned in the event build, compiled right after Projections.fs). These
/// are the functions over the IF41-VIEW presentation types: catalog validation, navigation, presentation language,
/// form specs, action contracts, binding helpers and stable ids. They are NOT contract text: the folded
/// 07-IF41-VIEW.fs holds only types, signatures and the private-representation boundary members (X17).
namespace Causa.Ui.Contracts

open System
open System.Text.RegularExpressions
open Causa.App
open Causa.Domain
open Causa.Persistence

// from Ids.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScreenId =
    let value (ScreenId s) = s

// from Ids.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FieldId =
    let value (FieldId s) = s

// from Ids.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActionId =
    let value (ActionId s) = s

// from Ids.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RegionId =
    let value (RegionId s) = s

// from Ids.fs
/// Screens: the catalog's three (investigation-list, investigation-start, investigation-workspace) plus the two
/// primary destinations UI-018/UI-019/UI-020 require and the URL contract's not-found route.
[<RequireQualifiedAccess>]
module Screens =
    let list = ScreenId "investigation-list"
    let start = ScreenId "investigation-start"
    let workspace = ScreenId "investigation-workspace"
    let administration = ScreenId "administration"
    let settings = ScreenId "settings"
    let notFound = ScreenId "not-found"
    let all = [ list; start; workspace; administration; settings; notFound ]

// from Ids.fs
[<RequireQualifiedAccess>]
module Fields =
    // investigation-list, connection region (EXT-DATA-002/003/004; labels: ODR-R10-07 patterns)
    let repositoryOwner = FieldId "UIF-CONN-OWNER"
    let repositoryName = FieldId "UIF-CONN-REPOSITORY"
    let branch = FieldId "UIF-CONN-BRANCH"
    let token = FieldId "UIF-CONN-TOKEN"
    // investigation-start (S01)
    let problemStatement = FieldId "INV-001"
    let title = FieldId "INV-002"
    let identifier = FieldId "INV-006"
    let question = FieldId "INV-023"
    // investigation-workspace content editors
    let hypothesisExplanation = FieldId "INV-011"
    let evidenceContent = FieldId "INV-012"
    let observationText = FieldId "INV-024"
    let observationSource = FieldId "INV-025"
    let hypothesisStatement = FieldId "INV-026"
    let evidenceLabel = FieldId "INV-027"
    let evidenceSource = FieldId "INV-028"
    let evidenceUrl = FieldId "INV-029"
    let relationshipKind = FieldId "INV-031"
    let conclusionStatement = FieldId "INV-033"
    /// The required choice groups (ODR-V416-RC3 A: nothing selected at first) and the optional residual uncertainty.
    let evidenceCategory = FieldId "INV-045"
    let conclusionStrength = FieldId "INV-046"
    let residualUncertainty = FieldId "INV-047"
    let relationshipRationale = FieldId "INV-038"
    /// The link editor's evidence and hypothesis choosers (J2-04 picks them by combobox name; INV-030 names the action).
    let linkEvidence = FieldId "UIF-LINK-EVIDENCE"
    let linkHypothesis = FieldId "UIF-LINK-HYPOTHESIS"
    /// The cause editor (feature/causes-actions: RecordCausalAssertion; no catalog entry names these fields, so their
    /// ids are UIF-* and their labels are the kernel's words: outcome, hypothesis, cause role, mechanism).
    let causeOutcome = FieldId "UIF-CAUSE-OUTCOME"
    let causeHypothesis = FieldId "UIF-CAUSE-HYPOTHESIS"
    let causeRole = FieldId "UIF-CAUSE-ROLE"
    let causeMechanism = FieldId "UIF-CAUSE-MECHANISM"
    /// The corrective action editor (feature/causes-actions: RecordCorrectiveAction; R7.1, R7.2, S12).
    let correctiveKind = FieldId "UIF-CORRECTIVE-KIND"
    let correctiveDescription = FieldId "UIF-CORRECTIVE-DESCRIPTION"
    let correctiveCause = FieldId "UIF-CORRECTIVE-CAUSE"
    /// The causes a conclusion names (RecordConclusion.CauseIds): the drafted ids, space-separated. Changed only by the
    /// toggle action UIA-CONCLUSION-CAUSE, never typed.
    let conclusionCauses = FieldId "UIF-CONCLUSION-CAUSES"
    // investigation-workspace details editor (INV-034 edits INV-001, INV-002, INV-023; the identifier is unchanged)
    let detailsTitle = FieldId "UIF-DETAILS-TITLE"
    let detailsProblem = FieldId "UIF-DETAILS-PROBLEM"
    let detailsQuestion = FieldId "UIF-DETAILS-QUESTION"

// from Ids.fs
[<RequireQualifiedAccess>]
module Actions =
    let connect = ActionId "UIA-CONNECT"
    let disconnect = ActionId "UIA-DISCONNECT"
    let newInvestigation = ActionId "UIA-NEW-INVESTIGATION"
    let openInvestigation = ActionId "INV-040"
    let startInvestigation = ActionId "INV-004"
    let cancelStart = ActionId "INV-022"
    let recordHypothesis = ActionId "INV-009"
    let recordObservation = ActionId "INV-014"
    let save = ActionId "INV-015"
    let linkEvidence = ActionId "INV-030"
    let recordConclusion = ActionId "INV-032"
    let editDetails = ActionId "INV-034"
    let cancelEditor = ActionId "INV-036"
    /// J2-03 activates /record evidence|add evidence/; no catalog entry names it (OWNER-DECISIONS CF-02).
    let recordEvidence = ActionId "UIA-RECORD-EVIDENCE"
    /// J2-06 activates "Reassess"; INV-030's note requires reassessment; no catalog entry names the action (CF-02).
    let reassess = ActionId "UIA-REASSESS"
    let saveDetails = ActionId "UIA-SAVE-DETAILS"
    /// feature/causes-actions: no catalog entry names these actions (the catalog has no cause or corrective-action ids).
    let recordCause = ActionId "UIA-RECORD-CAUSE"
    let recordCorrectiveAction = ActionId "UIA-RECORD-CORRECTIVE-ACTION"
    /// Adds a cause to, or removes it from, the conclusion draft (the row key is the cause id): a toggle button with
    /// aria-pressed, because a button is never re-sent by the form's submit (Limen 0.9.1 legacy protocol).
    let toggleConclusionCause = ActionId "UIA-CONCLUSION-CAUSE"
    let retryLoad = ActionId "UIA-RETRY-LOAD"
    /// The seven-state indicator's own actions (frozen IF41-VIEW SaveAction).
    let saveIndicator = ActionId "UIA-SAVE-INDICATOR"

// from Ids.fs
[<RequireQualifiedAccess>]
module Regions =
    let investigations = RegionId "INV-039"
    let primaryNavigation = RegionId "INV-041"
    let sections = RegionId "INV-042"
    let creationTimestamp = RegionId "INV-007"
    let originalProblem = RegionId "INV-008"
    let hypotheses = RegionId "INV-010"
    let evidence = RegionId "INV-013"
    let saveState = RegionId "INV-016"
    let investigationState = RegionId "INV-018"
    let saveOutcomes = RegionId "INV-043"
    let profileIndicator = RegionId "INV-003"
    let destinationIndicator = RegionId "INV-005"
    let connection = RegionId "UIR-CONNECTION"
    let header = RegionId "UIR-WORKSPACE-HEADER"
    let timeline = RegionId "UIR-TIMELINE"
    let causalAnalysis = RegionId "UIR-CAUSAL-ANALYSIS"
    let conclusions = RegionId "UIR-CONCLUSIONS"
    let observations = RegionId "UIR-OBSERVATIONS"
    let overview = RegionId "UIR-OVERVIEW"
    let nextSteps = RegionId "UIR-NEXT-STEPS"
    let conflictReview = RegionId "UIR-CONFLICT-REVIEW"
    let storedRecord = RegionId "UIR-STORED-RECORD"
    let diagnostics = RegionId "UIR-DIAGNOSTICS"
    let preferences = RegionId "UIR-PREFERENCES"

// from Types.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Shown =
    let value s = match s with AsStored v -> Visible.value v | AsProjected u -> Understood.value u
    let capability s = match s with AsStored _ -> DataVisible | AsProjected _ -> DataUnderstood
    let isFixture s =
        match s with
        | AsStored v -> (Visible.fixtureLabel v).IsSome
        | AsProjected u -> (Understood.fixtureLabel u).IsSome

// from Types.fs
[<RequireQualifiedAccess>]
module Validation =
    let private message (label: string) rule =
        match rule with
        | Required -> sprintf "%s is required." label
        | MinLength n -> sprintf "%s must have at least %d character%s." label n (if n = 1 then "" else "s")
        | MaxLength n -> sprintf "%s must have at most %d characters." label n
        | Pattern _ -> sprintf "%s is not in the expected form." label
        | OneOf options -> sprintf "Choose one of: %s." (String.Join(", ", options))

    let private holds (value: string) rule =
        match rule with
        | Required -> not (String.IsNullOrWhiteSpace value)
        // Catalog INV-019 (OD-INDY-21): whitespace-only counts as empty for a minimum length.
        | MinLength n -> value.Trim().Length >= n
        | MaxLength n -> value.Length <= n
        // An optional field that is empty is not checked against its pattern; Required/MinLength judge emptiness.
        | Pattern p -> value = "" || Regex.IsMatch(value, p)
        | OneOf options -> value = "" || List.contains value options

    /// Pure: the field's errors for this value, in rule order.
    let check (spec: FieldSpec) (value: string) : ValidationError list =
        spec.Rules
        |> List.filter (holds value >> not)
        |> List.map (fun r -> { Field = spec.Field; Rule = r; Message = message spec.Label r })

    let checkAll (specs: FieldSpec list) (values: Map<FieldId, string>) : ValidationError list =
        specs |> List.collect (fun s -> check s (values |> Map.tryFind s.Field |> Option.defaultValue ""))

// from Types.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Remote =
    let map f r =
        match r with
        | Ready x -> Ready(f x)
        | NotRequested -> NotRequested
        | Waiting t -> Waiting t
        | Failed e -> Failed e

// from UiStates.fs
/// The centralized presentation-language mapping (UI-024). Views take their words from here; no view template invents
/// a label, a meaning or a success claim. Technical detail goes to Administration (UI-017, UI-028).
[<RequireQualifiedAccess>]
module PresentationLanguage =
    /// The exact words the indicator shows (PERSIST-031; the journeys match them exactly: J1-04, J3-03..07).
    let saveState kind =
        match kind with
        | SavedKind -> "Saved"
        | UnsavedKind -> "Unsaved"
        | SavingKind -> "Saving"
        | OfflineKind -> "Offline"
        | ConflictKind -> "Conflict"
        | UnknownOutcomeKind -> "Unknown commit outcome"
        | SaveFailedKind -> "Save failed"

    let allSaveStates = [ SavedKind; UnsavedKind; SavingKind; OfflineKind; ConflictKind; UnknownOutcomeKind; SaveFailedKind ]

    /// UI-024's default labels, applied wherever the concept appears.
    let terms =
        [ "Historical investigations", "Investigations"
          "Fetching and validating the investigation", "Opening investigation"
          "Expected lifecycle", "Status"
          "Replayed state", "Investigation history"
          "Open a sandbox copy", "Explore changes"
          "Ask the engine", "Apply change to draft"
          "Engine decisions", "Change history"
          "Discard the sandbox", "Discard draft" ]

    let private unsavedNote = "Changes you have not saved are kept only in this tab until you save or close it."

    let failure (f: UiFailure) : FailureCopy =
        match f with
        | UiFailure.InvestigationNotFound _ ->
            { What = "This investigation could not be found in the connected repository."
              Preserved = "Nothing was changed."
              NextStep = "Return to Investigations."
              Retry = false }
        | UiFailure.InvalidData _ ->
            { What = "This investigation's stored history could not be read safely, so it is not shown."
              Preserved = "Nothing was changed in the repository."
              NextStep = "See Administration for details."
              Retry = false }
        | UiFailure.NotAuthorized ->
            { What = "You do not have access to this repository."
              Preserved = unsavedNote
              NextStep = "Check the repository name, or connect with a credential that can read it."
              Retry = false }
        | UiFailure.CredentialRefused ->
            { What = "The repository did not accept the credential."
              Preserved = unsavedNote
              NextStep = "Reconnect with a valid credential."
              Retry = false }
        | UiFailure.ProviderUnreachable ->
            { What = "The repository cannot be reached right now."
              Preserved = unsavedNote
              NextStep = "Try again when you are back online."
              Retry = true }
        | UiFailure.ProviderRateLimited ->
            { What = "The repository is limiting requests for now."
              Preserved = unsavedNote
              NextStep = "Try again in a few minutes."
              Retry = true }
        | UiFailure.ProviderError _ ->
            { What = "The repository returned an answer Causa does not recognise."
              Preserved = unsavedNote
              NextStep = "See Administration for details."
              Retry = true }
        | UiFailure.CapabilityAbsent _ ->
            { What = "This information is not available yet."
              Preserved = "Nothing was changed."
              NextStep = "Other sections of the investigation remain available."
              Retry = false }

    /// Words that must never appear in routine investigation copy (UI-017, UI-023). The contract tests check every
    /// string this module produces against them.
    let forbiddenInInvestigationCopy = [ "Verified"; "replay"; "schema"; "commit SHA"; "catalog identifier"; "sha256" ]

// from Navigation.fs
[<RequireQualifiedAccess>]
module Navigation =
    let sectionSlug s =
        match s with
        | Overview -> "overview"
        | CausalAnalysis -> "causal-analysis"
        | EvidenceSection -> "evidence"
        | HypothesesSection -> "hypotheses"
        | TimelineSection -> "timeline"
        | CorrectiveActions -> "corrective-actions"

    /// The accessible names J2-08 and the design contract (investigationSections) look the links up by.
    let sectionLabel s =
        match s with
        | Overview -> "Overview"
        | CausalAnalysis -> "Causal Analysis"
        | EvidenceSection -> "Evidence"
        | HypothesesSection -> "Hypotheses"
        | TimelineSection -> "Timeline"
        | CorrectiveActions -> "Corrective Actions"

    let sections = [ Overview; CausalAnalysis; EvidenceSection; HypothesesSection; TimelineSection; CorrectiveActions ]

    let destinationLabel d =
        match d with
        | Investigations -> "Investigations"
        | Administration -> "Administration"
        | Settings -> "Settings"

    let destinations = [ Investigations; Administration; Settings ]

    let private guidText (InvestigationId g) = g.ToString("D")

    let roleSlug r =
        match r with
        | WorkspaceRole.Catalog -> "catalog"
        | WorkspaceRole.WorkingCopy -> "working-copy"

    /// The route target of a resolved reference.
    let targetOf (r: InvestigationRef) : RouteTarget =
        let (Workspace ws) = r.Address.Workspace
        { Workspace = ws; Role = r.Address.Role; Id = r.Id }

    /// Resolves a route target against the references the engine listed. None is not found (no address is built
    /// from a URL: v4.1.5 W1).
    let resolve (known: InvestigationRef list) (t: RouteTarget) : InvestigationRef option =
        known |> List.tryFind (fun r -> targetOf r = t)

    let private targetQuery (t: RouteTarget) =
        sprintf "ws=%s&role=%s" (Uri.EscapeDataString t.Workspace) (roleSlug t.Role)

    let format route =
        match route with
        | InvestigationsRoute -> "#/"
        | StartRoute -> "#/investigations/new"
        | WorkspaceRoute(t, Overview) -> sprintf "#/investigations/%s?%s" (guidText t.Id) (targetQuery t)
        | WorkspaceRoute(t, s) -> sprintf "#/investigations/%s?%s&tab=%s" (guidText t.Id) (targetQuery t) (sectionSlug s)
        | AdministrationRoute -> "#/administration"
        | SettingsRoute -> "#/settings"
        | NotFoundRoute r -> "#/" + r.TrimStart('#', '/')

    let parse (hash: string) : UiRoute =
        let raw = hash.TrimStart('#')
        let path, query =
            match raw.IndexOf('?') with
            | -1 -> raw, ""
            | i -> raw.Substring(0, i), raw.Substring(i + 1)
        let param name =
            query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun kv -> match kv.Split('=') with [| k; v |] when k = name -> Some(Uri.UnescapeDataString v) | _ -> None)
        let tab = param "tab"
        let target id =
            match param "ws", param "role" with
            | Some ws, Some "catalog" when ws <> "" -> Some { Workspace = ws; Role = WorkspaceRole.Catalog; Id = id }
            | Some ws, Some "working-copy" when ws <> "" -> Some { Workspace = ws; Role = WorkspaceRole.WorkingCopy; Id = id }
            | _ -> None
        let section =
            match tab with
            | None -> Some Overview
            | Some t -> sections |> List.tryFind (fun s -> sectionSlug s = t)
        match path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
        | [] -> InvestigationsRoute
        | [ "investigations"; "new" ] -> StartRoute
        | [ "investigations"; id ] ->
            match Guid.TryParse id, section with
            | (true, g), Some s ->
                match target (InvestigationId g) with
                | Some t -> WorkspaceRoute(t, s)
                | None -> NotFoundRoute raw
            | _ -> NotFoundRoute raw
        | [ "administration" ] -> AdministrationRoute
        | [ "settings" ] -> SettingsRoute
        | _ -> NotFoundRoute raw

    let destinationOf route =
        match route with
        | InvestigationsRoute | StartRoute | WorkspaceRoute _ | NotFoundRoute _ -> Investigations
        | AdministrationRoute -> Administration
        | SettingsRoute -> Settings

    /// The primary navigation for a route: present on every screen (INV-041), exactly one current.
    let primary route : NavLink<PrimaryDestination> list =
        let hrefOf d =
            match d with
            | Investigations -> format InvestigationsRoute
            | Administration -> format AdministrationRoute
            | Settings -> format SettingsRoute
        destinations
        |> List.map (fun d -> { Destination = d; Label = destinationLabel d; Href = hrefOf d; Current = destinationOf route = d })

    /// The section navigation of a workspace; Corrective Actions appears only when the projection supports it.
    let sectionLinks (id: InvestigationRef) (current: Section) (correctiveActionsSupported: bool) : NavLink<Section> list =
        sections
        |> List.filter (fun s -> s <> CorrectiveActions || correctiveActionsSupported)
        |> List.map (fun s -> { Destination = s; Label = sectionLabel s; Href = format (WorkspaceRoute(targetOf id, s)); Current = s = current })

// from Connection.fs
[<RequireQualifiedAccess>]
module Connection =
    /// Labels satisfy the governed journey's patterns (support.mjs connect: owner, repository, branch, token|credential;
    /// ODR-R10-07). No requirement fixes the exact words.
    let fields : FieldSpec list =
        [ { Field = Fields.repositoryOwner; Label = "Repository owner"; Component = TextInput; Rules = [ Required ] }
          { Field = Fields.repositoryName; Label = "Repository"; Component = TextInput; Rules = [ Required ] }
          { Field = Fields.branch; Label = "Branch"; Component = TextInput; Rules = [ Required ] }
          { Field = Fields.token; Label = "Access token"; Component = PasswordInput; Rules = [ Required ] } ]

    let emptyForm = { Owner = ""; Repository = ""; Branch = ""; CredentialEntered = false }

    let validate (form: ConnectForm) : ValidationError list =
        let values =
            Map.ofList
                [ Fields.repositoryOwner, form.Owner
                  Fields.repositoryName, form.Repository
                  Fields.branch, form.Branch
                  Fields.token, (if form.CredentialEntered then "entered" else "") ]
        Validation.checkAll fields values

    let withField (field: FieldId) (value: string) (form: ConnectForm) =
        if field = Fields.repositoryOwner then { form with Owner = value }
        elif field = Fields.repositoryName then { form with Repository = value }
        elif field = Fields.branch then { form with Branch = value }
        else form

    /// The state word shown to the user (UI-024; never a raw state code).
    let describe (state: ConnectionState) =
        match state with
        | Disconnected -> "Not connected"
        | Connecting _ -> "Connecting"
        | Connected true -> "Connected"
        | Connected false -> "Connected (read only)"
        | ConnectionState.CredentialRefused -> "Credential not accepted"
        | Reauthenticating -> "Reconnecting"

// from InvestigationList.fs
[<RequireQualifiedAccess>]
module InvestigationList =
    let sort (by: ListSort) (items: InvestigationSummary list) =
        match by with
        | ByTitle -> items |> List.sortBy (fun s -> s.Title.ToLowerInvariant())
        | ByUpdated -> items |> List.sortByDescending (fun s -> s.LastActivity |> Option.defaultValue DateTimeOffset.MinValue)

    let screenState (investigations: Remote<Visible<InvestigationSummary list>>) =
        match investigations with
        | NotRequested -> ScreenReady
        | Waiting _ -> ScreenLoading
        | Ready v when List.isEmpty (Visible.value v) -> ScreenEmpty
        | Ready _ -> ScreenReady
        | Failed f -> ScreenFailed f

// from InvestigationDetail.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module WorkspaceFrame =
    let currentSection (frame: WorkspaceFrame) =
        frame.Sections |> List.tryFind (fun l -> l.Current) |> Option.map (fun l -> l.Destination)

// from InvestigationEditor.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StartForm =
    /// The catalog's names and validation blocks, verbatim (INV-001, INV-002, INV-006, INV-023; rules INV-019/020/021).
    let fields : FieldSpec list =
        [ { Field = Fields.problemStatement; Label = "Problem statement"; Component = TextArea
            Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.title; Label = "Title"; Component = TextInput; Rules = [ MaxLength 200 ] }
          { Field = Fields.question; Label = "Primary investigative question"; Component = TextArea; Rules = [ MaxLength 10000 ] }
          { Field = Fields.identifier; Label = "Investigation identifier"; Component = TextInput
            Rules = [ Required; MinLength 1; Pattern "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" ] } ]

    let empty identifier = { ProblemStatement = ""; Title = ""; Question = ""; Identifier = identifier }

    let values (f: StartForm) =
        Map.ofList
            [ Fields.problemStatement, f.ProblemStatement
              Fields.title, f.Title
              Fields.question, f.Question
              Fields.identifier, f.Identifier ]

    let validate (f: StartForm) = Validation.checkAll fields (values f)

    let withField (field: FieldId) (value: string) (f: StartForm) =
        if field = Fields.problemStatement then { f with ProblemStatement = value }
        elif field = Fields.title then { f with Title = value }
        elif field = Fields.question then { f with Question = value }
        elif field = Fields.identifier then { f with Identifier = value }
        else f

// from InvestigationEditor.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DetailsForm =
    let fields : FieldSpec list =
        [ { Field = Fields.detailsTitle; Label = "Title"; Component = TextInput; Rules = [ MaxLength 200 ] }
          { Field = Fields.detailsProblem; Label = "Problem statement"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.detailsQuestion; Label = "Primary investigative question"; Component = TextArea; Rules = [ MaxLength 10000 ] } ]

    let validate (f: DetailsForm) =
        Validation.checkAll fields (Map.ofList [ Fields.detailsTitle, f.Title; Fields.detailsProblem, f.ProblemStatement; Fields.detailsQuestion, f.Question ])

// from InvestigationEditor.fs
/// The frozen SaveAction choices each state offers (PERSIST-031..033), one mapping for fixture and adapter alike.
[<RequireQualifiedAccess>]
module SaveChoices =
    let forKind kind =
        match kind with
        | SavedKind | SavingKind | OfflineKind -> []
        | UnsavedKind -> [ Save ]
        | ConflictKind -> [ Review; ReapplyMine; DiscardMine; ExportMine ]
        | UnknownOutcomeKind -> [ CheckAgainAction ]
        | SaveFailedKind -> [ Retry ]

// from InvestigationAnalysis.fs
[<RequireQualifiedAccess>]
module ContentForms =
    let observation : FieldSpec list =
        [ { Field = Fields.observationText; Label = "Observation text"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.observationSource; Label = "Observation source reference"; Component = TextInput; Rules = [ MaxLength 10000 ] } ]

    let hypothesis : FieldSpec list =
        [ { Field = Fields.hypothesisStatement; Label = "Hypothesis statement"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          // INV-011 has no validation block in the catalog; required: true.
          { Field = Fields.hypothesisExplanation; Label = "Hypothesis explanation"; Component = TextArea; Rules = [ Required ] } ]

    /// The fields checked when a hypothesis is first recorded. J2-02 records with the statement only, while the catalog
    /// marks INV-011 required: until CF-03 is decided this list checks only the statement, and `hypothesis` keeps the
    /// catalog rule unchanged for the detail editor (S04). No requirement is weakened in the manifest.
    let hypothesisOnRecord : FieldSpec list = [ hypothesis.Head ]

    let evidence : FieldSpec list =
        [ { Field = Fields.evidenceLabel; Label = "Evidence label"; Component = TextInput; Rules = [ Required; MinLength 1; MaxLength 200 ] }
          // INV-028: required, 1..10000 (the kernel's AdmitEvidence needs a source).
          { Field = Fields.evidenceSource; Label = "Evidence source reference"; Component = TextInput; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.evidenceUrl; Label = "Evidence source URL"; Component = UrlInput; Rules = [ MaxLength 2000; Pattern SafeUrl.pattern ] }
          // INV-012 has no validation block; required: true.
          { Field = Fields.evidenceContent; Label = "Evidence original content"; Component = TextArea; Rules = [ Required ] }
          { Field = Fields.evidenceCategory; Label = "Evidence category"; Component = RadioGroup
            Rules = [ Required; OneOf [ "directObservation"; "measurement"; "log"; "document"; "experimentResult"; "testimony"; "externalReference" ] ] } ]

    let link : FieldSpec list =
        [ { Field = Fields.linkEvidence; Label = "Evidence"; Component = Select; Rules = [ Required ] }
          { Field = Fields.linkHypothesis; Label = "Hypothesis"; Component = Select; Rules = [ Required ] }
          { Field = Fields.relationshipKind; Label = "Evidence relationship kind"; Component = RadioGroup
            Rules = [ Required; OneOf [ "supports"; "contradicts"; "consistent-with"; "inconclusive" ] ] }
          { Field = Fields.relationshipRationale; Label = "Evidence relationship rationale"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] } ]

    let conclusion : FieldSpec list =
        [ { Field = Fields.conclusionStatement; Label = "Conclusion statement"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.conclusionStrength; Label = "Conclusion strength"; Component = RadioGroup
            Rules = [ Required; OneOf [ "plausibleExplanation"; "leadingExplanation"; "supportedCause"; "confirmedCause"; "acceptedWithResidualUncertainty" ] ] }
          { Field = Fields.residualUncertainty; Label = "Residual uncertainty"; Component = TextArea; Rules = [ MaxLength 10000 ] } ]

    /// The kernel's cause roles (CauseRole; R6.3 distinguishes root cause from contributing factors), as stored.
    let causeRoles = [ "rootCause"; "contributingFactor"; "trigger"; "enablingCondition" ]

    let cause : FieldSpec list =
        [ { Field = Fields.causeOutcome; Label = "Outcome it explains"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.causeHypothesis; Label = "Hypothesis"; Component = Select; Rules = [ Required ] }
          { Field = Fields.causeRole; Label = "Cause role"; Component = RadioGroup; Rules = [ Required; OneOf causeRoles ] }
          { Field = Fields.causeMechanism; Label = "Mechanism"; Component = TextArea; Rules = [ MaxLength 10000 ] } ]

    /// The kernel's corrective action kinds (CorrectiveActionKind; R7.2, S12-O8), as stored.
    let correctiveKinds = [ "mitigation"; "workaround"; "correction"; "prevention" ]

    let correctiveAction : FieldSpec list =
        [ { Field = Fields.correctiveDescription; Label = "Corrective action"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = Fields.correctiveKind; Label = "Corrective action kind"; Component = RadioGroup; Rules = [ Required; OneOf correctiveKinds ] }
          // S12-O3: the target causal assertion is required.
          { Field = Fields.correctiveCause; Label = "Cause it addresses"; Component = Select; Rules = [ Required ] } ]

    let all = observation @ hypothesis @ evidence @ link @ conclusion @ cause @ correctiveAction

    let kindValue k =
        match k with
        | Supports -> "supports"
        | Contradicts -> "contradicts"
        | ConsistentWith -> "consistent-with"
        | Inconclusive -> "inconclusive"

// from UiActions.fs
[<RequireQualifiedAccess>]
module ActionContracts =
    let private a action screen label comp needs validates outcome =
        { Action = action; Screen = screen; Label = label; Component = comp; Needs = needs; Validates = validates; Outcome = outcome }

    let all : ActionContract list =
        [ a Actions.connect Screens.list "Connect data repository" Button DataVisible
              [ Fields.repositoryOwner; Fields.repositoryName; Fields.branch; Fields.token ] "SM-CONNECTION Connecting, then Connected or a stated refusal"
          a Actions.disconnect Screens.list "Disconnect" Button DataVisible [] "SM-CONNECTION DisconnectConfirmed"
          a Actions.openInvestigation Screens.list "Open investigation" Link DataVisible [] "route #/investigations/{id}; the workspace shows its problem statement"
          a Actions.newInvestigation Screens.list "New investigation" Link DataEditable [] "route #/investigations/new"
          a Actions.retryLoad Screens.list "Try again" Button DataVisible [] "the failed read is sent again (offered only when it performs a real action)"
          a Actions.startInvestigation Screens.start "Start investigation" Button DataEditable
              [ Fields.problemStatement; Fields.title; Fields.question; Fields.identifier ] "StartInvestigation command; the workspace opens"
          a Actions.cancelStart Screens.start "Cancel" Button DataEditable [] "leaves the start screen; nothing is created or written"
          a Actions.recordObservation Screens.workspace "Record an observation" Button DataEditable [ Fields.observationText; Fields.observationSource ] "RecordObservation; the text is listed"
          a Actions.recordHypothesis Screens.workspace "Record a hypothesis" Button DataEditable [ Fields.hypothesisStatement ] "RecordHypothesis; it is listed"
          a Actions.recordEvidence Screens.workspace "Record evidence" Button DataEditable
              [ Fields.evidenceLabel; Fields.evidenceSource; Fields.evidenceUrl; Fields.evidenceContent; Fields.evidenceCategory ] "AdmitEvidence; it is listed"
          a Actions.linkEvidence Screens.workspace "Link evidence to a hypothesis" Button DataEditable
              [ Fields.linkEvidence; Fields.linkHypothesis; Fields.relationshipKind; Fields.relationshipRationale ] "RelateEvidence; shown on the evidence and the hypothesis"
          a Actions.reassess Screens.workspace "Reassess" Button DataEditable
              [ Fields.linkEvidence; Fields.linkHypothesis; Fields.relationshipKind; Fields.relationshipRationale ] "a new current assessment; the earlier one stays in the history"
          a Actions.recordConclusion Screens.workspace "Record a conclusion" Button DataEditable [ Fields.conclusionStatement; Fields.conclusionStrength; Fields.residualUncertainty ] "a conclusion snapshot; the investigation is not resolved"
          a Actions.recordCause Screens.workspace "Record a cause" Button DataEditable
              [ Fields.causeOutcome; Fields.causeHypothesis; Fields.causeRole; Fields.causeMechanism ] "RecordCausalAssertion; the cause is listed as proposed"
          a Actions.recordCorrectiveAction Screens.workspace "Record a corrective action" Button DataEditable
              [ Fields.correctiveDescription; Fields.correctiveKind; Fields.correctiveCause ] "RecordCorrectiveAction; listed under Corrective Actions as proposed"
          a Actions.toggleConclusionCause Screens.workspace "Cause the conclusion names" Button DataEditable [] "adds the cause to the conclusion draft, or removes it"
          // Catalog name "Edit investigation details"; the button reads "Edit details" (the governed INV-START probe).
          a Actions.editDetails Screens.workspace "Edit details" Button DataEditable [] "opens the details editor (causa-storage 1.2.0 investigations)"
          a Actions.saveDetails Screens.workspace "Save details" Button DataEditable [ Fields.detailsTitle; Fields.detailsProblem; Fields.detailsQuestion ] "details changed; the identifier is unchanged"
          a Actions.cancelEditor Screens.workspace "Cancel" Button DataEditable [] "closes the editor; nothing entered is recorded or written"
          a Actions.save Screens.workspace "Save" Button DataEditable [] "SM-SAVE SaveRequested; Saved only after read-back confirmation"
          a Actions.saveIndicator Screens.workspace "Save-state action" Button DataEditable [] "the frozen SaveAction (Retry, Reconnect, Check again, Review, Reapply, Discard, Export)" ]

// from Bindings.fs
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Bindings =
    let text (k: string) v : string * BindingValue = k, BindingValue.Text v
    let flag (k: string) v : string * BindingValue = k, BindingValue.Flag v
    let number (k: string) (v: float) : string * BindingValue = k, BindingValue.Number v
    let items (k: string) (rows: (string * BindingPrimitive) list list) : string * BindingValue =
        k, BindingValue.Items(rows |> List.map Map.ofList)

    /// Every key of a binding map (top level and row fields), for the contract test that no binding names a credential.
    let keys (b: Bindings) =
        b
        |> Map.toList
        |> List.collect (fun (k, v) ->
            match v with
            | BindingValue.Items rows -> k :: (rows |> List.collect (Map.toList >> List.map fst))
            | _ -> [ k ])
        |> List.distinct

    /// Every text value, for the copy checks (UI-017, UI-023, credential never rendered).
    let texts (b: Bindings) =
        b
        |> Map.toList
        |> List.collect (fun (_, v) ->
            match v with
            | BindingValue.Text s -> [ s ]
            | BindingValue.Items rows ->
                rows |> List.collect (Map.toList >> List.choose (fun (_, p) -> match p with BindingPrimitive.Text s -> Some s | _ -> None))
            | _ -> [])
