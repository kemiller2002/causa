// Owner: persistence. IF41-STORAGE producer contract tests: causa-storage 1.0.0/1.1.0 file sets.
namespace Causa.Contract.Tests.Storage

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence

module StorageFixtures =
    /// causa-data fixtures, gzip+base64 of the exact bytes: (directory id, manifest, events, snapshot).
    let embedded =
        [
          "d6e09cba-696d-5cb9-a822-9aa902a3d7e5"
          "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"
          "fc3f71a9-4c14-5a31-8196-7ce17a7041c1"
          "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"
        ]

    let private gunzip (b64: string) =
        use input = new MemoryStream(Convert.FromBase64String b64)
        use gz = new GZipStream(input, CompressionMode.Decompress)
        use output = new MemoryStream()
        gz.CopyTo output
        output.ToArray()

    /// (directory id, file set) of each embedded fixture.
    let fixtures : (string * FileSet) list =
        embedded
        |> List.chunkBySize 4
        |> List.map (function
            | [ id; m; e; s ] -> id, { Manifest = gunzip m; Events = gunzip e; Snapshot = gunzip s }
            | _ -> failwith "embedded fixture")

    let address = WorkspaceAddress.catalog (Workspace "workspaces/demo/everyday-v1")
    let at = CommitOid "0123456789abcdef0123456789abcdef01234567"

    let text (s: string) = NonEmptyText.create s |> Option.get
    let t0 = DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero)
    let human = Actor.Human(text "investigator-01")

    let envelope (inv: InvestigationId) (v: int64) (corr: Guid) (version: string) (event: CoreEvent) : EventEnvelope =
        let (InvestigationId g) = inv
        { EventId = EventId(Guid(Canonical.eventId (Canonical.guidText g) v (Canonical.guidText corr)))
          Investigation = inv
          Version = AggregateVersion v
          Provenance = { OccurredAt = t0.AddSeconds(float v); Actor = human; CorrelationId = corr; FormatVersion = version; SemanticVersion = version }
          Payload = CoreEvent event }

    let started (inv: InvestigationId) version =
        envelope inv 1L (Guid.NewGuid()) version (InvestigationStarted { Title = text "Pump trips at noon"; ObservationId = Guid.NewGuid(); Original = text "The pump trips at noon every day." })

    let bytesEqual (a: FileSet) (b: FileSet) = a.Manifest = b.Manifest && a.Events = b.Events && a.Snapshot = b.Snapshot

