// MigrateIntoFlipbookTool.cs
// Registers a context-menu tool that appears when you right-click a Flipbook
// Builder action in a VRCFury Toggle's inspector. It scans the toggle's
// GameObject + descendants for other (non-flipbook) VRCFury Toggles, and folds
// each one into the flipbook as a new page. The source VRCFury components are
// deleted afterward.
//
// Right-click anywhere on a Flipbook Builder action (the dark-grey header row
// or its body) and choose "VRCF QoL / Migrate child toggles as pages".

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class MigrateIntoFlipbookTool {
        static MigrateIntoFlipbookTool() {
            VrcfQol.RegisterPropertyTool(
                label: "VRCF QoL/Migrate child toggles as pages",
                match: IsFlipbookBuilderAction,
                action: Run,
                priority: 10
            );
        }

        private static bool IsFlipbookBuilderAction(SerializedProperty prop) {
            if (prop == null) return false;
            if (prop.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return false;
            // managedReferenceFullTypename is "AssemblyName Fully.Qualified.TypeName".
            var expected = VrcfQol.Reflection.FlipbookBuilderActionType.FullName;
            var typeName = prop.managedReferenceFullTypename;
            return !string.IsNullOrEmpty(typeName) && typeName.EndsWith(" " + expected);
        }

        private static void Run(SerializedProperty prop) {
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                EditorUtility.DisplayDialog("Migrate Child Toggles", err, "OK"); return;
            }
            var r = VrcfQol.Reflection;

            var destVrcf = prop.serializedObject?.targetObject as Component;
            if (destVrcf == null || destVrcf.GetType() != r.VRCFuryType) {
                EditorUtility.DisplayDialog("Migrate Child Toggles",
                    "Could not resolve the parent VRCFury component.", "OK"); return;
            }

            var destContent = r.ContentField.GetValue(destVrcf);
            if (destContent == null || destContent.GetType() != r.ToggleType) {
                EditorUtility.DisplayDialog("Migrate Child Toggles",
                    "Parent VRCFury component does not hold a Toggle.", "OK"); return;
            }
            var destName = (string)r.ToggleNameField.GetValue(destContent) ?? "";
            var destState = r.ToggleStateField.GetValue(destContent);
            var destActions = r.StateActionsField.GetValue(destState) as IList;
            var flipbookAction = VrcfQol.FindFlipbookAction(destActions);
            if (flipbookAction == null) {
                EditorUtility.DisplayDialog("Migrate Child Toggles",
                    "This Toggle no longer contains a Flipbook Builder action.", "OK"); return;
            }

            // Scope: the destination's own GameObject + all descendants.
            var root = destVrcf.gameObject;
            var sources = new List<SourceInfo>();
            foreach (Component c in root.GetComponentsInChildren(r.VRCFuryType, true)) {
                if (c == null || c == destVrcf) continue;
                var content = r.ContentField.GetValue(c);
                if (content == null || content.GetType() != r.ToggleType) continue;
                var state = r.ToggleStateField.GetValue(content);
                var actions = r.StateActionsField.GetValue(state) as IList;
                if (VrcfQol.FindFlipbookAction(actions) != null) continue; // skip other flipbooks
                sources.Add(new SourceInfo {
                    component = c,
                    state = state,
                    name = (string)r.ToggleNameField.GetValue(content) ?? "",
                });
            }

            if (sources.Count == 0) {
                EditorUtility.DisplayDialog("Migrate Child Toggles",
                    $"No non-flipbook VRCFury toggles found under '{root.name}'.", "OK");
                return;
            }

            var pages = r.PagesField.GetValue(flipbookAction) as IList;

            var preview = new StringBuilder();
            preview.AppendLine($"Destination flipbook: {(string.IsNullOrEmpty(destName) ? "(unnamed)" : destName)}");
            preview.AppendLine($"  ({pages.Count} existing page(s) — preserved; migrated toggles appended after)");
            preview.AppendLine();
            preview.AppendLine($"{sources.Count} toggle(s) will be migrated in hierarchy order:");
            foreach (var s in sources) {
                preview.AppendLine("  • " + (string.IsNullOrEmpty(s.name) ? "(unnamed)" : s.name) +
                                   "  [" + VrcfQol.GetGameObjectPath(s.component.gameObject) + "]");
            }
            preview.AppendLine();
            preview.AppendLine("Source VRCFury components will be DELETED (Undo restores everything).");

            if (!EditorUtility.DisplayDialog("Migrate Child Toggles Into Flipbook", preview.ToString(), "Migrate", "Cancel"))
                return;

            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("VRCF QoL: Migrate toggles into flipbook");
            try {
                Undo.RegisterCompleteObjectUndo(destVrcf, "Append migrated pages to flipbook");
                foreach (var s in sources) {
                    var newPage = Activator.CreateInstance(r.FlipbookPageType);
                    // Reuse the source's State — source VRCFury component is about to be destroyed.
                    r.PageStateField.SetValue(newPage, s.state);
                    pages.Add(newPage);
                }
                EditorUtility.SetDirty(destVrcf);
                foreach (var s in sources) {
                    if (s.component != null) Undo.DestroyObjectImmediate(s.component);
                }
                Undo.CollapseUndoOperations(group);
                Debug.Log($"[VRCF QoL] Migrated {sources.Count} toggles into flipbook '{destName}'. " +
                          $"Flipbook now has {pages.Count} page(s).");
            } catch (Exception e) {
                Debug.LogException(e);
                Undo.RevertAllInCurrentGroup();
                EditorUtility.DisplayDialog("Migrate Child Toggles",
                    "Migration failed; changes reverted.\n\n" + e.Message, "OK");
            }
        }

        private struct SourceInfo {
            public Component component;
            public object state;
            public string name;
        }
    }
}
