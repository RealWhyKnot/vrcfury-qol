// DuplicateFlipbookPageTool.cs
// Registers a context-menu tool AND an inline "Duplicate → End" button that
// duplicates a VRCFury Flipbook Builder page and appends the copy to the end
// of the flipbook. Page #7 -> new page #N+1.
//
// Right-click the "Page #N" field inside a Flipbook Builder and choose
// "VRCF QoL / Duplicate page to end", or click the inline button.

using System;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class DuplicateFlipbookPageTool {
        static DuplicateFlipbookPageTool() {
            VrcfQol.RegisterFlipbookPageTool(
                label: "VRCF QoL/Duplicate page to end",
                action: DuplicateToEnd,
                priority: 20
            );
            VrcfQol.RegisterFlipbookPageButton(
                text: "Duplicate → End",
                tooltip: "VRCF QoL: clone this page and append the copy at the end of the flipbook.",
                onClick: DuplicateToEnd,
                order: 0
            );
        }

        private static void DuplicateToEnd(VrcfQol.FlipbookContext ctx) {
            if (ctx.pages == null || ctx.pageIndex < 0 || ctx.pageIndex >= ctx.pages.Count) {
                EditorUtility.DisplayDialog("Duplicate Page",
                    $"Page #{ctx.pageIndex + 1} not found.", "OK");
                return;
            }
            try {
                Undo.RegisterCompleteObjectUndo(ctx.vrcfComponent,
                    $"Duplicate flipbook page #{ctx.pageIndex + 1}");
                var clone = VrcfQol.DeepClonePage(ctx.pages[ctx.pageIndex]);
                ctx.pages.Add(clone);
                EditorUtility.SetDirty(ctx.vrcfComponent);
                Debug.Log($"[VRCF QoL] Duplicated flipbook page #{ctx.pageIndex + 1} " +
                          $"as page #{ctx.pages.Count}.");
            } catch (Exception ex) {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Duplicate Page",
                    "Duplication failed. See Console.\n\n" + ex.Message, "OK");
            }
        }
    }
}
