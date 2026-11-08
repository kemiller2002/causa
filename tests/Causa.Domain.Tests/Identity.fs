// Owner: domain (T13). Table-driven kernel tests (STATE-TRANSITION-TABLES section 17): state x command legality,
// gates, emitted event kinds, the resulting projection, capability changes and version conflicts. Each refusal test
// is paired with the legal case it differs from, so it fails for the intended reason (AR-009).
namespace Causa.Domain.Tests

open System
open Xunit
open Causa.Domain

module Build =
    let text s = NonEmptyText.create s |> Option.get
    let investigation = InvestigationId(Guid "3d5c7b9a-1e2f-4a6b-8c0d-112233445566")
    let actor = Actor.Human(text "investigator-1")
    let t0 = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero)
    let mutable private counter = 0
    let id () =
        counter <- counter + 1
        Guid(counter, 7s, 7s, [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy |])

    let envelope (state: Investigation option) (command: CoreCommand) =
        { CommandId = id ()
          Investigation = investigation
          ExpectedVersion = state |> Option.map (fun s -> s.Version)
          Command = Core command }

    /// Decide then apply: the state after the command, or the refusal.
    let run (state: Investigation option) (command: CoreCommand) : Result<Investigation, Decision> =
        let env = envelope state command
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "investigator-1" } state env with
        | Allowed payloads ->
            let current = state |> Option.map (fun s -> s.Version) |> Option.defaultValue (AggregateVersion 0L)
            Events.envelopes investigation current env.CommandId t0 actor StorageVersion.v110 payloads
            |> List.fold (fun acc e -> acc |> Result.bind (fun s -> Kernel.kernel.Evolve s e |> Result.map Some |> Result.mapError (fun err -> Blocked [ { Code = "EVOLVE"; Message = err.Reason } ]))) (Ok state)
            |> Result.map Option.get
        | other -> Error other

    let ok state command =
        match run (Some state) command with
        | Ok s -> s
        | Error d -> failwithf "%s was refused: %A" (Commands.name command) d

    let blockedCodes state command =
        match run (Some state) command with
        | Error(Blocked bs) -> bs |> List.map (fun b -> b.Code)
        | Ok _ -> failwithf "%s was allowed" (Commands.name command)
        | Error other -> failwithf "%s: %A" (Commands.name command) other

    let started () =
        match run None (StartInvestigation { Title = text "Checkout errors"; ObservationId = id (); Original = text "Checkout fails for some users since 09:00." }) with
        | Ok s -> s
        | Error d -> failwithf "start refused: %A" d

    let active () = ok (started ()) ActivateInvestigation

    let withHypothesis (s: Investigation) statement =
        let h = HypothesisId(id ())
        ok s (RecordHypothesis { HypothesisId = h; Statement = text statement; Mechanism = None }), h

    let withEvidence (s: Investigation) =
        let e = id ()
        ok
            s
            (AdmitEvidence
                { EvidenceId = e
                  ContentKind = ContentKind.Text
                  Content = text "Error log shows TLS handshake failures."
                  Category = EvidenceCategory.Log
                  Source = text "gateway log"
                  CapturedAt = None
                  Label = text "Gateway log"
                  SourceUrl = None }),
        e

    let supported () =
        let s, h = withHypothesis (active ()) "An expired intermediate certificate"
        let s, e = withEvidence s
        let s = ok s (RelateEvidence { RelationshipId = id (); EvidenceId = e; HypothesisId = h; Kind = RelationshipKind.Supports; Rationale = None })
        let s = ok s (MarkHypothesisPlausible { HypothesisId = h; Reason = text "fits the timing"; EvidenceIds = [] })
        let s = ok s (MarkHypothesisSupported { HypothesisId = h; Reason = text "log confirms"; EvidenceIds = [ e ] })
        s, h, e

