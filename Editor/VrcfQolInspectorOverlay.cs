// VrcfQolInspectorOverlay.cs
// Best-effort: watches the Unity Inspector and appends visible action buttons
// next to recognisable VRCFury elements.
//
// Currently:
//   • Each "Page #N" label inside a Flipbook Builder gets a "Duplicate → End"
//     button right next to it.
//
// This is intentionally defensive: if VRCFury restructures its inspector, the
// overlay silently finds nothing and does nothing — the right-click context
// menu still works as the authoritative entry point.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UmeVrcfQol {

    [InitializeOnLoad]
    internal static class VrcfQolInspectorOverlay {
        // UI classes used to mark nodes we've already decorated, so we don't
        // inject twice on the same element.
        private const string InjectedClass = "vrcfqol-injected";
        private const string ButtonBarClass = "vrcfqol-buttons";

        // Matches "Page #<number>" exactly as VRCFury writes it.
        private static readonly Regex PageLabelRegex = new Regex(@"^Page #(\d+)$", RegexOptions.Compiled);

        // Throttle the scan — every ~250ms is plenty responsive and barely measurable.
        private const double ScanIntervalSeconds = 0.25;
        private static double _nextScan;

        static VrcfQolInspectorOverlay() {
            EditorApplication.update += Tick;
        }

        private static void Tick() {
            if (EditorApplication.timeSinceStartup < _nextScan) return;
            _nextScan = EditorApplication.timeSinceStartup + ScanIntervalSeconds;
            try { Scan(); } catch { /* defensive — never let overlay break the inspector */ }
        }

        private static void Scan() {
            if (!VrcfQol.Reflection.TryEnsure(out _)) return;

            // All InspectorWindow instances currently open.
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in allWindows) {
                if (w == null) continue;
                if (w.GetType().Name != "InspectorWindow") continue;
                var root = w.rootVisualElement;
                if (root == null) continue;
                ScanRoot(root);
            }
        }

        private static void ScanRoot(VisualElement root) {
            // Find every Label and check its text. Cheap — Unity's UQuery is fast.
            var labels = root.Query<Label>().ToList();
            foreach (var label in labels) {
                if (label.ClassListContains(InjectedClass)) continue;
                var match = PageLabelRegex.Match(label.text ?? "");
                if (!match.Success) continue;
                TryInjectPageButtons(label);
            }
        }

        private static void TryInjectPageButtons(Label pageLabel) {
            // Mark the label so we don't try again this lifecycle.
            pageLabel.AddToClassList(InjectedClass);

            // The label is drawn as a sibling of the page's content. We want a row
            // that reads  "Page #N           [Duplicate → End]".
            // Convert the label's container to a horizontal row and append a button.
            var parent = pageLabel.parent;
            if (parent == null) return;

            // Avoid re-decorating a parent we've already touched.
            if (parent.ClassListContains(ButtonBarClass)) return;
            parent.AddToClassList(ButtonBarClass);

            // Build the button row: we replace the bare label with a row containing
            // [label, spacer, button] so the button floats to the right of the label.
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            // Detach the label from its current spot so we can re-home it into `row`.
            int labelIndex = parent.IndexOf(pageLabel);
            parent.RemoveAt(labelIndex);
            row.Add(pageLabel);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            var btn = new Button(() => OnDuplicateClicked(pageLabel)) { text = "Duplicate → End" };
            btn.style.marginLeft = 4;
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.tooltip = "VRCFury QoL: clone this page and append the copy at the end of the flipbook.";
            row.Add(btn);

            parent.Insert(labelIndex, row);
        }

        /// <summary>
        /// When the button is clicked, walk up the UIElements tree to find the
        /// enclosing Inspector's target VRCFury component, then invoke the same
        /// duplication logic the context menu uses.
        /// </summary>
        private static void OnDuplicateClicked(Label pageLabel) {
            // Page index from "Page #N".
            var match = PageLabelRegex.Match(pageLabel.text ?? "");
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out var oneBasedIndex)) return;
            var sourceIndex = oneBasedIndex - 1;

            // Find the nearest SerializedObject binding by walking up to the inspector.
            // Easier: use the currently-active Selection, which is what the inspector
            // is showing. That's what the context-menu version does too.
            var selection = Selection.activeGameObject;
            if (selection == null) {
                EditorUtility.DisplayDialog("Duplicate → End",
                    "Could not determine which GameObject is inspected. Select it in the Hierarchy and try again.", "OK");
                return;
            }

            // Find every VRCFury component on the selection that holds a Toggle with
            // a FlipBookBuilderAction. Pick the one whose page count makes the index valid.
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                EditorUtility.DisplayDialog("Duplicate → End", err, "OK"); return;
            }
            var r = VrcfQol.Reflection;

            foreach (Component c in selection.GetComponents(r.VRCFuryType)) {
                if (c == null) continue;
                var content = r.ContentField.GetValue(c);
                if (content == null || content.GetType() != r.ToggleType) continue;
                var state = r.ToggleStateField.GetValue(content);
                var actions = r.StateActionsField.GetValue(state) as System.Collections.IList;
                var flipbookAction = VrcfQol.FindFlipbookAction(actions);
                if (flipbookAction == null) continue;
                var pages = r.PagesField.GetValue(flipbookAction) as System.Collections.IList;
                if (pages == null || sourceIndex < 0 || sourceIndex >= pages.Count) continue;

                try {
                    Undo.RegisterCompleteObjectUndo(c, $"Duplicate flipbook page #{oneBasedIndex}");
                    var clone = UmeVrcfQol.Tools.DuplicateFlipbookPageTool.DeepClonePage(pages[sourceIndex]);
                    pages.Add(clone);
                    EditorUtility.SetDirty(c);
                    Debug.Log($"[VRCF QoL] Duplicated flipbook page #{oneBasedIndex} as page #{pages.Count}.");
                } catch (System.Exception ex) {
                    Debug.LogException(ex);
                    EditorUtility.DisplayDialog("Duplicate → End",
                        "Duplication failed. See Console.\n\n" + ex.Message, "OK");
                }
                return;
            }

            EditorUtility.DisplayDialog("Duplicate → End",
                "Could not find a flipbook toggle on the selected GameObject that contains this page. " +
                "If the flipbook is on a different object than the current selection, use right-click → VRCF QoL → Duplicate page to end instead.",
                "OK");
        }
    }
}
