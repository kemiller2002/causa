// Owner: integration (T17). The real adapter (IF41-VIEW UiBackend over the frozen IF41-APP Engine): UiIntent -> Msg and
// Model -> AppView. Its shape follows the governed sketch (proposals/r10-contract-first-ui/real-adapter-sketch). The
// engine decides; this module only translates and holds UI-local text the frozen Model does not (form fields before
// submit, the latest announcements).
namespace Causa.App.Ui

open System
open System.Security.Cryptography
open System.Text
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Ui.Contracts

/// Command identities (causa-storage contract section 2.9): every CommandId is a UUIDv7 whose 48-bit timestamp is the
/// command's RequestedAt in milliseconds, so the time is fixed when the command is created and is the same in the view,
/// in every re-decision and in what is saved. Integration's helper until persistence's Writes.commandIdAt and
/// Writes.requestedAt (slice T19) are merged; then both delegate to those.
[<RequireQualifiedAccess>]
module CommandIds =
    /// A UUIDv7 for a command requested at `requestedAt` (persistence's Writes.commandIdAt; the rest is random).
    let at (requestedAt: DateTimeOffset) : Guid =
        Writes.commandIdAt requestedAt (System.Security.Cryptography.RandomNumberGenerator.GetBytes 16)

    /// The RequestedAt a UUIDv7 command id carries (Writes.requestedAt); None for any other id (a save fails closed).
    let requestedAt (id: Guid) : DateTimeOffset option = Writes.requestedAt id

/// How the adapter is configured at the edge (Program.fs).
type AdapterConfig =
    { /// A fresh investigation identifier for the Start form (INV-006), from the host.
      NewIdentifier: unit -> Guid
      /// The host clock, for new command identities (CommandIds.at).
      Now: unit -> DateTimeOffset
      /// The working-copy run the investigations of this tab are created in (CONTRACT section 9.4).
      Run: string
      /// True while no persistence adapter is wired: Start stays offered and the screens say nothing is saved.
      SessionLocal: bool
      /// The Start form's validation (Views/Start.fs StartScreen.validate, which compiles after this file).
      ValidateStart: StartForm -> ValidationError list
      /// A session's current investigation through the kernel (Update.investigationOf, which compiles after this file).
      InvestigationOf: Model -> InvestigationRef -> Result<Investigation option, string>
      /// Whether a save machine is wired (the persistence adapter, T19); without it Save is unavailable, with a reason.
      CanSave: bool
      /// The working copy this tab lists and creates in, when there is one (Retry lists it again).
      WorkingCopy: WorkspaceAddress.T option }

/// UI-local state the frozen Model does not hold. The credential is the frozen opaque type and is never rendered.
type Local =
    { Route: UiRoute
      Form: ConnectForm
      Credential: Credential.T option
      Errors: ValidationError list
      Start: StartForm
      /// The id of the Start command this form submits: fixed when the form opens, so a resubmission is a Duplicate.
      StartCommandId: Guid
      StartErrors: ValidationError list
      Submission: Submission
      /// The newest first; the shell shows the latest of each politeness.
      Announcements: (Politeness * string) list
      /// Connected with the deployed test build's injected connection (EXT-DATA-004-O5), not a typed key.
      DeployConnected: bool
      /// The last listing that failed, as the list shows it (never the empty state); cleared by a listing that lands.
      ListFailure: UiFailure option
      /// Why the deployed test build's injected connection did not connect (UI-010: what happened and a next step).
      DeployFailure: string option
      /// The content editors' fields as typed (observation INV-024/025, hypothesis INV-026/011), before submit.
      Content: Map<FieldId, string>
      ContentErrors: ValidationError list }

type AdapterState = { Model: Model; Local: Local }

/// Which content editor a record action submits.
type ContentKind =
    | Observation
    | Hypothesis

