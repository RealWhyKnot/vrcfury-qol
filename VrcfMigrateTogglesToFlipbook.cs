// VrcfMigrateTogglesToFlipbook.cs
// Editor-only tool that migrates non-flipbook VRCFury Toggles into a destination
// flipbook Toggle, as new pages inside its Flipbook Builder action.
//
// How it works:
//  1. Select the GameObject that holds the destination flipbook toggle (or any
//     ancestor of it). The script scans that GameObject + all descendants.
//  2. It finds exactly ONE VRCFury Toggle whose state contains a FlipBookBuilder
//     action — this is the destination.
//  3. Every OTHER VRCFury Toggle in the same subtree (that is NOT a flipbook) is
//     migrated: its entire state is copied into a new FlipBookPage appended to
//     the destination's flipbook, and the source VRCFury component is deleted.
//  4. Pages are appended in hierarchy / sibling order.
//
// Everything is done via reflection because VF.Model.* types are `internal`.
// Operations are wrapped in Undo groups, so Ctrl+Z reverts the whole migration.
//
// Place this file anywhere under Assets/Editor/ (the folder name "Editor" is
// what matters — Unity will compile it into an editor-only assembly).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfTools {
    internal static class VrcfMigrateTogglesToFlipbook {
        private const string MenuPath = "Tools/VRCFury/Migrate Toggles Into Flipbook";

        // Reflection handles, resolved lazily on first use
        private static Assembly _vrcfAsm;
        private static Type _tVRCFury;
        private static Type _tToggle;
        private static Type _tState;
        private static Type _tFlipbookBuilderAction;
        private static Type _tFlipbookPage;
        private static FieldInfo _fContent;       // VRCFury.content
        private static FieldInfo _fToggleName;    // Toggle.name
        private static FieldInfo _fToggleState;   // Toggle.state
        private static FieldInfo _fToggleSlider;  // Toggle.slider
        private static FieldInfo _fStateActions;  // State.actions
        private static FieldInfo _fPages;         // FlipBookBuilderAction.pages
        private static FieldInfo _fPageState;    // FlipBookPage.state

        [MenuItem(MenuPath, false, 100)]
        private static void Run() {
            if (!TryResolveTypes(out var err)) {
                EditorUtility.DisplayDialog("Migrate Toggles Into Flipbook", err, "OK");
                return;
            }

            var root = Selection.activeGameObject;
            if (root == null) {
                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    "Select a GameObject first.\n\nUsually this is the GameObject that holds the destination flipbook toggle, " +
                    "or an ancestor of it (the script scans the selection + all descendants).",
                    "OK");
                return;
            }

            // All VRCFury components in the subtree, in hierarchy order.
            var vrcfs = root.GetComponentsInChildren(_tVRCFury, true)
                .Cast<Component>()
                .Where(c => c != null)
                .ToList();

            // Split into destination(s) and migratable sources.
            var destinations = new List<ToggleRef>();
            var sources = new List<ToggleRef>();

            foreach (var c in vrcfs) {
                var content = _fContent.GetValue(c);
                if (content == null || content.GetType() != _tToggle) continue;

                var toggle = content;
                var state = _fToggleState.GetValue(toggle);
                var actions = _fStateActions.GetValue(state) as IList;
                var flipbookAction = FindFlipbookAction(actions);

                var name = (string)_fToggleName.GetValue(toggle) ?? "";
                var tref = new ToggleRef {
                    component = c,
                    toggle = toggle,
                    state = state,
                    menuPath = name,
                    flipbookAction = flipbookAction,
                };

                if (flipbookAction != null) destinations.Add(tref);
                else sources.Add(tref);
            }

            if (destinations.Count == 0) {
                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    $"No destination flipbook toggle found under '{root.name}'.\n\n" +
                    "A destination is a VRCFury Toggle whose state contains a Flipbook Builder action. " +
                    "Add one (with the slider enabled and an empty Flipbook Builder) before running this tool.",
                    "OK");
                return;
            }

            if (destinations.Count > 1) {
                var list = string.Join("\n", destinations.Select(d => " • " + (string.IsNullOrEmpty(d.menuPath) ? "(unnamed)" : d.menuPath) + "  [" + GetGameObjectPath(d.component.gameObject) + "]"));
                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    $"Found {destinations.Count} destination flipbook toggles in this subtree:\n\n{list}\n\n" +
                    "Narrow the selection so the script can see only one destination, then try again.",
                    "OK");
                return;
            }

            var dest = destinations[0];

            if (sources.Count == 0) {
                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    $"Destination found: '{dest.menuPath}'.\n\nBut no non-flipbook source toggles were found in this subtree.",
                    "OK");
                return;
            }

            // Preview & confirm.
            var preview = new StringBuilder();
            preview.AppendLine("Destination flipbook:");
            preview.AppendLine($"  {(string.IsNullOrEmpty(dest.menuPath) ? "(unnamed)" : dest.menuPath)}");
            preview.AppendLine($"  [{GetGameObjectPath(dest.component.gameObject)}]");
            var existingPages = _fPages.GetValue(dest.flipbookAction) as IList;
            preview.AppendLine($"  (existing pages: {existingPages.Count} — will be preserved; migrated toggles appended after)");
            preview.AppendLine();
            preview.AppendLine($"{sources.Count} toggle(s) will be migrated (in hierarchy order):");
            foreach (var s in sources) {
                preview.AppendLine($"  • {(string.IsNullOrEmpty(s.menuPath) ? "(unnamed)" : s.menuPath)}  [{GetGameObjectPath(s.component.gameObject)}]");
            }
            preview.AppendLine();
            preview.AppendLine("Source VRCFury components will be DELETED after migration (Undo will restore everything).");

            if (!EditorUtility.DisplayDialog("Migrate Toggles Into Flipbook", preview.ToString(), "Migrate", "Cancel")) {
                return;
            }

            // Perform migration with an Undo group.
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Migrate VRCFury Toggles Into Flipbook");
            try {
                // Record the destination component so changes to its serialized fields are undoable.
                Undo.RegisterCompleteObjectUndo(dest.component, "Migrate Toggles: mutate flipbook");

                var pages = _fPages.GetValue(dest.flipbookAction) as IList;
                foreach (var s in sources) {
                    // Build new FlipBookPage with the source toggle's state.
                    var newPage = Activator.CreateInstance(_tFlipbookPage);
                    _fPageState.SetValue(newPage, s.state); // reuse the existing State object — it has all the actions
                    pages.Add(newPage);
                }

                // Force serialization of the mutated destination so Unity sees the changes.
                EditorUtility.SetDirty(dest.component);

                // Destroy source VRCFury components (but not their GameObjects).
                foreach (var s in sources) {
                    if (s.component != null) {
                        Undo.DestroyObjectImmediate(s.component);
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);

                Debug.Log($"[VRCFury Migrate] Migrated {sources.Count} toggle(s) into flipbook '{dest.menuPath}'. " +
                          $"Pages in flipbook now: {pages.Count}. Source VRCFury components removed.");

                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    $"Done!\n\nMigrated {sources.Count} toggle(s) into '{dest.menuPath}'.\n" +
                    $"The flipbook now has {pages.Count} page(s).\n\n" +
                    "Save the scene to persist the change. Ctrl+Z reverts everything.",
                    "OK");
            } catch (Exception e) {
                Debug.LogException(e);
                Undo.RevertAllInCurrentGroup();
                EditorUtility.DisplayDialog(
                    "Migrate Toggles Into Flipbook",
                    "Migration failed; changes reverted. See Console for details:\n\n" + e.Message,
                    "OK");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool RunValidate() {
            return Selection.activeGameObject != null;
        }

        private struct ToggleRef {
            public Component component;  // the VRCFury MonoBehaviour
            public object toggle;         // VF.Model.Feature.Toggle
            public object state;          // VF.Model.State
            public string menuPath;       // Toggle.name
            public object flipbookAction; // FlipBookBuilderAction or null
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
            if (_tVRCFury != null) return true; // already resolved

            _vrcfAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "VRCFury");
            if (_vrcfAsm == null) {
                error = "VRCFury runtime assembly ('VRCFury') was not found. Is VRCFury installed in this project?";
                return false;
            }

            _tVRCFury = _vrcfAsm.GetType("VF.Model.VRCFury", false);
            _tToggle = _vrcfAsm.GetType("VF.Model.Feature.Toggle", false);
            _tState = _vrcfAsm.GetType("VF.Model.State", false);
            _tFlipbookBuilderAction = _vrcfAsm.GetType("VF.Model.StateAction.FlipBookBuilderAction", false);
            if (_tVRCFury == null || _tToggle == null || _tState == null || _tFlipbookBuilderAction == null) {
                error = "Failed to locate one or more VRCFury internal types. The VRCFury API may have changed; this script is written against VRCFury 1.1303.x-ish layout.";
                return false;
            }
            _tFlipbookPage = _tFlipbookBuilderAction.GetNestedType("FlipBookPage", BindingFlags.Public | BindingFlags.NonPublic);
            if (_tFlipbookPage == null) {
                error = "Failed to locate FlipBookBuilderAction.FlipBookPage nested type.";
                return false;
            }

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fContent = _tVRCFury.GetField("content", any);
            _fToggleName = _tToggle.GetField("name", any);
            _fToggleState = _tToggle.GetField("state", any);
            _fToggleSlider = _tToggle.GetField("slider", any);
            _fStateActions = _tState.GetField("actions", any);
            _fPages = _tFlipbookBuilderAction.GetField("pages", any);
            _fPageState = _tFlipbookPage.GetField("state", any);

            if (_fContent == null || _fToggleName == null || _fToggleState == null ||
                _fStateActions == null || _fPages == null || _fPageState == null) {
                error = "Failed to locate expected fields on VRCFury model types. The VRCFury API may have changed.";
                _tVRCFury = null; // force re-resolve next time
                return false;
            }

            return true;
        }
    }
}
               