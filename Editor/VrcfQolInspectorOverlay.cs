// VrcfQolInspectorOverlay.cs
// Best-effort: watches the Unity Inspector and enriches recognisable VRCFury
// elements with inline UI.
//
// Currently:
//   • Each "Page #N" label inside a Flipbook Builder gets a row of buttons
//     sourced from the VrcfQol.InlinePageButtons registry. Tools register the
//     button; the overlay handles placement.
//   • Each Toggle inspector (the VRCFury component with a Toggle content) gets a
//     status banner pinned to the top of the window explaining that the Global
//     Parameter is being auto-managed (and a button to disable that per-toggle).
//
// This is intentionally defensive: if VRCFury restructures its inspector, the
// overlay silently finds nothing and does nothing — the right-click context
// menu still works as the authoritative entry point.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UmeVrcfQol.Tools;

namespace UmeVrcfQol {

    [InitializeOnLoad]
    internal static class VrcfQolInspectorOverlay {
        // UI classes used to mark nodes we've already decorated, so we don't
        // inject twice on the same element.
        private const string InjectedClass = "vrcfqol-injected";
        private const string ButtonBarClass = "vrcfqol-buttons";
        private const string ToggleBannerClass = "vrcfqol-toggle-banner";

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
            // 1. Toggle-level auto-update banner.
            EnsureToggleBanner(root);

            // 2. Per-page inline buttons.
            var labels = root.Query<Label>().ToList();
            foreach (var label in labels) {
                if (label.ClassListContains(InjectedClass)) continue;
                var match = PageLabelRegex.Match(label.text ?? "");
                if (!match.Success) continue;
                TryInjectPageButtons(label);
            }
        }

        // ----------------------------------------------------------------------
        // Toggle-level auto-update banner
        // ----------------------------------------------------------------------

        private static void EnsureToggleBanner(VisualElement root) {
            var existing = root.Q<VisualElement>(className: ToggleBannerClass);

            // Determine what banner SHOULD be shown based on current selection.
            Component target = null;
            var selected = Selection.activeGameObject;
            if (selected != null && VrcfQol.Reflection.TryEnsure(out _)) {
                var r = VrcfQol.Reflection;
                foreach (Component c in selected.GetComponents(r.VRCFuryType)) {
                    if (c == null) continue;
                    var content = r.ContentField.GetValue(c);
                    if (content == null || content.GetType() != r.ToggleType) continue;
                    target = c;
                    break;
                }
            }

            if (target == null) {
                if (existing != null) existing.RemoveFromHierarchy();
                return;
            }

            bool optedOut = AutoGlobalParameterTool.IsOptedOut(target);
            bool supported = VrcfQol.Reflection.ToggleUseGlobalParamField != null
                          && VrcfQol.Reflection.ToggleGlobalParamField != null;

            // Rebuild (cheap — one line, two buttons) so state stays in sync.
            if (existing == null) {
                existing = new VisualElement();
                existing.AddToClassList(ToggleBannerClass);
                existing.style.flexDirection = FlexDirection.Row;
                existing.style.alignItems = Align.Center;
                existing.style.paddingLeft = 8;
                existing.style.paddingRight = 6;
                existing.style.paddingTop = 4;
                existing.style.paddingBottom = 4;
                existing.style.marginTop = 2;
                existing.style.marginBottom = 2;
                existing.style.borderTopWidth = 1;
                existing.style.borderBottomWidth = 1;
                existing.style.borderTopColor = new StyleColor(new Color(0, 0, 0, 0.3f));
                existing.style.borderBottomColor = new StyleColor(new Color(0, 0, 0, 0.3f));
                root.Insert(0, existing);
            }

            existing.Clear();

            if (!supported) {
                existing.style.backgroundColor = new StyleColor(new Color(0.35f, 0.35f, 0.35f, 0.8f));
                var msg = new Label("VRCF QoL: this VRCFury version doesn't expose Global Parameter fields — auto-update is disabled.");
                msg.style.color = new StyleColor(Color.white);
                msg.style.flexGrow = 1;
                msg.style.whiteSpace = WhiteSpace.Normal;
                existing.Add(msg);
                return;
            }

            existing.style.backgroundColor = optedOut
                ? new StyleColor(new Color(0.45f, 0.25f, 0.20f, 0.85f))
                : new StyleColor(new Color(0.18f, 0.40f, 0.22f, 0.85f));

            var icon = new Label(optedOut ? "●" : "✓");
            icon.style.color = new StyleColor(Color.white);
            icon.style.marginRight = 6;
            icon.style.unityFontStyleAndWeight = FontStyle.Bold;
            existing.Add(icon);

            var label = new Label(optedOut
                ? "VRCF QoL: Global Parameter auto-update DISABLED for this toggle"
                : "VRCF QoL: Global Parameter is auto-synced to Menu Path — don't edit it manually");
            label.style.color = new StyleColor(Color.white);
            label.style.flexGrow = 1;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.tooltip = optedOut
                ? "This toggle will NOT have its Global Parameter kept in sync with its Menu Path. Click Enable to resume auto-syncing."
                : "When enabled, VRCF QoL keeps 'Use Global Parameter' checked and 'globalParam' equal to the toggle's Menu Path. This prevents customisations from being wiped when VRCFury regenerates internal parameter names during an avatar update.";
            existing.Add(label);

            var capturedTarget = target;
            var capturedOptedOut = optedOut;
            var btn = new Button(() => {
                AutoGlobalParameterTool.SetOptedOut(capturedTarget, !capturedOptedOut);
                if (capturedOptedOut) {
                    // Was disabled, now enabling — apply immediately so the
                    // user sees the Global Parameter update on screen.
                    AutoGlobalParameterTool.ApplyTo(capturedTarget, force: true);
                }
            }) {
                text = optedOut ? "Enable" : "Disable"
            };
            btn.style.marginLeft = 4;
            btn.style.paddingLeft = 10;
            btn.style.paddingRight = 10;
            existing.Add(btn);
        }

