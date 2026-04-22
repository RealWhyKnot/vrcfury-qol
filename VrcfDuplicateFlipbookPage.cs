// VrcfDuplicateFlipbookPage.cs
// Editor-only tool that duplicates a single page of a VRCFury Flipbook Builder
// and appends the copy at the end of the flipbook.
//
// Example: the flipbook has pages 1-7. You click "Duplicate to end" on page #7
// and a new page #8 appears with the same actions. Editing page #8 afterward
// does NOT affect page #7 (the copy is deep).
//
// Usage:
//   1. Select the GameObject that holds the flipbook toggle (or an ancestor).
//   2. Menu: Tools → VRCFury → Duplicate Flipbook Page...
//   3. A window opens listing the pages. Click "Duplicate to end" on any row.
//   4. Save the scene. Ctrl+Z reverts.
//
// Place under Assets/Editor/ alongside VrcfMigrateTogglesToFlipbook.cs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfTools {
    internal class VrcfDuplicateFlipbookPageWindow : EditorWindow {
        private const string MenuPath = "Tools/VRCFury/Duplicate Flipbook Page...";

        // Reflection handles, resolved lazily
        private static Assembly _vrcfAsm;
        private static Type _tVRCFury;
        private static Type _tToggle;
        private static Type _tState;
        private static Type _tFlipbookBuilderAction;
        private static Type _tFlipbookPage;
        private static FieldInfo _fContent;
        private static FieldInfo _fToggleName;
        private static FieldInfo _fToggleState;
        private static FieldInfo _fStateActions;
        private static FieldInfo _fPages;
        private static FieldInfo _fPageState;

        // Window state — the VRCFury component holding the flipbook toggle
        [SerializeField] private Component destinationComponent;
        [NonSerialized] private object flipbookAction;
        [NonSerialized] private string flipbookToggleName;
        private Vector2 scroll;

        [MenuItem(MenuPath, false, 101)]
        private static void Open() {
            if (!TryResolveTypes(out var err)) {
                EditorUtility.DisplayDialog("Duplicate Flipbook Page", err, "OK");
                return;
            }

            var root = Selection.activeGameObject;
            if (root == null) {
                EditorUtility.DisplayDialog(
                    "Duplicate Flipbook Page",
                    "Select the GameObject that holds your flipbook toggle (or an ancestor) first.",
                    "OK");
                return;
            }

            var destinations = FindFlipbookToggles(root);

            if (destinations.Count == 0) {
                EditorUtility.DisplayDialog(
                    "Duplicate Flipbook Page",
                    $"No VRCFury Toggle with a Flipbook Builder was found under '{root.name}'.",
                    "OK");
                return;
            }

            if (destinations.Count > 1) {
                var list = string.Join("\n", destinations.Select(d =>
                    " • " + (string.IsNullOrEmpty(d.name) ? "(unnamed)" : d.name) +
                    "  [" + GetGameObjectPath(d.component.gameObject) + "]"));
                EditorUtility.DisplayDialog(
                    "Duplicate Flipbook Page",
                    $"Found {destinations.Count} flipbook toggles in this subtree:\n\n{list}\n\n" +
                    "Narrow the selection to a single flipbook and try again.",
                    "OK");
                return;
            }

            var window = GetWindow<VrcfDuplicateFlipbookPageWindow>(utility: true, "VRCF Duplicate Flipbook Page", focus: true);
            window.destinationComponent = destinations[0].component;
            window.flipbookAction = destinations[0].flipbookAction;
            window.flipbookToggleName = destinations[0].name;
            window.minSize = new Vector2(360, 200);
            window.Show();
        }

        [MenuItem(MenuPath, true)]
        private static bool OpenValidate() {
            return Selection.activeGameObject != null;
        }

        private void OnGUI() {
            if (destinationComponent == null) {
                EditorGUILayout.HelpBox(
                    "The destination VRCFury component is gone (maybe deleted or the scene changed). Close and reopen this window.",
                    MessageType.Warning);
                return;
            }

            // Re-resolve the flipbook action if the window was rebuilt (e.g. after a domain reload).
            if (flipbookAction == null) {
                if (!TryResolveTypes(out _)) {
                    EditorGUILayout.HelpBox("VRCFury runtime types not found.", MessageType.Error);
                    return;
                }
                var content = _fContent.GetValue(destinationComponent);
                if (content == null || content.GetType() != _tToggle) {
                    EditorGUILayout.HelpBox("This VRCFury component no longer holds a Toggle.", MessageType.Warning);
                    return;
                }
                flipbookToggleName = (string)_fToggleName.GetValue(content) ?? "";
                var state = _fToggleState.GetValue(content);
                var actions = _fStateActions.GetValue(state) as IList;
                flipbookAction = FindFlipbookAction(actions);
                if (flipbookAction == null) {
                    EditorGUILayout.HelpBox("This toggle no longer contains a Flipbook Builder action.", MessageType.Warning);
                    return;
                }
            }

            var pages = _fPages.GetValue(flipbookAction) as IList;

            EditorGUILayout.LabelField("Flipbook toggle", string.IsNullOrEmpty(flipbookToggleName) ? "(unnamed)" : flipbookToggleName);
            EditorGUILayout.LabelField("GameObject", GetGameObjectPath(destinationComponent.gameObject));
            EditorGUILayout.LabelField("Pages", pages.Count.ToString());
            EditorGUILayout.Space();

            if (pages.Count == 0) {
                EditorGUILayout.HelpBox("This flipbook has no pages yet. Add one in the Inspector first.", MessageType.Info);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < pages.Count; i++) {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                int actionCount = 0;
                try {
                    var pageState = _fPageState.GetValue(pages[i]);
                    var pageActions = _fStateActions.GetValue(pageState) as IList;
                    actionCount = pageActions?.Count ?? 0;
                } catch { /* ignore */ }

                EditorGUILayout.LabelField($"Page #{i + 1}   ({actionCount} action{(actionCount == 1 ? "" : "s")})");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Duplicate to end", GUILayout.Width(140))) {
                    DuplicatePageToEnd(i);
                    GUIUtility.ExitGUI(); // layout changed underneath us
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Save the scene to persist. Ctrl+Z reverts.", MessageType.None);
        }

        private void DuplicatePageToEnd(int sourceIndex) {
            try {
                Undo.RegisterCompleteObjectUndo(destinationComponent,
                    $"Duplicate flipbook page #{sourceIndex + 1} to end");

                var pages = _fPages.GetValue(flipbookAction) as IList;
                if (sourceIndex < 0 || sourceIndex >= pages.Count) return;

                var sourcePage = pages[sourceIndex];
                var newPage = DeepClonePage(sourcePage);
                pages.Add(newPage);

                EditorUtility.SetDirty(destinationComponent);
                Debug.Log($"[VRCFury Duplicate] Duplicated page #{sourceIndex + 1} as new page #{pages.Count} in '{flipbookToggleName}'.");
            } catch (Exception e) {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Duplicate Flipbook Page",
                    "Duplication failed; see Console.\n\n" + e.Message, "OK");
            }
        }

        /// <summary>
        /// Deep-clones a FlipBookPage: creates a fresh Page + fresh State + fresh action
        /// instances cloned via JsonUtility. UnityEngine.Object references (GameObjects,
        /// Renderers, Motion etc.) are preserved by Unity's serializer, which is what we want.
        /// </summary>
        private static object DeepClonePage(object sourcePage) {
            var srcState = _fPageState.GetValue(sourcePage);
            var srcActions = _fStateActions.GetValue(srcState) as IList;

            // Build a fresh actions list of the same declared type (List<Action>).
            var listType = _fStateActions.FieldType;
            var newActions = (IList)Activator.CreateInstance(listType);
            if (srcActions != null) {
                foreach (var action in srcActions) {
                    if (action == null) { newActions.Add(null); continue; }
                    var clone = CloneSerializable(action);
                    newActions.Add(clone);
                }
            }

            var newState = Activator.CreateInstance(_tState);
            _fStateActions.SetValue(newState, newActions);

            var newPage = Activator.CreateInstance(_tFlipbookPage);
            _fPageState.SetValue(newPage, newState);
            return newPage;
        }

        /// <summary>
        /// JsonUtility round-trip clone. Works for [Serializable] POCOs whose fields are
        /// primitives, enums, UnityEngine.Object refs, Color/Vector/Rect, nested [Serializable]
        /// classes, and Lists of the above — which covers every VRCFury StateAction we've seen.
        /// UnityEngine.Object refs are preserved (JsonUtility round-trips them by instance id).
        /// </summary>
        private static object CloneSerializable(object src) {
            var t = src.GetType();
            var json = JsonUtility.ToJson(src);
            return JsonUtility.FromJson(json, t);
        }

        private readonly struct DestInfo {
            public readonly Component component;
            public readonly string name;
            public readonly object flipbookAction;
            public DestInfo(Component c, string n, object a) { component = c; name = n; flipbookAction = a; }
        }

        private static List<DestInfo> FindFlipbookToggles(GameObject root) {
            var results = new List<DestInfo>();
            var vrcfs = root.GetComponentsInChildren(_tVRCFury, true).Cast<Component>().Where(c => c != null);
            foreach (var c in vrcfs) {
                var content = _fContent.GetValue(c);
                if (content == null || content.GetType() != _tToggle) continue;
                var state = _fToggleState.GetValue(content);
                var actions = _fStateActions.GetValue(state) as IList;
                var fb = FindFlipbookAction(actions);
                if (fb == null) continue;
                var name = (string)_fToggleName.GetValue(content) ?? "";
                results.Add(new DestInfo(c, name, fb));
            }
            return results;
        }

        private static object FindFlipbookAction(IList actions) {
            if (actions == null) return null;
            foreach (var a in actions) {
                if (a == null) continue;
                if (a.GetType() == _tFlipbookBuilderAction) return a;
            }
            return null;
        }

        private static string GetGameObjectPath(GameObject go) {
            if (go == null) return "(null)";
            var parts = new List<string>();
            var t = go.transform;
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool TryResolveTypes(out string error) {
            error = null;
            if (_tVRCFury != null) return true;

            _vrcfAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "VRCFury");
            if (_vrcfAsm == null) {
                error = "VRCFury runtime assembly ('VRCFury') was not found.";
                return false;
            }

            _tVRCFury = _vrcfAsm.GetType("VF.Model.VRCFury", false);
            _tToggle = _vrcfAsm.GetType("VF.Model.Feature.Toggle", false);
            _tState = _vrcfAsm.GetType("VF.Model.State", false);
            _tFlipbookBuilderAction = _vrcfAsm.GetType("VF.Model.StateAction.FlipBookBuilderAction", false);
            if (_tVRCFury == null || _tToggle == null || _tState == null || _tFlipbookBuilderAction == null) {
                error = "Failed to locate one or more VRCFury internal types.";
                return false;
            }
            _tFlipbookPage = _tFlipbookBuilderAction.GetNestedType("FlipBookPage",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (_tFlipbookPage == null) {
                error = "Failed to locate FlipBookBuilderAction.FlipBookPage nested type.";
                return false;
            }

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fContent = _tVRCFury.GetField("content", any);
            _fToggleName = _tToggle.GetField("name", any);
            _fToggleState = _tToggle.GetField("state", any);
            _fStateActions = _tState.GetField("actions", any);
            _fPages = _tFlipbookBuilderAction.GetField("pages", any);
            _fPageState = _tFlipbookPage.GetField("state", any);

            if (_fContent == null || _fToggleName == null || _fToggleState == null ||
                _fStateActions == null || _fPages == null || _fPageState == null) {
                error = "Failed to locate expected fields on VRCFury model types.";
                _tVRCFury = null;
                return false;
            }
            return true;
        }
    }
}
