// ReplaceReferencesWindow.cs
//
// EditorWindow that lists every Object reference inside any VRCFury component
// on a set of selected GameObjects, GROUPED BY the underlying object so each
// unique reference appears once with one drop target. The user drags a
// replacement onto the rows they want to swap and clicks Apply — every
// occurrence of that object across the scan is replaced in a single Undo step.
//
// Per-selection "include children" toggles let you scan a GameObject without
// recursing into its descendants — useful when two avatars share a parent or
// when you want to pin the scan to a single component.
//
// Why SerializedObject + SerializedProperty.NextVisible(true) instead of raw
// reflection: VRCFury features are [SerializeReference] polymorphic graphs,
// and Unity's SerializedProperty already descends into them safely. This is
// also future-proof — if VRCFury renames internal fields but keeps them
// serialized, the walk still works because we only care about
// `propertyType == ObjectReference`.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    internal sealed class ReplaceReferencesWindow : EditorWindow {

        // Persisted across domain reloads so the search list survives a script
        // recompile while the window is open.
        [SerializeField] private List<SearchRoot> _searchRoots = new List<SearchRoot>();

        // Scan output. Groups are recomputed on every scan; replacements live
        // on each group and are dropped on rescan (intentional — a domain
        // reload may have invalidated the user's queued replacement target).
        private readonly List<RefGroup> _groups = new List<RefGroup>();
        private bool _hideUnchanged;
        private string _scanSummary = "";

        private Vector2 _rootsScroll;
        private Vector2 _groupsScroll;

        // ---------------- Public entry point ------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<ReplaceReferencesWindow>(false, "Replace References", true);
            w.titleContent = new GUIContent("Replace References (VRCFury QoL)");
            w.minSize = new Vector2(540, 400);
            if (prefillFromSelection) {
                w._searchRoots = Selection.gameObjects
                    .Where(g => g != null)
                    .Distinct()
                    .Select(g => new SearchRoot { GameObject = g, IncludeChildren = true })
                    .ToList();
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
            DrawGroups();
            DrawDivider();
            DrawApplyBar();
        }

        // -------- Search roots panel --------

        private void DrawSearchRoots() {
            EditorGUILayout.LabelField("Search in", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinHeight(60), GUILayout.MaxHeight(150))) {
                _rootsScroll = EditorGUILayout.BeginScrollView(_rootsScroll);
                if (_searchRoots.Count == 0) {
                    EditorGUILayout.LabelField("(empty — pick GameObjects in the hierarchy and click 'Use selection')",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    bool dirty = false;
                    for (int i = 0; i < _searchRoots.Count; i++) {
                        var entry = _searchRoots[i];
                        using (new EditorGUILayout.HorizontalScope()) {
                            var newGo = (GameObject)EditorGUILayout.ObjectField(
                                GUIContent.none, entry.GameObject, typeof(GameObject), allowSceneObjects: true);
                            if (newGo != entry.GameObject) { entry.GameObject = newGo; dirty = true; }

                            var newIncludeChildren = GUILayout.Toggle(entry.IncludeChildren,
                                new GUIContent("children",
                                    "Recurse into this GameObject's descendants when scanning.\nUntick to scan only this exact GameObject."),
                                EditorStyles.miniButton, GUILayout.Width(64));
                            if (newIncludeChildren != entry.IncludeChildren) {
                                entry.IncludeChildren = newIncludeChildren;
                                dirty = true;
                            }

                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) {
                                removeIndex = i;
                            }
                        }
                    }
                    if (removeIndex >= 0) { _searchRoots.RemoveAt(removeIndex); dirty = true; }
                    if (dirty) Rescan();
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Use selection",
                        "Replace the search list with the currently selected GameObjects."))) {
                    _searchRoots = Selection.gameObjects
                        .Where(g => g != null)
                        .Distinct()
                        .Select(g => new SearchRoot { GameObject = g, IncludeChildren = true })
                        .ToList();
                    Rescan();
                }
                if (GUILayout.Button(new GUIContent("Add selection",
                        "Add the currently selected GameObjects to the search list."))) {
                    foreach (var g in Selection.gameObjects) {
                        if (g == null) continue;
                        if (_searchRoots.Any(r => r.GameObject == g)) continue;
                        _searchRoots.Add(new SearchRoot { GameObject = g, IncludeChildren = true });
                    }
                    Rescan();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear", "Empty the search list."), GUILayout.Width(70))) {
                    _searchRoots.Clear();
                    _groups.Clear();
                    _scanSummary = "";
                }
            }
        }

        // -------- Groups list --------

        private void DrawGroups() {
            using (new EditorGUILayout.HorizontalScope()) {
                int queued = _groups.Count(g => g.HasReplacement);
                EditorGUILayout.LabelField(_groups.Count > 0
                        ? $"References ({_groups.Count} unique, {queued} queued)"
                        : "References",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                _hideUnchanged = GUILayout.Toggle(_hideUnchanged,
                    new GUIContent("Only queued", "Only show rows that have a replacement queued."),
                    EditorStyles.miniButton, GUILayout.Width(90));
            }
            if (!string.IsNullOrEmpty(_scanSummary)) {
                EditorGUILayout.LabelField(_scanSummary, EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _groupsScroll = EditorGUILayout.BeginScrollView(_groupsScroll);
                if (_searchRoots.Count == 0) {
                    EditorGUILayout.LabelField("Add GameObjects above to begin.", EditorStyles.centeredGreyMiniLabel);
                } else if (_groups.Count == 0) {
                    EditorGUILayout.LabelField("No object references found.", EditorStyles.centeredGreyMiniLabel);
                } else {
                    foreach (var g in _groups) {
                        if (_hideUnchanged && !g.HasReplacement) continue;
                        DrawGroupRow(g);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawGroupRow(RefGroup g) {
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(4);
                using (new EditorGUILayout.VerticalScope()) {
                    using (new EditorGUILayout.HorizontalScope()) {
                        var typeName = g.CurrentValue == null ? "Object" : g.CurrentValue.GetType().Name;
                        EditorGUILayout.LabelField(
                            $"{(g.CurrentValue == null ? "(null)" : g.CurrentValue.name)}  ({typeName})",
                            EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField(
                            g.Sites.Count == 1 ? "1 reference" : $"{g.Sites.Count} references",
                            EditorStyles.miniLabel, GUILayout.Width(110));
                    }
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField("Current", GUILayout.Width(64));
                        using (new EditorGUI.DisabledScope(true)) {
                            EditorGUILayout.ObjectField(g.CurrentValue, typeof(Object), allowSceneObjects: true);
                        }
                    }
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField(
                            new GUIContent("Replace", "Drop a replacement object here. Leave empty to keep current."),
                            GUILayout.Width(64));
                        var newReplacement = EditorGUILayout.ObjectField(
                            g.Replacement, typeof(Object), allowSceneObjects: true);
                        if (newReplacement != g.Replacement) g.Replacement = newReplacement;
                    }
                    if (g.Sites.Count > 1) {
                        g.Foldout = EditorGUILayout.Foldout(g.Foldout, $"Locations ({g.Sites.Count})", true);
                        if (g.Foldout) {
                            EditorGUI.indentLevel++;
                            foreach (var s in g.Sites) {
                                using (new EditorGUILayout.HorizontalScope()) {
                                    EditorGUILayout.LabelField(
                                        $"• {s.GameObjectPath} ▸ {s.FeatureType}",
                                        EditorStyles.miniLabel);
                                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) {
                                        if (s.VrcfComponent != null) EditorGUIUtility.PingObject(s.VrcfComponent);
                                    }
                                }
                                EditorGUILayout.LabelField($"   {s.PropertyPath}", EditorStyles.miniLabel);
                            }
                            EditorGUI.indentLevel--;
                        }
                    } else if (g.Sites.Count == 1) {
                        var s = g.Sites[0];
                        EditorGUILayout.LabelField(
                            $"{s.GameObjectPath}  ▸  {s.FeatureType}  —  {s.PropertyPath}",
                            EditorStyles.miniLabel);
                    }
                }
                if (GUILayout.Button(new GUIContent("Ping", "Highlight one of the VRCFury components in the hierarchy."),
                        EditorStyles.miniButton, GUILayout.Width(40))) {
                    var first = g.Sites.FirstOrDefault();
                    if (first != null && first.VrcfComponent != null)
                        EditorGUIUtility.PingObject(first.VrcfComponent);
                }
            }
            EditorGUILayout.Space(2);
            DrawSubtleDivider();
        }

        // -------- Apply bar --------

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                int queuedSites = _groups.Where(g => g.HasReplacement).Sum(g => g.Sites.Count);
                using (new EditorGUI.DisabledScope(queuedSites == 0)) {
                    if (GUILayout.Button(queuedSites > 0
                            ? $"Apply {queuedSites} Replacement{(queuedSites == 1 ? "" : "s")}"
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
            _groups.Clear();
            if (_searchRoots.Count == 0) { _scanSummary = ""; return; }

            if (!VrcfQol.Reflection.TryEnsure(out var error)) {
                _scanSummary = error;
                return;
            }
            var r = VrcfQol.Reflection;

            int componentsScanned = 0;
            int rootsActive = 0;
            var seenComponents = new HashSet<Component>();
            // Buffer all sites first, then bucket by CurrentValue. A dict
            // keyed by Object identity would be cleaner, but Unity's Object
            // overrides == in ways that make dict-keys fragile across
            // destruction; running a linear search at scan time is fine —
            // counts are tiny relative to property iteration cost.
            var sites = new List<RefSite>();
            foreach (var entry in _searchRoots) {
                var root = entry.GameObject;
                if (root == null) continue;
                rootsActive++;
                Component[] components;
                if (entry.IncludeChildren) {
                    components = root.GetComponentsInChildren(r.VRCFuryType, true);
                } else {
                    components = root.GetComponents(r.VRCFuryType);
                }
                foreach (var c in components) {
                    if (c == null || !seenComponents.Add(c)) continue;
                    componentsScanned++;
                    ScanComponent(c, sites);
                }
            }

            // Group by CurrentValue. Use SequenceEqual via reference identity
            // (Object's == handles the destroyed-object case).
            foreach (var s in sites) {
                var existing = _groups.FirstOrDefault(g => g.CurrentValue == s.CurrentValue);
                if (existing == null) {
                    existing = new RefGroup { CurrentValue = s.CurrentValue };
                    _groups.Add(existing);
                }
                existing.Sites.Add(s);
            }

            // Stable, predictable ordering: by current value type, then name,
            // then deterministic site path so two identical-looking groups
            // still sort the same way across rescans.
            _groups.Sort((a, b) => {
                int t = string.Compare(
                    a.CurrentValue == null ? "" : a.CurrentValue.GetType().Name,
                    b.CurrentValue == null ? "" : b.CurrentValue.GetType().Name,
                    System.StringComparison.Ordinal);
                if (t != 0) return t;
                int n = string.Compare(
                    a.CurrentValue == null ? "" : a.CurrentValue.name,
                    b.CurrentValue == null ? "" : b.CurrentValue.name,
                    System.StringComparison.Ordinal);
                if (n != 0) return n;
                return string.Compare(
                    a.Sites.Count > 0 ? a.Sites[0].PropertyPath : "",
                    b.Sites.Count > 0 ? b.Sites[0].PropertyPath : "",
                    System.StringComparison.Ordinal);
            });
            foreach (var g in _groups) {
                g.Sites.Sort((x, y) => string.Compare(x.PropertyPath, y.PropertyPath, System.StringComparison.Ordinal));
            }

            _scanSummary = $"Scanned {componentsScanned} VRCFury component(s) across {rootsActive} root(s).";
        }

        private static void ScanComponent(Component vrcf, List<RefSite> output) {
            using (var so = new SerializedObject(vrcf)) {
                var iter = so.GetIterator();
                if (!iter.NextVisible(true)) return;
                do {
                    if (iter.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var current = iter.objectReferenceValue;
                    if (current == null) continue;
                    if (iter.propertyPath == "m_Script") continue;

                    output.Add(new RefSite {
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

        // ---------------- Apply --------------------------------------------

        private void Apply() {
            var queuedGroups = _groups.Where(g => g.HasReplacement).ToList();
            if (queuedGroups.Count == 0) return;

            // Flatten to (component, propertyPath, expectedCurrent, replacement)
            // tuples grouped by component for one SerializedObject per component.
            var flat = queuedGroups
                .SelectMany(g => g.Sites.Select(s => (Site: s, Replacement: g.Replacement)))
                .Where(x => x.Site.VrcfComponent != null)
                .ToList();

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("VRCF QoL: Replace VRCFury references");

            int applied = 0;
            int skipped = 0;
            try {
                foreach (var byComp in flat.GroupBy(x => x.Site.VrcfComponent)) {
                    using (var so = new SerializedObject(byComp.Key)) {
                        bool anyChanged = false;
                        foreach (var x in byComp) {
                            var prop = so.FindProperty(x.Site.PropertyPath);
                            if (prop == null) { skipped++; continue; }
                            if (prop.propertyType != SerializedPropertyType.ObjectReference) {
                                skipped++; continue;
                            }
                            // Snapshot guard: if the current value drifted between
                            // scan and apply, refuse rather than overwrite something
                            // the user didn't see.
                            if (prop.objectReferenceValue != x.Site.CurrentValue) { skipped++; continue; }
                            prop.objectReferenceValue = x.Replacement;
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
            Debug.Log($"[VRCF QoL] Replaced {applied} reference(s) across {queuedGroups.Count} unique object(s)." + skipNote);

            // Re-scan: groups that were fully applied disappear (their old refs
            // no longer exist); the new value may itself become a group if it
            // overlaps with anything else on the scan roots.
            Rescan();
        }

        // ---------------- Records ------------------------------------------

        [System.Serializable]
        private sealed class SearchRoot {
            public GameObject GameObject;
            public bool IncludeChildren = true;
        }

        // A single property-path occurrence inside a VRCFury component.
        private sealed class RefSite {
            public Component VrcfComponent;
            public string GameObjectPath;
            public string FeatureType;
            public string PropertyPath;
            public Object CurrentValue;
        }

        // All the occurrences that share the same CurrentValue, plus the user's
        // queued replacement. The unit the UI displays.
        private sealed class RefGroup {
            public Object CurrentValue;
            public readonly List<RefSite> Sites = new List<RefSite>();
            public Object Replacement;
            public bool Foldout;

            public bool HasReplacement => Replacement != null && Replacement != CurrentValue;
        }
    }
}
