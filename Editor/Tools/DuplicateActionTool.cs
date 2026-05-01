// DuplicateActionTool.cs
//
// Right-click any State Action inside a VRCFury Toggle (or inside a flipbook
// page's state) and pick "VRCF QoL/Duplicate this action". The action gets
// deep-cloned via the same JsonUtility round-trip the page-duplicator uses,
// then inserted right below itself in the enclosing actions list.
//
// Works at any depth, because the path resolver walks the SerializedProperty
// path with reflection. So it handles both:
//   - Top-level Toggle actions:      content.state.actions.Array.data[N]
//   - Flipbook page actions:         content.state.actions.Array.data[X].pages.Array.data[Y].state.actions.Array.data[N]
// (and any future deeper nesting VRCFury introduces, as long as the field
// names "state", "actions", "pages" stay the same).

using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class DuplicateActionTool {

        // Anchored at end-of-string, so the deepest .actions.Array.data[N] in
        // a nested path is the one we operate on. That's exactly what we want
        // when right-clicking an action inside a flipbook page.
        private static readonly Regex ActionTailRegex = new Regex(
            @"\.actions\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);

        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static DuplicateActionTool() {
            VrcfQol.RegisterPropertyTool(
                label: "VRCF QoL/Duplicate this action",
                match: prop => {
                    if (prop == null) return false;
                    if (prop.propertyType != SerializedPropertyType.ManagedReference) return false;
                    if (string.IsNullOrEmpty(prop.propertyPath)) return false;
                    return ActionTailRegex.IsMatch(prop.propertyPath);
                },
                action: Run,
                priority: 15
            );
        }

        private static void Run(SerializedProperty prop) {
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                EditorUtility.DisplayDialog("Duplicate Action", err, "OK");
                return;
            }
            var component = prop.serializedObject?.targetObject as Component;
            if (component == null) {
                EditorUtility.DisplayDialog("Duplicate Action",
                    "Could not resolve the parent VRCFury component.", "OK");
                return;
            }
            if (!TryResolveActionList(component, prop.propertyPath, out var list, out var index)) {
                EditorUtility.DisplayDialog("Duplicate Action",
                    "Could not resolve the actions list. The VRCFury layout may have changed.", "OK");
                return;
            }
            var src = list[index];
            if (src == null) {
                EditorUtility.DisplayDialog("Duplicate Action",
                    "The selected action is null and can't be duplicated.", "OK");
                return;
            }

            try {
                Undo.RegisterCompleteObjectUndo(component, "Duplicate VRCFury action");
                var json = JsonUtility.ToJson(src);
                var clone = JsonUtility.FromJson(json, src.GetType());
                list.Insert(index + 1, clone);
                EditorUtility.SetDirty(component);
                Debug.Log($"[VRCF QoL] Duplicated action #{index + 1} ({src.GetType().Name}) " +
                          $"in place (now at #{index + 2}).");
            } catch (Exception ex) {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Duplicate Action",
                    "Duplication failed. See Console.\n\n" + ex.Message, "OK");
            }
        }

        // ------ Path resolution -------------------------------------------

        // Resolve the actions IList containing `propertyPath`'s tail action.
        // propertyPath is something like "content.state.actions.Array.data[N]"
        // or "content.state.actions.Array.data[X].pages.Array.data[Y].state.actions.Array.data[N]".
        // Strips the trailing ".Array.data[N]" and walks the remaining path
        // with reflection to find the IList.
        private static bool TryResolveActionList(Component component, string propertyPath,
                                                  out IList list, out int index) {
            list = null; index = -1;
            if (string.IsNullOrEmpty(propertyPath)) return false;
            var tail = ActionTailRegex.Match(propertyPath);
            if (!tail.Success) return false;
            if (!int.TryParse(tail.Groups[1].Value, out index)) return false;
            // Path up to but not including the trailing ".Array.data[N]" — i.e.
            // the path of the actions IList itself.
            var listPath = propertyPath.Substring(0, tail.Index) + ".actions";
            // Sanity: that string starts with "." for nested cases since we left
            // the leading dot of ".actions.Array.data[N]" out of substring.
            // For the top-level case (path begins with "content."), the substring
            // already includes the leading segments without a leading dot. Either
            // way, TrimStart('.') normalises.
            var resolved = Walk(component, listPath.TrimStart('.'));
            list = resolved as IList;
            if (list == null || index < 0 || index >= list.Count) {
                list = null; index = -1; return false;
            }
            return true;
        }

        // Walk a SerializedProperty-style path against a runtime object graph
        // using reflection. Recognises ".Array.data[N]" subscripts on IList
        // fields. Returns null on any miss.
        internal static object Walk(object root, string path) {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            // Normalise array subscript: ".Array.data[N]" -> "[N]"
            var normalised = Regex.Replace(path, @"\.Array\.data\[(\d+)\]", "[$1]");
            object current = root;
            foreach (var raw in normalised.Split('.')) {
                if (string.IsNullOrEmpty(raw) || current == null) continue;
                string name; int idx;
                var m = Regex.Match(raw, @"^(.+?)\[(\d+)\]$");
                if (m.Success) {
                    name = m.Groups[1].Value;
                    idx = int.Parse(m.Groups[2].Value);
                } else {
                    name = raw;
                    idx = -1;
                }
                var field = FindFieldInHierarchy(current.GetType(), name);
                if (field == null) return null;
                current = field.GetValue(current);
                if (idx >= 0) {
                    if (!(current is IList list)) return null;
                    if (idx < 0 || idx >= list.Count) return null;
                    current = list[idx];
                }
            }
            return current;
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string name) {
            while (type != null) {
                var f = type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                type = type.BaseType;
            }
            return null;
        }
    }
}
