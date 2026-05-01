// ReplaceReferencesWindow.cs
//
// EditorWindow that lists every Object reference inside any VRCFury component
// on a set of selected GameObjects, and lets the user drop a replacement
// directly onto each row. All replacements happen in a single Undo step on
// Apply.
//
// Why SerializedObject + SerializedProperty.NextVisible(true) instead of raw
// reflection: VRCFury features are [SerializeReference] polymorphic graphs,
// and Unity's SerializedProperty already descends into them safely. This is
// also future-proof — if VRCFury renames internal fields but keeps them
// serialized, the walk still works because we only care about
// `propertyType == ObjectReference`, not field names.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    internal sealed class ReplaceReferencesWindow : EditorWindow {

        // Persisted across domain reloads so the search list survives a script
        // recompile while the window is open.
        [SerializeField] private List<GameObject> _searchRoots = new List<GameObject>();

        // Match list is recomputed on Scan. Replacements (Object) are stored
        // per row in non-serialized fields — domain reload re-scans and clears
        // any pending replacements, which is the safer choice (the user's
        // replacement targets may also have been scrubbed by the reload).
        private readonly List<Match> _matches = new List<Match>();
        private bool _hideUnchanged;
        private string _scanSummary = "";

        private Vector2 _rootsScroll;
        private Vector2 _matchesScroll;

        // ---------------- Public entry point ------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<ReplaceReferencesWindow>(false, "Replace References", true);
            w.titleContent = new GUIContent("Replace References (VRCFury QoL)");
            w.minSize = new Vector2(520, 380);
            if (prefillFromSelection) {
                w._searchRoots = Selection.gameObjects.Where(g => g != null).Distinct().ToList();
                w.Rescan();
            }
            w.Show();
            w.Focus();
        }

        // ---------------- GUI ---------------------------------------------

        private void OnGUI() {
            DrawSearchRoots();
            EditorGUILayout.Space(4);
            DrawDivider();
            DrawMatches();
            DrawDivider();
            DrawApplyBar();
        }

        // -------- Search roots panel --------

        private void DrawSearchRoots() {
            EditorGUILayout.LabelField("Search in", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinHeight(54), GUILayout.MaxHeight(140))) {
                _rootsScroll = EditorGUILayout.BeginScrollView(_rootsScroll);
                if (_searchRoots.Count == 0) {
                    EditorGUILayout.LabelField("(empty — pick GameObjects in the hierarchy and click 'Use selection')",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    for (int i = 0; i < _searchRoots.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            var newGo = (GameObject)EditorGUILayout.ObjectField(
                                GUIContent.none, _searchRoots[i], typeof(GameObject), allowSceneObjects: true);
                            if (newGo != _searchRoots[i]) {
                                _searchRoots[i] = newGo;
                                Rescan();
                            }
                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) {
                                removeIndex = i;
                            }
                        }
                    }
                    if (removeIndex >= 0) {
                        _searchRoots.RemoveAt(removeIndex);
                        Rescan();
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Use selection",
                        "Replace the search list with the currently selected GameObjects."))) {
                    _searchRoots = Selection.gameObjects.Where(g => g != null).Distinct().ToList();
                    Rescan();
                }
                if (GUILayout.Button(new GUIContent("Add selection",
                        "Add the currently selected GameObjects to the search list."))) {
                    foreach (var g in Selection.gameObjects) {
                        if (g != null && !_searchRoots.Contains(g)) _searchRoots.Add(g);
                    }
                    Rescan();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear", "Empty the search list."), GUILayout.Width(70))) {
                    _searchRoots.Clear();
                    _matches.Clear();
                    _scanSummary = "";
                }
            }
        }

        // -------- Matches list --------

        private void DrawMatches() {
            using (new EditorGUILayout.HorizontalScope()) {
                int queued = _matches.Count(m => m.HasReplacement);
                EditorGUILayout.LabelField(_matches.Count > 0
                        ? $"References ({_matches.Count} found, {queued} queued)"
                        : "References",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                _hideUnchanged = GUILayout.Toggle(_hideUnchanged,
                    new GUIContent("Only queued",
                        "Only show rows that have a replacement queued."),
                    EditorStyles.miniButton, GUILayout.Width(90));
            }
            if (!string.IsNullOrEmpty(_scanSummary)) {
                EditorGUILayout.LabelField(_scanSummary, EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _matchesScroll = EditorGUILayout.BeginScrollView(_matchesScroll);
                if (_searchRoots.Count == 0) {
                    EditorGUILayout.LabelField("Add GameObjects above to begin.", EditorStyles.centeredGreyMiniLabel);
                } else if (_matches.Count == 0) {
                    EditorGUILayout.LabelField("No object references found.", EditorStyles.centeredGreyMiniLabel);
                } else {
                    string lastGroup = null;
                    foreach (var m in _matches) {
                        if (_hideUnchanged && !m.HasReplacement) continue;
                        var group = m.HeaderText;
                        if (group != lastGroup) {
                            EditorGUILayout.LabelField(group, EditorStyles.boldLabel);
                            lastGroup = group;
                        }
                        DrawMatchRow(m);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawMatchRow(Match m) {
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope()) {
                    EditorGUILayout.LabelField(m.PropertyPath, EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField("Current", GUILayout.Width(64));
                        using (new EditorGUI.DisabledScope(true)) {
                            EditorGUILayout.ObjectField(m.CurrentValue, typeof(Object), allowSceneObjects: true);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField(
                            new GUIContent("Replace", "Drop a replacement object here. Leave empty to keep current."),
                            GUILayout.Width(64));
                        var newReplacement = EditorGUILayout.ObjectField(
                            m.Replacement, typeof(Object), allowSceneObjects: true);
                        if (newReplacement != m.Replacement) m.Replacement = newReplacement;
                        // Quick "apply same replacement to every row that has the same Current"
                        // for the common "rename a bone, swap it everywhere" workflow.
                        using (new EditorGUI.DisabledScope(m.Replacement == null)) {
                            if (GUILayout.Button(new GUIContent("All like this",
                                    "Set this same replacement on every row whose current value matches."),
                                EditorStyles.miniButton, GUILayout.Width(90))) {
                                ApplyReplacementToSiblings(m);
                            }
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent("Ping", "Highlight the VRCFury component in the hierarchy."),
                        EditorStyles.miniButton, GUILayout.Width(40))) {
                    if (m.VrcfComponent != null) EditorGUIUtility.PingObject(m.VrcfComponent);
                }
            }
            EditorGUILayout.Space(2);
            DrawSubtleDivider();
        }

        // -------- Apply bar --------

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                int queued = _matches.Count(m => m.HasReplacement);
                using (new EditorGUI.DisabledScope(queued == 0)) {
                    if (GUILayout.Button(queued > 0
                            ? $"Apply {queued} Replacement{(queued == 1 ? "" : "s")}"
                            : "Apply",
                        GUILayout.Height(24), GUILayout.MinWidth(180))) {
                        Apply();
                    }
                }
                using (new EditorGUI.DisabledScope(_searchRoots.Count == 0)) {
                    if (GUILayout.Button(new GUIContent("Refresh", "Re-scan the search roots."),
                            GUILayout.Height(24), GUILayout.Width(80))) {
                        Rescan();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Height(24), GUILayout.Width(80))) Close();
            }
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }

        private static void DrawSubtleDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.06f));
        }

        // ---------------- Scan ---------------------------------------------

        private void Rescan() {
            _matches.Clear();
            if (_searchRoots.Count == 0) { _scanSummary = ""; return; }

            if (!VrcfQol.Reflection.TryEnsure(out var error)) {
                _scanSummary = error;
                return;
            }
            var r = VrcfQol.Reflection;

            int componentsScanned = 0;
            var seenComponents = new HashSet<Component>();
            foreach (var root in _searchRoots) {
                if (root == null) continue;
                foreach (var c in root.GetComponentsInChildren(r.VRCFuryType, true)) {
                    if (c == null || !seenComponents.Add(c)) continue;
                    componentsScanned++;
                    ScanComponent(c);
                }
            }

            _matches.Sort((a, b) => {
                int g = string.Compare(a.GameObjectPath, b.GameObjectPath, System.StringComparison.Ordinal);
                if (g != 0) return g;
                int t = string.Compare(a.FeatureType, b.FeatureType, System.StringComparison.Ordinal);
                if (t != 0) return t;
                return string.Compare(a.PropertyPath, b.PropertyPath, System.StringComparison.Ordinal);
            });
            _scanSummary = $"Scanned {componentsScanned} VRCFury component(s) across {_searchRoots.Count(x => x != null)} root(s).";
        }

        private void ScanComponent(Component vrcf) {
            using (var so = new SerializedObject(vrcf)) {
                var iter = so.GetIterator();
                if (!iter.NextVisible(true)) return;
                do {
                    if (iter.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var current = iter.objectReferenceValue;
                    if (current == null) continue; // nothing to replace
                    // Skip the script reference at the top of every component.
                    if (iter.propertyPath == "m_Script") continue;

                    _matches.Add(new Match {
                        VrcfComponent  = vrcf,
                        GameObjectPath = VrcfQol.GetGameObjectPath(vrcf.gameObject),
                        FeatureType    = GetEnclosingFeatureTypeName(so, iter.propertyPath),
                        PropertyPath   = iter.propertyPath,
                        CurrentValue   = current,
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
                    return ShortenManagedReferenceTypeName(p.managedReferenceFullTypename);
                }
            }
            return "VRCFury";
        }

        private static string ShortenManagedReferenceTypeName(string fullName) {
            if (string.IsNullOrEmpty(fullName)) return "VRCFury";
            int space = fullName.LastIndexOf(' ');
            string typeName = space >= 0 ? fullName.Substring(space + 1) : fullName;
            int lastDot = typeName.LastIndexOf('.');
            return lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
        }

        private void ApplyReplacementToSiblings(Match source) {
            if (source.Replacement == null) return;
            foreach (var m in _matches) {
                if (m == source) continue;
                if (m.CurrentValue == source.CurrentValue) m.Replacement = source.Replacement;
            }
        }

        // ---------------- Apply --------------------------------------------

        private void Apply() {
            var queued = _matches.Where(m => m.HasReplacement && m.VrcfComponent != null).ToList();
            if (queued.Count == 0) return;

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("VRCF QoL: Replace VRCFury references");

            int applied = 0;
            int skipped = 0;
            try {
                foreach (var byComp in queued.GroupBy(m => m.VrcfComponent)) {
                    using (var so = new SerializedObject(byComp.Key)) {
                        bool anyChanged = false;
                        foreach (var m in byComp) {
                            var prop = so.FindProperty(m.PropertyPath);
                            if (prop == null) { skipped++; continue; }
                            if (prop.propertyType != SerializedPropertyType.ObjectReference) {
                                skipped++; continue;
                            }
                            // Snapshot guard: if the current value drifted between
                            // scan and apply, refuse rather than overwrite something
                            // the user didn't see.
                            if (prop.objectReferenceValue != m.CurrentValue) { skipped++; continue; }
                            prop.objectReferenceValue = m.Replacement;
                            applied++;
                            anyChanged = true;
                        }
                        if (anyChanged) so.ApplyModifiedProperties();
                    }
                }
                Undo.CollapseUndoOperations(group);
            } catch (System.Exception ex) {
                Undo.RevertAllInCurrentGroup();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Replace References",
                    "Apply failed; changes reverted.\n\n" + ex.Message, "OK");
                return;
            }

            string skipNote = skipped > 0 ? $" Skipped {skipped} stale entr{(skipped == 1 ? "y" : "ies")}." : "";
            Debug.Log($"[VRCF QoL] Replaced {applied} reference(s)." + skipNote);

            // Re-scan so the panel reflects the new state. Pending replacements
            // are dropped — if the user still wants to swap something else,
            // the new scan shows fresh rows to drop into.
            Rescan();
        }

        // ---------------- Match record ------------------------------------

        private sealed class Match {
            public Component VrcfComponent;
            public string GameObjectPath;
            public string FeatureType;
            public string PropertyPath;
            public Object CurrentValue;
            public Object Replacement;

            public bool HasReplacement => Replacement != null && Replacement != CurrentValue;
            public string HeaderText => $"{GameObjectPath}  ▸  {FeatureType}";
        }
    }
}
