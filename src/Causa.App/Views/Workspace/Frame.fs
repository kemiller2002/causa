// Owner: ui-2 (WI-INV-WORKSPACE, reassigned by the coordinator). The investigation workspace frame every section shares
// (INV-007 creation time, INV-008 original problem statement, INV-018 investigation state, INV-023 question, INV-042
// sections, INV-006 identifier kept): a WorkspaceView in, bindings out. Values are shown with their boundary (as
// stored or as the kernel projected them) and never invented: a value that is not there is not rendered.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module WorkspaceFrame =
    /// The text of a shown value, whichever boundary it came through.
    let shown (s: Shown<'a>) : 'a =
        match s with
        | AsStored v -> Visible.value v
        | AsProjected u -> Understood.value u

    let private copyKey kind =
        match kind with
        | FrozenDemonstration -> "frozen"
        | WorkingCopy -> "working"
        | CopyKindUnknown -> "unknown"

    /// The sections the view lists, in its order; a section the backend does not list is not linked (no dead links).
    let sectionRows (links: NavLink<Section> list) =
        [ for l in links ->
              [ Bind.cellText "key" (Shell.sectionSlug l.Destination)
                Bind.cellText "label" l.Label
                Bind.cellText "href" l.Href
                Bind.cellText "current" (Bind.current l.Current) ] ]

    let bindings (view: WorkspaceView) : Bindings =
        let frame =
            match view.Frame with
            | Ready f -> Some f
            | _ -> None
        let text f = frame |> Option.map f |> Option.defaultValue ""
        let question = frame |> Option.bind (fun f -> f.Question) |> Option.map shown |> Option.filter (fun q -> q.Trim() <> "")
        Bind.merge
            [ ScreenStates.bindings "workspace" (ScreenStates.ofRemote (fun _ -> false) view.Frame) (ScreenStates.activity view.Frame)
              Bind.ofList
                  [ Bind.flag "workspace.frame-present" frame.IsSome
                    // Without a frame (loading, failed) the screen still has its heading, from the shell's title.
                    Bind.flag "workspace.frame-absent" frame.IsNone
                    Bind.text "workspace.title" (text (fun f -> if f.Title.Trim() = "" then "Untitled investigation" else f.Title))
                    Bind.text "workspace.identifier" (text (fun f -> f.Identifier))
                    Bind.text "workspace.problem" (text (fun f -> shown f.ProblemStatement))
                    Bind.flag "workspace.question-present" question.IsSome
                    Bind.text "workspace.question" (question |> Option.defaultValue "")
                    Bind.text "workspace.created" (text (fun f -> Copy.date (shown f.CreatedAt)))
                    Bind.text "workspace.state" (text (fun f -> shown f.InvestigationState))
                    Bind.text "workspace.copy" (text (fun f -> Copy.copyKind f.Copy))
                    Bind.text "workspace.copy-tag" (text (fun f -> Copy.copyTag f.Copy))
                    Bind.text "workspace.copy-kind" (text (fun f -> copyKey f.Copy))
                    Bind.flag "workspace.sections-present" (frame |> Option.exists (fun f -> not f.Sections.IsEmpty))
                    Bind.items "workspace.sections" (frame |> Option.map (fun f -> sectionRows f.Sections) |> Option.defaultValue []) ] ]
