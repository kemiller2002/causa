// INV-034 on causa-storage 1.2.0: ReviseTitle and ReviseUnknown. Each refusal is paired with the legal case it differs
// from, so it fails for the intended reason (AR-009).
namespace Causa.Domain.Tests

open System
open Xunit
open Causa.Domain
open Build

module DetailsRevisionTests =
    let private withUnknown (s: Investigation) question materiality =
        let u = id ()
        ok s (IdentifyUnknown { UnknownId = u; Question = text question; Materiality = materiality }), u

    let private events (s: Investigation) =
        s.Timeline |> List.choose (fun e -> match e.Payload with CoreEvent c -> Some c | _ -> None)

    let private decideOn (s: Investigation) (command: CoreCommand) =
        Kernel.kernel.Decide Policies.coreDefault { ActorId = "investigator-1" } (Some s) (envelope (Some s) command)

    [<Fact>]
    let ``ReviseTitle sets the title verbatim and emits InvestigationTitleRevised alone`` () =
        let s = active ()
        match decideOn s (ReviseTitle { Title = text "  Checkout errors since 09:00  "; Reason = Some(text "narrowed") }) with
        | Allowed [ CoreEvent(InvestigationTitleRevised r) ] ->
            Assert.Equal("  Checkout errors since 09:00  ", r.Title.Value)
            Assert.Equal(Some "narrowed", r.Reason |> Option.map NonEmptyText.value)
        | other -> failwithf "%A" other
        let s2 = ok s (ReviseTitle { Title = text "  Checkout errors since 09:00  "; Reason = None })
        Assert.Equal("  Checkout errors since 09:00  ", s2.Title.Value)
        Assert.Equal(Investigation.versionNumber s.Version + 1L, Investigation.versionNumber s2.Version)

    [<Fact>]
    let ``ReviseUnknown revises the question and keeps the id, state, basis, materiality and provenance`` () =
        let s, u = withUnknown (active ()) "Why did checkout fail?" Materiality.Informational
        let before = s.Unknowns[u]
        let s2 = ok s (ReviseUnknown { UnknownId = u; Question = text "Why did checkout fail\nfor card payments?" })
        let after = s2.Unknowns[u]
        Assert.Equal("Why did checkout fail\nfor card payments?", after.Question.Value)
        Assert.Equal(before.Id, after.Id)
        Assert.Equal(before.State, after.State)
        Assert.Equal(before.Basis, after.Basis)
        Assert.Equal(before.Materiality, after.Materiality)
        Assert.Equal(before.Provenance, after.Provenance)
        Assert.Equal(s.Unknowns.Count, s2.Unknowns.Count)
        match events s2 |> List.last with
        | UnknownRevised r -> Assert.Equal(u, r.UnknownId)
        | other -> failwithf "%A" other

    [<Fact>]
    let ``an investigating unknown can be revised too`` () =
        let s, u = withUnknown (active ()) "Did the cache empty?" Materiality.Material
        let investigating = { s with Unknowns = s.Unknowns |> Map.add u { s.Unknowns[u] with State = UnknownState.Investigating } }
        let s2 = ok investigating (ReviseUnknown { UnknownId = u; Question = text "Did the token cache empty at 11:58?" })
        Assert.Equal(UnknownState.Investigating, s2.Unknowns[u].State)

    [<Fact>]
    let ``history is preserved: the timeline keeps the earlier title and question and both revisions`` () =
        let s, u = withUnknown (active ()) "Why did checkout fail?" Materiality.Informational
        let s2 = ok s (ReviseTitle { Title = text "Checkout fails for card payments"; Reason = None })
        let s3 = ok s2 (ReviseUnknown { UnknownId = u; Question = text "Why do card payments fail?" })
        let all = events s3
        Assert.Contains(all, fun e -> match e with InvestigationStarted st -> st.Title.Value = "Checkout errors" | _ -> false)
        Assert.Contains(all, fun e -> match e with UnknownIdentified x -> x.Question.Value = "Why did checkout fail?" | _ -> false)
        Assert.Contains(all, fun e -> match e with InvestigationTitleRevised r -> r.Title.Value = "Checkout fails for card payments" | _ -> false)
        Assert.Contains(all, fun e -> match e with UnknownRevised r -> r.Question.Value = "Why do card payments fail?" | _ -> false)
        Assert.Equal<EventEnvelope list>(s.Timeline, s3.Timeline |> List.take s.Timeline.Length)
        // Each revision is a command group of its own.
        let last2 = s3.Timeline |> List.skip (s3.Timeline.Length - 2)
        Assert.NotEqual(last2[0].Provenance.CorrelationId, last2[1].Provenance.CorrelationId)
        // Replaying the history gives the same state (Evolve is the projection).
        match Kernel.kernel.Replay s3.Timeline with
        | Ok r ->
            Assert.Equal(s3.Title, r.Title)
            Assert.Equal(s3.Unknowns[u].Question, r.Unknowns[u].Question)
        | Error e -> failwithf "%A" e

    [<Fact>]
    let ``a value equal to the current one is refused as DETAILS.UNCHANGED`` () =
        let s, u = withUnknown (active ()) "Why did checkout fail?" Materiality.Informational
        Assert.Equal<string list>([ "DETAILS.UNCHANGED" ], blockedCodes s (ReviseTitle { Title = s.Title; Reason = None }))
        Assert.Equal<string list>([ "DETAILS.UNCHANGED" ], blockedCodes s (ReviseUnknown { UnknownId = u; Question = text "Why did checkout fail?" }))
        // One character apart is a change (ordinal, verbatim).
        ok s (ReviseTitle { Title = text "Checkout errors "; Reason = None }) |> ignore
        ok s (ReviseUnknown { UnknownId = u; Question = text "Why did checkout fail? " }) |> ignore

    [<Fact>]
    let ``a closed or resolved investigation is not open: INVESTIGATION.NOT_OPEN`` () =
        let s, u = withUnknown (active ()) "Why did checkout fail?" Materiality.Informational
        for lifecycle in [ Lifecycle.Closed; Lifecycle.Resolved; Lifecycle.Archived ] do
            let shut = { s with Lifecycle = lifecycle }
            Assert.Equal<string list>([ "INVESTIGATION.NOT_OPEN" ], blockedCodes shut (ReviseTitle { Title = text "Other"; Reason = None }))
            Assert.Equal<string list>([ "INVESTIGATION.NOT_OPEN" ], blockedCodes shut (ReviseUnknown { UnknownId = u; Question = text "Other?" }))
        // draft is open
        ok (started ()) (ReviseTitle { Title = text "Other"; Reason = None }) |> ignore

    [<Fact>]
    let ``a missing unknown is ENTITY.NOT_FOUND and a resolved one UNKNOWN.NOT_OPEN`` () =
        let s, u = withUnknown (active ()) "Did the disk fill?" Materiality.Material
        Assert.Equal<string list>([ "ENTITY.NOT_FOUND" ], blockedCodes s (ReviseUnknown { UnknownId = id (); Question = text "Other?" }))
        for closedState in [ UnknownState.UnknownResolved; UnknownState.UnknownAcceptedResidual; UnknownState.UnknownSuperseded ] do
            let done' = { s with Unknowns = s.Unknowns |> Map.add u { s.Unknowns[u] with State = closedState } }
            Assert.Equal<string list>([ "UNKNOWN.NOT_OPEN" ], blockedCodes done' (ReviseUnknown { UnknownId = u; Question = text "Other?" }))
        ok s (ReviseUnknown { UnknownId = u; Question = text "Other?" }) |> ignore

    [<Fact>]
    let ``a blank title or question cannot be expressed: NonEmptyText refuses it`` () =
        for blank in [ ""; " "; " \t\n "; "　" ] do
            Assert.True((NonEmptyText.create blank).IsNone, sprintf "%A was accepted" blank)
        Assert.True((NonEmptyText.create " x ").IsSome)

    [<Fact>]
    let ``capabilities offer ReviseTitle while open and ReviseUnknown only with an open unknown`` () =
        let s = active ()
        let caps (i: Investigation) = Capabilities.available Policies.coreDefault i
        Assert.Contains("ReviseTitle", caps s)
        Assert.DoesNotContain("ReviseUnknown", caps s)
        let reason = Capabilities.evaluate Policies.coreDefault s |> List.find (fun c -> c.Id = "ReviseUnknown")
        Assert.Equal("UNKNOWN.NONE_OPEN", reason.Blockers.Head.Code)
        let s2, u = withUnknown s "Why?" Materiality.Informational
        Assert.Contains("ReviseUnknown", caps s2)
        let shut = { s2 with Lifecycle = Lifecycle.Closed }
        Assert.DoesNotContain("ReviseTitle", caps shut)
        Assert.DoesNotContain("ReviseUnknown", caps shut)
        let resolved = { s2 with Unknowns = s2.Unknowns |> Map.add u { s2.Unknowns[u] with State = UnknownState.UnknownResolved } }
        Assert.DoesNotContain("ReviseUnknown", caps resolved)
        Assert.Contains("ReviseTitle", (Kernel.kernel.Project s).Capabilities)

    [<Fact>]
    let ``the registry stores both events as 1.2.0 types and 1.0.0 and 1.1.0 investigations cannot hold them`` () =
        let title = InvestigationTitleRevised { Title = text "t"; Reason = None }
        let question = UnknownRevised { UnknownId = id (); Question = text "q?" }
        Assert.Equal(Some "InvestigationTitleRevised", EventRegistry.kindOf title |> Option.map (fun k -> k.EventType))
        Assert.Equal(Some "UnknownRevised", EventRegistry.kindOf question |> Option.map (fun k -> k.EventType))
        for e in [ title; question ] do
            Assert.Equal(Some "1.2.0", EventRegistry.requiredVersion e)
            Assert.True(EventRegistry.representableIn "1.2.0" e)
            Assert.False(EventRegistry.representableIn "1.1.0" e)
            Assert.False(EventRegistry.representableIn "1.0.0" e)
            Assert.False(EventRegistry.representableIn "2.0.0" e)
        Assert.Equal(35, EventRegistry.v120Types.Length)
        Assert.Equal(33, EventRegistry.v110Types.Length)
        Assert.Equal<string list>(EventRegistry.v110Types, EventRegistry.v120Types |> List.take 33)
        // Earlier events stay representable in every later version.
        let revision = ObservationRevised { ObservationId = id (); Original = text "o"; Source = None }
        Assert.True(EventRegistry.representableIn "1.2.0" revision)
        Assert.False(EventRegistry.representableIn "1.0.0" revision)
        Assert.True(EventRegistry.representableIn "1.0.0" (InvestigationActivated Lifecycle.Draft))
        Assert.Equal("ReviseTitle", Commands.name (ReviseTitle { Title = text "t"; Reason = None }))
        Assert.Equal("ReviseUnknown", Commands.name (ReviseUnknown { UnknownId = Guid.Empty; Question = text "q" }))
