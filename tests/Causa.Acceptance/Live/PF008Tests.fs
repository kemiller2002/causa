// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-008 provider-failure fixtures: every provider failure is modelled, classified and observed live, and kept apart
// from domain validation.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF008Tests(fixture: LiveFixture<PF008Tests>) =
    let prefix = RepoPath "workspaces/demo/causa-app-pf008/investigations/00000000-0000-0000-0000-000000000008"
    let file name = let (RepoPath p) = prefix in RepoPath(p + "/" + name)
    let classOf form =
        let r = Recordings.form form
        fst (Classify.classifyWithClass r.Operation (Recordings.outcome (RequestId 1L) r))

    let liveProbes () =
        let live = fixture.Run
        Conformance.run (Conformance.overLive live) live.Created

    interface IClassFixture<LiveFixture<PF008Tests>>

    /// PF-008-O1: authorization failure, forbidden access, not found, missing branch (404, GraphQL NOT_FOUND, and HTTP 422, which the error matrix classes as
    /// GH-VALIDATION), rate limit,
    /// timeout, offline, malformed response, retry and partial transport failure are each modelled: by a recorded form
    /// in its matrix class, or by a fault of the stateful fake with its typed cause; a lost request is retried to
    /// success; and the failures real GitHub can be asked for are observed live with the same cause.
    [<Fact>]
    member _.``PF-008-O1``() =
        for form, expected in
            [ "rest-401-bad-credentials", GhAuth; "rest-403-permission", GhPermission; "rest-404-file", GhNotFound
              "rest-404-branch", GhNotFound; "rest-422-missing-branch", GhValidation; "graphql-not-found-branch", GhNotFound
              "rest-403-primary-rate-limit", GhRatePrimary; "rest-429-primary-rate-limit", GhRatePrimary
              "rest-403-secondary-rate-limit", GhRateSecondary; "graphql-malformed-200", GhMalformed; "rest-ref-malformed", GhMalformed ] do
            Assert.True((classOf form = expected), sprintf "%s classified as %s, expected %s" form (MatrixClass.id (classOf form)) (MatrixClass.id expected))
        let fake = StatefulFake()
        let w = Provider.over fake.Execute fake.WriteConnection
        let put () = [ PutFile(file "a.json", [| 1uy |]) ]
        for fault, expected in
            [ TimeOutAfterApplying, "NetworkUnavailable.TimedOut"; FailBeforeSending, "NetworkUnavailable.FailedBeforeSending"
              LoseAfterApplying, "NetworkUnavailable.LostAfterSending"; LoseBeforeApplying, "NetworkUnavailable.LostAfterSending"
              AnswerWith "graphql-502", "Unrecognised.502" ] do
            fake.Inject fault
            Assert.Equal(expected, Conformance.kind (w.Commit (fake.Head()) prefix "fault" (put ())))
        fake.Inject LoseBeforeApplying
        Assert.Equal("NetworkUnavailable.LostAfterSending", Conformance.kind (w.Commit (fake.Head()) prefix "lost" (put ())))
        Assert.Equal("ok", Conformance.kind (w.Commit (fake.Head()) prefix "retried" (put ())))
        let live = liveProbes ()
        for name in [ "read head with a bad credential"; "commit with the read credential"; "read a missing branch"; "read a missing file at a commit" ] do
            let p = live |> List.find (fun p -> p.Name = name)
            Assert.True((p.Expected = p.Actual), sprintf "%s: expected %s, real GitHub answered %s" name p.Expected p.Actual)
        let impatient = Provider.over (LiveHttp.executor (TimeSpan.FromMilliseconds 1.0) fixture.Run.Log) fixture.Run.ReadConnection
        Assert.StartsWith("NetworkUnavailable.", Conformance.kind (impatient.ReadHead()))

    /// PF-008-O2: provider errors are distinct from domain validation errors: a provider refusal reaches the
    /// application as ProviderFailed with its typed cause (here a missing investigation and a refused credential, on the
    /// fake and on real GitHub), while a stored file set the storage rules refuse is Unverifiable, never a provider
    /// error.
    [<Fact>]
    member _.``PF-008-O2``() =
        AppLoad.requireCodec ()
        let address = (WorkspaceAddress.workingCopy [] "pf008").Value
        let id = Causa.Domain.InvestigationId(Guid "00000000-0000-4000-8000-000000000008")
        let target = { Address = address; Id = id }
        let fake = StatefulFake()
        match AppLoad.load fake.Execute fake.ReadConnection target with
        | LoadFailed(ProviderFailed(NotFound _)) -> ()
        | other -> failwithf "a missing investigation on the fake loaded as %A" other
        match AppLoad.load fake.Execute (fake.Connection "not-a-token") target with
        | LoadFailed(ProviderFailed AuthenticationFailed) -> ()
        | other -> failwithf "a refused credential on the fake loaded as %A" other
        let (RepoPath p) = Writes.prefixOf address id
        fake.CommitAsOtherWriter("invalid", [ for n in Writes.fileNames -> p + "/" + n, Some(Encoding.UTF8.GetBytes "not json\n") ]) |> ignore
        match AppLoad.load fake.Execute fake.ReadConnection target with
        | LoadFailed(Unverifiable(_ :: _)) -> ()
        | other -> failwithf "an invalid file set loaded as %A" other
        let live = fixture.Run
        match AppLoad.load live.Execute live.ReadConnection target with
        | LoadFailed(ProviderFailed(NotFound _)) -> ()
        | other -> failwithf "a missing investigation on real GitHub loaded as %A" other
        match AppLoad.load live.Execute { live.ReadConnection with Credential = Credential.create "causa-pf008-not-a-token" } target with
        | LoadFailed(ProviderFailed AuthenticationFailed) -> ()
        | other -> failwithf "a refused credential on real GitHub loaded as %A" other
