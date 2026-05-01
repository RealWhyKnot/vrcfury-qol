// MoveVrcfComponentsTool.cs
//
// Moves every VRCFury component on a source GameObject to a destination
// GameObject in one undoable operation. Two modes:
//
//   • Move whole components (default)
//       Each VRCFury MonoBehaviour on the source is recreated on the destination
//       as its own component (preserves serialization shape exactly), then the
//       source is destroyed. Uses ComponentUtility.CopyComponent /
//       PasteComponentAsNew, which round-trips [SerializeReference] graphs
//       correctly via Unity's own serialization.
//
//   • Merge into one component
//       All features (the source's `content` SerializeReference, plus any legacy
//       `config.features` entries) get appended into a single VRCFury component
//       on the destination, using the legacy `config.features` list. Useful when
//       you want everything authored on one carrier object. Requires that the
//       installed VRCFury version still exposes `VRCFuryConfig.features`; if it
//       doesn't, the dialog warns and the user can fall back to "Move whole
//       components".
//
// Entry points:
//   • GameObject hierarchy right-click  → "VRCFury QoL/Move all VRCFury
//     components to..." (most ergonomic; the user is already in the hierarchy
//     to pick a destination next).
//   • Top-level menu                    → "GameObject/VRCFury QoL/Move all
//     VRCFury components to..." (mirrors the right-click).
//
// Both entry points open a small modal-ish EditorWindow where the user picks a
// destination GameObject + mode.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    internal static class MoveVrcfComponentsTool {

        private const string GameObjectMenuPath = "GameObject/VRCFury QoL/Move all VRCFury components to...";

        // Hierarchy right-click + GameObject menu. Priority 49 puts it just above
        // Unity's default "Center On Children" group (50).
        [MenuItem(GameObjectMenuPath, false, 49)]
        private static void Open(MenuCommand command) {
            var go = command.context as GameObject ?? Selection.activeGameObject;
            if (go == null) {
                EditorUtility.DisplayDialog("Move VRCFury Components",
                    "Select a GameObject first.", "OK");
                return;
            }
            if (!VrcfQol.Reflection.TryEnsure(out var error)) {
                EditorUtility.DisplayDialog("Move VRCFury Components", error, "OK");
                return;
            }

            var components = go.GetComponents(VrcfQol.Reflection.VRCFuryType);
            if (components == null || components.Length == 0) {
                EditorUtility.DisplayDialog("Move VRCFury Components",
                    $"'{go.name}' has no VRCFury components to move.", "OK");
                return;
            }

            MoveVrcfComponentsWindow.Show(go);
        }

        // Validate so the menu item is greyed out when the selection has no
        // VRCFury components on it. Cheap because we only check the immediate
        // GameObject (the operation is single-source by definition).
        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenValidate(MenuCommand command) {
            var go = command.context as GameObject ?? Selection.activeGameObject;
            if (go == null) return false;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return false;
            return go.GetComponent(VrcfQol.Reflection.VRCFuryType) != null;
        }

        // ---------- Core move logic, called by the dialog ------------------

        internal enum Mode {
            WholeComponents,
            MergeIntoOne,
        }

        /// <summary>
        /// Performs the move under a single Undo group. Returns the number of
        /// VRCFury components that were on the source (non-zero on success).
        /// On failure throws or shows a dialog, depending on the failure mode.
        /// </summary>
        internal static int Run(GameObject source, GameObject destination, Mode mode) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (source == destination) throw new InvalidOperationException(
                "Source and destination are the same GameObject.");
            if (!VrcfQol.Reflection.TryEnsure(out var error))
                throw new InvalidOperationException(error);

            var r = VrcfQol.Reflection;
            // Snapshot the source's VRCFury components up front. The list will
            // shrink as we destroy them, so iterating GetComponents() during the
            // loop would skip entries.
            var sources = new List<Component>(source.GetComponents(r.VRCFuryType));
            if (sources.Count == 0) return 0;

            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("VRCF QoL: Move VRCFury components");
            try {
                if (mode == Mode.WholeComponents) {
                    foreach (var src in sources) {
                        if (src == null) continue;
                        if (!ComponentUtility.CopyComponent(src))
                            throw new InvalidOperationException(
                                $"CopyComponent failed for {src.name}.");
                        if (!ComponentUtility.PasteComponentAsNew(destination))
                            throw new InvalidOperationException(
                                $"PasteComponentAsNew failed for {src.name}.");
                        // PasteComponentAsNew creates a real component on the
                        // destination (with its own Undo entry); register the
                        // newly added one so the destination is also marked
                        // dirty for save.
                    }
                    EditorUtility.SetDirty(destination);
                    foreach (var src in sources) {
                        if (src == null) continue;
                        Undo.DestroyObjectImmediate(src);
                    }
                } else {
                    MergeFeaturesInto(destination, sources, r);
                }
                Undo.CollapseUndoOperations(group);
                Debug.Log($"[VRCF QoL] Moved {sources.Count} VRCFury component(s) " +
                          $"from '{VrcfQol.GetGameObjectPath(source)}' to " +
                          $"'{VrcfQol.GetGameObjectPath(destination)}' " +
                          $"(mode: {(mode == Mode.WholeComponents ? "whole" : "merge")}).");
                return sources.Count;
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                Debug.LogException(ex);
                throw;
            }
        }

        /// <summary>
        /// Returns true iff the installed VRCFury version exposes the legacy
        /// `config.features` list that merge mode appends into.
        /// </summary>
        internal static bool MergeModeAvailable() {
            if (!VrcfQol.Reflection.TryEnsure(out _)) return false;
            var r = VrcfQol.Reflection;
            return r.ConfigType != null && r.ConfigField != null && r.FeaturesField != null;
        }

        private static void MergeFeaturesInto(GameObject destination, List<Component> sources, VrcfQol.ReflectionCache r) {
            if (!MergeModeAvailable())
                throw new InvalidOperationException(
                    "Merge mode requires VRCFuryConfig.features, which the installed VRCFury version does not expose. " +
                    "Use 'Move whole components' instead.");

            // Reuse an existing target VRCFury component if present, otherwise
            // add a new one (under Undo so it round-trips).
            var target = destination.GetComponent(r.VRCFuryType);
            if (target == null) {
                target = Undo.AddComponent(destination, r.VRCFuryType);
                if (target == null)
                    throw new InvalidOperationException(
                        "Failed to add VRCFury component to destination.");
            }
            var targetConfig = r.ConfigField.GetValue(target);
            if (targetConfig == null) {
                targetConfig = Activator.CreateInstance(r.ConfigType);
                r.ConfigField.SetValue(target, targetConfig);
            }
            var targetFeatures = r.FeaturesField.GetValue(targetConfig) as IList;
            if (targetFeatures == null) {
                targetFeatures = (IList)Activator.CreateInstance(r.FeaturesField.FieldType);
                r.FeaturesField.SetValue(targetConfig, targetFeatures);
            }

            Undo.RegisterCompleteObjectUndo(target, "Append features to VRCFury component");

            foreach (var src in sources) {
                if (src == null) continue;
                // 1) Take the modern single-feature `content` if non-null.
                var content = r.ContentField.GetValue(src);
                if (content != null) targetFeatures.Add(content);

                // 2) Take legacy `config.features` entries if present.
                if (r.ConfigField != null) {
                    var srcConfig = r.ConfigField.GetValue(src);
                    if (srcConfig != null && r.FeaturesField != null) {
                        if (r.FeaturesField.GetValue(srcConfig) is IList srcFeatures) {
                            foreach (var f in srcFeatures) {
                                if (f != null) targetFeatures.Add(f);
                            }
                        }
                    }
                }
            }

            EditorUtility.SetDirty(target);

            foreach (var src in sources) {
                if (src == null) continue;
                Undo.DestroyObjectImmediate(src);
            }
        }
    }

    // ---------------- Modal dialog window ---------------------------------

    internal sealed class MoveVrcfComponentsWindow : EditorWindow {
        private GameObject _source;
        private GameObject _destination;
        private MoveVrcfComponentsTool.Mode _mode = MoveVrcfComponentsTool.Mode.WholeComponents;

        internal static void Show(GameObject source) {
            var w = CreateInstance<MoveVrcfComponentsWindow>();
            w._source = source;
            w.titleContent = new GUIContent("Move VRCFury Components");
            // Position near the cursor so it feels like a contextual dialog.
            var p = GUIUtility.GUIToScreenPoint(Event.current?.mousePosition ?? Vector2.zero);
            w.position = new Rect(p.x + 10, p.y + 10, 360, 180);
            w.minSize = new Vector2(360, 180);
            w.maxSize = new Vector2(640, 280);
            w.ShowUtility();
        }

        private void OnGUI() {
            if (_source == null) {
                EditorGUILayout.HelpBox("Source GameObject was deleted. Close this window.", MessageType.Error);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            EditorGUILayout.LabelField("Source", VrcfQol.GetGameObjectPath(_source));

            using (new EditorGUI.DisabledScope(false)) {
                _destination = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Destination", "GameObject that will receive the VRCFury components."),
                    _destination, typeof(GameObject), allowSceneObjects: true);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);

            bool mergeAvailable = MoveVrcfComponentsTool.MergeModeAvailable();

            // Two radio rows. We use Toggle() with EditorStyles.radioButton so
            // the layout is compact and IMGUI-native.
            bool whole = _mode == MoveVrcfComponentsTool.Mode.WholeComponents;
            bool newWhole = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Move whole components",
                    "Each VRCFury MonoBehaviour is recreated on the destination as its own component. Preserves shape exactly."),
                whole);
            if (newWhole && !whole) _mode = MoveVrcfComponentsTool.Mode.WholeComponents;

            bool merge = _mode == MoveVrcfComponentsTool.Mode.MergeIntoOne;
            using (new EditorGUI.DisabledScope(!mergeAvailable)) {
                bool newMerge = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Merge into one component" + (mergeAvailable ? "" : "  (unsupported on this VRCFury version)"),
                        "Append all source features into a single VRCFury component on the destination using the legacy config.features list."),
                    merge);
                if (newMerge && !merge && mergeAvailable) _mode = MoveVrcfComponentsTool.Mode.MergeIntoOne;
            }
            if (!mergeAvailable && _mode == MoveVrcfComponentsTool.Mode.MergeIntoOne) {
                _mode = MoveVrcfComponentsTool.Mode.WholeComponents;
            }

            EditorGUILayout.Space(8);

            string disabledReason = ValidationError();
            if (disabledReason != null) {
                EditorGUILayout.HelpBox(disabledReason, MessageType.Info);
            }

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) {
                    Close();
                }
                using (new EditorGUI.DisabledScope(disabledReason != null)) {
                    if (GUILayout.Button("Move", GUILayout.Width(120))) {
                        DoMove();
                    }
                }
            }
        }

        private string ValidationError() {
            if (_destination == null) return "Pick a destination GameObject.";
            if (_destination == _source) return "Destination must differ from the source.";
            // Same scene check — moving across scenes is technically possible,
            // but the resulting Undo step is split across scenes and tends to
            // confuse users. Easy guardrail.
            if (_destination.scene != _source.scene)
                return "Source and destination must be in the same scene / prefab stage.";
            return null;
        }

        private void DoMove() {
            try {
                int count = MoveVrcfComponentsTool.Run(_source, _destination, _mode);
                if (count == 0) {
                    EditorUtility.DisplayDialog("Move VRCFury Components",
                        "No VRCFury components were found on the source.", "OK");
                } else {
                    Selection.activeGameObject = _destination;
                }
                Close();
            } catch (Exception ex) {
                EditorUtility.DisplayDialog("Move VRCFury Components",
                    "Move failed; changes reverted.\n\n" + ex.Message, "OK");
            }
        }
    }
}
