// Owner: integration (written by the T18 completion lane under the coordinator's ownership exception).
// PF-016 visual verification: the five Causa identity colours with labels and contrast, and deployed exact-SHA
// screenshots at the required sizes.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Persistence
open Causa.TestSupport

type PF016Tests(fixture: LiveFixture<PF016Tests>) =
    /// UI-016's canonical identity colours.
    let palette = [ "navy", "#14283F"; "teal", "#087F8C"; "amber", "#E9A23B"; "coral", "#D66B58"; "ivory", "#F5F3ED" ]
    let hex name = palette |> List.find (fst >> (=) name) |> snd

    /// WCAG 2.1 relative luminance and contrast ratio.
    let luminance (h: string) =
        let channel (i: int) =
            let c = float (Convert.ToInt32(h.Substring(i, 2), 16)) / 255.0
            if c <= 0.03928 then c / 12.92 else Math.Pow((c + 0.055) / 1.055, 2.4)
        0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5
    let contrast a b =
        let la, lb = luminance a, luminance b
        (max la lb + 0.05) / (min la lb + 0.05)

    let env name = match Environment.GetEnvironmentVariable name with null | "" -> None | v -> Some v
    let blocked reason : 'a = raise (InvalidOperationException("BLOCKED: " + reason))

    interface IClassFixture<LiveFixture<PF016Tests>>

    /// PF-016-O1: the scenarios exercise navy, teal, amber, coral and ivory with textual state labels and contrast
    /// checks: the label colours meet WCAG AA on the ivory surface (navy text 4.5:1; teal and coral indicators 3:1;
    /// amber only with a text label, DEC-R7-002); the populated case read from real GitHub holds the states those
    /// colours stand for (contradictions, unresolved unknowns, active hypotheses); and the application stylesheet maps
    /// all five to named Forma tokens.
    [<Fact>]
    member _.``PF-016-O1``() =
        Assert.True(contrast (hex "navy") (hex "ivory") >= 4.5, "navy text on ivory is below 4.5:1")
        Assert.True(contrast (hex "ivory") (hex "navy") >= 4.5, "ivory text on navy is below 4.5:1")
        Assert.True(contrast (hex "teal") (hex "ivory") >= 3.0, "the teal indicator is below 3:1 on ivory")
        Assert.True(contrast (hex "coral") (hex "ivory") >= 3.0, "the coral indicator is below 3:1 on ivory")
        let live = fixture.Run
        let head = PinnedCatalog.ok "read head" (live.Reader.ReadHead())
        let case = PinnedCatalog.cases |> List.find (fun c -> c.Key = "challenger-sts-51l")
        let events = PinnedCatalog.ok "read case" (PinnedCatalog.readFiles live.Reader head case) |> PinnedCatalog.events
        let has kind = events |> List.exists (fun e -> e |> Json.field "eventType" |> Json.text = Some kind)
        for kind, colour in [ "ContradictionRecorded", "coral"; "UnknownIdentified", "amber"; "HypothesisRecorded", "teal" ] do
            Assert.True(has kind, sprintf "the populated case has no %s state for %s" kind colour)
        let stylesheet = "web/causa.css"
        if not (Repo.exists stylesheet) then blocked (sprintf "the application stylesheet %s does not exist yet (ui role), so the five tokens cannot be checked" stylesheet)
        let css = (Repo.read stylesheet).ToLowerInvariant()
        for (name, value) in palette do
            Assert.True(css.Contains(value.ToLowerInvariant()) || css.Contains name, sprintf "%s does not map %s %s to a Forma token" stylesheet name value)

    /// PF-016-O2: final acceptance captures deployed exact-SHA screenshots at desktop, iPad portrait and landscape and
    /// mobile sizes, in Chromium and WebKit; local fixture screenshots do not count.
    [<Fact>]
    member _.``PF-016-O2``() =
        let live = fixture.Run
        Assert.Equal("ok", Conformance.kind (live.Reader.ReadHead()))
        let candidate = env "CAUSA_CANDIDATE" |> Option.defaultWith (fun () -> blocked "no deployed candidate: CAUSA_CANDIDATE is not set")
        let sha = env "GITHUB_SHA" |> Option.defaultWith (fun () -> blocked "not a CI run: GITHUB_SHA is not set")
        Assert.Equal(sha, candidate)
        let dir = env "CAUSA_SCREENSHOTS" |> Option.defaultWith (fun () -> blocked "no deployed screenshots: CAUSA_SCREENSHOTS is not set (the governed design-contract job writes them)")
        for engine in [ "chromium"; "webkit" ] do
            let shots = IO.Path.Combine(dir, engine)
            Assert.True(IO.Directory.Exists shots, sprintf "no %s screenshots in %s" engine dir)
            let names = IO.Directory.GetFiles(shots, "*.png") |> Array.map (IO.Path.GetFileName >> string)
            for viewport in [ "desktop"; "ipad-portrait"; "ipad-landscape"; "mobile" ] do
                Assert.True(names |> Array.exists (fun n -> n.StartsWith(viewport + "-")), sprintf "no %s %s screenshot of %s" engine viewport candidate)
