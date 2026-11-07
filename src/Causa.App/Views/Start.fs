// Owner: ui-2 (WI-INV-START, reassigned by the coordinator). investigation-start (`#/investigations/new`, S01): the
// problem statement (INV-001), title (INV-002), primary investigative question (INV-023), the pre-filled investigation
// identifier (INV-006), where it will be written (INV-005), the domain profile when one applies (INV-003), Start
// (INV-004) and Cancel (INV-022), with the catalog's validation (INV-019, INV-020, INV-021, INV-023, INV-006).
// A StartView in, a Bindings map out; the screen decides nothing: Start is offered as the view says, and the engine
// and persistence decide whether the investigation is created.
namespace Causa.App.Views

open System
open System.Text.RegularExpressions
open Causa.Ui.Contracts

module StartScreen =
    /// Field and action ids are the catalog ids, which are also the Limen binding and event names (Ids.fs).
    let problemField = FieldId "INV-001"
    let titleField = FieldId "INV-002"
    let identifierField = FieldId "INV-006"
    let questionField = FieldId "INV-023"
    let startAction = ActionId "INV-004"
    let cancelAction = ActionId "INV-022"

    let identifierPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"

    /// The catalog's fields with their accessible names (the governed journeys look them up by these) and rules.
    let fields : FieldSpec list =
        [ { Field = problemField; Label = "Problem statement"; Component = TextArea; Rules = [ Required; MinLength 1; MaxLength 10000 ] }
          { Field = titleField; Label = "Title"; Component = TextInput; Rules = [ MaxLength 200 ] }
          { Field = questionField; Label = "Primary investigative question"; Component = TextArea; Rules = [ MaxLength 10000 ] }
          { Field = identifierField; Label = "Investigation identifier"; Component = TextInput; Rules = [ Required; MinLength 1; Pattern identifierPattern ] } ]

    let private labelOf (field: FieldId) =
        fields |> List.tryFind (fun f -> f.Field = field) |> Option.map (fun f -> f.Label) |> Option.defaultValue ""

    /// The message for one failed rule: what is wrong and how to fix it (UI-010), naming the field.
    let message (field: FieldId) (rule: Rule) =
        let label = labelOf field
        match rule with
        | Required
        | MinLength _ -> sprintf "Enter the %s; it cannot be empty." (label.ToLowerInvariant())
        | MaxLength n -> sprintf "The %s must be %s characters or fewer." (label.ToLowerInvariant()) (n.ToString("N0", Globalization.CultureInfo.InvariantCulture))
        | Pattern _ -> sprintf "The %s must be a GUID such as 3f2a8c1e-0b4d-4e6f-9a7b-1c2d3e4f5a6b." (label.ToLowerInvariant())
        | OneOf options -> sprintf "Choose one of: %s." (String.Join(", ", options))

    let private broken (value: string) (rule: Rule) =
        match rule with
        | Required -> String.IsNullOrWhiteSpace value
        | MinLength n -> value.Trim().Length < n
        | MaxLength n -> value.Length > n
        | Pattern p -> not (Regex.IsMatch(value, p))
        | OneOf options -> not (List.contains value options)

    let private valueOf (form: StartForm) (field: FieldId) =
        if field = problemField then form.ProblemStatement
        elif field = titleField then form.Title
        elif field = questionField then form.Question
        else form.Identifier

    /// Every rule the form breaks, first rule per field only (Required and MinLength say the same thing). An empty or
    /// whitespace-only problem statement is refused; nothing is created (INV-019, OD-INDY-21).
    let validate (form: StartForm) : ValidationError list =
        [ for spec in fields do
              let value = valueOf form spec.Field
              match spec.Rules |> List.tryFind (broken value) with
              | Some rule -> { Field = spec.Field; Rule = rule; Message = message spec.Field rule }
              | None -> () ]

    let private offered a =
        match a with
        | Available
        | Unavailable _ -> true
        | NotOffered -> false

    let private enabled a =
        match a with
        | Available -> true
        | _ -> false

    let private fieldKey (FieldId f) = f

    /// Where the investigation will be written, in words, before anything is written (INV-005).
    let destination (d: Remote<Destination>) =
        match d with
        | Ready d when d.Writable -> sprintf "%s on branch %s (a new working copy will be created there)" d.Repository d.Branch
        | Ready d -> sprintf "%s on branch %s (read-only: this connection cannot save)" d.Repository d.Branch
        | Waiting activity -> activity
        | NotRequested -> "Not connected to a data repository"
        | Failed f -> (Copy.failure f).What

    let bindings (view: StartView) : Bindings =
        let errorOf field =
            view.Errors |> List.tryFind (fun e -> e.Field = field) |> Option.map (fun e -> e.Message)
        let fieldBindings (key: string) (field: FieldId) (value: string) =
            let error = errorOf field
            [ Bind.text ("start." + key) value
              Bind.flag ("start." + key + "-invalid") error.IsSome
              Bind.text ("start." + key + "-aria-invalid") (if error.IsSome then "true" else "false")
              Bind.text ("start." + key + "-error") (error |> Option.defaultValue "") ]
        let refusals =
            match view.Submission with
            | Submission.Refused reasons -> reasons
            | _ -> []
        Bind.ofList
            [ yield! fieldBindings "problem" problemField view.Form.ProblemStatement
              yield! fieldBindings "title" titleField view.Form.Title
              yield! fieldBindings "question" questionField view.Form.Question
              yield! fieldBindings "identifier" identifierField view.Form.Identifier
              yield Bind.text "start.problem-count" (sprintf "%d of 10,000 characters" view.Form.ProblemStatement.Length)
              yield Bind.text "start.title-count" (sprintf "%d of 200 characters" view.Form.Title.Length)
              yield Bind.text "start.question-count" (sprintf "%d of 10,000 characters" view.Form.Question.Length)
              yield Bind.flag "start.errors-present" (not view.Errors.IsEmpty)
              yield
                  Bind.items
                      "start.errors"
                      [ for e in view.Errors -> [ Bind.cellText "field" (fieldKey e.Field); Bind.cellText "message" e.Message ] ]
              yield Bind.text "start.destination" (destination view.Destination)
              yield Bind.flag "start.destination-writable" (match view.Destination with Ready d -> d.Writable | _ -> false)
              yield Bind.flag "start.profile-present" view.Profile.IsSome
              yield Bind.text "start.profile" (view.Profile |> Option.defaultValue "")
              yield Bind.flag "start.start-offered" (offered view.Start)
              yield Bind.flag "start.start-disabled" (not (enabled view.Start) || view.Submission = Submission.Submitting)
              yield Bind.text "start.start-unavailable" (match view.Start with Unavailable reason -> reason | _ -> "")
              yield Bind.flag "start.start-explained" (match view.Start with Unavailable _ -> true | _ -> false)
              yield Bind.flag "start.cancel-offered" (offered view.Cancel)
              yield Bind.flag "start.cancel-disabled" (not (enabled view.Cancel))
              yield Bind.flag "start.submitting" (view.Submission = Submission.Submitting)
              yield Bind.flag "start.refused" (not refusals.IsEmpty)
              yield Bind.items "start.refusals" [ for r in refusals -> [ Bind.cellText "reason" r ] ]
              yield Bind.flag "start.accepted" (view.Submission = Submission.Accepted) ]
