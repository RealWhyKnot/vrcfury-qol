// ReplaceReferencesTool.cs
//
// Registers the entry points for the Replace-References window. The window
// itself lives in ReplaceReferencesWindow.cs.
//
// Two ways to open it:
//   1. Window/VRCFury QoL/Replace References...    (top-level menu)
//   2. Right-click a GameObject in the hierarchy → "VRCFury QoL/Replace
//      references in selection..." (pre-fills the search list with the
//      currently selected GameObjects)

using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class ReplaceReferencesTool {

        private const string GameObjectMenuPath = "GameObject/VRCFury QoL/Replace references in selection...";
        private const string WindowMenuPath     = "Window/VRCFury QoL/Replace References...";

        static ReplaceReferencesTool() {
            // Registration is implicit — both menu items below are static, so
            // Unity discovers them on script reload. The static constructor
            // is here for symmetry with the rest of the framework's tools and
            // so users can confirm the file is being loaded.
        }

        [MenuItem(WindowMenuPath, false, 2000)]
        private static void OpenFromWindowMenu() {
            ReplaceReferencesWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 49)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Unity calls a hierarchy menu item once per selected GameObject.
            // Bail out for all but the first to avoid opening N windows.
            if (command.context != Selection.activeGameObject) return;
            ReplaceReferencesWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return false;
            // Enabled when at least one selected GameObject (or its descendants)
            // has a VRCFury component. Cheap up-front check on the active object.
            var go = command.context as GameObject;
            return go != null && go.GetComponentInChildren(VrcfQol.Reflection.VRCFuryType, true) != null;
        }
    }
}
