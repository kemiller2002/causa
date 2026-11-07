// Owner: ui. investigation-list (`#/`): the connection region before Connect, then the Investigations region (INV-039)
// with each card's copy kind in words (UI-014-O3) and counts that are never a fabricated zero (UI-022).
namespace Causa.App.Views

open Causa.Ui.Contracts

module InvestigationListScreen =
    let private shownText (s: Shown<string> option) =
        match s with
        | Some(AsStored v) -> Visible.value v
        | Some(AsProjected u) -> Understood.value u
        | None -> "Not known"

    let private counts (s: Shown<SummaryCounts> option) =
        match s with
        | Some(AsStored v) -> Some(Visible.value v)
        | Some(AsProjected u) -> Some(Understood.value u)
        | None -> None

    let private copyKey kind =
        match kind with
        | FrozenDemonstration -> "frozen"
        | WorkingCopy -> "working"
        | CopyKindUnknown -> "unknown"

    let sorted (by: ListSort) (items: InvestigationSummary list) =
        match by with
        | ByTitle -> items |> List.sortBy (fun i -> i.Title.ToLowerInvariant())
        | ByUpdated -> items |> List.sortByDescending (fun i -> i.LastActivity |> Option.defaultValue System.DateTimeOffset.MinValue)

    let rows (by: ListSort) (items: InvestigationSummary list) =
        [ for i in sorted by items ->
              let c = counts i.Counts
              let count f = c |> Option.map (f >> Copy.count) |> Option.defaultValue "Not known"
              [ Bind.cellText "id" i.Href
                Bind.cellText "href" i.Href
                Bind.cellText "title" i.Title
                Bind.cellText "copy" (Copy.copyKind i.Copy)
                Bind.cellText "copyTag" (Copy.copyTag i.Copy)
                Bind.cellText "copyKind" (copyKey i.Copy)
                Bind.cellFlag "frozen" (i.Copy = FrozenDemonstration)
                Bind.cellText "status" (shownText i.Status)
                Bind.cellText "lastActivity" (Copy.dateOption i.LastActivity)
                Bind.cellText "hypotheses" (count (fun x -> x.Hypotheses))
                Bind.cellText "evidence" (count (fun x -> x.Evidence))
                Bind.cellText "contradictions" (count (fun x -> x.UnresolvedContradictions)) ] ]

    let bindings (view: InvestigationListView) : Bindings =
        let items =
            match view.Investigations with
            | Ready v -> Visible.value v
            | _ -> []
        let listed = match view.Investigations with Ready _ -> true | _ -> false
        Bind.merge
            [ ConnectionScreen.bindings view.Connection
              ScreenStates.bindings "list" (ScreenStates.ofRemote (fun v -> List.isEmpty (Visible.value v)) view.Investigations) (ScreenStates.activity view.Investigations)
              Bind.ofList
                  [ Bind.flag "list.visible" (listed && not (List.isEmpty items))
                    Bind.items "investigations" (rows view.Sort items)
                    Bind.text "list.sort" (match view.Sort with ByUpdated -> "updated" | ByTitle -> "title")
                    Bind.flag "list.new-offered" (match view.NewInvestigation with NotOffered -> false | _ -> true)
                    Bind.text
                        "list.new-unavailable"
                        (match view.NewInvestigation with Unavailable r -> r | _ -> "") ] ]
