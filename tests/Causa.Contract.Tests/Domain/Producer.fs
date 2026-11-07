// Owner: domain (T13). Producer contract tests for IF41-DOMAIN: the kernel value exists with the frozen signatures,
// its decisions are typed, and every event it can emit for storage has a registered causa-storage wire form.
namespace Causa.Contract.Tests.Domain

open System
open Xunit
open Causa.Domain

module Producer =
    let private text s = NonEmptyText.create s |> Option.get
    let private investigation = InvestigationId(Guid "aa000000-0000-4000-8000-000000000001")

    [<Fact>]
    let ``IF41-DOMAIN kernel starts an investigation and replays what it decided`` () =
        let kernel : DomainKernel = Kernel.kernel
        let command =
            { CommandId = Guid "aa000000-0000-4000-8000-000000000002"
              Investigation = investigation
              ExpectedVersion = None
              Command = Core(StartInvestigation { Title = text "t"; ObservationId = Guid "aa000000-0000-4000-8000-000000000003"; Original = text "o" }) }
        match kernel.Decide Policies.coreDefault { ActorId = "a" } None command with
        | Allowed payloads ->
            let events =
                Events.envelopes investigation (AggregateVersion 0L) command.CommandId DateTimeOffset.UnixEpoch (Actor.System(text "test")) StorageVersion.v110 payloads
            Assert.Equal(EventIdentity.derive investigation (AggregateVersion 1L) command.CommandId, (List.head events).EventId)
            match kernel.Replay events with
            | Ok s ->
                Assert.Equal(investigation, s.Id)
                Assert.Equal(AggregateVersion 1L, s.Version)
                Assert.Contains("ActivateInvestigation", (kernel.Project s).Capabilities)
            | Error e -> failwithf "replay failed: %A" e
        | other -> failwithf "start refused: %A" other

    [<Fact>]
    let ``IF41-DOMAIN refuses a command before start with a typed blocker, not an exception`` () =
        let command = { CommandId = Guid.Empty; Investigation = investigation; ExpectedVersion = None; Command = Core ActivateInvestigation }
        match Kernel.kernel.Decide Policies.coreDefault { ActorId = "a" } None command with
        | Blocked(b :: _) -> Assert.Equal("INVESTIGATION.NOT_STARTED", b.Code)
        | other -> failwithf "%A" other

    [<Fact>]
    let ``only ExtensionAttached lacks a v1 wire form`` () =
        Assert.Equal(None, EventRegistry.kindOf (ExtensionAttached { Semantic = SemanticId "x"; PayloadJson = "{}" }))
        Assert.Equal(Some "LifecycleChanged", EventRegistry.kindOf (InvestigationActivated Lifecycle.Draft) |> Option.map (fun k -> k.EventType))
        Assert.False(EventRegistry.representableIn StorageVersion.v100 (EvidenceRevised { EvidenceId = Guid.Empty; Label = text "l"; Source = text "s"; SourceUrl = None }))