/// The analysis the workspace sections render, read from a replayed Investigation (Data Understood once the adapter
/// wraps it with Understood.projected at the investigation's version). Pure; it invents nothing: a value the domain
/// does not hold is not shown, and a section with nothing in it is not linked.
[<RequireQualifiedAccess>]
module WorkspaceAnalysis =
    let private text (t: NonEmptyText) = NonEmptyText.value t
    let private textOpt (t: NonEmptyText option) = t |> Option.map NonEmptyText.value
    let private guid (g: Guid) = g.ToString "D"
    let private hid (HypothesisId g) = guid g
    let private version (AggregateVersion v) = v

    /// "MarkedPlausible" -> "marked plausible"
    let private words (name: string) =
        let sb = StringBuilder()
        name |> Seq.iteri (fun i c -> (if i > 0 && Char.IsUpper c then sb.Append(' ') |> ignore); sb.Append(Char.ToLowerInvariant c) |> ignore)
        sb.ToString()

    let hypothesisState (s: HypothesisState) =
        match s with
        | HypothesisState.Proposed -> "Proposed"
        | HypothesisState.Plausible -> "Plausible"
        | HypothesisState.Supported -> "Supported"
        | HypothesisState.Weakened -> "Weakened"
        | HypothesisState.Falsified -> "Falsified"
        | HypothesisState.Superseded -> "Superseded"

    let private kind (k: Causa.Domain.RelationshipKind) : KindShown =
        match k with
        | Causa.Domain.RelationshipKind.Supports -> CatalogKind Supports
        | Causa.Domain.RelationshipKind.Contradicts -> CatalogKind Contradicts
        | Causa.Domain.RelationshipKind.ConsistentWith -> CatalogKind ConsistentWith
        | Causa.Domain.RelationshipKind.Inconclusive -> CatalogKind Inconclusive
        | Causa.Domain.RelationshipKind.Discriminates -> UninterpretedKind "discriminates"
        | Causa.Domain.RelationshipKind.Insufficient -> UninterpretedKind "insufficient"

    let private category (c: EvidenceCategory) =
        match c with
        | EvidenceCategory.DirectObservation -> "Direct observation"
        | EvidenceCategory.Measurement -> "Measurement"
        | EvidenceCategory.Log -> "Log"
        | EvidenceCategory.Document -> "Document"
        | EvidenceCategory.ExperimentResult -> "Experiment result"
        | EvidenceCategory.Testimony -> "Testimony"
        | EvidenceCategory.ExternalReference -> "External reference"

    let conclusionStrength (s: ConclusionStrength) =
        match s with
        | ConclusionStrength.PlausibleExplanation -> "Plausible explanation"
        | ConclusionStrength.LeadingExplanation -> "Leading explanation"
        | ConclusionStrength.SupportedCause -> "Supported cause"
        | ConclusionStrength.ConfirmedCause -> "Confirmed cause"
        | ConclusionStrength.AcceptedWithResidualUncertainty -> "Accepted with residual uncertainty"

    let private evidenceLabel (e: EvidenceEntity) =
        match e.Label with
        | Some l -> text l
        | None ->
            let c = text e.Content
            if c.Length <= 80 then c else c.Substring(0, 77).TrimEnd() + "…"

    let links (i: Investigation) : LinkCard list =
        i.Relationships
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun r -> r.Provenance.RecordedAt)
        |> List.map (fun r ->
            let ev = i.Evidence |> Map.tryFind r.EvidenceId
            let hy = i.Hypotheses |> Map.tryFind r.HypothesisId
            // INV-030: the latest assessment of a pair is current; earlier ones stay as history.
            let latest =
                i.Relationships
                |> Map.toList
                |> List.map snd
                |> List.filter (fun x -> x.EvidenceId = r.EvidenceId && x.HypothesisId = r.HypothesisId)
                |> List.maxBy (fun x -> x.Provenance.RecordedAt)
            { Id = guid r.Id
              Evidence = { Kind = EvidenceEntity; Id = guid r.EvidenceId }
              EvidenceLabel = ev |> Option.map evidenceLabel |> Option.defaultValue "Evidence not on record"
              Hypothesis = { Kind = HypothesisEntity; Id = hid r.HypothesisId }
              HypothesisStatement = hy |> Option.map (fun h -> text h.Statement) |> Option.defaultValue "Hypothesis not on record"
              Kind = kind r.Kind
              Rationale = textOpt r.Rationale |> Option.defaultValue ""
              Current = (latest.Id = r.Id) })

    let private summary (p: EventPayload) =
        match p with
        | ExtensionEvent(_, _) -> "Extension recorded"
        | CoreEvent e ->
            match e with
            | InvestigationStarted s -> "Investigation started: " + text s.Title
            | ObservationRecorded o -> "Observation recorded: " + text o.Original
            | HypothesisRecorded h -> "Hypothesis recorded: " + text h.Statement
            | EvidenceAdmitted ev -> "Evidence admitted: " + text ev.Content
            | FindingRecorded f -> "Finding recorded: " + text f.OriginalResult
            | ConclusionRecorded c -> "Conclusion recorded: " + text c.Statement
            | UnknownIdentified u -> "Unknown identified: " + text u.Question
            | other ->
                let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(other, typeof<CoreEvent>)
                let w = words case.Name
                string (Char.ToUpperInvariant w[0]) + w.Substring 1

    let private eventType (p: EventPayload) =
        match p with
        | ExtensionEvent(_, _) -> "Extension"
        | CoreEvent e -> (fst (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<CoreEvent>))).Name

    let private actor (a: Actor) =
        match a with
        | Actor.Human id
        | Actor.System id -> text id
        | Actor.Agent(id, _, _) -> text id

    let ofInvestigation (actions: Map<ActionId, Availability>) (i: Investigation) : AnalysisProjection =
        let allLinks = links i
        let observations =
            i.Observations
            |> Map.toList
            |> List.map snd
            |> List.sortBy (fun o -> o.Provenance.RecordedAt)
            |> List.map (fun o -> { Id = guid o.Id; Text = text o.Original; SourceReference = textOpt o.Source; ObservedAt = o.ObservedAt })
        let hypotheses =
            i.Hypotheses
            |> Map.toList
            |> List.map snd
            |> List.sortBy (fun h -> h.Provenance.RecordedAt)
            |> List.map (fun h ->
                { Id = hid h.Id
                  Statement = text h.Statement
                  State = hypothesisState h.State
                  Explanation = textOpt h.Mechanism
                  Links = allLinks |> List.filter (fun l -> l.Hypothesis.Id = hid h.Id) })
        let evidence =
            i.Evidence
            |> Map.toList
            |> List.map snd
            |> List.sortBy (fun e -> e.Provenance.RecordedAt)
            |> List.map (fun e ->
                { Id = guid e.Id
                  Label = textOpt e.Label
                  Content = text e.Content
                  SourceReference = Some(text e.Source)
                  SourceUrl = e.SourceUrl |> Option.bind SafeUrl.tryCreate
                  Category = Some(category e.Category)
                  CapturedAt = e.CapturedAt
                  Links = allLinks |> List.filter (fun l -> l.Evidence.Id = guid e.Id) })
        let timeline =
            i.Timeline
            |> List.map (fun e ->
                { Version = version e.Version
                  EventType = eventType e.Payload
                  Summary = summary e.Payload
                  OccurredAt = e.Provenance.OccurredAt
                  Actor = Some(actor e.Provenance.Actor)
                  Affects = [] })
        let conclusions =
            i.Conclusions
            |> Map.toList
            |> List.map snd
            |> List.sortBy (fun c -> c.RecordedAtVersion)
            |> List.map (fun c ->
                { Id = guid c.Id
                  Statement = text c.Statement
                  Strength = conclusionStrength c.Strength
                  ResidualUncertainty = textOpt c.ResidualUncertainty
                  RecordedAtVersion = Some(version c.RecordedAtVersion) })
        let openContradictions =
            i.Contradictions |> Map.filter (fun _ c -> (c.State <> ContradictionState.ContradictionResolved && c.State <> ContradictionState.ContradictionAcceptedResidual)) |> Map.count
        { Observations = observations
          Hypotheses = hypotheses
          Evidence = evidence
          Relationships = allLinks
          Timeline = timeline
          Conclusions = conclusions
          NextSteps = []
          UnresolvedContradictions = Known openContradictions
          CorrectiveActionsSupported = false
          Actions = actions }

    /// The content actions an editable investigation offers, as the kernel's capabilities state them (UI-004): available,
    /// or unavailable with the kernel's first blocker as the reason.
    let contentActions (i: Investigation) : Map<ActionId, Availability> =
        let caps = Capabilities.evaluate Policies.coreDefault i
        let of' (command: string) =
            match caps |> List.tryFind (fun c -> c.Id = command) with
            | Some c when c.Blockers.IsEmpty -> Available
            | Some c -> Unavailable(c.Blockers.Head.Message)
            | None -> Available
        Map.ofList [ Actions.recordObservation, of' "RecordObservation"; Actions.recordHypothesis, of' "RecordHypothesis" ]

    /// The sections that have something to show, in the fixed order (UI-029-O2); the Overview always.
    let sections (target: RouteTarget) (current: Section) (offered: bool) (i: Investigation) : NavLink<Section> list =
        let has s =
            match s with
            | Overview -> true
            | CausalAnalysis -> not i.Relationships.IsEmpty
            | EvidenceSection -> not i.Evidence.IsEmpty
            // Hypotheses also when one can be recorded here (an editable working copy).
            | HypothesesSection -> not i.Hypotheses.IsEmpty || offered
            | TimelineSection -> not i.Timeline.IsEmpty
            | CorrectiveActions -> false
        Navigation.sections
        |> List.filter has
        |> List.map (fun s ->
            { Destination = s
              Label = Navigation.sectionLabel s
              Href = Navigation.format (WorkspaceRoute(target, s))
              Current = (s = current) })


