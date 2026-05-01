// MissingReferenceWarningTool.cs
//
// Scans every VRCFury component in the open scene(s) + active prefab stage
// for "missing" Object references — properties whose serialized instanceID
// is non-zero but whose objectReferenceValue resolves to null. That's the
// telltale of a deleted asset / scene object that the VRCFury data still
// expects to find.
//
// On editor startup, scene-open, and prefab-stage-open the scanner runs once
// and, if anything is missing, pops the MissingReferenceWindow. Dismissing
// the window (or closing it for any reason) sets a session-scoped flag that
// suppresses further auto-pops until the next assembly reload — script
// recompile, Reload Domain, or restart. Users who want to re-check anyway
// can use Tools/VRCFury QoL/Check for missing references...
//
// Why session-scoped (not per-project preference): missing refs are usually
// transient — the user notices, fixes the problem, and a recompile re-arms
// the scanner. A persistent "don't ask again" preference would let real
// problems linger silently. Reload-scoped is the right unit here.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class MissingReferenceWarningTool {

        private const string MenuPath = "Tools/VRCFury QoL/Check for missing references...";

        // Suppresses auto-show for the rest of the session. Cleared by
        // assembly reload because static state doesn't survive that.
        private static bool _dismissedThisSession;

        // Avoid re-entrant scans on a tight delayCall queue.
        private static bool _scanScheduled;

        static MissingReferenceWarningTool() {
            ScheduleAutoCheck();
            EditorSceneManager.sceneOpened += (s, m) => ScheduleAutoCheck();
            PrefabStage.prefabStageOpened += _ => ScheduleAutoCheck();
        }

        // ------ Manual entry point (Tools menu) -----------------------------

        [MenuItem(MenuPath, false, 2010)]
        private static void OpenManually() {
            // Manual checks bypass the dismiss flag and always show, even if
            // the scan is empty (so the user gets a "no missing refs" reply
            // when they ask explicitly).
            if (!VrcfQol.Reflection.TryEnsure(out var error)) {
                EditorUtility.DisplayDialog("Check for missing references", error, "OK");
                return;
            }
            var refs = Scan();
            MissingReferenceWindow.OpenManual(refs);
        }

        // ------ Auto-show pipeline ------------------------------------------

        internal static void MarkDismissedThisSession() {
            _dismissedThisSession = true;
        }

        private static void ScheduleAutoCheck() {
            if (_scanScheduled) return;
            _scanScheduled = true;
            EditorApplication.delayCall += () => {
                _scanScheduled = false;
                AutoCheck();
            };
        }

        private static void AutoCheck() {
            if (_dismissedThisSession) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return;
            var refs = Scan();
            if (refs.Count == 0) return;
            MissingReferenceWindow.OpenAuto(refs);
        }

        // ------ Scan --------------------------------------------------------

        internal static List<MissingRef> Scan() {
            var output = new List<MissingRef>();
            if (!VrcfQol.Reflection.TryEnsure(out _)) return output;
            var r = VrcfQol.Reflection;

            var seen = new HashSet<Component>();
            foreach (var o in Resources.FindObjectsOfTypeAll(r.VRCFuryType)) {
                var c = o as Component;
                if (c == null || !seen.Add(c)) continue;
                // Filter to scene / prefab-stage instances (matches the
                // pattern AutoGlobalParameterTool uses).
                if ((c.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) != 0) continue;
                if (c.gameObject == null) continue;
                if (!c.gameObject.scene.IsValid()) continue;
                ScanComponent(c, output);
            }
            return output;
        }

        private static void ScanComponent(Component vrcf, List<MissingRef> output) {
            using (var so = new SerializedObject(vrcf)) {
                var iter = so.GetIterator();
                if (!iter.NextVisible(true)) return;
                do {
                    if (iter.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (iter.propertyPath == "m_Script") continue;
                    // Missing iff the serialized instanceID is non-zero but
                    // Unity couldn't resolve it back to a live Object.
                    if (iter.objectReferenceValue != null) continue;
                    if (iter.objectReferenceInstanceIDValue == 0) continue;

                    output.Add(new MissingRef {
                        VrcfComponent  = vrcf,
                        GameObjectPath = VrcfQol.GetGameObjectPath(vrcf.gameObject),
                        FeatureType    = GetEnclosingFeatureTypeName(so, iter.propertyPath),
                        PropertyPath   = iter.propertyPath,
                        InstanceID     = iter.objectReferenceInstanceIDValue,
                    });
                } while (iter.NextVisible(true));
            }
        }

        private static string GetEnclosingFeatureTypeName(SerializedObject so, string propertyPath) {
            string parent = propertyPath;
            while (true) {
                int dot = parent.LastIndexOf('.');
                if (dot < 0) break;
                parent = parent.Substring(0, dot);
                var p = so.FindProperty(parent);
                if (p == null) continue;
                if (p.propertyType == SerializedPropertyType.ManagedReference) {
                    var fullName = p.managedReferenceFullTypename;
                    if (string.IsNullOrEmpty(fullName)) return "VRCFury";
                    int space = fullName.LastIndexOf(' ');
                    string typeName = space >= 0 ? fullName.Substring(space + 1) : fullName;
                    int lastDot = typeName.LastIndexOf('.');
                    return lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
                }
            }
            return "VRCFury";
        }

        // ------ Records -----------------------------------------------------

        internal sealed class MissingRef {
            public Component VrcfComponent;
            public string GameObjectPath;
            public string FeatureType;
            public string PropertyPath;
            public int InstanceID;
        }
    }
}
