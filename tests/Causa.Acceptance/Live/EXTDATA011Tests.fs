// Owner: domain (v4 T15, frozen fixture replay against the storage contract). EXT-DATA-011: the frozen causa-storage
// contract is a reader/writer specification, the application vendors none of its demonstration records, the contract is
// versioned and incompatible data fails closed.
//
// Live (SU-DOTNET-LIVE): the catalog is read from the authorized GitHub test repository with CAUSA_GITHUB_TOKEN
// (read-only) at CAUSA_TEST_OWNER/CAUSA_TEST_REPO@CAUSA_TEST_BRANCH. Without a credential the tests fail closed with
// "BLOCKED: no credential"; they never pass vacuously. Every catalog case is decoded from its stored events by the
// contract's rules, replayed through the one domain kernel, and compared with the stored snapshot's state.
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Causa.Domain

/// The stored bytes of the catalog, by repository path.
type CatalogReader = string -> Result<string, string>

/// Why a stored history was refused (the contract's reader codes this replay checks).
type ReadRefusal =
    | UnsupportedVersion of line: int * version: string
    | MixedVersions of string list
    | UnknownEventType of line: int * eventType: string
    | SchemaViolation of line: int * detail: string
    | EventIdMismatch of line: int
    | NotReplayable of ReplayError

