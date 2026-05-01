// DuplicateFlipbookPageTool.cs
// Page-level operations for VRCFury's Flipbook Builder. All variants live
// here because they share the FlipbookContext resolver and the same Undo /
// SetDirty boilerplate.
//
// Inline buttons (rendered next to every "Page #N" label by the inspector
// overlay):
//   • Dup ↓     — duplicate this page right below itself
//   • Insert ↓  — insert a new empty page right below this one
//
// Right-click menu (on a Flipbook page row):
//   • VRCF QoL/Duplicate page below       (priority 25)
//   • VRCF QoL/Insert empty page below    (priority 24)
//   • VRCF QoL/Duplicate page to end      (priority 20, original)
//
// "Below" semantics: the new page goes at index + 1 in the same flipbook.
// Existing pages from index + 1 onward shift down.

using System;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class DuplicateFlipbookPageTool {

        static DuplicateFlipbookPageTool() {
            VrcfQol.RegisterFlipbookPageTool(
                label: "VRCF QoL/Duplicate page below",
                action: DuplicateBelow,
                priority: 25
            );
            VrcfQol.RegisterFlipbookPageTool(
                label: "VRCF QoL/Insert empty page below",
                action: InsertEmptyBelow,
                priority: 24
            );
            VrcfQol.RegisterFlipbookPageTool(
                label: "VRCF QoL/Duplicate page to end",
                action: DuplicateToEnd,
                priority: 20
            );

            VrcfQol.RegisterFlipbookPageButton(
                text: "Dup ↓",
                tooltip: "VRCF QoL: clone this page and insert the copy right below it.",
                onClick: DuplicateBelow,
                order: 0
            );
            VrcfQol.RegisterFlipbookPageButton(
                text: "Insert ↓",
                tooltip: "VRCF QoL: insert a new, empty page right below this one.",
                onClick: InsertEmptyBelow,
                order: 1
            );
        }

        // ------ Actions ----------------------------------------------------

        private static void DuplicateBelow(VrcfQol.FlipbookContext ctx) {
            if (!ValidateContext(ctx, "Duplicate Page Below", out _)) return;
            try {
                Undo.RegisterCompleteObjectUndo(ctx.vrcfComponent,
                    $"Duplicate flipbook page #{ctx.pageIndex + 1} below");
                var clone = VrcfQol.DeepClonePage(ctx.pages[ctx.pageIndex]);
                ctx.pages.Insert(ctx.pageIndex + 1, clone);
                EditorUtility.SetDirty(ctx.vrcfComponent);
                Debug.Log($"[VRCF QoL] Duplicated flipbook page #{ctx.pageIndex + 1} " +
                          $"as new page #{ctx.pageIndex + 2}.");
            } catch (Exception ex) { Fail("Duplicate Page Below", ex); }
        }

        private static void InsertEmptyBelow(VrcfQol.FlipbookContext ctx) {
            if (!ValidateContext(ctx, "Insert Empty Page Below", out _)) return;
            try {
                Undo.RegisterCompleteObjectUndo(ctx.vrcfComponent,
                    $"Insert empty flipbook page after #{ctx.pageIndex + 1}");
                var blank = CreateEmptyPage();
                if (blank == null) {
                    EditorUtility.DisplayDialog("Insert Empty Page Below",
                        "Could not construct a fresh FlipBookPage. The VRCFury API may have changed.", "OK");
                    return;
                }
                ctx.pages.Insert(ctx.pageIndex + 1, blank);
                EditorUtility.SetDirty(ctx.vrcfComponent);
                Debug.Log($"[VRCF QoL] Inserted empty flipbook page at #{ctx.pageIndex + 2}.");
            } catch (Exception ex) { Fail("Insert Empty Page Below", ex); }
        }

        private static void DuplicateToEnd(VrcfQol.FlipbookContext ctx) {
            if (!ValidateContext(ctx, "Duplicate Page", out _)) return;
            try {
                Undo.RegisterCompleteObjectUndo(ctx.vrcfComponent,
                    $"Duplicate flipbook page #{ctx.pageIndex + 1}");
                var clone = VrcfQol.DeepClonePage(ctx.pages[ctx.pageIndex]);
                ctx.pages.Add(clone);
                EditorUtility.SetDirty(ctx.vrcfComponent);
                Debug.Log($"[VRCF QoL] Duplicated flipbook page #{ctx.pageIndex + 1} " +
                          $"as page #{ctx.pages.Count}.");
            } catch (Exception ex) { Fail("Duplicate Page", ex); }
        }

        // ------ Helpers ----------------------------------------------------

        private static bool ValidateContext(VrcfQol.FlipbookContext ctx, string title, out string error) {
            error = null;
            if (ctx.pages == null || ctx.pageIndex < 0 || ctx.pageIndex >= ctx.pages.Count) {
                error = $"Page #{ctx.pageIndex + 1} not found.";
                EditorUtility.DisplayDialog(title, error, "OK");
                return false;
            }
            return true;
        }

        // Produces a brand-new FlipBookPage with an empty State and an empty
        // actions list inside. Mirrors what VRCFury would create when the
        // user adds a page via its own "+" button.
        private static object CreateEmptyPage() {
            if (!VrcfQol.Reflection.TryEnsure(out _)) return null;
            var r = VrcfQol.Reflection;
            try {
                var newState = Activator.CreateInstance(r.StateType);
                var actionsList = Activator.CreateInstance(r.StateActionsField.FieldType);
                r.StateActionsField.SetValue(newState, actionsList);
                var page = Activator.CreateInstance(r.FlipbookPageType);
                r.PageStateField.SetValue(page, newState);
                return page;
            } catch (Exception ex) {
                Debug.LogException(ex);
                return null;
            }
        }

        private static void Fail(string title, Exception ex) {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(title, "Operation failed. See Console.\n\n" + ex.Message, "OK");
        }
    }
}
