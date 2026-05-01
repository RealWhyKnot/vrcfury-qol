// ReplaceReferencesWindow.cs
//
// EditorWindow that finds every Object reference matching `From` inside any
// VRCFury component on the selected search roots, and lets the user opt-in
// per match to replace them with `To`. All replacements happen in a single
// Undo step.
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

        // Persisted across domain reloads so a Find→Apply→Find loop survives
        // (e.g. a script edit between Find and Apply).
        [SerializeField] private List<GameObject> _searchRoots = new List<GameObject>();
        [SerializeField] private Object _from;
        [SerializeField] private Object _to;

        // Match list is recomputed on Find; not serialized.
        private readonly List<Match> _matches = new List<Match>();
        private bool _hasSearched;
        private string _searchSummary = "";

        private Vector2 _rootsScroll;
        private Vector2 _matchesScroll;

        private static class Style {
            public static readonly GUIContent FindBtn   = new GUIContent("Find Matches");
            public static readonly GUIContent UseSel    = new GUIContent("Use selection",       "Replace the search list with currently selected GameObjects.");
            public static readonly GUIContent AddSel    = new GUIContent("Add selection",       "Add currently selected GameObjects to the search list.");
            public static readonly GUIContent ClearList = new GUIContent("Clear",               "Empty the search list.");
            public static readonly GUIContent CheckAll  = new GUIContent("All",                 "Include every match.");
            public static readonly GUIContent UncheckAll= new GUIContent("None",                "Include no matches.");
            public static readonly GUIContent Refresh   = new GUIContent("Refresh",             "Re-scan the search roots for matches.");
        }

        // ---------------- Public entry point ------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<ReplaceReferencesWindow>(false, "Replace References", true);
            w.titleContent = new GUIContent("Replace References (VRCFury QoL)");
            w.minSize = new Vector2(440, 360);
            if (prefillFromSelection) {
                w._searchRoots = Selection.gameObjects.Where(g => g != null).Distinct().ToList();
                w._matches.Clear();
                w._hasSearched = false;
            }
            w.Show();
            w.Focus();
        }

        // ---------------- GUI ---------------------------------------------

        private void OnGUI() {
            DrawSearchRoots();
            EditorGUILayout.Space(4);
            DrawFromTo();
            EditorGUILayout.Space(4);
            DrawFindBar();
            EditorGUILayout.Space(2);
            DrawDivider();
            DrawMatches();
            DrawDivider();
            DrawApplyBar();
        }

        // -------- Search roots panel --------

        private void DrawSearchRoots() {
            EditorGUILayout.LabelField("Search in", EditorStyles.boldLabel);
            using (var scope = new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinHeight(72), GUILayout.MaxHeight(160))) {
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
                                _hasSearched = false;
                            }
                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) {
                                removeIndex = i;
                            }
                        }
                    }
                    if (removeIndex >= 0) {
                        _searchRoots.RemoveAt(removeIndex);
                        _hasSearched = false;
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(Style.UseSel)) {
                    _searchRoots = Selection.gameObjects.Where(g => g != null).Distinct().ToList();
                    _hasSearched = false;
                }
                if (GUILayout.Button(Style.AddSel)) {
                    foreach (var g in Selection.gameObjects) {
                        if (g != null && !_searchRoots.Contains(g)) _searchRoots.Add(g);
                    }
                    _hasSearched = false;
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Style.ClearList, GUILayout.Width(70))) {
                    _searchRoots.Clear();
                    _hasSearched = false;
                }
            }
        }

        // -------- From / To picker --------

        private void DrawFromTo() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField("From", GUILayout.Width(40));
                var newFrom = EditorGUILayout.ObjectField(_from, typeof(Object), allowSceneObjects: true);
                if (newFrom != _from) { _from = newFrom; _hasSearched = false; }
            }
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField("To",   GUILayout.Width(40));
                var newTo = EditorGUILayout.ObjectField(_to,   typeof(Object), allowSceneObjects: true);
                if (newTo != _to) { _to = newTo; }
            }
            if (_from != null && _to != null && _from.GetType() != _to.GetType()) {
                EditorGUILayout.HelpBox(
                    $"From is {_from.GetType().Name} but To is {_to.GetType().Name}. " +
                    "VRCFury fields are typed — assignments to incompatible field types will be skipped.",
                    MessageType.Warning);
            }
        }

        // -------- Find bar --------

        private void DrawFindBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canFind = _searchRoots.Count > 0 && _from != null && VrcfQol.Reflection.TryEnsure(out _);
                using (new EditorGUI.DisabledScope(!canFind)) {
                    if (GUILayout.Button(Style.FindBtn, GUILayout.Height(22))) FindMatches();
                }
                if (_hasSearched) {
                    GUILayout.Label(_searchSummary, EditorStyles.miniLabel);
                }
            }
        }

        // -------- Matches list --------

        private void DrawMatches() {
            using (new EditorGUILayout.HorizontalScope()) {
                int included = _matches.Count(m => m.Include);
                EditorGUILayout.LabelField(_hasSearched
                        ? $"Matches ({_matches.Count})  —  {included} selected"
                        : "Matches",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_matches.Count == 0)) {
                    if (GUILayout.Button(Style.CheckAll,   EditorStyles.miniButtonLeft,  GUILayout.Width(50)))
                        foreach (var m in _matches) m.Include = true;
                    if (GUILayout.Button(Style.UncheckAll, EditorStyles.miniButtonRight, GUILayout.Width(50)))
                        foreach (var m in _matches) m.Include = false;
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _matchesScroll = EditorGUILayout.BeginScrollView(_matchesScroll);
                if (!_hasSearched) {
                    EditorGUILayout.LabelField("Click Find Matches to scan.", EditorStyles.centeredGreyMiniLabel);
                } else if (_matches.Count == 0) {
                    EditorGUILayout.LabelField("No matches found.", EditorStyles.centeredGreyMiniLabel);
                } else {
                    foreach (var m in _matches) DrawMatchRow(m);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawMatchRow(Match m) {
            using (new EditorGUILayout.HorizontalScope()) {
                m.Include = EditorGUILayout.Toggle(m.Include, GUILayout.Width(18));
                using (new EditorGUILayout.VerticalScope()) {
                    EditorGUILayout.LabelField(m.HeaderText, EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(true)) {
                        EditorGUILayout.LabelField(m.PropertyPath, EditorStyles.miniLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField("Current:", GUILayout.Width(56));
                        EditorGUILayout.ObjectField(m.CurrentValue, typeof(Object), allowSceneObjects: true);
                    }
                }
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) {
                    if (m.VrcfComponent != null) EditorGUIUtility.PingObject(m.VrcfComponent);
                }
            }
            EditorGUILayout.Space(2);
        }

        // -------- Apply bar --------

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                int included = _matches.Count(m => m.Include);
                bool canApply = _hasSearched && included > 0 && _to != null;
                using (new EditorGUI.DisabledScope(!canApply)) {
                    if (GUILayout.Button(included > 0
                            ? $"Apply {included} Replacement{(included == 1 ? "" : "s")}"
                            : "Apply",
                        GUILayout.Height(24), GUILayout.MinWidth(180))) {
                        Apply();
                    }
                }
                using (new EditorGUI.DisabledScope(!_hasSearched)) {
                    if (GUILayout.Button(Style.Refresh, GUILayout.Height(24), GUILayout.Width(80)))
                        FindMatches();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Height(24), GUILayout.Width(80))) Close();
            }
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }

        // ---------------- Find ---------------------------------------------

        private void FindMatches() {
            _matches.Clear();
            _hasSearched = true;
            if (!VrcfQol.Reflection.TryEnsure(out var error)) {
                _searchSummary = error;
                return;
            }
            var r = VrcfQol.Reflection;
            if (_from == null) { _searchSummary = "Pick a 'From' object."; return; }

            int componentsScanned = 0;
            var seenComponents = new HashSet<Component>();
            foreach (var root in _searchRoots) {
                if (root == null) continue;
                foreach (var c in root.GetComponentsInChildren(r.VRCFuryType, true)) {
                    if (c == null || !seenComponents.Add(c)) continue;
                    componentsScanned++;
                    ScanComponent(c, _from);
                }
            }

            // Stable ordering for predictable UX: by GameObject path, then property path.
            _matches.Sort((a, b) => {
                int g = string.Compare(a.GameObjectPath, b.GameObjectPath, System.StringComparison.Ordinal);
                if (g != 0) return g;
                return string.Compare(a.PropertyPath, b.PropertyPath, System.StringComparison.Ordinal);
            });
            _searchSummary = $"Scanned {componentsScanned} VRCFury component(s) across {_searchRoots.Count(r => r != null)} root(s).";
        }

        private void ScanComponent(Component vrcf, Object needle) {
            using (var so = new SerializedObject(vrcf)) {
                var iter = so.GetIterator();
                if (!iter.NextVisible(true)) return;
                do {
                    if (iter.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (iter.objectReferenceValue != needle) continue;

                    _matches.Add(new Match {
                        VrcfComponent  = vrcf,
                        GameObjectPath = VrcfQol.GetGameObjectPath(vrcf.gameObject),
                        FeatureType    = GetEnclosingFeatureTypeName(so, iter.propertyPath),
                        PropertyPath   = iter.propertyPath,
                        CurrentValue   = iter.objectReferenceValue,
                    });
                } while (iter.NextVisible(true));
            }
        }

        // Walk up the property path until we find an ancestor that is a
        // [SerializeReference] field — that's the feature/sub-feature
        // containing this reference. Returns short type name (e.g.
        // "ArmatureLink", "Toggle"); falls back to "VRCFury" if nothing found.
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

        // ---------------- Apply --------------------------------------------

        private void Apply() {
            if (_to == null) return;
            var included = _matches.Where(m => m.Include && m.VrcfComponent != null).ToList();
            if (included.Count == 0) return;

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("VRCF QoL: Replace VRCFury references");

            int applied = 0;
            int skipped = 0;
            try {
                foreach (var byComp in included.GroupBy(m => m.VrcfComponent)) {
                    using (var so = new SerializedObject(byComp.Key)) {
                        bool anyChanged = false;
                        foreach (var m in byComp) {
                            var prop = so.FindProperty(m.PropertyPath);
                            if (prop == null) { skipped++; continue; }
                            if (prop.propertyType != SerializedPropertyType.ObjectReference) {
                                skipped++; continue;
                            }
                            // Snapshot the matched value so we don't blindly overwrite
                            // a property whose ref drifted between Find and Apply.
                            if (prop.objectReferenceValue != m.CurrentValue) { skipped++; continue; }
                            prop.objectReferenceValue = _to;
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

            // Re-scan so the panel reflects the new state (matches for the same
            // From should now be 0, plus any new matches appear if To references
            // happen to also exist on the search roots).
            FindMatches();
        }

        // ---------------- Match record ------------------------------------

        private sealed class Match {
            public Component VrcfComponent;
            public string GameObjectPath;
            public string FeatureType;
            public string PropertyPath;
            public Object CurrentValue;
            public bool Include = true;

            public string HeaderText => $"{GameObjectPath}  ▸  {FeatureType}";
        }
    }
}