module KernelTests =
    open Build

    [<Fact>]
    let ``event identity namespace is the contract's N`` () =
        Assert.Equal(Guid "241fefd4-c810-5a3a-9cfa-4bbd968ae1cb", EventIdentity.eventNamespace)

    [<Fact>]
    let ``wire enumerations map both ways exactly`` () =
        let roundTrip (e: WireEnum<'T>) =
            for (v, w) in e.Pairs do
                Assert.Equal(w, WireEnum.toWire e v)
                Assert.Equal(Some v, WireEnum.ofWire e w)
            Assert.Equal(None, WireEnum.ofWire e "NotAValue")
            Assert.Equal(List.length e.Pairs, e.Pairs |> List.map snd |> List.distinct |> List.length)
        roundTrip Wire.lifecycle
        roundTrip Wire.hypothesisState
        roundTrip Wire.relationshipKind
        roundTrip Wire.correctiveActionState
        Assert.Equal(None, WireEnum.ofWire Wire.lifecycle "Active")

    [<Fact>]
    let ``the registry has the 33 v1.1.0 event types and every storable core event maps into it`` () =
        Assert.Equal(31, EventRegistry.v100Types.Length)
        Assert.Equal(33, EventRegistry.v110Types |> List.distinct |> List.length)
        let s, h, e = supported ()
        for env in s.Timeline do
            match env.Payload with
            | CoreEvent ev ->
                let kind = EventRegistry.kindOf ev
                Assert.True(kind.IsSome)
                Assert.Contains(kind.Value.EventType, EventRegistry.v110Types)
            | ExtensionEvent _ -> ()
        ignore (h, e)

    [<Fact>]
    let ``start creates a draft investigation holding the original observation verbatim`` () =
        let s = started ()
        Assert.Equal(Lifecycle.Draft, s.Lifecycle)
        Assert.Equal(AggregateVersion 1L, s.Version)
        Assert.Equal("Checkout fails for some users since 09:00.", (s.Observations |> Map.toList |> List.exactlyOne |> snd).Original.Value)
        Assert.Equal(t0, s.CreatedAt)

    [<Fact>]
    let ``a stale expected version is a conflict and a repeated command is a duplicate`` () =
        let s = active ()
        let cmd = RecordHypothesis { HypothesisId = HypothesisId(id ()); Statement = text "DNS"; Mechanism = None }
        let env = { envelope (Some s) cmd with ExpectedVersion = Some(AggregateVersion 1L) }
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } (Some s) env with
        | Conflict(expected, actual) ->
            Assert.Equal(AggregateVersion 1L, expected)
            Assert.Equal(s.Version, actual)
        | other -> failwithf "expected a conflict, got %A" other
        // The same command at the current version is allowed (control).
        let current = { env with ExpectedVersion = Some s.Version }
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } (Some s) current with
        | Allowed _ -> ()
        | other -> failwithf "expected allowed, got %A" other
        // Applied once, the same command id is a duplicate.
        let after =
            match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } (Some s) current with
            | Allowed payloads ->
                Events.envelopes investigation s.Version current.CommandId t0 actor StorageVersion.v110 payloads
                |> List.fold (fun st e -> Kernel.kernel.Evolve (Some st) e |> Result.toOption |> Option.get) s
            | _ -> failwith "unreachable"
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } (Some after) { current with ExpectedVersion = Some after.Version } with
        | Duplicate ids -> Assert.NotEmpty ids
        | other -> failwithf "expected a duplicate, got %A" other

    [<Fact>]
    let ``hypothesis transitions follow the table`` () =
        let s, h = withHypothesis (active ()) "A cache stampede"
        let s, e = withEvidence s
        // Proposed -> Supported is not in the table; Proposed -> Plausible is.
        Assert.Contains("HYPOTHESIS.ILLEGAL_TRANSITION", blockedCodes s (MarkHypothesisSupported { HypothesisId = h; Reason = text "r"; EvidenceIds = [ e ] }))
        let s = ok s (MarkHypothesisPlausible { HypothesisId = h; Reason = text "r"; EvidenceIds = [] })
        // Support must cite evidence (R2.3).
        Assert.Contains("HYPOTHESIS.EVIDENCE_REQUIRED", blockedCodes s (MarkHypothesisSupported { HypothesisId = h; Reason = text "r"; EvidenceIds = [] }))
        let s = ok s (MarkHypothesisSupported { HypothesisId = h; Reason = text "r"; EvidenceIds = [ e ] })
        let entity = s.Hypotheses.[h]
        Assert.Equal(HypothesisState.Supported, entity.State)
        Assert.Equal(2, entity.History.Length)
        Assert.Equal("markSupported", (List.last entity.History).Command)
        Assert.Equal<Guid list>([ e ], (List.last entity.History).EvidenceIds)

    [<Fact>]
    let ``a falsified hypothesis reopens only with evidence admitted after the falsification`` () =
        let s, h = withHypothesis (active ()) "A bad deploy"
        let s, old = withEvidence s
        let s = ok s (FalsifyHypothesis { HypothesisId = h; Reason = text "no deploy that day"; EvidenceIds = [ old ] })
        Assert.Contains("HYPOTHESIS.REOPEN_NEEDS_NEW_EVIDENCE", blockedCodes s (ReopenHypothesis { HypothesisId = h; Reason = text "again"; EvidenceIds = [ old ] }))
        let s, fresh = withEvidence s
        let s = ok s (ReopenHypothesis { HypothesisId = h; Reason = text "a hotfix was deployed"; EvidenceIds = [ fresh ] })
        Assert.Equal(HypothesisState.Plausible, s.Hypotheses.[h].State)
        // R2.4: the falsification stays in history.
        Assert.Contains(s.Hypotheses.[h].History, fun c -> c.To = HypothesisState.Falsified)

    [<Fact>]
    let ``a hypothesis that repeats a falsified one is refused`` () =
        let s, h = withHypothesis (active ()) "A bad deploy"
        let s, e = withEvidence s
        let s = ok s (FalsifyHypothesis { HypothesisId = h; Reason = text "none"; EvidenceIds = [ e ] })
        Assert.Contains("HYPOTHESIS.REPEATS_FALSIFIED", blockedCodes s (RecordHypothesis { HypothesisId = HypothesisId(id ()); Statement = text "  a BAD deploy "; Mechanism = None }))
        let s, _ = withHypothesis s "A different explanation"
        Assert.Equal(2, s.Hypotheses.Count)

    [<Fact>]
    let ``the cause gate needs supported members with supporting evidence and no open blocking unknown`` () =
        let s, h, _ = supported ()
        let s, weak = withHypothesis s "A secondary factor"
        let cause = CausalAssertionId(id ())
        let s =
            ok s (RecordCausalAssertion { CauseId = cause; Outcome = text "checkout failures"; Members = [ { HypothesisId = h; Role = CauseRole.RootCause }; { HypothesisId = weak; Role = CauseRole.ContributingFactor } ]; Mechanism = None })
        Assert.Contains("CAUSE.MEMBER_NOT_SUPPORTED", blockedCodes s (ConfirmCause { CauseId = cause }))
        let alone = CausalAssertionId(id ())
        let s = ok s (RecordCausalAssertion { CauseId = alone; Outcome = text "checkout failures"; Members = [ { HypothesisId = h; Role = CauseRole.RootCause } ]; Mechanism = None })
        let blocked = ok s (IdentifyUnknown { UnknownId = id (); Question = text "Which regions?"; Materiality = Materiality.Blocking })
        Assert.Contains("UNKNOWN.BLOCKING_OPEN", blockedCodes blocked (ConfirmCause { CauseId = alone }))
        Assert.DoesNotContain("ConfirmCause", Capabilities.available Policies.coreDefault blocked)
        Assert.Contains("ConfirmCause", Capabilities.available Policies.coreDefault s)
        let s = ok s (ConfirmCause { CauseId = alone })
        Assert.Equal(CauseState.CauseConfirmed, s.Causes.[alone].State)

    [<Fact>]
    let ``resolution needs a confirmed cause and a satisfied, evidence-citing verification`` () =
        let s, h, e = supported ()
        let cause = CausalAssertionId(id ())
        let s = ok s (RecordCausalAssertion { CauseId = cause; Outcome = text "failures"; Members = [ { HypothesisId = h; Role = CauseRole.RootCause } ]; Mechanism = None })
        Assert.Contains("RESOLUTION.NO_CONFIRMED_CAUSE", blockedCodes s ResolveInvestigation)
        let s = ok s (ConfirmCause { CauseId = cause })
        let fix = CorrectiveActionId(id ())
        let s = ok s (RecordCorrectiveAction { CorrectiveActionId = fix; Kind = CorrectiveActionKind.Correction; Description = text "Renew the certificate"; TargetCauseIds = [ cause ] })
        let criterion = id ()
        let s = ok s (RecordVerificationCriterion { CriterionId = criterion; CorrectiveActionId = fix; ExpectedOutcome = text "No handshake failures for 24 h"; Method = None })
        Assert.Contains("RESOLUTION.UNVERIFIED", blockedCodes s ResolveInvestigation)
        Assert.Contains("VERIFICATION.EVIDENCE_REQUIRED", blockedCodes s (RecordVerification { VerificationId = id (); CriterionId = criterion; Outcome = VerificationOutcome.Satisfied; EvidenceIds = [] }))
        let s = ok s (RecordVerification { VerificationId = id (); CriterionId = criterion; Outcome = VerificationOutcome.Satisfied; EvidenceIds = [ e ] })
        Assert.Contains("ResolveInvestigation", Capabilities.available Policies.coreDefault s)
        let s = ok s ResolveInvestigation
        Assert.Equal(Lifecycle.Resolved, s.Lifecycle)
        // A resolved investigation records nothing until reopened (open-investigation rule).
        Assert.Contains("INVESTIGATION.NOT_OPEN", blockedCodes s (IdentifyUnknown { UnknownId = id (); Question = text "q"; Materiality = Materiality.Material }))
        let s = ok s (ReopenInvestigation { Reason = text "failures returned" })
        Assert.Equal(Lifecycle.Active, s.Lifecycle)

    [<Fact>]
    let ``evidence admission emits the revision in the same group and refuses an embedded artifact`` () =
        let s = active ()
        let s, e = withEvidence s
        let group = s.Timeline |> List.filter (fun env -> env.Version > AggregateVersion 2L)
        Assert.Equal(2, group.Length)
        Assert.Equal(1, group |> List.map (fun env -> env.Provenance.CorrelationId) |> List.distinct |> List.length)
        Assert.Equal(Some(text "Gateway log"), s.Evidence.[e].Label)
        let artifact =
            AdmitEvidence
                { EvidenceId = id ()
                  ContentKind = ContentKind.Artifact
                  Content = text "binary-bytes-here"
                  Category = EvidenceCategory.Document
                  Source = text "upload"
                  CapturedAt = None
                  Label = text "Upload"
                  SourceUrl = None }
        Assert.Contains("EVIDENCE.URL", blockedCodes s artifact)

    [<Fact>]
    let ``replay of the decided history equals the incremental state, and broken histories are refused`` () =
        let s, _, _ = supported ()
        match Kernel.kernel.Replay s.Timeline with
        | Ok replayed -> Assert.Equal(s, replayed)
        | Error e -> failwithf "replay failed: %A" e
        Assert.Equal(Error EmptyHistory, Kernel.kernel.Replay [])
        match Kernel.kernel.Replay(List.tail s.Timeline) with
        | Error(NonContiguousVersion(AggregateVersion 1L, AggregateVersion 2L)) -> ()
        | other -> failwithf "expected a gap at version 1, got %A" other
        let reordered = (List.item 1 s.Timeline) :: (List.head s.Timeline) :: (List.skip 2 s.Timeline)
        Assert.True(Result.isError (Kernel.kernel.Replay reordered))

    [<Fact>]
    let ``commands before start and extension commands are refused with typed decisions`` () =
        match run None (RecordHypothesis { HypothesisId = HypothesisId(id ()); Statement = text "x"; Mechanism = None }) with
        | Error(Blocked [ b ]) -> Assert.Equal("INVESTIGATION.NOT_STARTED", b.Code)
        | other -> failwithf "%A" other
        let s = active ()
        let env = { envelope (Some s) ActivateInvestigation with Command = Extension(SemanticId "acme/pack@1", "{}") }
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } (Some s) env with
        | UnsupportedSemantics [ "acme/pack@1" ] -> ()
        | other -> failwithf "%A" other

    [<Fact>]
    let ``projection lists the capabilities the state allows`` () =
        let draft = started ()
        let projection = Kernel.kernel.Project draft
        Assert.Contains("ActivateInvestigation", projection.Capabilities)
        Assert.DoesNotContain("ResolveInvestigation", projection.Capabilities)
        Assert.DoesNotContain("ReopenInvestigation", projection.Capabilities)
        Assert.Equal<EventEnvelope list>(draft.Timeline, projection.Timeline)
        let explained = Capabilities.evaluate Policies.coreDefault draft |> List.find (fun c -> c.Id = "ResolveInvestigation")
        Assert.NotEmpty explained.Blockers

