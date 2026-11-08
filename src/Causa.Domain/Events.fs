// Owner: domain (T13). Turning the payloads one command produced into the stored envelopes of its command group
// (contract v1.0.0 section 5): consecutive versions after the current one, one correlationId (the command id), one
// occurredAt and actor, and the derived event ids. Pure: the caller supplies the time and actor.
namespace Causa.Domain

open System

[<RequireQualifiedAccess>]
module Events =
    let envelopes
        (investigation: InvestigationId)
        (current: AggregateVersion)
        (commandId: Guid)
        (occurredAt: DateTimeOffset)
        (actor: Actor)
        (storageVersion: string)
        (payloads: EventPayload list)
        : EventEnvelope list =
        let (AggregateVersion start) = current
        payloads
        |> List.mapi (fun index payload ->
            let version = AggregateVersion(start + int64 index + 1L)
            { EventId = EventIdentity.derive investigation version commandId
              Investigation = investigation
              Version = version
              Provenance =
                { OccurredAt = occurredAt
                  Actor = actor
                  CorrelationId = commandId
                  FormatVersion = storageVersion
                  SemanticVersion = storageVersion }
              Payload = payload })
