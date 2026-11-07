// Owner: ui-2 (WI-INV-START, reassigned). investigation-start: the catalog's validation (INV-019, INV-020, INV-021,
// INV-023, INV-006), the bindings the fragment reads, and the fragment's accessible names and requirement ids (the names
// the governed journey J1 uses). Each refusal is paired with the value one step inside the limit (AR-009).
namespace Causa.App.Tests.Ui

open System
open System.IO
open Xunit
open Causa.Ui.Contracts
open Causa.App.Views

module StartTests =
    let private id = "3f2a8c1e-0b4d-4e6f-9a7b-1c2d3e4f5a6b"
    let private form = { ProblemStatement = "Checkout fails since 09:00."; Title = "Checkout"; Question = "Why?"; Identifier = id }

    let private view =
        { Form = form
          Errors = []
          Destination = Ready { Repository = "acme/causa-data"; Branch = "main"; Writable = true }
          Profile = None
          Start = Available
          Cancel = Available
          Submission = Submission.NotSubmitted }

    let private fieldsIn (errors: ValidationError list) = errors |> List.map (fun e -> let (FieldId f) = e.Field in f)

    let private text (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Text s) -> s
        | other -> failwithf "%s is %A" key other

    let private flag (b: Bindings) key =
        match Map.tryFind key b with
        | Some(BindingValue.Flag f) -> f
        | other -> failwithf "%s is %A" key other

    [<Fact>]
    let ``a complete form passes validation`` () = Assert.Empty(StartScreen.validate form)

    [<Fact>]
    let ``INV-019 an empty or whitespace-only problem statement is refused`` () =
        Assert.Equal<string list>([ "INV-001" ], fieldsIn (StartScreen.validate { form with ProblemStatement = "" }))
        Assert.Equal<string list>([ "INV-001" ], fieldsIn (StartScreen.validate { form with ProblemStatement = " \n\t " }))
        Assert.Empty(StartScreen.validate { form with ProblemStatement = "x" })

    [<Fact>]
    let ``INV-021 the problem statement is at most 10,000 characters`` () =
        Assert.Empty(StartScreen.validate { form with ProblemStatement = String('p', 10000) })
        Assert.Equal<string list>([ "INV-001" ], fieldsIn (StartScreen.validate { form with ProblemStatement = String('p', 10001) }))

    [<Fact>]
    let ``INV-020 the title is optional and at most 200 characters`` () =
        Assert.Empty(StartScreen.validate { form with Title = "" })
        Assert.Empty(StartScreen.validate { form with Title = String('t', 200) })
        Assert.Equal<string list>([ "INV-002" ], fieldsIn (StartScreen.validate { form with Title = String('t', 201) }))

    [<Fact>]
    let ``INV-023 the question is optional and at most 10,000 characters`` () =
        Assert.Empty(StartScreen.validate { form with Question = "" })
        Assert.Empty(StartScreen.validate { form with Question = String('q', 10000) })
        Assert.Equal<string list>([ "INV-023" ], fieldsIn (StartScreen.validate { form with Question = String('q', 10001) }))

    [<Fact>]
    let ``INV-006 the identifier must be a GUID`` () =
        Assert.Equal<string list>([ "INV-006" ], fieldsIn (StartScreen.validate { form with Identifier = "" }))
        Assert.Equal<string list>([ "INV-006" ], fieldsIn (StartScreen.validate { form with Identifier = "not-a-guid" }))
        Assert.Empty(StartScreen.validate { form with Identifier = id.ToUpperInvariant() })

    [<Fact>]
    let ``every message names the field and says how to fix it`` () =
        let errors = StartScreen.validate { form with ProblemStatement = ""; Title = String('t', 201) }
        Assert.Contains(errors, fun e -> e.Message.Contains "problem statement")
        Assert.Contains(errors, fun e -> e.Message.Contains "200")

    [<Fact>]
    let ``bindings carry the values, the destination and the actions`` () =
        let b = StartScreen.bindings view
        Assert.Equal(form.ProblemStatement, text b "start.problem")
        Assert.Equal(id, text b "start.identifier")
        Assert.Contains("acme/causa-data on branch main", text b "start.destination")
        Assert.True(flag b "start.start-offered")
        Assert.False(flag b "start.start-disabled")
        Assert.True(flag b "start.cancel-offered")
        Assert.False(flag b "start.errors-present")
        Assert.Equal("false", text b "start.problem-aria-invalid")

    [<Fact>]
    let ``errors mark their field and are listed`` () =
        let errors = StartScreen.validate { form with ProblemStatement = "" }
        let b = StartScreen.bindings { view with Errors = errors }
        Assert.True(flag b "start.errors-present")
        Assert.True(flag b "start.problem-invalid")
        Assert.Equal("true", text b "start.problem-aria-invalid")
        Assert.NotEqual<string>("", text b "start.problem-error")
        Assert.False(flag b "start.title-invalid")

    [<Fact>]
    let ``Start is disabled while submitting and with the engine's reason when unavailable`` () =
        Assert.True(flag (StartScreen.bindings { view with Submission = Submission.Submitting }) "start.start-disabled")
        let unavailable = StartScreen.bindings { view with Start = Unavailable "This connection cannot save." }
        Assert.True(flag unavailable "start.start-disabled")
        Assert.Equal("This connection cannot save.", text unavailable "start.start-unavailable")
        Assert.False(flag (StartScreen.bindings { view with Start = NotOffered }) "start.start-offered")
        let refused = StartScreen.bindings { view with Submission = Submission.Refused [ "GitHub refused the write." ] }
        Assert.True(flag refused "start.refused")

    let private fragment () =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "repository root not found"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent
        File.ReadAllText(Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "web", "screens", "investigation-start.html"))

    [<Fact>]
    let ``the fragment labels each field by its catalog name and maps every requirement`` () =
        let html = fragment ()
        for spec in StartScreen.fields do
            Assert.Contains(sprintf ">%s</label>" spec.Label, html)
            let (FieldId f) = spec.Field
            Assert.Contains(sprintf "data-requirement-id=\"%s\"" f, html)
            Assert.Contains(sprintf "data-event=\"%s\"" f, html)
        for id in [ "INV-004"; "INV-005"; "INV-019"; "INV-020"; "INV-022" ] do
            Assert.Contains(sprintf "data-requirement-id=\"%s\"" id, html)
        Assert.Contains(">Start investigation</button>", html)
        Assert.Contains("data-if=\"screen.investigation-start\"", html)
        // Every binding the fragment reads is one the screen module writes (no silent empty element).
        let written = StartScreen.bindings view |> Map.toList |> List.map fst |> Set.ofList
        let read =
            Text.RegularExpressions.Regex.Matches(html, "data-(?:text|if|each|bind-[a-z-]+)=\"(start\\.[a-z-]+)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value)
            |> Set.ofSeq
        Assert.Empty(Set.difference read written)
