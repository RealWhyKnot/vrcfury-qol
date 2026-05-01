// MissingReferenceWindow.cs
//
// Non-modal window listing missing Object references found by
// MissingReferenceWarningTool. Pops automatically on startup / scene-open
// when there's anything to report; can also be opened manually from
// Tools/VRCFury QoL/Check for missing references...
//
// Closing the window in auto-show mode marks the session as dismissed so
// the warning won't pop again until the next assembly reload. Manually
// opened instances don't set the dismiss flag (the user opened it on
// purpose; closing isn't a dismissal).

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    internal sealed class MissingReferenceWindow : EditorWindow {

        private List<MissingReferenceWarningTool.MissingRef> _missing =
            new List<MissingReferenceWarningTool.MissingRef>();
        // Bool flags survive domain reloads; the list is rebuilt by Scan().
        [SerializeField] private bool _shouldDismissOnClose;
        [SerializeField] private bool _autoShow;
        private Vector2 _scroll;

        // ------ Open variants ----------------------------------------------

        internal static void OpenAuto(List<MissingReferenceWarningTool.MissingRef> missing) {
            var w = GetWindow<MissingReferenceWindow>(false, "Missing References", true);
            w.titleContent = new GUIContent("VRCFury QoL — Missing References");
            w._missing = missing ?? new List<MissingReferenceWarningTool.MissingRef>();
            w._shouldDismissOnClose = true;
            w._autoShow = true;
            w.minSize = new Vector2(520, 280);
            w.Show();
        }

        internal static void OpenManual(List<MissingReferenceWarningTool.MissingRef> missing) {
            var w = GetWindow<MissingReferenceWindow>(false, "Missing References", true);
            w.titleContent = new GUIContent("VRCFury QoL — Missing References");
            w._missing = missing ?? new List<MissingReferenceWarningTool.MissingRef>();
            w._shouldDismissOnClose = false;
            w._autoShow = false;
            w.minSize = new Vector2(520, 280);
            w.Show();
            w.Focus();
        }

        // ------ Lifecycle ---------------------------------------------------

        private void OnEnable() {
            // Domain reload nukes the un-serialized list. Rescan so the
            // window has something to show until the InitializeOnLoad
            // re-populates via OpenAuto/OpenManual.
            if (_missing == null || _missing.Count == 0) {
                _missing = MissingReferenceWarningTool.Scan();
            }
        }

        private void OnDestroy() {
            if (_shouldDismissOnClose) {
                MissingReferenceWarningTool.MarkDismissedThisSession();
            }
        }

        // ------ GUI ---------------------------------------------------------

        private void OnGUI() {
            DrawHeader();
            DrawDivider();
            DrawList();
            DrawDivider();
            DrawFooter();
        }

        private void DrawHeader() {
            using (new EditorGUILayout.HorizontalScope()) {
                if (_missing.Count == 0) {
                    EditorGUILayout.HelpBox(
                        "No missing references found. VRCFury components look healthy.",
                        MessageType.Info);
                } else {
                    EditorGUILayout.HelpBox(
                        $"{_missing.Count} missing reference{(_missing.Count == 1 ? "" : "s")} on VRCFury component(s). " +
                        (_autoShow
                            ? "This warning won't pop again until the next reload (script recompile / restart). " +
                              "You can re-check anytime from Tools/VRCFury QoL/Check for missing references..."
                            : "Fix them in the inspector or replace via Tools/VRCFury QoL/Replace References."),
                        MessageType.Warning);
                }
            }
        }

        private void DrawList() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_missing.Count == 0) {
                    EditorGUILayout.LabelField(
                        "Click Re-scan to check again.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    string lastGroup = null;
                    foreach (var m in _missing) {
                        var group = $"{m.GameObjectPath}  ▸  {m.FeatureType}";
                        if (group != lastGroup) {
                            EditorGUILayout.LabelField(group, EditorStyles.boldLabel);
                            lastGroup = group;
                        }
                        DrawRow(m);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawRow(MissingReferenceWarningTool.MissingRef m) {
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope()) {
                    EditorGUILayout.LabelField(m.PropertyPath, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"missing — instanceID was {m.InstanceID}",
                        EditorStyles.miniLabel);
                }
                if (GUILayout.Button(new GUIContent("Ping", "Highlight the affected VRCFury component."),
                        EditorStyles.miniButton, GUILayout.Width(50))) {
                    if (m.VrcfComponent != null) {
                        Selection.activeObject = m.VrcfComponent;
                        EditorGUIUtility.PingObject(m.VrcfComponent);
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        private void DrawFooter() {
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Re-scan", "Walk the scenes again and refresh the list."),
                        GUILayout.Height(22), GUILayout.Width(100))) {
                    _missing = MissingReferenceWarningTool.Scan();
                }
                GUILayout.FlexibleSpace();
                string closeLabel = _shouldDismissOnClose ? "Dismiss" : "Close";
                if (GUILayout.Button(closeLabel, GUILayout.Height(22), GUILayout.Width(100))) {
                    Close();
                }
            }
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }
    }
}
