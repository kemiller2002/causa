// Owner: ui-3 (T22, reassigned to ui). Composes the whole investigation-workspace screen: the frame, the stored record,
// the details editor and the save state (ui-2's modules), and the section the route names (Overview with its
// conclusions, Hypotheses, Evidence, Causal Analysis, Timeline). View.fs (integration) maps WorkspaceScreen here. It
// sits in the last ui file so every section module compiles before it.
namespace Causa.App.Views

open Causa.Ui.Contracts
open Causa.App.Views.Workspace

/// investigation-workspace (`#/investigations/{id}`).
module InvestigationWorkspace =
    /// Which section is shown and its loading/failed state; a section never shows another's content.
    let sectionBindings (view: WorkspaceView) : Bindings =
        let current = SectionData.slug view.Section
        let remote = SectionData.remote view.Section
        let ready = match remote with Ready _ -> true | _ -> false
        let frameReady = match view.Frame with Ready _ -> true | _ -> false
        Bind.merge
            [ Bind.ofList
                  [ for s in SectionData.slugs do
                        yield Bind.flag ("section." + s) (frameReady && ready && s = current)
                    // The frame's facts and statements belong to the Overview; other sections open on their content.
                    yield Bind.flag "section.show-summary" (not (frameReady && ready) || current = "overview")
                    yield Bind.text "section.heading" (Shell.sectionLabel (match view.Section with
                                                                           | OverviewSection _ -> Overview
                                                                           | CausalAnalysisView _ -> CausalAnalysis
                                                                           | EvidenceView _ -> EvidenceSection
                                                                           | HypothesesView _ -> HypothesesSection
                                                                           | TimelineView _ -> TimelineSection
                                                                           | CorrectiveActionsView _ -> CorrectiveActions)) ]
              ScreenStates.bindings
                  "section"
                  (if frameReady then ScreenStates.ofRemote (fun _ -> false) remote else ScreenReady)
                  (ScreenStates.activity remote)
              OverviewSectionView.bindings view
              ConclusionsView.bindings view
              CausesView.bindings view
              CorrectionsView.bindings view
              HypothesesSectionView.bindings view
              EvidenceSectionView.bindings view
              CausalAnalysisSectionView.bindings view
              TimelineSectionView.bindings view
              ContentEditors.bindings view
              CausalGraph.allBindings view ]

    let bindings (view: WorkspaceView) : Bindings =
        Bind.merge
            [ WorkspaceFrame.bindings view
              StoredRecord.bindings view
              DetailsEditor.bindings view
              SaveIndicator.bindings view.Save
              sectionBindings view ]
