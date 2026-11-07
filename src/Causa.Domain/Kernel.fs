// Owner: domain (T13). IF41-DOMAIN: the F# kernel (Boundaries A-C). Pure: no network, clock, randomness, GitHub or
// browser types. ReplayError, frozen with this contract, is declared in Entities.fs (unchanged) so Evolve can use it.
namespace Causa.Domain

type InvestigationProjection =
    { Investigation: Investigation
      Timeline: EventEnvelope list
      Capabilities: string list }

type DomainKernel =
    { Decide: PolicySet -> ActorContext -> Investigation option -> CommandEnvelope -> Decision
      Evolve: Investigation option -> EventEnvelope -> Result<Investigation, ProjectionError>
      Replay: EventEnvelope list -> Result<Investigation, ReplayError>
      Project: Investigation -> InvestigationProjection }

/// The one domain decision path (STATE-TRANSITION-TABLES section 17): every consumer uses this value.
[<RequireQualifiedAccess>]
module Kernel =
    let project (investigation: Investigation) : InvestigationProjection =
        { Investigation = investigation
          Timeline = investigation.Timeline
          Capabilities = Capabilities.available Policies.coreDefault investigation }

    let kernel : DomainKernel =
        { Decide = Decide.decide
          Evolve = Evolve.evolve
          Replay = Evolve.replay
          Project = project }