module Producer =
    open StorageFixtures

    /// The causa-data fixtures verify, and decoding then encoding reproduces every file byte for byte.
    [<Fact>]
    let GoldenFixturesRoundTrip () =
        for (id, files) in fixtures do
            match Wire.verify id files with
            | Error e -> failwithf "%s: %A" id e
            | Ok w -> Assert.True(bytesEqual files (Wire.fileSet w.Version id w.Events), id)
            match StorageCodecs.codec.Verify address at files with
            | Error e -> failwithf "%s: %A" id e
            | Ok v ->
                Assert.Equal(at, v.ReadAt)
                match StorageCodecs.encodeHistory v.Investigation.Id v.History with
                | Ok re -> Assert.True(bytesEqual files re, id)
                | Error e -> failwith e

    let private mutate (files: FileSet) (f: string -> string) = { files with Events = Encoding.UTF8.GetBytes(f (Encoding.UTF8.GetString files.Events)) }

    /// Every integrity check refuses its forgery with its primary code (CONTRACT section 11.1).
    [<Fact>]
    let EachIntegrityCheckRefusesItsForgery () =
        let id, files = List.head fixtures
        let code (fs: FileSet) = match Wire.verify id fs with Ok _ -> "OK" | Error r -> r.Code
        let lines = (Encoding.UTF8.GetString files.Events).TrimEnd('\n').Split('\n')
        Assert.Equal("CAUSA.ENCODING", code { files with Events = [||] })
        Assert.Equal("CAUSA.ENCODING", code (mutate files (fun t -> t.Replace("\n", "\r\n"))))
        Assert.Equal("CAUSA.ENCODING", code (mutate files (fun t -> t.TrimEnd('\n'))))
        Assert.Equal("CAUSA.ENCODING", code (mutate files (fun t -> t + "\n")))
        Assert.Equal("CAUSA.JSON_MALFORMED", code (mutate files (fun t -> t.Replace("\"aggregateVersion\":1,", "\"aggregateVersion\":1.5,"))))
        Assert.Equal("CAUSA.NON_CANONICAL", code (mutate files (fun t -> t.Replace("{\"actor\"", "{ \"actor\""))))
        Assert.Equal("CAUSA.UNSUPPORTED_VERSION", code (mutate files (fun t -> t.Replace("\"formatVersion\":\"1.1.0\"", "\"formatVersion\":\"1.2.0\""))))
        Assert.Equal("CAUSA.UNKNOWN_EVENT_TYPE", code (mutate files (fun t -> t.Replace("\"eventType\":\"InvestigationStarted\"", "\"eventType\":\"InvestigationBegun\""))))
        Assert.Equal("CAUSA.SCHEMA_VIOLATION", code (mutate files (fun t -> t.Replace("\"kind\":\"human\"", "\"kind\":\"robot\""))))
        Assert.Equal("CAUSA.EVENTS_HASH_MISMATCH", code (mutate files (fun t -> t.Replace("\"actorId\":\"", "\"actorId\":\"x"))))
        let droppedLast = String.Join("\n", lines |> Array.take (lines.Length - 1)) + "\n"
        Assert.Equal("CAUSA.MANIFEST_MISMATCH", code { files with Events = Encoding.UTF8.GetBytes droppedLast })
        let swapped = String.Join("\n", Array.append [| lines[1]; lines[0] |] lines[2..]) + "\n"
        Assert.NotEqual<string>("OK", code { files with Events = Encoding.UTF8.GetBytes swapped })
        Assert.Equal("CAUSA.SNAPSHOT_HASH_MISMATCH", code { files with Snapshot = Encoding.UTF8.GetBytes((Encoding.UTF8.GetString files.Snapshot).Replace("\"projectionVersion\":1", "\"projectionVersion\":1").Replace("Checkout", "Checkout ")) })

    /// Every event of every embedded catalog case decodes to the domain and encodes back to its exact wire record.
    [<Fact>]
    let EveryCatalogEventRoundTrips () =
        for (id, files) in fixtures do
            match Wire.verify id files with
            | Error e -> failwithf "%A" e
            | Ok w ->
                for ev in w.Events do
                    match DomainWire.ofWire ev |> Result.bind DomainWire.toWire with
                    | Ok back -> Assert.Equal(Canonical.serialize (Wire.toJson ev), Canonical.serialize (Wire.toJson back))
                    | Error e -> failwithf "%s v%d: %s" id ev.AggregateVersion e

    /// A new investigation's first file set verifies and round-trips (EXT-DATA-008).
    [<Fact>]
    let EncodeNewVerifiesAndRoundTrips () =
        let inv = InvestigationId(Guid.NewGuid())
        let history = [ started inv "1.1.0" ]
        match StorageCodecs.codec.EncodeNew inv history with
        | Error e -> failwithf "%A" e
        | Ok files ->
            match StorageCodecs.codec.Verify address at files with
            | Ok v ->
                Assert.Equal("Pump trips at noon", v.Investigation.Title.Value)
                Assert.Equal<EventEnvelope list>(history, v.History)
            | Error e -> failwithf "%A" e
        // A sub-millisecond time has no wire form: refused, never truncated.
        let tooFine = { (List.head history) with Provenance = { (List.head history).Provenance with OccurredAt = t0.AddTicks 1L } }
        Assert.True(Result.isError (StorageCodecs.codec.EncodeNew inv [ tooFine ]))

    [<Fact>]
    let WritableRefusesCatalog () =
        Assert.True((Writable.ofAddress (WorkspaceAddress.catalog (Workspace "workspaces/demo/historical-v1"))).IsNone)
        Assert.True((WorkspaceAddress.workingCopy [] "run-1" |> Option.bind Writable.ofAddress).IsSome)

    [<Fact>]
    let WorkingCopyNeverNamesACatalogWorkspace () =
        let catalog = Workspace "workspaces/demo/causa-app-x"
        Assert.True((WorkspaceAddress.workingCopy [ catalog ] "x").IsNone)
        Assert.True((WorkspaceAddress.workingCopy [] "Bad/Run").IsNone)
        Assert.Equal(Workspace "workspaces/demo/causa-app-run-1", (WorkspaceAddress.workingCopy [] "run-1" |> Option.get).Workspace)

    /// 1.1.0: revisions and the two new kinds are stored and read back (the embedded j2 fixture covers both events
    /// and both kinds).
    [<Fact>]
    let V110RevisionsAndKindsRoundTrip () =
        let id, files = fixtures |> List.find (fun (_, f) -> (Encoding.UTF8.GetString f.Manifest).Contains "1.1.0")
        let text = Encoding.UTF8.GetString files.Events
        for needle in [ "EvidenceRevised"; "ObservationRevised"; "consistentWith"; "inconclusive" ] do
            Assert.Contains(needle, text)
        match StorageCodecs.codec.Verify address at files with
        | Ok v ->
            let kinds = v.History |> List.choose (fun e -> match e.Payload with CoreEvent(EvidenceRelationshipRecorded r) -> Some r.Kind | _ -> None)
            Assert.Contains(RelationshipKind.ConsistentWith, kinds)
            Assert.Contains(RelationshipKind.Inconclusive, kinds)
        | Error e -> failwithf "%s %A" id e

    /// A 1.0.0 investigation refuses a 1.1.0-only event as CAUSA.UNREPRESENTABLE before anything is written.
    [<Fact>]
    let V100InvestigationRefusesV110Events () =
        let inv = InvestigationId(Guid.NewGuid())
        let start = started inv "1.0.0"
        let s = match start.Payload with CoreEvent(InvestigationStarted s) -> s | _ -> failwith "not a start"
        let revision = envelope inv 2L (Guid.NewGuid()) "1.0.0" (ObservationRevised { ObservationId = s.ObservationId; Original = s.Original; Source = Some(text "operator") })
        match StorageCodecs.encodeHistory inv [ start; revision ] with
        | Error e -> Assert.Contains("CAUSA.UNREPRESENTABLE", e)
        | Ok _ -> failwith "a 1.0.0 investigation stored a 1.1.0 event"
        Assert.True(Result.isOk (StorageCodecs.encodeHistory inv [ start ]))

    /// Stored histories replay unchanged through the engine: the replayed version and title equal the stored state.
    [<Fact>]
    let StoredHistoriesReplayUnchanged () =
        for (id, files) in fixtures do
            match Wire.verify id files, StorageCodecs.codec.Verify address at files with
            | Ok w, Ok v ->
                Assert.Equal(Json.field "title" (Some w.State) |> Json.text, Some v.Investigation.Title.Value)
                Assert.Equal(AggregateVersion(int64 w.Events.Length), v.Investigation.Version)
            | e1, e2 -> failwithf "%A %A" e1 e2
