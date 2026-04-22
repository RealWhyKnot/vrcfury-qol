// DuplicateFlipbookPageTool.cs
// Registers a context-menu tool that duplicates a VRCFury Flipbook Builder page
// and appends the copy to the end of the flipbook. Page #7 -> new page #N+1.
//
// Right-click the "Page #N" field inside a Flipbook Builder and choose
// "VRCF QoL / Duplicate page to end".

using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class DuplicateFlipbookPageTool {
        // FlipBookPage lives inside FlipBookBuilderAction.pages (a List<FlipBookPage>).
        // SerializedProperty path will end with ".pages.Array.data[<n>]".
        private static readonly Regex PagePathRegex = new Regex(@"\.pages\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);

        static DuplicateFlipbookPageTool() {
            VrcfQol.RegisterPropertyTool(
                label: "VRCF QoL/Duplicate page to end",
                match: IsFlipbookPage,
                action: Duplicate,
                priority: 10
            );
        }

        private static bool IsFlipbookPage(SerializedProperty prop) {
            if (prop == null) return false;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return false;
            return PagePathRegex.IsMatch(prop.propertyPath);
        }

        private static void Duplicate(SerializedProperty prop) {
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                EditorUtility.DisplayDialog("Duplicate Page", err, "OK"); return;
            }

            var match = PagePathRegex.Match(prop.propertyPath);
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out var sourceIndex)) return;

            var target = prop.serializedObject?.targetObject as Component;
            if (target == null) {
                EditorUtility.DisplayDialog("Duplicate Page", "Target VRCFury component not resolvable.", "OK"); return;
            }

            // Walk from the VRCFury component down to the FlipBookBuilderAction that
            // contains `pages`. We can't just eval the SerializedProperty's parent
            // because its parent is an array element, not the action itself.
            var r = VrcfQol.Reflection;
            var content = r.ContentField.GetValue(target);
            if (content == null || content.GetType() != r.ToggleType) {
                EditorUtility.DisplayDialog("Duplicate Page", "Parent component isn't a VRCFury Toggle.", "OK"); return;
            }
            var toggleState = r.ToggleStateField.GetValue(content);
            var toggleActions = r.StateActionsField.GetValue(toggleState) as IList;
            var flipbookAction = VrcfQol.FindFlipbookAction(toggleActions);
            if (flipbookAction == null) {
                EditorUtility.DisplayDialog("Duplicate Page", "Could not locate the parent Flipbook Builder action.", "OK"); return;
            }

            var pages = r.PagesField.GetValue(flipbookAction) as IList;
            if (pages == null || sourceIndex < 0 || sourceIndex >= pages.Count) {
                EditorUtility.DisplayDialog("Duplicate Page", $"Page #{sourceIndex + 1} not found.", "OK"); return;
            }

            try {
                Undo.RegisterCompleteObjectUndo(target, $"Duplicate flipbook page #{sourceIndex + 1}");
                var clone = DeepClonePage(pages[sourceIndex]);
                pages.Add(clone);
                EditorUtility.SetDirty(target);
                Debug.Log($"[VRCF QoL] Duplicated flipbook page #{sourceIndex + 1} as page #{pages.Count}.");
            } catch (Exception e) {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Duplicate Page", "Duplication failed; see Console.\n\n" + e.Message, "OK");
            }
        }

        /// <summary>
        /// Deep-clone a FlipBookPage. Creates fresh Page/State/Action instances;
        /// JsonUtility preserves UnityEngine.Object references (GameObjects,
        /// Renderers, clips) by instance id while deep-copying value fields.
        /// </summary>
        internal static object DeepClonePage(object sourcePage) {
            var r = VrcfQol.Reflection;
            var srcState = r.PageStateField.GetValue(sourcePage);
            var srcActions = r.StateActionsField.GetValue(srcState) as IList;

            var listType = r.StateActionsField.FieldType;
            var newActions = (IList)Activator.CreateInstance(listType);
            if (srcActions != null) {
                foreach (var action in srcActions) {
                    if (action == null) { newActions.Add(null); continue; }
                    var json = JsonUtility.ToJson(action);
                    var clone = JsonUtility.FromJson(json, action.GetType());
                    newActions.Add(clone);
                }
            }

            var newState = Activator.CreateInstance(r.StateType);
            r.StateActionsField.SetValue(newState, newActions);

            var newPage = Activator.CreateInstance(r.FlipbookPageType);
            r.PageStateField.SetValue(newPage, newState);
            return newPage;
        }
    }
}