/// A test-side reader of the causa-storage event line (contract v1.0.0 sections 5-7, v1.1.0 section 4). It decodes
/// exactly the wire fields into the kernel's types; versions and the registry are the kernel's (StorageVersion,
/// EventRegistry, WireEnum), so what it refuses is what the kernel declares unsupported.
module StoredHistory =
    exception private Violation of string

    let private prop (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> v
        | _ -> raise (Violation(sprintf "missing field %s" name))

    let private str (e: JsonElement) (name: string) =
        let v = prop e name
        if v.ValueKind <> JsonValueKind.String then raise (Violation(sprintf "%s is not a string" name))
        match v.GetString() with
        | null -> raise (Violation(sprintf "%s is null" name))
        | s -> s

    let private optStr (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Null -> None
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
        | true, _ -> raise (Violation(sprintf "%s is not a string" name))
        | _ -> raise (Violation(sprintf "missing field %s" name))

    let private text e name =
        match NonEmptyText.create (str e name) with
        | Some t -> t
        | None -> raise (Violation(sprintf "%s is blank" name))

    let private optText e name =
        optStr e name
        |> Option.map (fun s ->
            match NonEmptyText.create s with
            | Some t -> t
            | None -> raise (Violation(sprintf "%s is blank" name)))

    let private guid e name = Guid.Parse(str e name)
    let private guids (e: JsonElement) name = [ for v in (prop e name).EnumerateArray() -> Guid.Parse(v.GetString() |> Option.ofObj |> Option.defaultValue "") ]
    let private time e name = optStr e name |> Option.map (fun s -> DateTimeOffset.Parse(s, Globalization.CultureInfo.InvariantCulture))

    let private enumOf (wire: WireEnum<'T>) e name =
        let s = str e name
        match WireEnum.ofWire wire s with
        | Some v -> v
        | None -> raise (Violation(sprintf "%s: \"%s\" is not a %s value" name s wire.Name))

    let private actor (e: JsonElement) =
        let id = text e "actorId"
        match str e "kind" with
        | "human" -> Actor.Human id
        | "system" -> Actor.System id
        | "agent" -> Actor.Agent(id, text e "provider", text e "model")
        | other -> raise (Violation(sprintf "actor kind %s" other))

    let private transition (p: JsonElement) : HypothesisTransition =
        { HypothesisId = HypothesisId(guid p "hypothesisId")
          From = enumOf Wire.hypothesisState p "from"
          Reason = text p "reason"
          EvidenceIds = guids p "evidenceIds" }

    let private payload (eventType: string) (p: JsonElement) : CoreEvent =
        match eventType with
        | "InvestigationStarted" -> InvestigationStarted { Title = text p "title"; ObservationId = guid p "observationId"; Original = text p "original" }
        | "LifecycleChanged" ->
            let from = enumOf Wire.lifecycle p "from"
            let reason () =
                match optText p "reason" with
                | Some r -> r
                | None -> raise (Violation "LifecycleChanged needs a reason")
            match str p "command" with
            | "activateInvestigation" -> InvestigationActivated from
            | "markAwaitingEvidence" -> InvestigationMarkedAwaitingEvidence(from, reason ())
            | "markAwaitingVerification" -> InvestigationMarkedAwaitingVerification from
            | "resolveInvestigation" -> InvestigationResolved from
            | "closeInvestigation" -> InvestigationClosed from
            | "reopenInvestigation" -> InvestigationReopened(from, reason ())
            | "verificationFailed" -> InvestigationVerificationFailed(reason ())
            | other -> raise (Violation(sprintf "lifecycle command %s" other))
        | "ObservationRecorded" -> ObservationRecorded { ObservationId = guid p "observationId"; Original = text p "original"; ObservedAt = time p "observedAt" }
        | "ObservationRevised" -> ObservationRevised { ObservationId = guid p "observationId"; Original = text p "original"; Source = optText p "source" }
        | "HypothesisRecorded" -> HypothesisRecorded { HypothesisId = HypothesisId(guid p "hypothesisId"); Statement = text p "statement"; Mechanism = optText p "mechanism" }
        | "HypothesisTransitioned" ->
            let t = transition p
            match str p "command" with
            | "markPlausible" -> HypothesisMarkedPlausible t
            | "markSupported" -> HypothesisMarkedSupported t
            | "markWeakened" -> HypothesisWeakened t
            | "falsify" -> HypothesisFalsified t
            | "supersede" -> HypothesisSuperseded t
            | "reopen" -> HypothesisReopened t
            | other -> raise (Violation(sprintf "hypothesis command %s" other))
        | "EvidenceAdmitted" ->
            EvidenceAdmitted
                { EvidenceId = guid p "evidenceId"
                  ContentKind = enumOf Wire.contentKind p "contentKind"
                  Content = text p "content"
                  Category = enumOf Wire.evidenceCategory p "category"
                  Source = text p "source"
                  CapturedAt = time p "capturedAt" }
        | "EvidenceSuperseded" ->
            EvidenceSuperseded { EvidenceId = guid p "evidenceId"; ReplacementEvidenceId = guid p "replacementEvidenceId"; Reason = text p "reason" }
        | "EvidenceVerificationAssessed" ->
            EvidenceVerificationAssessed { EvidenceId = guid p "evidenceId"; Verification = enumOf Wire.verificationStatus p "verification" }
        | "EvidenceRevised" -> EvidenceRevised { EvidenceId = guid p "evidenceId"; Label = text p "label"; Source = text p "source"; SourceUrl = optStr p "sourceUrl" }
        | "EvidenceRelated" ->
            EvidenceRelationshipRecorded
                { RelationshipId = guid p "relationshipId"
                  EvidenceId = guid p "evidenceId"
                  HypothesisId = HypothesisId(guid p "hypothesisId")
                  Kind = enumOf Wire.relationshipKind p "kind"
                  Rationale = optText p "rationale" }
        | "UnknownIdentified" -> UnknownIdentified { UnknownId = guid p "unknownId"; Question = text p "question"; Materiality = enumOf Wire.materiality p "materiality" }
        | "UnknownTransitioned" -> UnknownTransitioned { UnknownId = guid p "unknownId"; State = enumOf Wire.unknownState p "state"; Basis = text p "basis" }
        | "AssumptionRecorded" ->
            AssumptionRecorded { AssumptionId = guid p "assumptionId"; Proposition = text p "proposition"; Materiality = enumOf Wire.materiality p "materiality" }
        | "AssumptionTransitioned" -> AssumptionTransitioned { AssumptionId = guid p "assumptionId"; State = enumOf Wire.assumptionState p "state" }
        | "ContradictionRecorded" ->
            ContradictionRecorded
                { ContradictionId = guid p "contradictionId"
                  Statement = text p "statement"
                  EvidenceIds = guids p "evidenceIds"
                  Materiality = enumOf Wire.materiality p "materiality" }
        | "ContradictionTransitioned" ->
            ContradictionTransitioned { ContradictionId = guid p "contradictionId"; State = enumOf Wire.contradictionState p "state"; Basis = text p "basis" }
        | "PredictionRecorded" ->
            PredictionRecorded
                { PredictionId = guid p "predictionId"
                  HypothesisId = HypothesisId(guid p "hypothesisId")
                  Statement = text p "statement"
                  Strength = enumOf Wire.predictionStrength p "strength"
                  Timing = enumOf Wire.predictionTiming p "timing" }
        | "ActionRecorded" ->
            InvestigationActionRecorded
                { ActionId = guid p "actionId"
                  Kind = enumOf Wire.actionKind p "kind"
                  Purpose = text p "purpose"
                  TargetHypothesisIds = guids p "targetHypothesisIds" |> List.map HypothesisId }
        | "ActionTransitioned" -> InvestigationActionTransitioned { ActionId = guid p "actionId"; State = enumOf Wire.actionState p "state" }
        | "FindingRecorded" ->
            FindingRecorded { FindingId = guid p "findingId"; ActionId = guid p "actionId"; OriginalResult = text p "originalResult"; Interpretation = optText p "interpretation" }
        | "NegativeKnowledgeEstablished" ->
            NegativeKnowledgeEstablished
                { NegativeKnowledgeId = guid p "negativeKnowledgeId"
                  Statement = text p "statement"
                  Scope = text p "scope"
                  BasisEvidenceIds = guids p "basisEvidenceIds" }
        | "CausalAssertionRecorded" ->
            CausalAssertionRecorded
                { CauseId = CausalAssertionId(guid p "causeId")
                  Outcome = text p "outcome"
                  Members =
                    [ for m in (prop p "members").EnumerateArray() ->
                          { HypothesisId = HypothesisId(guid m "hypothesisId"); Role = enumOf Wire.causeRole m "role" } ]
                  Mechanism = optText p "mechanism" }
        | "CausalAssertionConfirmed" -> CauseConfirmed { CauseId = CausalAssertionId(guid p "causeId"); Policy = text p "policy" }
        | "CausalAssertionReopened" -> ConfirmedCauseReopened(CausalAssertionId(guid p "causeId"), text p "reason")
        | "CorrectiveActionRecorded" ->
            CorrectiveActionRecorded
                { CorrectiveActionId = CorrectiveActionId(guid p "correctiveActionId")
                  Kind = enumOf Wire.correctiveActionKind p "kind"
                  Description = text p "description"
                  TargetCauseIds = guids p "targetCauseIds" |> List.map CausalAssertionId }
        | "CorrectiveActionTransitioned" ->
            let id = CorrectiveActionId(guid p "correctiveActionId")
            match enumOf Wire.correctiveActionState p "state" with
            | CorrectiveActionState.CorrectionApproved -> CorrectiveActionApproved id
            | CorrectiveActionState.CorrectionInProgress -> CorrectiveActionStarted id
            | CorrectiveActionState.CorrectionImplemented -> CorrectiveActionImplemented id
            | CorrectiveActionState.CorrectionAwaitingVerificationOfEffect -> CorrectiveActionAwaitingVerificationOfEffect id
            | CorrectiveActionState.CorrectionVerifiedEffective -> CorrectiveActionVerifiedEffective id
            | CorrectiveActionState.CorrectionVerificationFailed -> CorrectiveActionVerificationFailed id
            | CorrectiveActionState.CorrectionReworkRequired -> CorrectiveActionReworkRequired id
            | CorrectiveActionState.CorrectionCancelled -> CorrectiveActionCancelled id
            | CorrectiveActionState.CorrectionProposed -> raise (Violation "proposed is not a corrective action target")
        | "CriterionRecorded" ->
            VerificationCriterionRecorded
                { CriterionId = guid p "criterionId"
                  CorrectiveActionId = CorrectiveActionId(guid p "correctiveActionId")
                  ExpectedOutcome = text p "expectedOutcome"
                  Method = optText p "method" }
        | "VerificationRecorded" ->
            VerificationRecorded
                { VerificationId = guid p "verificationId"
                  CriterionId = guid p "criterionId"
                  Outcome = enumOf Wire.verificationOutcome p "outcome"
                  EvidenceIds = guids p "evidenceIds" }
        | "ConclusionRecorded" ->
            ConclusionRecorded
                { ConclusionId = guid p "conclusionId"
                  Statement = text p "statement"
                  Strength = enumOf Wire.conclusionStrength p "strength"
                  CauseIds = guids p "causeIds" |> List.map CausalAssertionId
                  ResidualUncertainty = optText p "residualUncertainty" }
        | "ProposalRecorded" ->
            ProposalRecorded
                { ProposalId = ProposalId(guid p "proposalId")
                  Kind = enumOf Wire.proposalKind p "kind"
                  Text = text p "text"
                  Rationale = optText p "rationale" }
        | "ProposalWasAccepted" -> ProposalAccepted(ProposalId(guid p "proposalId"), guid p "targetId")
        | "ProposalWasRejected" -> ProposalRejected(ProposalId(guid p "proposalId"), text p "reason")
        | other -> raise (Violation(sprintf "no decoder for registered type %s" other))

    /// Decodes events.ndjson and replays it through the kernel. Version and registry checks come first and refuse the
    /// whole history (contract v1.0.0 section 11.1 stages 5-6; v1.1.0 section 2.1 homogeneity).
    let read (eventsNdjson: string) : Result<Investigation * string, ReadRefusal> =
        let lines = eventsNdjson.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        let docs = lines |> List.map (fun l -> JsonDocument.Parse l)
        try
            let versionOf (d: JsonDocument) = str d.RootElement "formatVersion"
            let versions = docs |> List.map versionOf
            match versions |> List.indexed |> List.tryFind (fun (_, v) -> not (StorageVersion.isSupported v)) with
            | Some(i, v) -> Error(UnsupportedVersion(i + 1, v))
            | None ->
                match List.distinct versions with
                | [ storage ] ->
                    let typed = docs |> List.map (fun d -> str d.RootElement "eventType")
                    match typed |> List.indexed |> List.tryFind (fun (_, t) -> not (EventRegistry.isRegistered storage t)) with
                    | Some(i, t) -> Error(UnknownEventType(i + 1, t))
                    | None ->
                        let decode (index: int) (d: JsonDocument) =
                            let e = d.RootElement
                            try
                                let inv = InvestigationId(guid e "investigationId")
                                let version = AggregateVersion((prop e "aggregateVersion").GetInt64())
                                let correlation = guid e "correlationId"
                                let id = EventId(guid e "eventId")
                                if id <> EventIdentity.derive inv version correlation then
                                    Error(EventIdMismatch(index + 1))
                                else
                                    Ok
                                        { EventId = id
                                          Investigation = inv
                                          Version = version
                                          Provenance =
                                            { OccurredAt = DateTimeOffset.Parse(str e "occurredAt", Globalization.CultureInfo.InvariantCulture)
                                              Actor = actor (prop e "actor")
                                              CorrelationId = correlation
                                              FormatVersion = storage
                                              SemanticVersion = str e "semanticVersion" }
                                          Payload = CoreEvent(payload (str e "eventType") (prop e "payload")) }
                            with
                            | Violation detail -> Error(SchemaViolation(index + 1, detail))
                            | :? FormatException as ex -> Error(SchemaViolation(index + 1, ex.Message))
                        let decoded = docs |> List.mapi decode
                        match decoded |> List.tryPick (function Error r -> Some r | Ok _ -> None) with
                        | Some refusal -> Error refusal
                        | None ->
                            decoded
                            |> List.choose Result.toOption
                            |> Kernel.kernel.Replay
                            |> Result.map (fun s -> s, storage)
                            |> Result.mapError NotReplayable
                | mixed -> Error(MixedVersions mixed)
        finally
            docs |> List.iter (fun d -> d.Dispose())

/// The kernel's state rendered as the snapshot's (id, salient fields) rows, per entity array, for comparison.
module StateRows =
    let private g (id: Guid) = EventIdentity.canonical id
    let private opt f = Option.map f >> Option.defaultValue "null"
    let private w = WireEnum.toWire

    let ofInvestigation (s: Investigation) : Map<string, Set<string>> =
        let rows name xs = name, Set.ofList xs
        Map.ofList
            [ rows "observations" [ for KeyValue(_, o) in s.Observations -> sprintf "%s|%s" (g o.Id) o.Original.Value ]
              rows "hypotheses" [ for KeyValue(_, h) in s.Hypotheses -> let (HypothesisId id) = h.Id in sprintf "%s|%s|%d" (g id) (w Wire.hypothesisState h.State) h.History.Length ]
              rows "evidence"
                  [ for KeyValue(_, e) in s.Evidence ->
                        sprintf "%s|%s|%s|%d" (g e.Id) (w Wire.verificationStatus e.Verification) (opt g e.SupersededBy) (Investigation.versionNumber e.AdmittedAtVersion) ]
              rows "relationships" [ for KeyValue(_, r) in s.Relationships -> sprintf "%s|%s" (g r.Id) (w Wire.relationshipKind r.Kind) ]
              rows "unknowns" [ for KeyValue(_, u) in s.Unknowns -> sprintf "%s|%s" (g u.Id) (w Wire.unknownState u.State) ]
              rows "assumptions" [ for KeyValue(_, a) in s.Assumptions -> sprintf "%s|%s" (g a.Id) (w Wire.assumptionState a.State) ]
              rows "contradictions" [ for KeyValue(_, c) in s.Contradictions -> sprintf "%s|%s" (g c.Id) (w Wire.contradictionState c.State) ]
              rows "predictions" [ for KeyValue(_, p) in s.Predictions -> sprintf "%s|%s" (g p.Id) (w Wire.predictionTiming p.Timing) ]
              rows "actions" [ for KeyValue(_, a) in s.Actions -> sprintf "%s|%s" (g a.Id) (w Wire.actionState a.State) ]
              rows "findings" [ for KeyValue(_, f) in s.Findings -> g f.Id ]
              rows "negativeKnowledge" [ for KeyValue(_, n) in s.NegativeKnowledge -> g n.Id ]
              rows "causes" [ for KeyValue(_, c) in s.Causes -> let (CausalAssertionId id) = c.Id in sprintf "%s|%s" (g id) (w Wire.causeState c.State) ]
              rows "correctiveActions"
                  [ for KeyValue(_, c) in s.CorrectiveActions ->
                        let (CorrectiveActionId id) = c.Id
                        sprintf "%s|%s|%d" (g id) (w Wire.correctiveActionState c.State) (Investigation.versionNumber c.AwaitingSinceVersion) ]
              rows "criteria" [ for KeyValue(_, c) in s.Criteria -> g c.Id ]
              rows "verifications"
                  [ for KeyValue(_, v) in s.Verifications -> sprintf "%s|%s|%d" (g v.Id) (w Wire.verificationOutcome v.Outcome) (Investigation.versionNumber v.RecordedAtVersion) ]
              rows "conclusions" [ for KeyValue(_, c) in s.Conclusions -> sprintf "%s|%s" (g c.Id) (w Wire.conclusionStrength c.Strength) ]
              rows "proposals" [ for KeyValue(_, p) in s.Proposals -> let (ProposalId id) = p.Id in sprintf "%s|%s" (g id) (w Wire.proposalState p.State) ] ]

    let private s (e: JsonElement) (name: string) =
        match e.GetProperty(name) with
        | v when v.ValueKind = JsonValueKind.Null -> "null"
        | v when v.ValueKind = JsonValueKind.Number -> string (v.GetInt64())
        | v -> v.GetString() |> Option.ofObj |> Option.defaultValue "null"

    /// The same rows read from the stored snapshot's `state`.
    let ofSnapshot (state: JsonElement) : Map<string, Set<string>> =
        let rows (name: string) (f: JsonElement -> string) = name, Set.ofSeq [ for e in state.GetProperty(name).EnumerateArray() -> f e ]
        Map.ofList
            [ rows "observations" (fun e -> s e "id" + "|" + s e "original")
              rows "hypotheses" (fun e -> sprintf "%s|%s|%d" (s e "id") (s e "state") (e.GetProperty("history").GetArrayLength()))
              rows "evidence" (fun e -> sprintf "%s|%s|%s|%s" (s e "id") (s e "verification") (s e "supersededBy") (s e "admittedAtVersion"))
              rows "relationships" (fun e -> s e "id" + "|" + s e "kind")
              rows "unknowns" (fun e -> s e "id" + "|" + s e "state")
              rows "assumptions" (fun e -> s e "id" + "|" + s e "state")
              rows "contradictions" (fun e -> s e "id" + "|" + s e "state")
              rows "predictions" (fun e -> s e "id" + "|" + s e "timing")
              rows "actions" (fun e -> s e "id" + "|" + s e "state")
              rows "findings" (fun e -> s e "id")
              rows "negativeKnowledge" (fun e -> s e "id")
              rows "causes" (fun e -> s e "id" + "|" + s e "state")
              rows "correctiveActions" (fun e -> sprintf "%s|%s|%s" (s e "id") (s e "state") (s e "awaitingSinceVersion"))
              rows "criteria" (fun e -> s e "id")
              rows "verifications" (fun e -> sprintf "%s|%s|%s" (s e "id") (s e "outcome") (s e "recordedAtVersion"))
              rows "conclusions" (fun e -> s e "id" + "|" + s e "strength")
              rows "proposals" (fun e -> s e "id" + "|" + s e "state") ]

/// The catalog the connected repository names (catalog/active.json, EXT-DATA-013), replayed case by case.
module CatalogReplay =
    type CaseResult = { CaseKey: string; Path: string; Problems: string list; Events: string }

    let private json (text: string) = JsonDocument.Parse text

    let private get (e: JsonElement) (name: string) =
        e.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""

    /// Every case of the active catalog: its stored events replayed through the kernel and compared with its snapshot,
    /// its manifest entry (version, lifecycle, title) and the event ids.
    let run (read: CatalogReader) : Result<CaseResult list, string> =
        read "catalog/active.json"
        |> Result.bind (fun active ->
            use activeDoc = json active
            read (get activeDoc.RootElement "manifest"))
        |> Result.bind (fun manifest ->
            use manifestDoc = json manifest
            let cases = [ for c in manifestDoc.RootElement.GetProperty("cases").EnumerateArray() -> c.Clone() ]
            if cases.IsEmpty then
                Error "the active catalog lists no cases"
            else
                cases
                |> List.map (fun c ->
                    let path = get c "path"
                    let key = get c "caseKey"
                    match read (path + "/events.ndjson"), read (path + "/snapshot.json") with
                    | Ok events, Ok snapshot ->
                        use snap = json snapshot
                        let problems =
                            match StoredHistory.read events with
                            | Error refusal -> [ sprintf "the stored history was refused: %A" refusal ]
                            | Ok(state, _) ->
                                let stored = snap.RootElement.GetProperty("state")
                                let mine = StateRows.ofInvestigation state
                                let theirs = StateRows.ofSnapshot stored
                                [ if Investigation.versionNumber state.Version <> c.GetProperty("expectedVersion").GetInt64() then
                                      sprintf "version %d, the catalog expects %d" (Investigation.versionNumber state.Version) (c.GetProperty("expectedVersion").GetInt64())
                                  if WireEnum.toWire Wire.lifecycle state.Lifecycle <> get c "expectedLifecycle" then
                                      sprintf "lifecycle %s, the catalog expects %s" (WireEnum.toWire Wire.lifecycle state.Lifecycle) (get c "expectedLifecycle")
                                  if state.Title.Value <> get stored "title" then "the title differs from the snapshot"
                                  if EventIdentity.canonical (let (InvestigationId g) = state.Id in g) <> get c "investigationId" then "the investigation id differs"
                                  for KeyValue(name, rows) in theirs do
                                      let own = Map.tryFind name mine |> Option.defaultValue Set.empty
                                      if own <> rows then
                                          sprintf
                                              "%s differ from the snapshot: %d missing, %d unexpected (e.g. %A)"
                                              name
                                              (Set.difference rows own).Count
                                              (Set.difference own rows).Count
                                              (Set.union (Set.difference rows own) (Set.difference own rows) |> Seq.truncate 2 |> List.ofSeq) ]
                        { CaseKey = key; Path = path; Problems = problems; Events = events }
                    | Error e, _
                    | _, Error e -> { CaseKey = key; Path = path; Problems = [ e ]; Events = "" })
                |> Ok)

/// The live source: the authorized test repository through the GitHub contents API, read-only.
module GitHubCatalog =
    let private env name = Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let reader () : Result<CatalogReader, string> =
        match env "CAUSA_GITHUB_TOKEN", env "CAUSA_TEST_OWNER", env "CAUSA_TEST_REPO", env "CAUSA_TEST_BRANCH" with
        | Some token, Some owner, Some repo, Some branch ->
            let client = new HttpClient()
            client.DefaultRequestHeaders.UserAgent.ParseAdd "causa-acceptance-tests"
            client.DefaultRequestHeaders.Accept.ParseAdd "application/vnd.github.raw+json"
            client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
            Ok(fun path ->
                let url = sprintf "https://api.github.com/repos/%s/%s/contents/%s?ref=%s" owner repo path (Uri.EscapeDataString branch)
                use response = client.GetAsync(url).Result
                let body = response.Content.ReadAsStringAsync().Result
                if response.IsSuccessStatusCode then Ok body else Error(sprintf "GET %s: %d" path (int response.StatusCode)))
        | None, _, _, _ -> Error "BLOCKED: no credential (CAUSA_GITHUB_TOKEN is not set); this live test reads the catalog from the authorized GitHub test repository"
        | _ -> Error "BLOCKED: CAUSA_TEST_OWNER, CAUSA_TEST_REPO and CAUSA_TEST_BRANCH must name the authorized GitHub test repository"

module EXTDATA011Tests =
    open Xunit

    let private fail (problems: string list) =
        if not problems.IsEmpty then failwith (String.Join(Environment.NewLine, problems))

    let private catalog () =
        match GitHubCatalog.reader () |> Result.bind CatalogReplay.run with
        | Ok cases -> cases
        | Error message -> failwith message

    /// This checkout, found from the test assembly's location.
    let private repositoryRoot () =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> None
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> Some d.FullName
            | d -> up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    /// O1: compatibility with the frozen storage contract as a reader/writer specification, without vendoring its
    /// demonstration records. Every active catalog case, read live, replays through the kernel to its stored state; the
    /// application's own tree holds no stored investigation.
    [<Fact>]
    let ``EXT-DATA-011-O1`` () =
        let cases = catalog ()
        let vendored =
            match repositoryRoot () with
            | None -> [ "the repository root (Causa.slnx) was not found from the test assembly" ]
            | Some root ->
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun f ->
                    let rel = Path.GetRelativePath(root, f).Replace('\\', '/')
                    not (rel.StartsWith "node_modules/" || rel.Contains "/bin/" || rel.Contains "/obj/" || rel.StartsWith ".git/")
                    && (Path.GetFileName f = "events.ndjson" || rel.Contains "investigations/"))
                |> Seq.map (sprintf "vendored storage record: %s")
                |> List.ofSeq
        fail
            [ yield! vendored
              for c in cases do
                  for p in c.Problems -> sprintf "%s (%s): %s" c.CaseKey c.Path p ]

    /// O2: the contract is versioned and incompatible data fails closed. Starting from a live catalog history (which is
    /// accepted, the control), an unsupported version, mixed versions, an unregistered type, a 1.1.0 type in a 1.0.0
    /// history, a forged event id, a gap and an illegal transition are each refused whole, never repaired.
    [<Fact>]
    let ``EXT-DATA-011-O2`` () =
        let case = catalog () |> List.head
        let lines = case.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        let join (ls: string list) = String.Join("\n", ls) + "\n"
        let replaceLine i (f: string -> string) = lines |> List.mapi (fun j l -> if j = i then f l else l) |> join
        let storage = StoredHistory.read (join lines)
        let original = match storage with Ok(_, v) -> v | Error _ -> "1.0.0"
        let refused name (input: string) (expected: ReadRefusal -> bool) =
            match StoredHistory.read input with
            | Error r when expected r -> []
            | Error r -> [ sprintf "%s: refused for another reason: %A" name r ]
            | Ok _ -> [ sprintf "%s: accepted, but must fail closed" name ]
        let last = lines.Length - 1
        fail
            [ match storage with
              | Ok _ -> ()
              | Error r -> sprintf "control: the unmodified catalog history %s was refused: %A" case.CaseKey r
              yield!
                  refused
                      "every line at 2.0.0"
                      (lines |> List.map (fun l -> l.Replace("\"formatVersion\":\"" + original + "\"", "\"formatVersion\":\"2.0.0\"")) |> join)
                      (function UnsupportedVersion(1, "2.0.0") -> true | _ -> false)
              yield!
                  refused
                      "one line at another supported version"
                      (replaceLine last (fun l -> l.Replace("\"formatVersion\":\"" + original + "\"", "\"formatVersion\":\"" + (if original = "1.0.0" then "1.1.0" else "1.0.0") + "\"")))
                      (function MixedVersions _ -> true | _ -> false)
              yield!
                  refused
                      "an unregistered event type"
                      (replaceLine last (fun l -> Text.RegularExpressions.Regex.Replace(l, "\"eventType\":\"[A-Za-z]+\"", "\"eventType\":\"InvestigationDeleted\"")))
                      (function UnknownEventType(_, "InvestigationDeleted") -> true | _ -> false)
              if original = "1.0.0" then
                  yield!
                      refused
                          "a 1.1.0 event type in a 1.0.0 history"
                          (replaceLine last (fun l -> Text.RegularExpressions.Regex.Replace(l, "\"eventType\":\"[A-Za-z]+\"", "\"eventType\":\"EvidenceRevised\"")))
                          (function UnknownEventType(_, "EvidenceRevised") -> true | _ -> false)
              yield!
                  refused
                      "a forged event id"
                      (replaceLine last (fun l -> Text.RegularExpressions.Regex.Replace(l, "\"eventId\":\"[0-9a-f-]+\"", "\"eventId\":\"00000000-0000-5000-8000-000000000000\"")))
                      (function EventIdMismatch _ -> true | _ -> false)
              yield!
                  refused
                      "a history with its second event removed"
                      (lines |> List.mapi (fun i l -> i, l) |> List.filter (fun (i, _) -> i <> 1) |> List.map snd |> join)
                      (function NotReplayable _ | EventIdMismatch _ -> true | _ -> false)
              yield!
                  refused
                      "a history that does not begin with InvestigationStarted"
                      (List.tail lines |> join)
                      (function NotReplayable _ | EventIdMismatch _ -> true | _ -> false) ]
