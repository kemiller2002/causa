// Owner: ui-3 (T22, reassigned to ui). The Timeline section (UIR-TIMELINE, UI-025): every recorded change, oldest
// first, with when and by whom. Read-only.
namespace Causa.App.Views.Workspace

open Causa.Ui.Contracts
open Causa.App.Views

module TimelineSectionView =
    /// A change's one-line summary; the record itself is in its section, so a long text is shortened here.
    let brief (s: string) =
        let oneLine = s.Replace("\r", " ").Replace("\n", " ")
        if oneLine.Length <= 240 then oneLine else oneLine.Substring(0, 237).TrimEnd() + "…"

    let bindings (view: WorkspaceView) : Bindings =
        let ts = SectionData.analysis view.Section |> Option.map (fun a -> a.Timeline) |> Option.defaultValue []
        Bind.ofList
            [ Bind.flag "timeline.present" (not ts.IsEmpty)
              Bind.text "timeline.count" (string ts.Length)
              Bind.items
                  "timeline.items"
                  [ for t in ts ->
                        [ Bind.cellText "key" (string t.Version)
                          Bind.cellText "summary" (brief t.Summary)
                          Bind.cellText "when" (Copy.date t.OccurredAt)
                          Bind.cellText "datetime" (t.OccurredAt.ToUniversalTime().ToString("o"))
                          Bind.cellText "actor" (t.Actor |> Option.defaultValue "Not recorded")
                          Bind.cellText "version" (string t.Version) ] ] ]
