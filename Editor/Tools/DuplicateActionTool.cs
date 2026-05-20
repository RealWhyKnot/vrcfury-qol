// DuplicateActionTool.cs
//
// Right-click any State Action inside a VRCFury Toggle (or inside a flipbook
// page's state) and pick "WhyKnot/vrcfury-qol/Duplicate this action". The action gets
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

        private static readonly Regex FlipbookPageActionRegex = new Regex(
            @"\.pages\.Array\.data\[(\d+)\]\.state\.actions\.Array\.data\[(\d+)\]$",
            RegexOptions.Compiled);

        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal struct FlipbookActionCopyInfo {
            public int SourcePageIndex;
            public int ActionIndex;
            public int PageCount;
        }

        static DuplicateActionTool() {
            VrcfQol.RegisterPropertyTool(
                label: "WhyKnot/vrcfury-qol/Duplicate this action",
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
            var component = prop.serializedObject?.targetObject as Component;
            if (!TryDuplicate(component, prop.propertyPath, out var error)) {
                EditorUtility.DisplayDialog("Duplicate Action", error, "OK");
            }
        }

        internal static bool TryDuplicate(Component component, string propertyPath, out string error) {
            error = null;
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                error = err;
                return false;
            }
            if (component == null) {
                error = "Could not resolve the parent VRCFury component.";
                return false;
            }
            if (!TryResolveActionList(component, propertyPath, out var list, out var index)) {
                error = "Could not resolve the actions list. The VRCFury layout may have changed.";
                return false;
            }
            var src = list[index];
            if (src == null) {
                error = "The selected action is null and can't be duplicated.";
                return false;
            }

            try {
                Undo.RegisterCompleteObjectUndo(component, "Duplicate VRCFury action");
                var clone = CloneAction(src);
                list.Insert(index + 1, clone);
                EditorUtility.SetDirty(component);
                Debug.Log($"[VRCF QoL] Duplicated action #{index + 1} ({src.GetType().Name}) " +
                          $"in place (now at #{index + 2}).");
                return true;
            } catch (Exception ex) {
                Debug.LogException(ex);
                error = "Duplication failed. See Console.\n\n" + ex.Message;
                return false;
            }
        }

        internal static bool TryGetFlipbookActionCopyInfo(
            Component component,
            string propertyPath,
            out FlipbookActionCopyInfo info,
            out string error) {
            info = default;
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                error = err;
                return false;
            }
            if (!TryResolveFlipbookAction(component, propertyPath,
                    out _, out var actionIndex, out var pages, out var sourcePageIndex, out error)) {
                return false;
            }

            info = new FlipbookActionCopyInfo {
                SourcePageIndex = sourcePageIndex,
                ActionIndex = actionIndex,
                PageCount = pages.Count,
            };
            return true;
        }

        internal static bool TryDuplicateToFlipbookPage(
            Component component,
            string propertyPath,
            int targetPageIndex,
            out string error) {
            error = null;
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                error = err;
                return false;
            }
            if (!TryResolveFlipbookAction(component, propertyPath,
                    out var sourceActions, out var actionIndex, out var pages, out var sourcePageIndex, out error)) {
                return false;
            }
            if (targetPageIndex < 0 || targetPageIndex >= pages.Count) {
                error = $"Page #{targetPageIndex + 1} was not found.";
                return false;
            }

            var src = sourceActions[actionIndex];
            if (src == null) {
                error = "The selected action is null and can't be duplicated.";
                return false;
            }

            try {
                var targetActions = EnsureActionListOnPage(pages[targetPageIndex]);
                if (targetActions == null) {
                    error = $"Could not resolve the action list on page #{targetPageIndex + 1}.";
                    return false;
                }

                Undo.RegisterCompleteObjectUndo(component, "Duplicate VRCFury action to flipbook page");
                var clone = CloneAction(src);
                targetActions.Add(clone);
                EditorUtility.SetDirty(component);
                Debug.Log($"[VRCF QoL] Duplicated action #{actionIndex + 1} ({src.GetType().Name}) " +
                          $"from page #{sourcePageIndex + 1} to page #{targetPageIndex + 1}.");
                return true;
            } catch (Exception ex) {
                Debug.LogException(ex);
                error = "Duplication failed. See Console.\n\n" + ex.Message;
                return false;
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

        private static bool TryResolveFlipbookAction(
            Component component,
            string propertyPath,
            out IList sourceActions,
            out int actionIndex,
            out IList pages,
            out int sourcePageIndex,
            out string error) {
            sourceActions = null;
            actionIndex = -1;
            pages = null;
            sourcePageIndex = -1;
            error = null;

            if (component == null) {
                error = "Could not resolve the parent VRCFury component.";
                return false;
            }
            if (string.IsNullOrEmpty(propertyPath)) {
                error = "Could not resolve this action row.";
                return false;
            }

            var pageMatch = FlipbookPageActionRegex.Match(propertyPath);
            if (!pageMatch.Success ||
                !int.TryParse(pageMatch.Groups[1].Value, out sourcePageIndex) ||
                !int.TryParse(pageMatch.Groups[2].Value, out actionIndex)) {
                error = "This action is not inside a flipbook page.";
                return false;
            }

            if (!TryResolveActionList(component, propertyPath, out sourceActions, out actionIndex)) {
                error = "Could not resolve the source action list. The VRCFury layout may have changed.";
                return false;
            }

            var flipbookPath = propertyPath.Substring(0, pageMatch.Index);
            var pagesPath = (flipbookPath + ".pages").TrimStart('.');
            pages = Walk(component, pagesPath) as IList;
            if (pages == null || sourcePageIndex < 0 || sourcePageIndex >= pages.Count) {
                pages = null;
                error = "Could not resolve the flipbook pages list. The VRCFury layout may have changed.";
                return false;
            }

            return true;
        }

        private static IList EnsureActionListOnPage(object page) {
            if (page == null || !VrcfQol.Reflection.TryEnsure(out _)) return null;
            var r = VrcfQol.Reflection;
            var state = r.PageStateField.GetValue(page);
            if (state == null) {
                state = Activator.CreateInstance(r.StateType);
                r.PageStateField.SetValue(page, state);
            }

            var actions = r.StateActionsField.GetValue(state) as IList;
            if (actions == null) {
                actions = (IList)Activator.CreateInstance(r.StateActionsField.FieldType);
                r.StateActionsField.SetValue(state, actions);
            }
            return actions;
        }

        private static object CloneAction(object src) {
            var json = JsonUtility.ToJson(src);
            return JsonUtility.FromJson(json, src.GetType());
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