        // ----------------------------------------------------------------------
        // Per-page inline buttons
        // ----------------------------------------------------------------------

        private static void TryInjectPageButtons(Label pageLabel) {
            // Mark the label so we don't try again this lifecycle.
            pageLabel.AddToClassList(InjectedClass);

            var parent = pageLabel.parent;
            if (parent == null) return;

            // Avoid re-decorating a parent we've already touched.
            if (parent.ClassListContains(ButtonBarClass)) return;
            parent.AddToClassList(ButtonBarClass);

            // No buttons registered — nothing to do.
            var specs = VrcfQol.InlinePageButtons;
            if (specs == null || specs.Count == 0) return;

            // Build the button row: we replace the bare label with a row containing
            // [label, spacer, button1, button2, ...].
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            int labelIndex = parent.IndexOf(pageLabel);
            parent.RemoveAt(labelIndex);
            row.Add(pageLabel);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            foreach (var spec in specs) {
                var capturedSpec = spec;
                var btn = new Button(() => OnInlineButtonClicked(pageLabel, capturedSpec)) {
                    text = spec.Text,
                };
                btn.tooltip = spec.Tooltip ?? string.Empty;
                btn.style.marginLeft = 4;
                btn.style.paddingLeft = 8;
                btn.style.paddingRight = 8;
                row.Add(btn);
            }

            parent.Insert(labelIndex, row);
        }

        private static void OnInlineButtonClicked(Label pageLabel, VrcfQol.InlineButtonSpec spec) {
            // Page index from "Page #N".
            var match = PageLabelRegex.Match(pageLabel.text ?? "");
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out var oneBasedIndex)) return;
            var sourceIndex = oneBasedIndex - 1;

            var selection = Selection.activeGameObject;
            if (selection == null) {
                EditorUtility.DisplayDialog("VRCF QoL",
                    "Could not determine which GameObject is inspected. Select it in the Hierarchy and try again.", "OK");
                return;
            }
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                EditorUtility.DisplayDialog("VRCF QoL", err, "OK"); return;
            }
            var r = VrcfQol.Reflection;

            foreach (Component c in selection.GetComponents(r.VRCFuryType)) {
                if (c == null) continue;
                if (!VrcfQol.TryResolveFlipbookFromComponent(c, out var ctx)) continue;
                if (ctx.pages == null || sourceIndex < 0 || sourceIndex >= ctx.pages.Count) continue;
                ctx.pageIndex = sourceIndex;

                // Hide/disable check.
                if (spec.Visible != null) {
                    bool vis;
                    try { vis = spec.Visible(ctx); } catch { vis = true; }
                    if (!vis) {
                        EditorUtility.DisplayDialog("VRCF QoL",
                            "This action is not available for this page right now.", "OK");
                        return;
                    }
                }

                try {
                    spec.OnClick(ctx);
                } catch (System.Exception ex) {
                    Debug.LogException(ex);
                    EditorUtility.DisplayDialog("VRCF QoL",
                        "Action failed. See Console.\n\n" + ex.Message, "OK");
                }
                return;
            }

            EditorUtility.DisplayDialog("VRCF QoL",
                "Could not find a flipbook toggle on the selected GameObject that contains this page. " +
                "If the flipbook is on a different object than the current selection, use right-click → VRCF QoL → ... instead.",
                "OK");
        }
    }
}
