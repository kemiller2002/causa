// Owner: persistence. causa-storage 1.0.0/1.1.0 file sets as wire data: the event line, the projection (section 8.3,
// 1.1.0 section 4.7), the snapshot and manifest, the writer, and the reader stages of CONTRACT section 11.1.
namespace Causa.Persistence

open System
open System.Text

/// One stored event line (CONTRACT section 5). The payload is the event's wire record.
type WireEvent =
    { FormatVersion: string
      EventId: string
      InvestigationId: string
      AggregateVersion: int64
      EventType: string
      OccurredAt: string
      Actor: Json
      SemanticVersion: string
      CorrelationId: string
      Payload: Json }

/// An event before it is placed in a history: the writer assigns version and id.
type NewWireEvent =
    { EventType: string
      OccurredAt: string
      Actor: Json
      CorrelationId: string
      Payload: Json }

/// A reader stage refusal (CONTRACT section 11.1): the primary code, the file and line, and a detail.
type StageRefusal = { Code: string; File: string; Line: int option; Detail: string }

/// A file set that passed the reader stages it evaluates (1-14, 16, 17). Stage 15 (domain admissibility) is the
/// engine's; this reader reports it as not evaluated (CONTRACT section 11.3).
type WireInvestigation =
    { Version: string
      InvestigationId: string
      Events: WireEvent list
      State: Json
      Bytes: FileSet }

