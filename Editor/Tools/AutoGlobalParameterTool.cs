// AutoGlobalParameterTool.cs
//
// Problem this solves:
//   VRCFury generates an internal parameter name for each Toggle. When you
//   update an avatar (e.g., regenerate), VRCFury is free to reshuffle those
//   internal names — which silently wipes any customisations you've pinned to
//   that parameter (animator constraints, external OSC clients, VRChat's
//   per-avatar parameter memory, etc.).
//
//   The workaround VRCFury itself provides is the "Use Global Parameter"
//   checkbox plus a user-supplied parameter name. As long as you set that
//   string yourself, VRCFury is forced to keep using it.
//
// What this tool does:
//   For every VRCFury Toggle in open scenes it enforces, every ~0.5 s:
//       useGlobalParam = true
//       globalParam    = toggle.name   (i.e. the menu path)
//
//   This is the sensible default, because matching the menu path means the
//   parameter name is already meaningful AND already unique within the menu.
//
//   It's NOT forced blindly: each toggle can opt out via:
//       • Right-click → "VRCF QoL / Disable auto-update of global parameter",
//       • or the inline "Disable" button on the green banner at the top of the
//         Toggle inspector (see VrcfQolInspectorOverlay).
//
//   The opt-out is stored in EditorPrefs keyed by the component's
//   GlobalObjectId, so it survives editor restarts on this machine. (It does
//   NOT travel across machines/users — that's intentional; opting out is a
//   local preference for a local customisation.)
//
// Non-goals / things this tool deliberately does NOT do:
//   • It does not wrap each sync in Undo. An auto-applied sync on every tick
//     would pollute the Undo stack with noise; instead we just SetDirty so the
//     scene saves. Undo still works normally for the user's direct edits to
//     either field.
//   • It does not touch assets on disk — only scene/prefab-stage components.
//   • It does not fight the user: if VRCFury removes or renames `globalParam`
//     in a future version, the Reflection cache returns null and this tool
//     becomes a no-op with a visible explanation in the inspector banner.

using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class AutoGlobalParameterTool {
        // Keyed per-component via GlobalObjectId.
        private const string OptOutPrefKey = "VrcfQol.AutoUpdateParam.OptOut.";

        // Polling cadence. Cheap — one reflection read per Toggle per tick.
        private const double TickSeconds = 0.5;
        private static double _nextTick;

        static AutoGlobalParameterTool() {
            EditorApplication.update += Tick;

            // Right-click menu items for manual control from the Toggle inspector.
            VrcfQol.RegisterToggleTool(
                label: "VRCF QoL/Disable auto-update of global parameter",
                action: ctx => SetOptedOut(ctx.vrcfComponent, true),
                priority: 6,
                enabled: ctx => !IsOptedOut(ctx.vrcfComponent)
            );
            VrcfQol.RegisterToggleTool(
                label: "VRCF QoL/Enable auto-update of global parameter",
                action: ctx => {
                    SetOptedOut(ctx.vrcfComponent, false);
                    ApplyTo(ctx.vrcfComponent, force: true);
                },
                priority: 6,
                enabled: ctx => IsOptedOut(ctx.vrcfComponent)
            );
            VrcfQol.RegisterToggleTool(
                label: "VRCF QoL/Re-sync global parameter now",
                action: ctx => ApplyTo(ctx.vrcfComponent, force: true),
                priority: 5
            );
        }

        // ------------------------------------------------------------------
        // Opt-out storage
        // ------------------------------------------------------------------

        internal static string GetOptOutKey(Component c) {
            if (c == null) return null;
            // GetGlobalObjectIdSlow returns a string that encodes scene/asset + fileID.
            // Stable enough for per-component preferences on this machine.
            var id = GlobalObjectId.GetGlobalObjectIdSlow(c);
            return OptOutPrefKey + id.ToString();
        }

        internal static bool IsOptedOut(Component c) {
            var k = GetOptOutKey(c);
            return k != null && EditorPrefs.GetBool(k, false);
        }

        internal static void SetOptedOut(Component c, bool value) {
            var k = GetOptOutKey(c);
            if (k == null) return;
            if (value) {
                EditorPrefs.SetBool(k, true);
                Debug.Log($"[VRCF QoL] Global-parameter auto-update DISABLED for '{c.name}'. " +
                          $"You can re-enable it from the green banner on the Toggle inspector.");
            } else {
                EditorPrefs.DeleteKey(k);
                Debug.Log($"[VRCF QoL] Global-parameter auto-update re-enabled for '{c.name}'.");
            }
        }

        // ------------------------------------------------------------------
        // Background sync
        // ------------------------------------------------------------------

        private static void Tick() {
            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + TickSeconds;
            try { SyncAll(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private static void SyncAll() {
            if (!VrcfQol.Reflection.TryEnsure(out _)) return;
            var r = VrcfQol.Reflection;
            if (r.ToggleUseGlobalParamField == null || r.ToggleGlobalParamField == null) {
                // VRCFury version doesn't expose those fields — nothing to do.
                return;
            }
            // Includes scene objects AND prefab-stage objects; asset-only objects
            // are filtered below by Scene.IsValid.
            var all = Resources.FindObjectsOfTypeAll(r.VRCFuryType);
            foreach (var o in all) {
                var c = o as Component;
                if (c == null) continue;
                // Skip assets / hidden / non-scene instances.
                if ((c.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) != 0) continue;
                if (c.gameObject == null) continue;
                if (!c.gameObject.scene.IsValid()) continue;
                ApplyTo(c, force: false);
            }
        }

        /// <summary>
        /// Enforce the (useGlobalParam, globalParam) contract on a single VRCFury
        /// component. Respects the per-component opt-out unless <paramref name="force"/>
        /// is set. Uses SetDirty but no Undo — this is a background sync.
        /// </summary>
        internal static void ApplyTo(Component c, bool force) {
            if (c == null) return;
            if (!VrcfQol.Reflection.TryEnsure(out _)) return;
            var r = VrcfQol.Reflection;
            if (c.GetType() != r.VRCFuryType) return;
            if (!force && IsOptedOut(c)) return;

            var useField = r.ToggleUseGlobalParamField;
            var paramField = r.ToggleGlobalParamField;
            if (useField == null || paramField == null) return;

            object content;
            try { content = r.ContentField.GetValue(c); }
            catch { return; }
            if (content == null || content.GetType() != r.ToggleType) return;

            bool use;
            string currentParam, name;
            try {
                use = (bool)useField.GetValue(content);
                currentParam = (string)paramField.GetValue(content) ?? "";
                name = (string)r.ToggleNameField.GetValue(content) ?? "";
            } catch { return; }

            // Don't force a name onto an unnamed toggle — that's not useful
            // ("" isn't a valid parameter name) and would only noisy SetDirty.
            // We'll pick it up on the next tick once the user names the toggle.
            if (string.IsNullOrWhiteSpace(name)) return;

            bool changed = false;
            if (!use) {
                useField.SetValue(content, true);
                changed = true;
            }
            if (currentParam != name) {
                paramField.SetValue(content, name);
                changed = true;
            }
            if (changed) {
                EditorUtility.SetDirty(c);
            }
        }
    }
}