/// feature/causes-actions: the command paths the workspace's cause, corrective action and conclusion editors send
/// (RecordCausalAssertion, RecordCorrectiveAction, RecordConclusion with CauseIds), each refusal paired with the legal
/// case it differs from, and every event they emit storable in causa-storage 1.2.0 (the version new investigations
/// are written in).
module CauseCorrectionTests =
    open Build

    let private cause (h: HypothesisId) role outcome =
        { CauseId = CausalAssertionId(id ()); Outcome = text outcome; Members = [ { HypothesisId = h; Role = role } ]; Mechanism = Some(text "the chain breaks") }

    let private eventsOf (before: Investigation) (after: Investigation) =
        after.Timeline
        |> List.filter (fun e -> e.Version > before.Version)
        |> List.choose (fun e -> match e.Payload with CoreEvent c -> Some c | _ -> None)

    [<Fact>]
    let ``RecordCausalAssertion records a proposed cause with its member in its role, in one event`` () =
        let s, h = withHypothesis (active ()) "The disk filled up"
        let c = cause h CauseRole.RootCause "The export stopped"
        let after = ok s (RecordCausalAssertion c)
        match eventsOf s after with
        | [ CausalAssertionRecorded recorded ] ->
            Assert.Equal(c.CauseId, recorded.CauseId)
            Assert.Equal<CausalMember list>(c.Members, recorded.Members)
        | other -> failwithf "expected one CausalAssertionRecorded, got %A" other
        let entity = after.Causes.[c.CauseId]
        Assert.Equal(CauseState.CauseProposed, entity.State)
        Assert.Equal(text "The export stopped", entity.Outcome)
        Assert.Equal(Some(text "the chain breaks"), entity.Mechanism)

    [<Fact>]
    let ``RecordCausalAssertion refuses no member, an unknown hypothesis, a falsified member and a reused id`` () =
        let s, h = withHypothesis (active ()) "The disk filled up"
        let legal = cause h CauseRole.RootCause "The export stopped"
        Assert.Contains("CAUSE.MEMBERS_REQUIRED", blockedCodes s (RecordCausalAssertion { legal with Members = [] }))
        Assert.Contains("ENTITY.NOT_FOUND", blockedCodes s (RecordCausalAssertion(cause (HypothesisId(id ())) CauseRole.RootCause "x")))
        let s2, e = withEvidence s
        let falsified = ok s2 (FalsifyHypothesis { HypothesisId = h; Reason = text "plenty of space"; EvidenceIds = [ e ] })
        Assert.Contains("CAUSE.MEMBER_EXCLUDED", blockedCodes falsified (RecordCausalAssertion legal))
        let recorded = ok s (RecordCausalAssertion legal)
        Assert.Contains("ID.DUPLICATE", blockedCodes recorded (RecordCausalAssertion legal))
        Assert.Equal(1, recorded.Causes.Count)

    [<Fact>]
    let ``RecordCausalAssertion is offered once a hypothesis can take part, and not before`` () =
        let s = active ()
        Assert.DoesNotContain("RecordCausalAssertion", Capabilities.available Policies.coreDefault s)
        let s, _ = withHypothesis s "The disk filled up"
        Assert.Contains("RecordCausalAssertion", Capabilities.available Policies.coreDefault s)

    [<Fact>]
    let ``RecordCorrectiveAction records a proposed action against a cause and refuses an unknown cause`` () =
        let s, h = withHypothesis (active ()) "The disk filled up"
        let c = cause h CauseRole.RootCause "The export stopped"
        let s = ok s (RecordCausalAssertion c)
        let fix = CorrectiveActionId(id ())
        let command = { CorrectiveActionId = fix; Kind = CorrectiveActionKind.Mitigation; Description = text "Rotate the logs nightly"; TargetCauseIds = [ c.CauseId ] }
        Assert.Contains("ENTITY.NOT_FOUND", blockedCodes s (RecordCorrectiveAction { command with TargetCauseIds = [ CausalAssertionId(id ()) ] }))
        Assert.Contains("ID.REPEATED", blockedCodes s (RecordCorrectiveAction { command with TargetCauseIds = [ c.CauseId; c.CauseId ] }))
        let after = ok s (RecordCorrectiveAction command)
        match eventsOf s after with
        | [ CorrectiveActionRecorded recorded ] -> Assert.Equal<CausalAssertionId list>([ c.CauseId ], recorded.TargetCauseIds)
        | other -> failwithf "expected one CorrectiveActionRecorded, got %A" other
        let entity = after.CorrectiveActions.[fix]
        Assert.Equal(CorrectiveActionState.CorrectionProposed, entity.State)
        Assert.Equal(CorrectiveActionKind.Mitigation, entity.Kind)

    [<Fact>]
    let ``a conclusion names the causes it rests on; confirmedCause needs every named cause confirmed`` () =
        let s, h, _ = supported ()
        let c = cause h CauseRole.RootCause "Checkout fails"
        let s = ok s (RecordCausalAssertion c)
        let conclusion strength causes =
            RecordConclusion { ConclusionId = id (); Statement = text "The certificate expired."; Strength = strength; CauseIds = causes; ResidualUncertainty = None }
        // Before: CauseIds was always empty, so confirmedCause was refused for want of causes.
        Assert.Contains("CONCLUSION.CAUSE_NOT_CONFIRMED", blockedCodes s (conclusion ConclusionStrength.ConfirmedCause []))
        // Naming a proposed cause is not enough for confirmedCause (R6.1)...
        Assert.Contains("CONCLUSION.CAUSE_NOT_CONFIRMED", blockedCodes s (conclusion ConclusionStrength.ConfirmedCause [ c.CauseId ]))
        // ...a cause the investigation does not hold is refused...
        Assert.Contains("ENTITY.NOT_FOUND", blockedCodes s (conclusion ConclusionStrength.SupportedCause [ CausalAssertionId(id ()) ]))
        // ...while a supported-cause conclusion may name it, and the cause ids are kept.
        let supportedAfter = ok s (conclusion ConclusionStrength.SupportedCause [ c.CauseId ])
        match eventsOf s supportedAfter with
        | [ ConclusionRecorded recorded ] -> Assert.Equal<CausalAssertionId list>([ c.CauseId ], recorded.CauseIds)
        | other -> failwithf "expected one ConclusionRecorded, got %A" other
        // Once the cause is confirmed, the confirmed-cause conclusion naming it is allowed.
        let confirmed = ok s (ConfirmCause { CauseId = c.CauseId })
        match eventsOf s confirmed with
        | [ CauseConfirmed _ ] -> ()
        | other -> failwithf "expected one CauseConfirmed, got %A" other
        let after = ok confirmed (conclusion ConclusionStrength.ConfirmedCause [ c.CauseId ])
        Assert.Equal(1, after.Conclusions.Count)

    [<Fact>]
    let ``every event the cause, corrective action and conclusion editors emit is storable in causa-storage 1.2.0`` () =
        let s, h, _ = supported ()
        let c = cause h CauseRole.ContributingFactor "Checkout fails"
        let s1 = ok s (RecordCausalAssertion c)
        let s2 = ok s1 (ConfirmCause { CauseId = c.CauseId })
        let s3 = ok s2 (RecordCorrectiveAction { CorrectiveActionId = CorrectiveActionId(id ()); Kind = CorrectiveActionKind.Prevention; Description = text "Alert 30 days before expiry"; TargetCauseIds = [ c.CauseId ] })
        let s4 = ok s3 (RecordConclusion { ConclusionId = id (); Statement = text "Expired."; Strength = ConclusionStrength.ConfirmedCause; CauseIds = [ c.CauseId ]; ResidualUncertainty = None })
        let emitted = eventsOf s s4
        Assert.Equal(4, emitted.Length)
        for e in emitted do
            Assert.True(EventRegistry.representableIn StorageVersion.v120 e, sprintf "%A is not storable in 1.2.0" e)
            Assert.True(EventRegistry.representableIn StorageVersion.v100 e, sprintf "%A needs more than 1.0.0" e)
