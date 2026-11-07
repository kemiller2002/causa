// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-006 populated investigation: a compact, complete case with stable ids, timestamps and deterministic order.
namespace Causa.Acceptance.Live

open System
open System.Text.RegularExpressions
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF006Tests(fixture: LiveFixture<PF006Tests>) =
    /// The populated case: active, with competing hypotheses and contradicting evidence.
    let populated = PinnedCatalog.cases |> List.find (fun c -> c.Key = "challenger-sts-51l")

    let read () =
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        live, head, PinnedCatalog.ok ("read " + populated.Key) (PinnedCatalog.readFiles live.Reader head populated)

    let str name (e: Json option) = e |> Json.field name |> Json.text

    interface IClassFixture<LiveFixture<PF006Tests>>

    /// PF-006-O1: the populated case read from real GitHub holds observations, competing hypotheses, supporting and
    /// contradicting evidence with provenance, unknowns, causal relationships, corrective actions, verification records
    /// and timeline events, and it opens through the application's load path as one verified investigation.
    [<Fact>]
    member _.``PF-006-O1``() =
        let live, _, files = read ()
        let events = PinnedCatalog.events files
        let count kind = events |> List.filter (fun e -> str "eventType" e = Some kind) |> List.length
        for kind in [ "ObservationRecorded"; "EvidenceAdmitted"; "UnknownIdentified"; "CausalAssertionRecorded"; "CorrectiveActionRecorded"; "VerificationRecorded" ] do
            Assert.True(count kind > 0, sprintf "the populated case has no %s" kind)
        Assert.True(count "HypothesisRecorded" >= 2, "the populated case has no competing hypotheses")
        let relations = events |> List.filter (fun e -> str "eventType" e = Some "EvidenceRelated") |> List.choose (Json.field "payload" >> str "kind") |> set
        Assert.True(Set.isSubset (set [ "supports"; "contradicts" ]) relations, sprintf "relations: %A" relations)
        let admitted = events |> List.filter (fun e -> str "eventType" e = Some "EvidenceAdmitted")
        Assert.All(admitted, fun e -> Assert.True((e |> Json.path [ "payload"; "source" ]).IsSome, "evidence without provenance"))
        Assert.All(events, fun e -> Assert.True((str "occurredAt" e).IsSome, "an event without a timeline time"))
        AppLoad.requireCodec ()
        let v = AppLoad.load live.Execute live.ReadConnection (PinnedCatalog.target populated) |> AppLoad.requireVerified populated.Key
        Assert.Equal(events.Length, v.History.Length)

    /// PF-006-O2: stable ids, timestamps and deterministic order: two reads at one commit are byte-identical, versions
    /// run 1..n without a gap, event ids are unique UUIDs and every timestamp is UTC with milliseconds.
    [<Fact>]
    member _.``PF-006-O2``() =
        let live, head, files = read ()
        let again = PinnedCatalog.ok "second read" (PinnedCatalog.readFiles live.Reader head populated)
        Assert.Equal<byte[]>(files.Events, again.Events)
        Assert.Equal(populated.EventsSha256, PinnedCatalog.sha256 files.Events)
        let events = PinnedCatalog.events files
        Assert.Equal<int list>([ 1 .. events.Length ], events |> List.map (Json.field "aggregateVersion" >> Json.int >> Option.defaultValue -1))
        let ids = events |> List.choose (str "eventId")
        Assert.Equal(events.Length, ids |> List.distinct |> List.length)
        Assert.All(ids, fun id -> Assert.True(fst (Guid.TryParse id), id))
        let stamp = Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")
        Assert.All(events |> List.choose (str "occurredAt"), fun t -> Assert.Matches(stamp, t))