[<RequireQualifiedAccess>]
module UiAdapter =
    /// Total over the frozen SM-SAVE states: an eighth state, or a missing one, fails to compile (PERSIST-031).
    let saveKind (s: SaveState) : SaveStateKind =
        match s with
        | SaveState.Saved _ -> SavedKind
        | SaveState.Unsaved -> UnsavedKind
        | SaveState.Saving -> SavingKind
        | SaveState.Offline -> OfflineKind
        | SaveState.Conflict _ -> ConflictKind
        | SaveState.UnknownOutcome _ -> UnknownOutcomeKind
        | SaveState.SaveFailed _ -> SaveFailedKind

    /// A stable identifier derived from another and a purpose (the start command and its first observation), so the
    /// same submission always produces the same command (idempotent retries; Decide reports Duplicate).
    let derivedId (from: Guid) (purpose: string) =
        // SHA-256, not MD5: MD5 is not available in the browser (.NET WebAssembly).
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(from.ToString("D") + "/" + purpose)) |> Array.truncate 16
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x30uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(bytes)

    let workingCopy (config: AdapterConfig) =
        WorkspaceAddress.workingCopy [] config.Run |> Option.bind Writable.ofAddress

    /// The StartInvestigation command a valid Start form describes (INV-004).
    let startCommand (config: AdapterConfig) (commandId: Guid) (form: StartForm) : Result<CommandEnvelope, ValidationError list * string list> =
        match config.ValidateStart form, Guid.TryParse form.Identifier with
        | [], (true, id) ->
            let problem = form.ProblemStatement.Trim()
            // Coordinator's implementer decision (WI-INV-START wiring): INV-002 is optional but the kernel's Title cannot
            // be empty, so a blank title takes the problem statement's first line, trimmed and cut to 200 characters.
            let title =
                if String.IsNullOrWhiteSpace form.Title then
                    let first = problem.Split('\n').[0].Trim()
                    if first.Length > 200 then first.Substring(0, 200) else first
                else form.Title.Trim()
            match NonEmptyText.create title, NonEmptyText.create problem with
            | Some t, Some original ->
                Ok
                    { CommandId = commandId
                      Investigation = InvestigationId id
                      ExpectedVersion = None
                      Command = Core(StartInvestigation { Title = t; ObservationId = derivedId commandId "start-observation"; Original = original }) }
            | _ -> Error([], [ "A problem statement is required." ])
        | [], _ -> Error([], [ "The investigation identifier is not a GUID." ])
        | errors, _ -> Error(errors, errors |> List.map (fun e -> e.Message))

    let private announcementsOf (effects: HostEffect list) =
        effects |> List.choose (function LiveAnnounce(p, text) -> Some(p, text) | _ -> None) |> List.rev

    /// Runs one engine message and keeps the announcements it asked for.
    let private step (engine: Engine) (msg: Msg) (s: AdapterState) : AdapterState * HostEffect list =
        let model, effects = engine.Update msg s.Model
        { Model = model; Local = { s.Local with Announcements = announcementsOf effects @ s.Local.Announcements |> List.truncate 8 } }, effects

    let private connect (engine: Engine) (s: AdapterState) =
        match Connection.validate s.Local.Form, s.Local.Credential with
        | [], Some credential ->
            let repository = { Owner = s.Local.Form.Owner; Name = s.Local.Form.Repository; Branch = s.Local.Form.Branch }
            step engine (Msg.Connection(ConnectionInput.ConnectSubmitted(repository, credential))) { s with Local = { s.Local with Errors = [] } }
        | errors, credential ->
            let missing =
                match credential with
                | Some _ -> []
                | None -> [ { Field = Fields.token; Rule = Required; Message = "Enter an access token." } ]
            { s with Local = { s.Local with Errors = errors @ missing |> List.distinct } }, []

    let private freshStart (config: AdapterConfig) (local: Local) =
        { local with
            Start = StartForm.empty (config.NewIdentifier().ToString "D")
            StartCommandId = CommandIds.at (config.Now())
            StartErrors = []
            Submission = Submission.NotSubmitted }

    /// P19 = A: the primary question (INV-023) is persisted as an informational unknown, in its own command group right
    /// after the start (persistence lane's PrimaryQuestion.startCommands shape, slice T19 e55a5d5). None when blank.
    /// TODO(T19 merge): build both through PrimaryQuestion.startCommands and Writes.commandIdAt.
    let questionCommand (config: AdapterConfig) (start: CommandEnvelope) (question: string) : CommandEnvelope option =
        NonEmptyText.create (question.Trim())
        |> Option.map (fun q ->
            let commandId = CommandIds.at (config.Now())
            { CommandId = commandId
              Investigation = start.Investigation
              ExpectedVersion = Some(AggregateVersion 1L)
              Command = Core(IdentifyUnknown { UnknownId = derivedId commandId "primary-question"; Question = q; Materiality = Materiality.Informational }) })

    /// INV-004: validate, then CreateRequested in the working copy (and the question as the next command); on
    /// acceptance open the new investigation's workspace.
    /// Whether Start is offered: a writable connection, or the session-local build (UI-004: the engine's reason shown).
    let startAvailability (config: AdapterConfig) (s: AdapterState) =
        match s.Model.Connection with
        | Connected true -> Available
        | Connected false -> Unavailable "This token cannot save changes."
        | _ when config.SessionLocal -> Available
        | _ -> Unavailable "Connect a data repository first."

    let private start (config: AdapterConfig) (engine: Engine) (s: AdapterState) =
        match startAvailability config s with
        | Unavailable reason -> { s with Local = { s.Local with Submission = Submission.Refused [ reason ] } }, []
        | NotOffered -> s, []
        | Available ->
        match startCommand config s.Local.StartCommandId s.Local.Start, workingCopy config with
        | Error(errors, reasons), _ ->
            { s with Local = { s.Local with StartErrors = errors; Submission = Submission.Refused reasons } }, []
        | Ok _, None -> { s with Local = { s.Local with Submission = Submission.Refused [ "No working copy is available." ] } }, []
        | Ok envelope, Some writable ->
            let address = Writable.address writable
            let ref = { Address = address; Id = envelope.Investigation }
            let before = s.Model.Sessions.ContainsKey ref
            let s2, effects = step engine (Msg.CreateRequested(writable, envelope)) { s with Local = { s.Local with Submission = Submission.Submitting; StartErrors = [] } }
            if not before && s2.Model.Sessions.ContainsKey ref then
                // P19 = A: the question is the next command group (PrimaryQuestion.startCommands, T19).
                let questionId = CommandIds.at (config.Now())
                let rest = PrimaryQuestion.startCommands envelope (s.Local.Start.Question.Trim()) questionId (derivedId questionId "primary-question") |> List.tail
                let s2, effects =
                    rest |> List.fold (fun (st, acc) c -> let st2, fx = step engine (Msg.DomainCommand c) st in st2, acc @ fx) (s2, effects)
                let (Workspace workspace) = address.Workspace
                let route = WorkspaceRoute({ Workspace = workspace; Role = WorkspaceRole.WorkingCopy; Id = envelope.Investigation }, Overview)
                let s3, fx = step engine (Msg.Navigate(Route(Navigation.format route))) s2
                { s3 with Local = { s3.Local with Route = route; Submission = Submission.Accepted } }, effects @ fx
            else
                { s2 with Local = { s2.Local with Submission = Submission.Refused(announcementsOf effects |> List.map snd) } }, effects

    /// INV-022: back to the list, the draft dropped, nothing written.
    let private cancelStart (config: AdapterConfig) (engine: Engine) (s: AdapterState) =
        let s2, fx = step engine (Msg.Navigate(Route(Navigation.format InvestigationsRoute))) s
        { s2 with Local = { freshStart config s2.Local with Route = InvestigationsRoute } }, fx

    /// A browser signal for the engine (an HTTP answer, connectivity, a timer, a leave attempt).
    let browser (engine: Engine) (signal: BrowserSignal) (s: AdapterState) : AdapterState * HostEffect list =
        let s2, fx = step engine (Msg.Browser signal) s
        match signal with
        | HttpCompleted outcome when s.Local.DeployConnected && (match s.Model.Connection with Connecting id -> id = (match outcome with Answered(i, _, _, _) | NotAnswered(i, _) -> i) | _ -> false) ->
            match s2.Model.Connection, s.Model.Context with
            | Connected _, _ -> { s2 with Local = { s2.Local with DeployFailure = None } }, fx
            | _, Some ctx ->
                let target = sprintf "%s/%s@%s (test deployment)" ctx.Repository.Owner ctx.Repository.Name ctx.Repository.Branch
                let reason =
                    match Classify.classify Operation.Connect outcome with
                    | Error(NetworkUnavailable _) -> sprintf "Could not reach GitHub to connect to %s. Check your connection and try again." target
                    | Error e -> sprintf "Could not connect to %s. %s" target (Classify.message e)
                    | Ok _ -> sprintf "Could not connect to %s. GitHub sent an answer that could not be read." target
                { s2 with Local = { s2.Local with DeployFailure = Some reason } }, fx
            | _ -> s2, fx
        | HttpCompleted outcome ->
            let rid = match outcome with Answered(id, _, _, _) | NotAnswered(id, _) -> id
            let wasListing = s.Model.ListingsInFlight.ContainsKey rid
            let stillListing = s2.Model.ListingsInFlight |> Map.exists (fun _ (t, _) -> s.Model.ListingsInFlight.TryFind rid |> Option.exists (fun (t0, _) -> t0 = t))
            let landed = s2.Model.Listings <> s.Model.Listings
            if wasListing && landed then { s2 with Local = { s2.Local with ListFailure = None } }, fx
            elif wasListing && not stillListing then
                // The listing ended without a result: the same classifier the engine used says why.
                let failure =
                    match Classify.classify Operation.ReadHead outcome with
                    | Error(RateLimited _) -> UiFailure.ProviderRateLimited
                    | Error AuthenticationFailed -> UiFailure.CredentialRefused
                    | Error(PermissionDenied _)
                    | Error(NotFound _) -> UiFailure.NotAuthorized
                    | Error(NetworkUnavailable _) -> UiFailure.ProviderUnreachable
                    | Error e -> UiFailure.ProviderError(ReadFailure.text e)
                    | Ok _ -> UiFailure.ProviderError "The repository's catalog could not be read."
                { s2 with Local = { s2.Local with ListFailure = Some failure } }, fx
            else s2, fx
        | _ -> s2, fx

    /// The deployed test build's injected connection (EXT-DATA-004-O5): used only while the tab is disconnected, so a
    /// connection the user already made is never replaced. The key stays the opaque credential.
    let deployConnect (engine: Engine) (repository: Repository) (credential: Credential.T) (s: AdapterState) : AdapterState * HostEffect list =
        let retrying = s.Local.DeployConnected && s.Model.Connection = CredentialRefused
        match s.Model.Connection with
        | _ when s.Model.Connection = Disconnected || retrying ->
            let s2, fx = step engine (Msg.Connection(ConnectionInput.ConnectSubmitted(repository, credential))) s
            { s2 with
                Local =
                    { s2.Local with
                        DeployConnected = true
                        DeployFailure = None
                        Form = { Owner = repository.Owner; Repository = repository.Name; Branch = repository.Branch; CredentialEntered = true } } },
            fx
        | _ -> s, []

    /// The shell's own bindings beyond the AppView: the deploy connection's failure, shown as an alert with Try again.
    let shellBindings (s: AdapterState) : Bindings =
        Map.ofList
            [ "deploy.failed", BindingValue.Flag s.Local.DeployFailure.IsSome
              "deploy.failure-text", BindingValue.Text(s.Local.DeployFailure |> Option.defaultValue "") ]

    /// The investigation a route names: a catalog workspace, or a working copy (`workspaces/demo/causa-app-<run>`).
    let refOf (config: AdapterConfig) (target: RouteTarget) : InvestigationRef option =
        let prefix = "workspaces/demo/causa-app-"
        match target.Role with
        | WorkspaceRole.Catalog -> Some { Address = WorkspaceAddress.catalog (Workspace target.Workspace); Id = target.Id }
        | WorkspaceRole.WorkingCopy when target.Workspace.StartsWith prefix ->
            WorkspaceAddress.workingCopy [] (target.Workspace.Substring prefix.Length) |> Option.map (fun a -> { Address = a; Id = target.Id })
        | WorkspaceRole.WorkingCopy -> None

    let private sessionOfRoute (s: AdapterState) =
        match s.Local.Route with
        | WorkspaceRoute(target, _) -> s.Model.Sessions |> Map.tryFindKey (fun ref _ -> ref.Id = target.Id && ref.Address.Workspace = Workspace target.Workspace)
        | _ -> None

    /// INV-014 / INV-009: validate the editor, then send the kernel the command at the session's current version.
    let private recordContent (config: AdapterConfig) (engine: Engine) (kind: ContentKind) (s: AdapterState) =
        let spec, fields =
            match kind with
            | Observation -> ContentForms.observation, [ Fields.observationText; Fields.observationSource ]
            | Hypothesis -> ContentForms.hypothesisOnRecord, [ Fields.hypothesisStatement; Fields.hypothesisExplanation ]
        match sessionOfRoute s with
        | None -> s, []
        | Some ref ->
            match Validation.checkAll spec s.Local.Content with
            | _ :: _ as errors -> { s with Local = { s.Local with ContentErrors = errors } }, []
            | [] ->
                match config.InvestigationOf s.Model ref with
                | Ok(Some i) ->
                    let value f = s.Local.Content |> Map.tryFind f |> Option.defaultValue ""
                    let cmdId = CommandIds.at (config.Now())
                    let command =
                        match kind with
                        | Observation ->
                            Core(
                                RecordObservation
                                    { ObservationId = derivedId cmdId "observation"
                                      Original = (NonEmptyText.create (value Fields.observationText)).Value
                                      ObservedAt = None
                                      Source = NonEmptyText.create (value Fields.observationSource) }
                            )
                        | Hypothesis ->
                            Core(
                                RecordHypothesis
                                    { HypothesisId = HypothesisId(derivedId cmdId "hypothesis")
                                      Statement = (NonEmptyText.create (value Fields.hypothesisStatement)).Value
                                      Mechanism = NonEmptyText.create (value Fields.hypothesisExplanation) }
                            )
                    let envelope = { CommandId = cmdId; Investigation = ref.Id; ExpectedVersion = Some i.Version; Command = command }
                    let s2, fx = step engine (Msg.DomainCommand envelope) s
                    { s2 with
                        Local =
                            { s2.Local with
                                Content = fields |> List.fold (fun m f -> Map.remove f m) s2.Local.Content
                                ContentErrors = [] } },
                    fx
                | _ -> s, []

    let private currentRef (s: AdapterState) =
        match s.Local.Route with
        | WorkspaceRoute(t, _) ->
            s.Model.Sessions |> Map.tryFindKey (fun ref _ -> ref.Id = t.Id && ref.Address.Workspace = Workspace t.Workspace)
        | _ -> None

    let dispatch (config: AdapterConfig) (engine: Engine) (intent: UiIntent) (s: AdapterState) : AdapterState * HostEffect list =
        match intent with
        | UiIntent.Navigate r ->
            let s1, fx0 = step engine (Msg.Navigate(Route(Navigation.format r))) s
            // Opening a workspace that is neither being edited nor loaded reads it (SM-LOAD).
            let s2, fx =
                match r with
                | WorkspaceRoute(target, _) ->
                    match refOf config target with
                    | Some ref when not (s1.Model.Sessions.ContainsKey ref) ->
                        match s1.Model.Loads |> Map.tryFind ref with
                        | Some(Loaded _)
                        | Some(LoadState.Loading _) -> s1, fx0
                        | _ ->
                            let s3, fx1 = step engine (Msg.LoadStep(ref, LoadInput.OpenRequested ref)) s1
                            s3, fx0 @ fx1
                    | _ -> s1, fx0
                | _ -> s1, fx0
            let local =
                match r, s.Local.Route with
                | StartRoute, StartRoute -> s2.Local
                | StartRoute, _ -> freshStart config s2.Local
                | _ -> s2.Local
            { s2 with Local = { local with Route = r } }, fx
        | UiIntent.Input(f, v, _) when Connection.fields |> List.exists (fun x -> x.Field = f) ->
            { s with Local = { s.Local with Form = Connection.withField f v s.Local.Form } }, []
        | UiIntent.Input(f, v, _) when StartForm.fields |> List.exists (fun x -> x.Field = f) ->
            { s with Local = { s.Local with Start = StartForm.withField f v s.Local.Start; Submission = Submission.NotSubmitted } }, []
        | UiIntent.Input(f, v, _) when ContentForms.observation @ ContentForms.hypothesis |> List.exists (fun x -> x.Field = f) ->
            { s with Local = { s.Local with Content = s.Local.Content |> Map.add f v } }, []
        | UiIntent.Input _ -> s, []
        | UiIntent.Secret(_, c) ->
            { s with Local = { s.Local with Credential = Some c; Form = { s.Local.Form with CredentialEntered = true } } }, []
        | UiIntent.Activate(a, _) when a = Actions.connect -> connect engine s
        | UiIntent.Activate(a, _) when a = Actions.disconnect -> step engine (Msg.Connection ConnectionInput.DisconnectConfirmed) s
        | UiIntent.Activate(a, _) when a = Actions.startInvestigation -> start config engine s
        | UiIntent.Activate(a, _) when a = Actions.recordObservation -> recordContent config engine Observation s
        | UiIntent.Activate(a, _) when a = Actions.recordHypothesis -> recordContent config engine Hypothesis s
        | UiIntent.Activate(a, _) when a = Actions.cancelEditor ->
            let cleared = [ Fields.observationText; Fields.observationSource; Fields.hypothesisStatement; Fields.hypothesisExplanation ]
            { s with Local = { s.Local with Content = cleared |> List.fold (fun m f -> Map.remove f m) s.Local.Content; ContentErrors = [] } }, []
        | UiIntent.Activate(a, _) when a = Actions.cancelStart -> cancelStart config engine s
        | UiIntent.Activate(a, _) when a = Actions.save || a = ActionId "UIA-SAVE-RETRY" ->
            match currentRef s with
            | Some ref -> step engine (Msg.SessionInput(ref, SaveInput.SaveRequested)) s
            | None -> s, []
        | UiIntent.Activate(a, _) when a = ActionId "UIA-SAVE-CHECK-AGAIN" ->
            match currentRef s with
            | Some ref -> step engine (Msg.SessionInput(ref, SaveInput.CheckAgain)) s
            | None -> s, []
        | UiIntent.Activate(a, _) when a = ActionId "UIA-SAVE-RECONNECT" ->
            // Reconnect is the Connect form again, on the list (EXT-DATA-004-O3: the key is entered again).
            let s2, fx = step engine (Msg.Connection ConnectionInput.DisconnectConfirmed) s
            let s3, fx2 = step engine (Msg.Navigate(Route(Navigation.format InvestigationsRoute))) s2
            { s3 with Local = { s3.Local with Route = InvestigationsRoute; DeployConnected = false } }, fx @ fx2
        | UiIntent.Activate(a, _) when a = Actions.retryLoad ->
            match s.Model.Connection with
            | Connected _ ->
                let s1 = { s with Local = { s.Local with ListFailure = None } }
                let s2, fx = step engine (Msg.ListRequested ActiveCatalog) s1
                match config.WorkingCopy with
                | Some wc ->
                    let s3, fx2 = step engine (Msg.ListRequested(WorkingCopyOf wc)) s2
                    s3, fx @ fx2
                | None -> s2, fx
            | _ -> s, []
        // Content commands arrive with the workspace screens (T21 onwards); until then they change nothing.
        | UiIntent.Activate _ -> s, []

    let connectionView (s: AdapterState) : ConnectionView =
        { State = s.Model.Connection
          Form = s.Local.Form
          Errors = s.Local.Errors
          Connect = (match s.Model.Connection with Connected _ | Connecting _ -> NotOffered | _ -> Available)
          Disconnect = (match s.Model.Connection with Connected _ -> Available | _ -> NotOffered)
          Connected =
            s.Model.Context
            |> Option.map (fun c ->
                if s.Local.DeployConnected then sprintf "%s/%s@%s (test deployment)" c.Repository.Owner c.Repository.Name c.Repository.Branch
                else sprintf "%s/%s on %s" c.Repository.Owner c.Repository.Name c.Repository.Branch)
          WriteScope = (match s.Model.Connection with Connected true -> Writable | Connected false -> ReadOnlyConnection | _ -> WriteScopeUnknown) }

    let private sessionLocalFailure =
        UiFailure.CapabilityAbsent(DataVisible, "No data repository is connected in this build: changes stay in this tab and are not saved.")

    let startView (config: AdapterConfig) (s: AdapterState) : StartView =
        let destination =
            match s.Model.Context, s.Model.Connection with
            | Some c, Connected canWrite -> Ready { Repository = c.Repository.Owner + "/" + c.Repository.Name; Branch = c.Repository.Branch; Writable = canWrite }
            | _ when config.SessionLocal -> Failed sessionLocalFailure
            | _ -> Failed(UiFailure.CapabilityAbsent(DataVisible, "Connect a data repository to choose where the investigation is saved."))
        { Form = s.Local.Start
          Errors = s.Local.StartErrors
          Destination = destination
          Profile = None
          Start = startAvailability config s
          Cancel = Available
          Submission = s.Local.Submission }

    /// One card per listed investigation (UI-001, UI-022): title and place as stored (Data Visible); lifecycle and
    /// counts are not claimed before the investigation is opened and replayed.
    let summaries (model: Model) (listing: Listing) : InvestigationSummary list =
        listing.Investigations
        |> List.map (fun i ->
            let loadedTitle =
                match model.Loads |> Map.tryFind i.Ref with
                | Some(Loaded v) -> Some(NonEmptyText.value v.Investigation.Title)
                | _ -> None
            let (Workspace workspace) = i.Ref.Address.Workspace
            let catalog = i.Ref.Address.Role = WorkspaceRole.Catalog
            let route =
                WorkspaceRoute(
                    { Workspace = workspace; Role = (if catalog then WorkspaceRole.Catalog else WorkspaceRole.WorkingCopy); Id = i.Ref.Id },
                    Overview
                )
            { Ref = i.Ref
              Title = loadedTitle |> Option.orElse i.Title |> Option.defaultValue (let (InvestigationId g) = i.Ref.Id in g.ToString "D")
              Status = None
              LastActivity = None
              Counts = None
              Copy = (if catalog then FrozenDemonstration else WorkingCopy)
              Href = Navigation.format route })

    let listView (config: AdapterConfig) (s: AdapterState) : InvestigationListView =
        let listings = s.Model.Listings |> Map.toList |> List.map snd
        let investigations, state =
            match s.Model.Connection, listings with
            | Connected _, _ when s.Local.ListFailure.IsSome ->
                let f = s.Local.ListFailure.Value
                Failed f, ScreenFailed f
            | Connected _, [] when s.Model.ListingsInFlight.IsEmpty ->
                Failed(UiFailure.ProviderError "The investigations could not be listed."), ScreenFailed(UiFailure.ProviderError "The investigations could not be listed.")
            | Connected _, [] -> Waiting "Listing investigations", ScreenLoading
            | Connected _, _ ->
                let all = listings |> List.collect (summaries s.Model)
                let (CommitOid commit) = (List.head listings).ReadAt
                Ready(Visible.stored { Path = "catalog/active.json"; Commit = Some commit } all), (if all.IsEmpty then ScreenEmpty else ScreenReady)
            | _ -> NotRequested, ScreenReady
        { Connection = connectionView s
          Investigations = investigations
          Sort = ByUpdated
          NewInvestigation =
            (match s.Model.Connection with
             | Connected true -> Available
             | Connected false -> Unavailable "This token cannot save changes."
             | _ when config.SessionLocal -> Available
             | _ -> NotOffered)
          State = state }

    let private notFound (requested: string) : NotFoundView =
        { Requested = requested
          Copy =
            { What = sprintf "There is no page at %s." requested
              Preserved = "Nothing was changed."
              NextStep = "Go to Investigations."
              Retry = false } }

    /// The words and choices of a save state (PERSIST-031; the total mapping above).
    let savePresentation (config: AdapterConfig) (s: AdapterState) (session: InvestigationSession) : SavePresentation =
        let kind = saveKind session.State
        let saveAvailability =
            match session.State with
            | SaveState.Unsaved when not config.CanSave -> Unavailable "Saving arrives with the data repository writer; this build keeps changes in this tab."
            | SaveState.Unsaved -> Available
            | SaveState.SaveFailed(Refused(RateLimited _))
            | SaveState.SaveFailed(Refused(NetworkUnavailable _))
            | SaveState.SaveFailed RepositoryKeptChanging when config.CanSave -> Available
            | SaveState.SaveFailed _ -> Unavailable "This save cannot be retried as it is; your changes are kept in this tab."
            | SaveState.Saving -> Unavailable "Saving now."
            | _ -> NotOffered
        let notSaved =
            s.Model.Sessions
            |> Map.toList
            |> List.filter (fun (_, x) -> not x.Unsaved.IsEmpty || not x.AfterInFlight.IsEmpty)
            |> List.map (fun (ref, _) ->
                match config.InvestigationOf s.Model ref with
                | Ok(Some i) -> NonEmptyText.value i.Title
                | _ -> let (InvestigationId g) = ref.Id in g.ToString "D")
        { Kind = kind
          Indicator =
            { InvestigationId = session.Id
              Label = PresentationLanguage.saveState kind
              Text = PresentationLanguage.saveState kind
              Glyph = ""
              Actions = SaveChoices.forKind kind
              Politeness = (match kind with ConflictKind | SaveFailedKind | UnknownOutcomeKind -> Assertive | _ -> Polite)
              LastSavedCommitTime =
                (match session.State with
                 | SaveState.Saved(Confirmed c) -> Some c.CommitTime
                 | _ -> None) }
          Save = saveAvailability
          Conflict = None
          NotSaved = notSaved }

    let private lifecycleText (l: Lifecycle) =
        match l with
        | Lifecycle.Draft -> "Draft"
        | Lifecycle.Active -> "Active"
        | Lifecycle.AwaitingEvidence -> "Awaiting evidence"
        | Lifecycle.AwaitingVerification -> "Awaiting verification"
        | Lifecycle.Resolved -> "Resolved"
        | Lifecycle.Closed -> "Closed"
        | Lifecycle.Archived -> "Archived"

    /// The frame of an investigation the kernel projected (UI-013: every value at one version).
    let frameOf (target: RouteTarget) (section: Section) (ref: InvestigationRef) (i: Investigation) : WorkspaceFrame =
        let at v = AsProjected(Understood.projected i.Version v)
        let problem =
            i.Timeline
            |> List.tryPick (fun e ->
                match e.Payload with
                | CoreEvent(InvestigationStarted s) -> Some(NonEmptyText.value s.Original)
                | _ -> None)
            |> Option.defaultValue ""
        // P19 = A: the primary question is the informational unknown recorded with the start.
        let question =
            i.Unknowns
            |> Map.toList
            |> List.map snd
            |> List.filter (fun u -> u.Materiality = Materiality.Informational)
            |> List.sortBy (fun u -> u.Provenance.RecordedAt)
            |> List.tryHead
            |> Option.map (fun u -> at (NonEmptyText.value u.Question))
        let (InvestigationId g) = i.Id
        { Ref = ref
          Identifier = g.ToString "D"
          Title = NonEmptyText.value i.Title
          ProblemStatement = at problem
          Question = question
          CreatedAt = at i.CreatedAt
          InvestigationState = at (lifecycleText i.Lifecycle)
          Copy = (if ref.Address.Role = WorkspaceRole.WorkingCopy then WorkingCopy else FrozenDemonstration)
          // Only the sections that have content (WorkspaceAnalysis.sections).
          Sections = WorkspaceAnalysis.sections target section (ref.Address.Role = WorkspaceRole.WorkingCopy) i
          Version = Some i.Version }

    let workspaceView (config: AdapterConfig) (s: AdapterState) (target: RouteTarget) (section: Section) : WorkspaceView =
        let sectionView r =
            match section with
            | Overview -> OverviewSection r
            | CausalAnalysis -> CausalAnalysisView r
            | EvidenceSection -> EvidenceView r
            | HypothesesSection -> HypothesesView r
            | TimelineSection -> TimelineView r
            | CorrectiveActions -> CorrectiveActionsView r
        let analysisOf (actions: Map<ActionId, Availability>) (i: Investigation) =
            sectionView (Ready(Understood.projected i.Version (WorkspaceAnalysis.ofInvestigation actions i)))
        let emptyWith analysis frame state save details =
            { Frame = frame
              Stored = NotRequested
              Section = analysis
              Drafts = { Values = Map.empty; Errors = []; OpenEditor = None }
              Details = details
              Save = save
              State = state }
        let empty frame state save details = emptyWith (sectionView NotRequested) frame state save details
        let closedDetails form = { Open = false; Form = form; Errors = []; Edit = NotOffered; SaveDetails = NotOffered; Cancel = NotOffered }
        let blank = closedDetails { Title = ""; ProblemStatement = ""; Question = "" }
        match s.Model.Sessions |> Map.tryFindKey (fun ref _ -> ref.Id = target.Id && ref.Address.Workspace = Workspace target.Workspace) with
        | Some ref ->
            match config.InvestigationOf s.Model ref with
            | Ok(Some i) ->
                let frame = frameOf target section ref i
                let details =
                    closedDetails
                        { Title = frame.Title
                          ProblemStatement = (match frame.ProblemStatement with AsProjected u -> Understood.value u | AsStored v -> Visible.value v)
                          Question = frame.Question |> Option.map (function AsProjected u -> Understood.value u | AsStored v -> Visible.value v) |> Option.defaultValue "" }
                let v = emptyWith (analysisOf (WorkspaceAnalysis.contentActions i) i) (Ready frame) ScreenReady (Some(savePresentation config s s.Model.Sessions[ref])) details
                { v with Drafts = { Values = s.Local.Content; Errors = s.Local.ContentErrors; OpenEditor = None } }
            | Ok None -> let f = UiFailure.InvestigationNotFound target in empty (Failed f) (ScreenFailed f) None blank
            | Error reason -> let f = UiFailure.InvalidData reason in empty (Failed f) (ScreenFailed f) None blank
        | None ->
            match refOf config target with
            | None -> let f = UiFailure.InvestigationNotFound target in empty (Failed f) (ScreenFailed f) None blank
            | Some ref ->
                match s.Model.Loads |> Map.tryFind ref with
                | Some(Loaded v) ->
                    let frame = frameOf target section ref v.Investigation
                    let save =
                        if ref.Address.Role = WorkspaceRole.WorkingCopy then
                            let kind = SavedKind
                            Some
                                { Kind = kind
                                  Indicator =
                                    { InvestigationId = ref.Id
                                      Label = PresentationLanguage.saveState kind
                                      Text = PresentationLanguage.saveState kind
                                      Glyph = ""
                                      Actions = []
                                      Politeness = Polite
                                      LastSavedCommitTime = None }
                                  Save = NotOffered
                                  Conflict = None
                                  NotSaved = [] }
                        else None
                    let details =
                        closedDetails
                            { Title = frame.Title
                              ProblemStatement = (match frame.ProblemStatement with AsProjected u -> Understood.value u | AsStored v -> Visible.value v)
                              Question = frame.Question |> Option.map (function AsProjected u -> Understood.value u | AsStored v -> Visible.value v) |> Option.defaultValue "" }
                    emptyWith (analysisOf Map.empty v.Investigation) (Ready frame) ScreenReady save details
                | Some(LoadState.Loading _) -> empty (Waiting "Opening investigation") ScreenLoading None blank
                | Some(LoadFailed(ProviderFailed(NotFound _))) -> let f = UiFailure.InvestigationNotFound target in empty (Failed f) (ScreenFailed f) None blank
                | Some(LoadFailed(ProviderFailed AuthenticationFailed)) -> let f = UiFailure.CredentialRefused in empty (Failed f) (ScreenFailed f) None blank
                | Some(LoadFailed(ProviderFailed e)) -> let f = UiFailure.ProviderError(Classify.message e) in empty (Failed f) (ScreenFailed f) None blank
                | Some(LoadFailed(Unverifiable fs)) -> let f = UiFailure.InvalidData(sprintf "%d verification failure(s)" fs.Length) in empty (Failed f) (ScreenFailed f) None blank
                | Some UnavailableOffline -> let f = UiFailure.ProviderUnreachable in empty (Failed f) (ScreenFailed f) None blank
                | Some NotLoaded
                | None ->
                    match s.Model.Connection with
                    | Connected _ -> empty (Waiting "Opening investigation") ScreenLoading None blank
                    | _ -> let f = UiFailure.CapabilityAbsent(DataVisible, "Connect a data repository to open this investigation.") in empty (Failed f) (ScreenFailed f) None blank

    let private unknownFact label = { Label = label; Status = FactUnknown }

    let administrationView (s: AdapterState) : AdministrationView =
        let connected = s.Model.Context |> Option.map (fun c -> sprintf "%s/%s on %s" c.Repository.Owner c.Repository.Name c.Repository.Branch)
        { Provider = { Label = "Persistence provider"; Status = FactKnown "GitHub" }
          Repository = (match connected with Some r -> { Label = "Data repository"; Status = FactKnown r } | None -> unknownFact "Data repository")
          Connection = { Label = "Connection"; Status = FactKnown(Connection.describe s.Model.Connection) }
          Catalog = unknownFact "Investigation catalog"
          StorageContract = unknownFact "Storage contract"
          ApplicationVersion = unknownFact "Application version"
          DeployedCommit = unknownFact "Deployed commit"
          DataValidation = unknownFact "Data validation"
          Diagnostics = [] }

    let settingsView (s: AdapterState) : SettingsView =
        { // The one preference this build has: the investigation list's order, which the list view applies (ByUpdated).
          Preferences =
            [ { Id = "list-sort"
                Label = "Investigation list order"
                Scope = SessionScope
                Options = [ "Most recently updated"; "Title" ]
                Effective = "Most recently updated" } ]
          ConnectionPreference = { Label = "Data repository connection"; Status = FactKnown(Connection.describe s.Model.Connection) } }

    let project (config: AdapterConfig) (s: AdapterState) : AppView =
        let screen =
            match s.Local.Route with
            | InvestigationsRoute -> ListScreen(listView config s)
            | StartRoute -> StartScreen(startView config s)
            | AdministrationRoute -> AdministrationScreen(administrationView s)
            | SettingsRoute -> SettingsScreen(settingsView s)
            | NotFoundRoute requested -> NotFoundScreen(notFound requested)
            | WorkspaceRoute(target, section) -> WorkspaceScreen(workspaceView config s target section)
        { Route = s.Local.Route
          Primary = Navigation.primary s.Local.Route
          Screen = screen
          Announcements = s.Local.Announcements
          Origin = IntegratedBackend }

    let init (config: AdapterConfig) (engine: Engine) (route: UiRoute) : AdapterState * HostEffect list =
        let model, effects = engine.Init(Route(Navigation.format route))
        { Model = model
          Local =
            { Route = route
              Form = Connection.emptyForm
              Credential = None
              Errors = []
              Start = StartForm.empty (config.NewIdentifier().ToString "D")
              StartCommandId = CommandIds.at (config.Now())
              StartErrors = []
              Submission = Submission.NotSubmitted
              Announcements = []
              DeployConnected = false
              ListFailure = None
              DeployFailure = None
              Content = Map.empty
              ContentErrors = [] } },
        effects

    /// The frozen Engine as a UiBackend. Effects are the frozen HostEffect, performed by the browser host.
    let backend (config: AdapterConfig) (engine: Engine) : UiBackend<AdapterState, HostEffect> =
        { Provides = (if config.SessionLocal then [ DataEditable ] else [ DataVisible; DataEditable ])
          Init = init config engine
          Dispatch = dispatch config engine
          Project = project config }