[<RequireQualifiedAccess>]
module Wire =

    let supportedVersions = [ "1.0.0"; "1.1.0" ]

    // ---- field kinds of the wire records (stage 7) -------------------------------------------------------------------

    type Kind =
        | Uuid
        | Text
        | Timestamp
        | HttpUrl
        | Enum of string list
        | UuidList of nonEmpty: bool
        | Members
        | Nullable of Kind

    let private e (values: string) = Enum(values.Split(' ') |> List.ofArray)
    let materiality = e "blocking material nonMaterial informational"
    let lifecycle = e "draft active awaitingEvidence awaitingVerification resolved closed archived"
    let hypothesisState = e "proposed plausible supported weakened falsified superseded"

    let relationshipKind (version: string) =
        if version = "1.1.0" then e "supports contradicts discriminates insufficient consistentWith inconclusive"
        else e "supports contradicts discriminates insufficient"

    /// The 31 v1.0.0 payloads and the two 1.1.0 ones, field by field (CONTRACT section 7; 1.1.0 sections 4.1-4.2).
    let payloadFields (version: string) (eventType: string) : (string * Kind) list option =
        match eventType with
        | "InvestigationStarted" -> Some [ "title", Text; "observationId", Uuid; "original", Text ]
        | "LifecycleChanged" ->
            Some [ "from", lifecycle; "to", e "active awaitingEvidence awaitingVerification resolved closed"
                   "command", e "activateInvestigation markAwaitingEvidence markAwaitingVerification resolveInvestigation closeInvestigation reopenInvestigation verificationFailed"
                   "reason", Nullable Text ]
        | "ObservationRecorded" -> Some [ "observationId", Uuid; "original", Text; "observedAt", Nullable Timestamp ]
        | "HypothesisRecorded" -> Some [ "hypothesisId", Uuid; "statement", Text; "mechanism", Nullable Text ]
        | "HypothesisTransitioned" ->
            Some [ "hypothesisId", Uuid; "from", hypothesisState; "to", e "plausible supported weakened falsified superseded"
                   "command", e "markPlausible markSupported markWeakened falsify supersede reopen"; "reason", Text; "evidenceIds", UuidList false ]
        | "EvidenceAdmitted" ->
            Some [ "evidenceId", Uuid; "contentKind", e "text artifact"; "content", Text
                   "category", e "directObservation measurement log document experimentResult testimony externalReference"
                   "source", Text; "capturedAt", Nullable Timestamp ]
        | "EvidenceSuperseded" -> Some [ "evidenceId", Uuid; "replacementEvidenceId", Uuid; "reason", Text ]
        | "EvidenceVerificationAssessed" -> Some [ "evidenceId", Uuid; "verification", e "unverified partiallyVerified verified invalidated" ]
        | "EvidenceRelated" ->
            Some [ "relationshipId", Uuid; "evidenceId", Uuid; "hypothesisId", Uuid; "kind", relationshipKind version; "rationale", Nullable Text ]
        | "UnknownIdentified" -> Some [ "unknownId", Uuid; "question", Text; "materiality", materiality ]
        | "UnknownTransitioned" -> Some [ "unknownId", Uuid; "state", e "investigating resolved acceptedResidual"; "basis", Text ]
        | "AssumptionRecorded" -> Some [ "assumptionId", Uuid; "proposition", Text; "materiality", materiality ]
        | "AssumptionTransitioned" -> Some [ "assumptionId", Uuid; "state", e "provisionallyAccepted verified rejected" ]
        | "ContradictionRecorded" -> Some [ "contradictionId", Uuid; "statement", Text; "evidenceIds", UuidList false; "materiality", materiality ]
        | "ContradictionTransitioned" -> Some [ "contradictionId", Uuid; "state", e "triaged resolved acceptedResidual"; "basis", Text ]
        | "PredictionRecorded" ->
            Some [ "predictionId", Uuid; "hypothesisId", Uuid; "statement", Text; "strength", e "necessary expected discriminating nonDiscriminating"
                   "timing", e "prospective retrospective" ]
        | "ActionRecorded" ->
            Some [ "actionId", Uuid; "kind", e "test inspection query comparison experiment"; "purpose", Text; "targetHypothesisIds", UuidList false ]
        | "ActionTransitioned" -> Some [ "actionId", Uuid; "state", e "approved rejected inProgress completed cancelled" ]
        | "FindingRecorded" -> Some [ "findingId", Uuid; "actionId", Uuid; "originalResult", Text; "interpretation", Nullable Text ]
        | "NegativeKnowledgeEstablished" -> Some [ "negativeKnowledgeId", Uuid; "statement", Text; "scope", Text; "basisEvidenceIds", UuidList true ]
        | "CausalAssertionRecorded" -> Some [ "causeId", Uuid; "outcome", Text; "members", Members; "mechanism", Nullable Text ]
        | "CausalAssertionConfirmed" -> Some [ "causeId", Uuid; "policy", Text ]
        | "CausalAssertionReopened" -> Some [ "causeId", Uuid; "reason", Text ]
        | "CorrectiveActionRecorded" ->
            Some [ "correctiveActionId", Uuid; "kind", e "mitigation workaround correction prevention"; "description", Text; "targetCauseIds", UuidList false ]
        | "CorrectiveActionTransitioned" ->
            Some [ "correctiveActionId", Uuid
                   "state", e "approved cancelled inProgress implemented awaitingVerificationOfEffect verificationFailed verifiedEffective reworkRequired" ]
        | "CriterionRecorded" -> Some [ "criterionId", Uuid; "correctiveActionId", Uuid; "expectedOutcome", Text; "method", Nullable Text ]
        | "VerificationRecorded" -> Some [ "verificationId", Uuid; "criterionId", Uuid; "outcome", e "satisfied failed inconclusive"; "evidenceIds", UuidList false ]
        | "ConclusionRecorded" ->
            Some [ "conclusionId", Uuid; "statement", Text
                   "strength", e "plausibleExplanation leadingExplanation supportedCause confirmedCause acceptedWithResidualUncertainty"
                   "causeIds", UuidList false; "residualUncertainty", Nullable Text ]
        | "ProposalRecorded" -> Some [ "proposalId", Uuid; "kind", e "hypothesis unknown action"; "text", Text; "rationale", Nullable Text ]
        | "ProposalWasAccepted" -> Some [ "proposalId", Uuid; "targetId", Uuid ]
        | "ProposalWasRejected" -> Some [ "proposalId", Uuid; "reason", Text ]
        | "EvidenceRevised" when version = "1.1.0" -> Some [ "evidenceId", Uuid; "label", Text; "source", Text; "sourceUrl", Nullable HttpUrl ]
        | "ObservationRevised" when version = "1.1.0" -> Some [ "observationId", Uuid; "original", Text; "source", Nullable Text ]
        | _ -> None

    let private httpUrl = RegularExpressions.Regex(@"^[Hh][Tt][Tt][Pp][Ss]?://[^\t\n\u000b\f\r \u0085   -     　]", RegularExpressions.RegexOptions.Compiled)

    let rec valueFits (kind: Kind) (v: Json) : bool =
        match kind, v with
        | Nullable _, Json.Null -> true
        | Nullable k, other -> valueFits k other
        | Uuid, Json.String s -> Canonical.isUuid s
        | Text, Json.String s -> s.Length > 0 && Canonical.isNonBlank s
        | Timestamp, Json.String s -> (Canonical.parseTimestamp s).IsSome
        | HttpUrl, Json.String s -> httpUrl.IsMatch s
        | Enum values, Json.String s -> List.contains s values
        | UuidList nonEmpty, Json.Array items ->
            let ids = items |> List.choose (function Json.String s when Canonical.isUuid s -> Some s | _ -> None)
            ids.Length = items.Length && List.length (List.distinct ids) = ids.Length && (not nonEmpty || not ids.IsEmpty)
        | Members, Json.Array items ->
            let roles = set [ "rootCause"; "contributingFactor"; "trigger"; "enablingCondition" ]
            let ok =
                items
                |> List.forall (function
                    | Json.Object [ (a, va); (b, vb) ] ->
                        let m = Map.ofList [ a, va; b, vb ]
                        match m.TryFind "hypothesisId", m.TryFind "role" with
                        | Some(Json.String h), Some(Json.String r) -> Canonical.isUuid h && roles.Contains r
                        | _ -> false
                    | _ -> false)
            ok && not items.IsEmpty && List.length (List.distinctBy Canonical.serialize items) = items.Length
        | _ -> false

    let private keys (j: Json) = match j with Json.Object props -> props |> List.map fst | _ -> []

    let private exactly (expected: string list) (j: Json) =
        match j with
        | Json.Object props -> List.sort (List.map fst props) = List.sort expected
        | _ -> false

    let private field name j = Json.field name (Some j)
    let private text name j = field name j |> Json.text

    /// The per-payload invariants the schemas state (CONTRACT section 7).
    let private payloadInvariants (eventType: string) (p: Json) : bool =
        let s n = text n p
        let nonEmptyIds n = match field n p with Some(Json.Array(_ :: _)) -> true | _ -> false
        match eventType with
        | "LifecycleChanged" ->
            let reasonText = match field "reason" p with Some(Json.String _) -> true | _ -> false
            match s "command", s "to" with
            | Some "activateInvestigation", Some "active" -> not reasonText
            | Some "markAwaitingEvidence", Some "awaitingEvidence" -> reasonText
            | Some "markAwaitingVerification", Some "awaitingVerification" -> not reasonText
            | Some "resolveInvestigation", Some "resolved" -> not reasonText
            | Some "closeInvestigation", Some "closed" -> not reasonText
            | Some "reopenInvestigation", Some "active" -> reasonText
            | Some "verificationFailed", Some "active" -> reasonText && s "from" = Some "awaitingVerification"
            | _ -> false
        | "HypothesisTransitioned" ->
            match s "command", s "to" with
            | Some "markPlausible", Some "plausible" -> true
            | Some "markSupported", Some "supported" -> nonEmptyIds "evidenceIds"
            | Some "markWeakened", Some "weakened" -> nonEmptyIds "evidenceIds"
            | Some "falsify", Some "falsified" -> nonEmptyIds "evidenceIds"
            | Some "supersede", Some "superseded" -> true
            | Some "reopen", Some "plausible" -> nonEmptyIds "evidenceIds"
            | _ -> false
        | "EvidenceAdmitted" ->
            match s "contentKind", s "content" with
            | Some "artifact", Some c -> valueFits HttpUrl (Json.String c)
            | _ -> true
        | "VerificationRecorded" -> s "outcome" <> Some "satisfied" || nonEmptyIds "evidenceIds"
        | _ -> true

    let private actorFits (eventType: string) (actor: Json) =
        match text "kind" actor with
        | Some "human"
        | Some "system" -> exactly [ "actorId"; "kind" ] actor && valueFits Text (defaultArg (field "actorId" actor) Json.Null)
        | Some "agent" ->
            eventType = "ProposalRecorded"
            && exactly [ "actorId"; "kind"; "provider"; "model" ] actor
            && [ "actorId"; "provider"; "model" ] |> List.forall (fun n -> valueFits Text (defaultArg (field n actor) Json.Null))
        | _ -> false

    let envelopeKeys =
        [ "formatVersion"; "eventId"; "investigationId"; "aggregateVersion"; "eventType"; "occurredAt"; "actor"; "semanticVersion"; "correlationId"; "payload" ]

    /// Stage 7 for one event line: the envelope and its payload.
    let eventSchemaProblems (version: string) (j: Json) : string option =
        if not (exactly envelopeKeys j) then Some "the envelope's fields are not exactly the contract's"
        else
            let eventType = text "eventType" j |> Option.defaultValue ""
            let ok kind n = valueFits kind (defaultArg (field n j) Json.Null)
            if not ((text "formatVersion" j).IsSome && (text "semanticVersion" j).IsSome) then Some "a version is not a string"
            elif not (ok Uuid "eventId" && ok Uuid "investigationId" && ok Uuid "correlationId" && ok Timestamp "occurredAt") then
                Some "an envelope identifier or timestamp is invalid"
            elif (match field "aggregateVersion" j with Some(Json.Number n) -> n < 1M | _ -> true) then Some "aggregateVersion is not a positive integer"
            elif not (actorFits eventType (defaultArg (field "actor" j) Json.Null)) then Some "the actor is invalid for this event"
            else
                match payloadFields version eventType, field "payload" j with
                | Some fields, Some payload ->
                    if not (exactly (List.map fst fields) payload) then Some(sprintf "%s payload fields are not exactly the contract's" eventType)
                    else
                        match fields |> List.tryFind (fun (n, k) -> not (valueFits k (defaultArg (field n payload) Json.Null))) with
                        | Some(n, _) -> Some(sprintf "%s.%s is invalid" eventType n)
                        | None -> if payloadInvariants eventType payload then None else Some(sprintf "%s violates a payload invariant" eventType)
                | _ -> Some "unknown payload"

    // ---- the event line ------------------------------------------------------------------------------------------------

    let toJson (ev: WireEvent) : Json =
        Json.Object
            [ "formatVersion", Json.String ev.FormatVersion
              "eventId", Json.String ev.EventId
              "investigationId", Json.String ev.InvestigationId
              "aggregateVersion", Json.Number(decimal ev.AggregateVersion)
              "eventType", Json.String ev.EventType
              "occurredAt", Json.String ev.OccurredAt
              "actor", ev.Actor
              "semanticVersion", Json.String ev.SemanticVersion
              "correlationId", Json.String ev.CorrelationId
              "payload", ev.Payload ]

    let ofJson (j: Json) : WireEvent option =
        match text "formatVersion" j, text "eventId" j, text "investigationId" j, field "aggregateVersion" j, text "eventType" j, text "occurredAt" j,
              field "actor" j, text "semanticVersion" j, text "correlationId" j, field "payload" j with
        | Some fv, Some id, Some inv, Some(Json.Number v), Some et, Some at, Some actor, Some sv, Some corr, Some payload ->
            Some { FormatVersion = fv; EventId = id; InvestigationId = inv; AggregateVersion = int64 v; EventType = et; OccurredAt = at; Actor = actor
                   SemanticVersion = sv; CorrelationId = corr; Payload = payload }
        | _ -> None

    // ---- projection (section 8.3; 1.1.0 section 4.7) ------------------------------------------------------------------

    let arrays =
        [ "observations"; "hypotheses"; "evidence"; "relationships"; "unknowns"; "assumptions"; "contradictions"; "predictions"; "actions"
          "findings"; "negativeKnowledge"; "causes"; "correctiveActions"; "criteria"; "verifications"; "conclusions"; "proposals" ]

    type private Projection =
        { Header: Map<string, Json>
          Entities: Map<string, Map<string, Map<string, Json>>> }

    let private setField (arr: string) (id: string) (updates: (string * Json) list) (p: Projection) =
        match p.Entities.TryFind arr |> Option.bind (Map.tryFind id) with
        | None -> p
        | Some entity ->
            let entity' = updates |> List.fold (fun m (k, v) -> Map.add k v m) entity
            { p with Entities = Map.add arr (Map.add id entity' p.Entities[arr]) p.Entities }

    let private create (arr: string) (id: string) (fields: (string * Json) list) (ev: WireEvent) (p: Projection) =
        let entity = Map.ofList (("id", Json.String id) :: ("provenance", Json.Object [ "actor", ev.Actor; "recordedAt", Json.String ev.OccurredAt ]) :: fields)
        match p.Entities.TryFind arr with
        | Some m when not (m.ContainsKey id) -> { p with Entities = Map.add arr (Map.add id entity m) p.Entities }
        | _ -> p

    let private pf name (ev: WireEvent) = defaultArg (field name ev.Payload) Json.Null
    let private pid name (ev: WireEvent) = text name ev.Payload |> Option.defaultValue ""

    let private apply (version: string) (p: Projection) (ev: WireEvent) : Projection =
        let v11 = version = "1.1.0"
        let copy names = names |> List.map (fun n -> n, pf n ev)
        let p =
            match ev.EventType with
            | "InvestigationStarted" ->
                let header =
                    Map.ofList [ "investigationId", Json.String ev.InvestigationId; "title", pf "title" ev; "lifecycle", Json.String "draft"; "createdAt", Json.String ev.OccurredAt ]
                let empty = { Header = header; Entities = arrays |> List.map (fun a -> a, Map.empty) |> Map.ofList }
                create "observations" (pid "observationId" ev) ([ "original", pf "original" ev; "observedAt", Json.Null ] @ (if v11 then [ "source", Json.Null ] else [])) ev empty
            | "LifecycleChanged" -> { p with Header = Map.add "lifecycle" (pf "to" ev) p.Header }
            | "ObservationRecorded" ->
                create "observations" (pid "observationId" ev) (copy [ "original"; "observedAt" ] @ (if v11 then [ "source", Json.Null ] else [])) ev p
            | "ObservationRevised" -> setField "observations" (pid "observationId" ev) (copy [ "original"; "source" ]) p
            | "HypothesisRecorded" ->
                create "hypotheses" (pid "hypothesisId" ev) (copy [ "statement"; "mechanism" ] @ [ "state", Json.String "proposed"; "history", Json.Array [] ]) ev p
            | "HypothesisTransitioned" ->
                let id = pid "hypothesisId" ev
                let change =
                    Json.Object(copy [ "from"; "to"; "command"; "reason"; "evidenceIds" ] @ [ "at", Json.String ev.OccurredAt; "aggregateVersion", Json.Number(decimal ev.AggregateVersion) ])
                let history =
                    p.Entities.TryFind "hypotheses" |> Option.bind (Map.tryFind id) |> Option.bind (Map.tryFind "history") |> fun h -> Json.items h
                setField "hypotheses" id [ "state", pf "to" ev; "history", Json.Array(history @ [ change ]) ] p
            | "EvidenceAdmitted" ->
                create "evidence" (pid "evidenceId" ev)
                    (copy [ "contentKind"; "content"; "category"; "source"; "capturedAt" ]
                     @ [ "verification", Json.String "unverified"; "supersededBy", Json.Null; "admittedAtVersion", Json.Number(decimal ev.AggregateVersion) ]
                     @ (if v11 then [ "label", Json.Null; "sourceUrl", Json.Null ] else []))
                    ev p
            | "EvidenceSuperseded" -> setField "evidence" (pid "evidenceId" ev) [ "supersededBy", pf "replacementEvidenceId" ev ] p
            | "EvidenceVerificationAssessed" -> setField "evidence" (pid "evidenceId" ev) [ "verification", pf "verification" ev ] p
            | "EvidenceRevised" -> setField "evidence" (pid "evidenceId" ev) (copy [ "label"; "source"; "sourceUrl" ]) p
            | "EvidenceRelated" -> create "relationships" (pid "relationshipId" ev) (copy [ "evidenceId"; "hypothesisId"; "kind"; "rationale" ]) ev p
            | "UnknownIdentified" -> create "unknowns" (pid "unknownId" ev) (copy [ "question"; "materiality" ] @ [ "state", Json.String "identified"; "basis", Json.Null ]) ev p
            | "UnknownTransitioned" -> setField "unknowns" (pid "unknownId" ev) (copy [ "state"; "basis" ]) p
            | "AssumptionRecorded" -> create "assumptions" (pid "assumptionId" ev) (copy [ "proposition"; "materiality" ] @ [ "state", Json.String "explicitUnverified" ]) ev p
            | "AssumptionTransitioned" -> setField "assumptions" (pid "assumptionId" ev) (copy [ "state" ]) p
            | "ContradictionRecorded" ->
                create "contradictions" (pid "contradictionId" ev) (copy [ "statement"; "evidenceIds"; "materiality" ] @ [ "state", Json.String "detected"; "basis", Json.Null ]) ev p
            | "ContradictionTransitioned" -> setField "contradictions" (pid "contradictionId" ev) (copy [ "state"; "basis" ]) p
            | "PredictionRecorded" -> create "predictions" (pid "predictionId" ev) (copy [ "hypothesisId"; "statement"; "strength"; "timing" ]) ev p
            | "ActionRecorded" -> create "actions" (pid "actionId" ev) (copy [ "kind"; "purpose"; "targetHypothesisIds" ] @ [ "state", Json.String "proposed" ]) ev p
            | "ActionTransitioned" -> setField "actions" (pid "actionId" ev) (copy [ "state" ]) p
            | "FindingRecorded" -> create "findings" (pid "findingId" ev) (copy [ "actionId"; "originalResult"; "interpretation" ]) ev p
            | "NegativeKnowledgeEstablished" -> create "negativeKnowledge" (pid "negativeKnowledgeId" ev) (copy [ "statement"; "scope"; "basisEvidenceIds" ]) ev p
            | "CausalAssertionRecorded" -> create "causes" (pid "causeId" ev) (copy [ "outcome"; "members"; "mechanism" ] @ [ "state", Json.String "proposed" ]) ev p
            | "CausalAssertionConfirmed" -> setField "causes" (pid "causeId" ev) [ "state", Json.String "confirmed" ] p
            | "CausalAssertionReopened" -> setField "causes" (pid "causeId" ev) [ "state", Json.String "reopened" ] p
            | "CorrectiveActionRecorded" ->
                create "correctiveActions" (pid "correctiveActionId" ev) (copy [ "kind"; "description"; "targetCauseIds" ] @ [ "state", Json.String "proposed"; "awaitingSinceVersion", Json.Number 0M ]) ev p
            | "CorrectiveActionTransitioned" ->
                let since = if text "state" ev.Payload = Some "awaitingVerificationOfEffect" then [ "awaitingSinceVersion", Json.Number(decimal ev.AggregateVersion) ] else []
                setField "correctiveActions" (pid "correctiveActionId" ev) (copy [ "state" ] @ since) p
            | "CriterionRecorded" -> create "criteria" (pid "criterionId" ev) (copy [ "correctiveActionId"; "expectedOutcome"; "method" ]) ev p
            | "VerificationRecorded" ->
                create "verifications" (pid "verificationId" ev) (copy [ "criterionId"; "outcome"; "evidenceIds" ] @ [ "recordedAtVersion", Json.Number(decimal ev.AggregateVersion) ]) ev p
            | "ConclusionRecorded" ->
                create "conclusions" (pid "conclusionId" ev) (copy [ "statement"; "strength"; "causeIds"; "residualUncertainty" ] @ [ "recordedAtVersion", Json.Number(decimal ev.AggregateVersion) ]) ev p
            | "ProposalRecorded" -> create "proposals" (pid "proposalId" ev) (copy [ "kind"; "text"; "rationale" ] @ [ "state", Json.String "active"; "decision", Json.Null ]) ev p
            | "ProposalWasAccepted" -> setField "proposals" (pid "proposalId" ev) [ "state", Json.String "accepted" ] p
            | "ProposalWasRejected" -> setField "proposals" (pid "proposalId" ev) [ "state", Json.String "rejected"; "decision", pf "reason" ev ] p
            | _ -> p
        { p with Header = Map.add "version" (Json.Number(decimal ev.AggregateVersion)) p.Header }

    /// The state object of a history (section 8.2): entity arrays sorted by id, ordinal.
    let project (version: string) (events: WireEvent list) : Json =
        let start = { Header = Map.empty; Entities = Map.empty }
        let p = events |> List.fold (apply version) start
        let entityArrays =
            arrays
            |> List.map (fun a ->
                let entities = p.Entities.TryFind a |> Option.defaultValue Map.empty
                a,
                entities
                |> Map.toList
                |> List.sortWith (fun (x, _) (y, _) -> String.CompareOrdinal(x, y))
                |> List.map (fun (_, m) -> Json.Object(Map.toList m))
                |> Json.Array)
        Json.Object((p.Header |> Map.toList) @ entityArrays)

    let snapshot (version: string) (investigationId: string) (events: WireEvent list) : Json =
        let last = List.last events
        Json.Object
            [ "formatVersion", Json.String version
              "investigationId", Json.String investigationId
              "aggregateVersion", Json.Number(decimal last.AggregateVersion)
              "projectionVersion", Json.Number 1M
              "semanticVersion", Json.String version
              "generatedAt", Json.String last.OccurredAt
              "state", project version events ]

    let eventsBytes (events: WireEvent list) : byte[] =
        events |> List.map (fun e -> Canonical.serialize (toJson e) + "\n") |> String.concat "" |> Canonical.utf8

    let manifest (version: string) (investigationId: string) (events: WireEvent list) (eventsFile: byte[]) (snap: Json) : Json =
        Json.Object
            [ "formatVersion", Json.String version
              "investigationId", Json.String investigationId
              "schemaVersion", Json.String version
              "semanticVersion", Json.String version
              "aggregateVersion", Json.Number(decimal events.Length)
              "eventCount", Json.Number(decimal events.Length)
              "eventsSha256", Json.String(Canonical.sha256Hex eventsFile)
              "snapshotVersion", Json.Number 1M
              "snapshotSha256", Json.String(Canonical.digest snap)
              "domainPacks", Json.Array []
              "createdAt", Json.String (List.head events).OccurredAt
              "updatedAt", Json.String (List.last events).OccurredAt ]

    /// The three files of a history (section 9.2: events appended, snapshot and manifest recomputed).
    let fileSet (version: string) (investigationId: string) (events: WireEvent list) : FileSet =
        let eventsFile = eventsBytes events
        let snap = snapshot version investigationId events
        { Manifest = Canonical.fileBytes (manifest version investigationId events eventsFile snap)
          Events = eventsFile
          Snapshot = Canonical.fileBytes snap }

    /// Places new events after a history: versions continue, ids are derived (section 5.2).
    let append (version: string) (investigationId: string) (history: WireEvent list) (added: NewWireEvent list) : WireEvent list =
        let start = int64 history.Length
        history
        @ (added
           |> List.mapi (fun i n ->
               let v = start + int64 i + 1L
               { FormatVersion = version; EventId = Canonical.eventId investigationId v n.CorrelationId; InvestigationId = investigationId; AggregateVersion = v
                 EventType = n.EventType; OccurredAt = n.OccurredAt; Actor = n.Actor; SemanticVersion = version; CorrelationId = n.CorrelationId; Payload = n.Payload }))

    // ---- the reader (section 11.1) ---------------------------------------------------------------------------------

    let private refuse code file line detail : Result<'a, StageRefusal> = Error { Code = code; File = file; Line = line; Detail = detail }

    let rec private firstError (items: Result<'a, StageRefusal> list) : Result<'a list, StageRefusal> =
        items
        |> List.fold
            (fun acc r ->
                match acc, r with
                | Error e, _ -> Error e
                | Ok xs, Ok x -> Ok(x :: xs)
                | Ok _, Error e -> Error e)
            (Ok [])
        |> Result.map List.rev

    /// Stage 2 framing of one file.
    let private framing (name: string) (bytes: byte[]) : Result<string, StageRefusal> =
        if bytes.Length = 0 then refuse "CAUSA.ENCODING" name None "empty file"
        elif bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then refuse "CAUSA.ENCODING" name None "byte order mark"
        else
            match Canonical.decodeUtf8 bytes with
            | None -> refuse "CAUSA.ENCODING" name None "invalid UTF-8"
            | Some text when text.Contains '\r' -> refuse "CAUSA.ENCODING" name None "CR"
            | Some text when not (text.EndsWith "\n") -> refuse "CAUSA.ENCODING" name None "missing final LF"
            | Some text when name = "events.ndjson" && (text.StartsWith "\n" || text.Contains "\n\n") -> refuse "CAUSA.ENCODING" name None "blank line"
            | Some text -> Ok text

    let private versionOf (j: Json) (names: string list) =
        names |> List.choose (fun n -> match field n j with Some(Json.String s) -> Some s | _ -> None)

    /// The 1.1.0 admissibility rules of CONTRACT v1.1.0 section 4.6 (1), evaluated in history order: the part of stage
    /// 15 this contract states normatively. The core view under the engine (4.6 (2)) is the engine's.
    let revisionRules (events: WireEvent list) : (int64 * string) option =
        let isRevision (e: WireEvent) = e.EventType = "EvidenceRevised" || e.EventType = "ObservationRevised"
        let groups =
            events
            |> List.fold (fun acc e -> match acc with (g: WireEvent list) :: rest when (List.head g).CorrelationId = e.CorrelationId -> (g @ [ e ]) :: rest | _ -> [ e ] :: acc) []
            |> List.rev
        let openStates = set [ "draft"; "active"; "awaitingEvidence"; "awaitingVerification" ]
        let wellFormedUrl (u: Json) =
            match u with
            | Json.Null -> true
            | Json.String s ->
                (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                && (match Uri.TryCreate(s, UriKind.Absolute) with | true, _ -> true | _ -> false)
            | _ -> false
        let rec go (lifecycle: string) (observations: Set<string>) (evidence: Map<string, bool>) (rest: WireEvent list list) =
            match rest with
            | [] -> None
            | g :: tail ->
                let revs = g |> List.filter isRevision
                let first = List.head g
                let fail code = Some(first.AggregateVersion, code)
                let verdict =
                    if revs.IsEmpty then None
                    else
                        let shapeOk =
                            match g with
                            | [ r ] when isRevision r -> true
                            | [ c; r ] when c.EventType = "EvidenceAdmitted" && r.EventType = "EvidenceRevised" -> text "evidenceId" c.Payload = text "evidenceId" r.Payload
                            | [ c; r ] when c.EventType = "ObservationRecorded" && r.EventType = "ObservationRevised" -> text "observationId" c.Payload = text "observationId" r.Payload
                            | _ -> false
                        let sameTime = g |> List.forall (fun e -> e.OccurredAt = first.OccurredAt && Canonical.serialize e.Actor = Canonical.serialize first.Actor)
                        if not (shapeOk && sameTime) then fail "HISTORY.INADMISSIBLE_EVENT"
                        elif not (openStates.Contains lifecycle) then fail "INVESTIGATION.NOT_OPEN"
                        else
                            let r = List.last g
                            match g with
                            | [ c; _ ] when c.EventType = "EvidenceAdmitted" ->
                                if not (wellFormedUrl (defaultArg (field "sourceUrl" r.Payload) Json.Null)) then fail "EVIDENCE.URL"
                                elif field "source" c.Payload <> field "source" r.Payload then fail "EVIDENCE.REVISION_MISMATCH"
                                else None
                            | [ c; _ ] -> if field "original" c.Payload <> field "original" r.Payload then fail "OBSERVATION.REVISION_MISMATCH" else None
                            | _ when r.EventType = "EvidenceRevised" ->
                                match evidence.TryFind(text "evidenceId" r.Payload |> Option.defaultValue "") with
                                | None -> fail "HISTORY.INADMISSIBLE_EVENT"
                                | Some true -> fail "EVIDENCE.SUPERSEDED"
                                | Some false -> if wellFormedUrl (defaultArg (field "sourceUrl" r.Payload) Json.Null) then None else fail "EVIDENCE.URL"
                            | _ -> if observations.Contains(text "observationId" r.Payload |> Option.defaultValue "") then None else fail "HISTORY.INADMISSIBLE_EVENT"
                match verdict with
                | Some v -> Some v
                | None ->
                    let lifecycle', observations', evidence' =
                        g
                        |> List.fold
                            (fun (l, o: Set<string>, ev: Map<string, bool>) e ->
                                let id n = text n e.Payload |> Option.defaultValue ""
                                match e.EventType with
                                | "InvestigationStarted" -> "draft", o.Add(id "observationId"), ev
                                | "LifecycleChanged" -> (text "to" e.Payload |> Option.defaultValue l), o, ev
                                | "ObservationRecorded" -> l, o.Add(id "observationId"), ev
                                | "EvidenceAdmitted" -> l, o, ev.Add(id "evidenceId", false)
                                | "EvidenceSuperseded" -> l, o, ev.Add(id "evidenceId", true)
                                | _ -> l, o, ev)
                            (lifecycle, observations, evidence)
                    go lifecycle' observations' evidence' tail
        go "draft" Set.empty Map.empty groups

    /// Verifies a file set as a reader would (stages 2-14, 16, 17); stage 1 is the caller's (the three paths).
    let verify (expectedId: string) (files: FileSet) : Result<WireInvestigation, StageRefusal> =
        // stage 2
        match framing "manifest.json" files.Manifest, framing "events.ndjson" files.Events, framing "snapshot.json" files.Snapshot with
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
        | Ok manifestText, Ok eventsText, Ok snapshotText ->
            let lines = eventsText.Substring(0, eventsText.Length - 1).Split('\n') |> List.ofArray
            let parseFile name (text: string) =
                let body = text.Substring(0, text.Length - 1)
                match Canonical.parse body with
                | Error(JsonRefusal why) -> refuse "CAUSA.JSON_MALFORMED" name None why
                | Ok j -> Ok(j, body)
            // stage 3
            let parsedLines = lines |> List.mapi (fun i l -> match Canonical.parse l with Ok j -> Ok(j, l) | Error(JsonRefusal why) -> refuse "CAUSA.JSON_MALFORMED" "events.ndjson" (Some(i + 1)) why)
            match parseFile "manifest.json" manifestText, firstError parsedLines, parseFile "snapshot.json" snapshotText with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
            | Ok(m, mText), Ok evs, Ok(s, sText) ->
                // stage 4
                let nonCanonical =
                    [ if Canonical.serialize m <> mText then yield "manifest.json", None
                      for i, (j, l) in List.indexed evs do
                          if Canonical.serialize j <> l then yield "events.ndjson", Some(i + 1)
                      if Canonical.serialize s <> sText then yield "snapshot.json", None ]
                match nonCanonical with
                | (file, line) :: _ -> refuse "CAUSA.NON_CANONICAL" file line "not the RFC 8785 canonical text"
                | [] ->
                    // stage 5: string versions, supported and homogeneous
                    let versions =
                        versionOf m [ "formatVersion"; "schemaVersion"; "semanticVersion" ]
                        @ (evs |> List.collect (fun (j, _) -> versionOf j [ "formatVersion"; "semanticVersion" ]))
                        @ versionOf s [ "formatVersion"; "semanticVersion" ]
                    match versions |> List.tryFind (fun v -> not (List.contains v supportedVersions)), List.distinct versions with
                    | Some v, _ -> refuse "CAUSA.UNSUPPORTED_VERSION" "manifest.json" None (sprintf "version %s" v)
                    | None, _ :: _ :: _ -> refuse "CAUSA.UNSUPPORTED_VERSION" "events.ndjson" None "mixed versions in one investigation"
                    | None, vs ->
                        let version = List.tryHead vs |> Option.defaultValue "1.0.0"
                        // stage 6
                        let unknownType =
                            evs |> List.indexed |> List.tryPick (fun (i, (j, _)) -> match field "eventType" j with Some(Json.String t) when (payloadFields version t).IsNone -> Some(i + 1, t) | _ -> None)
                        match unknownType with
                        | Some(line, t) -> refuse "CAUSA.UNKNOWN_EVENT_TYPE" "events.ndjson" (Some line) t
                        | None ->
                            // stage 7
                            let manifestKeys =
                                [ "formatVersion"; "investigationId"; "schemaVersion"; "semanticVersion"; "aggregateVersion"; "eventCount"; "eventsSha256"; "snapshotVersion"; "snapshotSha256"; "domainPacks"; "createdAt"; "updatedAt" ]
                            let manifestOk =
                                exactly manifestKeys m
                                && valueFits Uuid (defaultArg (field "investigationId" m) Json.Null)
                                && valueFits Timestamp (defaultArg (field "createdAt" m) Json.Null)
                                && valueFits Timestamp (defaultArg (field "updatedAt" m) Json.Null)
                                && field "snapshotVersion" m = Some(Json.Number 1M)
                                && field "domainPacks" m = Some(Json.Array [])
                                && (match field "aggregateVersion" m, field "eventCount" m with Some(Json.Number a), Some(Json.Number c) -> a >= 1M && c >= 1M | _ -> false)
                                && [ "formatVersion"; "schemaVersion"; "semanticVersion"; "eventsSha256"; "snapshotSha256" ] |> List.forall (fun n -> (text n m).IsSome)
                            let eventProblem = evs |> List.indexed |> List.tryPick (fun (i, (j, _)) -> eventSchemaProblems version j |> Option.map (fun p -> i + 1, p))
                            let snapshotOk =
                                exactly [ "formatVersion"; "investigationId"; "aggregateVersion"; "projectionVersion"; "semanticVersion"; "generatedAt"; "state" ] s
                                && field "projectionVersion" s = Some(Json.Number 1M)
                                && (text "formatVersion" s).IsSome && (text "semanticVersion" s).IsSome
                                && valueFits Uuid (defaultArg (field "investigationId" s) Json.Null)
                                && valueFits Timestamp (defaultArg (field "generatedAt" s) Json.Null)
                            if not manifestOk then refuse "CAUSA.SCHEMA_VIOLATION" "manifest.json" None "the manifest does not match its schema"
                            else
                                match eventProblem with
                                | Some(line, why) -> refuse "CAUSA.SCHEMA_VIOLATION" "events.ndjson" (Some line) why
                                | None when not snapshotOk -> refuse "CAUSA.SCHEMA_VIOLATION" "snapshot.json" None "the snapshot does not match its schema"
                                | None ->
                                    let events = evs |> List.choose (fst >> ofJson)
                                    let n = decimal events.Length
                                    // stage 8
                                    if text "investigationId" m <> Some expectedId then refuse "CAUSA.MANIFEST_MISMATCH" "manifest.json" None "investigationId differs from the directory"
                                    elif field "aggregateVersion" m <> Some(Json.Number n) || field "eventCount" m <> Some(Json.Number n) then
                                        refuse "CAUSA.MANIFEST_MISMATCH" "manifest.json" None "counts differ from the number of lines"
                                    elif text "createdAt" m <> Some (List.head events).OccurredAt || text "updatedAt" m <> Some (List.last events).OccurredAt then
                                        refuse "CAUSA.MANIFEST_MISMATCH" "manifest.json" None "createdAt/updatedAt differ from the first/last events"
                                    // stage 9
                                    elif text "eventsSha256" m <> Some(Canonical.sha256Hex files.Events) then refuse "CAUSA.EVENTS_HASH_MISMATCH" "manifest.json" None "eventsSha256"
                                    else
                                        match events |> List.tryFindIndex (fun e -> e.InvestigationId <> expectedId) with
                                        | Some i -> refuse "CAUSA.INVESTIGATION_MISMATCH" "events.ndjson" (Some(i + 1)) "an event names another investigation"
                                        | None ->
                                            let ids = events |> List.map (fun e -> e.EventId)
                                            if List.length (List.distinct ids) <> ids.Length then refuse "CAUSA.EVENT_ID_DUPLICATE" "events.ndjson" None "duplicate eventId"
                                            else
                                                match events |> List.indexed |> List.tryFind (fun (i, e) -> e.AggregateVersion <> int64 i + 1L) with
                                                | Some(i, _) -> refuse "CAUSA.SEQUENCE_INVALID" "events.ndjson" (Some(i + 1)) "versions are not 1..N"
                                                | None ->
                                                    match events |> List.tryFind (fun e -> e.EventId <> Canonical.eventId e.InvestigationId e.AggregateVersion e.CorrelationId) with
                                                    | Some e -> refuse "CAUSA.EVENT_ID_MISMATCH" "events.ndjson" (Some(int e.AggregateVersion)) "eventId derivation"
                                                    | None ->
                                                        // stage 14: a correlationId names one group of adjacent events
                                                        let groups =
                                                            events
                                                            |> List.fold (fun acc e -> match acc with (c, k) :: _ when c = e.CorrelationId -> acc | _ -> (e.CorrelationId, e.AggregateVersion) :: acc) []
                                                        let corrs = groups |> List.map fst
                                                        if List.length (List.distinct corrs) <> corrs.Length then refuse "CAUSA.CORRELATION_REUSED" "events.ndjson" None "a correlationId names two groups"
                                                        elif (List.head events).EventType <> "InvestigationStarted" then
                                                            refuse "CAUSA.HISTORY_INADMISSIBLE" "events.ndjson" (Some 1) "HISTORY.NO_START"
                                                        else
                                                            match (if version = "1.1.0" then revisionRules events else None) with
                                                            | Some(v, domainCode) -> refuse "CAUSA.HISTORY_INADMISSIBLE" "events.ndjson" (Some(int v)) domainCode
                                                            | None ->
                                                            // the rest of stage 15 is the engine's (not evaluated here); stages 16 and 17
                                                            let expected = snapshot version expectedId events
                                                            if text "snapshotSha256" m <> Some(Canonical.digest s) then refuse "CAUSA.SNAPSHOT_HASH_MISMATCH" "manifest.json" None "snapshotSha256"
                                                            elif Canonical.serialize s <> Canonical.serialize expected then
                                                                refuse "CAUSA.SNAPSHOT_MISMATCH" "snapshot.json" None "the snapshot is not the projection of the history"
                                                            else Ok { Version = version; InvestigationId = expectedId; Events = events; State = Json.field "state" (Some s) |> Option.defaultValue Json.Null; Bytes = files }

    /// A successor file set must extend the accepted events byte for byte (section 9.3).
    let isSuccessor (accepted: FileSet) (candidate: FileSet) =
        candidate.Events.Length >= accepted.Events.Length && Array.sub candidate.Events 0 accepted.Events.Length = accepted.Events
