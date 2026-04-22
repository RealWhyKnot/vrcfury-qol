// VrcfQol.cs
// Core framework for VRCFury QoL tools.
//
// Exposes two things to tool authors:
//   1. VrcfQol.Reflection  - cached reflection handles for VRCFury internal types,
//                            resolved lazily once.
//   2. VrcfQol.RegisterPropertyTool(...)  - register a right-click menu item that
//                            appears whenever the user right-clicks a matching
//                            SerializedProperty anywhere in the Inspector.
//
// Tools live in the Editor/Tools/ folder. Each tool is a small [InitializeOnLoad]
// static class that calls RegisterPropertyTool in its static ctor — see the files
// in Editor/Tools/ for examples.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol {

    /// <summary>
    /// Entry points for tool authors. Most tools only need to call
    /// <see cref="RegisterPropertyTool"/>.
    /// </summary>
    internal static class VrcfQol {

        // ============================== Registry ==============================

        internal delegate bool PropertyMatcher(SerializedProperty prop);
        internal delegate void PropertyToolAction(SerializedProperty prop);

        private sealed class Entry {
            public string Label;
            public PropertyMatcher Match;
            public PropertyToolAction Action;
            public int Priority;
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static bool _contextHookInstalled;

        /// <summary>
        /// Register a context-menu tool that fires when the user right-clicks a
        /// SerializedProperty matching <paramref name="match"/>. Label supports
        /// slash-separated submenus ("VRCFury QoL/Duplicate to end").
        /// </summary>
        public static void RegisterPropertyTool(string label, PropertyMatcher match, PropertyToolAction action, int priority = 0) {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required");
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (action == null) throw new ArgumentNullException(nameof(action));
            EnsureContextHook();
            _entries.Add(new Entry { Label = label, Match = match, Action = action, Priority = priority });
        }

        /// <summary>
        /// Enumerate all tools that match the given property. Used by the inline
        /// overlay so it can render visible buttons alongside the same tools that
        /// appear in the right-click menu.
        /// </summary>
        internal static IEnumerable<(string label, Action execute)> GetToolsFor(SerializedProperty prop) {
            if (prop == null) yield break;
            foreach (var e in _entries.OrderByDescending(e => e.Priority)) {
                bool matched;
                try { matched = e.Match(prop); } catch { matched = false; }
                if (!matched) continue;
                // Capture prop by copying — the original SerializedProperty iterator may have moved on.
                var captured = prop.Copy();
                var entryAction = e.Action;
                yield return (e.Label, () => entryAction(captured));
            }
        }

        private static void EnsureContextHook() {
            if (_contextHookInstalled) return;
            _contextHookInstalled = true;
            EditorApplication.contextualPropertyMenu += OnContextMenu;
        }

        private static void OnContextMenu(GenericMenu menu, SerializedProperty property) {
            if (property == null) return;
            // Separator before our items so they don't collide with Unity's built-ins.
            bool addedSeparator = false;
            foreach (var e in _entries.OrderByDescending(e => e.Priority)) {
                bool matched;
                try { matched = e.Match(property); } catch { matched = false; }
                if (!matched) continue;
                if (!addedSeparator) {
                    menu.AddSeparator(string.Empty);
                    addedSeparator = true;
                }
                // Capture a copy of the property — the original is mutated by the Inspector.
                var captured = property.Copy();
                var entryAction = e.Action;
                menu.AddItem(new GUIContent(e.Label), false, () => {
                    try { entryAction(captured); } catch (Exception ex) { Debug.LogException(ex); }
                });
            }
        }

        // =============================== Reflection ===========================

        /// <summary>
        /// Cached reflection handles for VRCFury internal types. Resolved lazily.
        /// Never returns null fields once <see cref="EnsureResolved"/> succeeds.
        /// </summary>
        internal static class Reflection {
            public static Assembly VrcfuryAsm { get; private set; }
            public static Type VRCFuryType { get; private set; }       // VF.Model.VRCFury (MonoBehaviour)
            public static Type ToggleType { get; private set; }         // VF.Model.Feature.Toggle
            public static Type StateType { get; private set; }          // VF.Model.State
            public static Type FlipbookBuilderActionType { get; private set; } // VF.Model.StateAction.FlipBookBuilderAction
            public static Type FlipbookPageType { get; private set; }   // nested FlipBookPage

            public static FieldInfo ContentField { get; private set; }   // VRCFury.content
            public static FieldInfo ToggleNameField { get; private set; }// Toggle.name
            public static FieldInfo ToggleStateField { get; private set; }// Toggle.state
            public static FieldInfo StateActionsField { get; private set; }// State.actions (List<Action>)
            public static FieldInfo PagesField { get; private set; }     // FlipBookBuilderAction.pages (List<FlipBookPage>)
            public static FieldInfo PageStateField { get; private set; } // FlipBookPage.state

            public static bool TryEnsure(out string error) {
                error = null;
                if (VRCFuryType != null) return true;

                VrcfuryAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "VRCFury");
                if (VrcfuryAsm == null) {
                    error = "VRCFury runtime assembly ('VRCFury') not found. Is VRCFury installed?";
                    return false;
                }

                VRCFuryType = VrcfuryAsm.GetType("VF.Model.VRCFury", false);
                ToggleType = VrcfuryAsm.GetType("VF.Model.Feature.Toggle", false);
                StateType = VrcfuryAsm.GetType("VF.Model.State", false);
                FlipbookBuilderActionType = VrcfuryAsm.GetType("VF.Model.StateAction.FlipBookBuilderAction", false);
                if (VRCFuryType == null || ToggleType == null || StateType == null || FlipbookBuilderActionType == null) {
                    error = "Could not locate one or more VRCFury internal types. The VRCFury API may have changed.";
                    return false;
                }
                FlipbookPageType = FlipbookBuilderActionType.GetNestedType("FlipBookPage", BindingFlags.Public | BindingFlags.NonPublic);
                if (FlipbookPageType == null) {
                    error = "FlipBookBuilderAction.FlipBookPage not found.";
                    return false;
                }

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                ContentField = VRCFuryType.GetField("content", any);
                ToggleNameField = ToggleType.GetField("name", any);
                ToggleStateField = ToggleType.GetField("state", any);
                StateActionsField = StateType.GetField("actions", any);
                PagesField = FlipbookBuilderActionType.GetField("pages", any);
                PageStateField = FlipbookPageType.GetField("state", any);
                if (ContentField == null || ToggleNameField == null || ToggleStateField == null ||
                    StateActionsField == null || PagesField == null || PageStateField == null) {
                    error = "One or more expected fields on VRCFury model types were not found.";
                    VRCFuryType = null;
                    return false;
                }
                return true;
            }
        }

        // =============================== Helpers ==============================

        /// <summary>
        /// Find a FlipBookBuilderAction instance inside a State.actions list.
        /// </summary>
        internal static object FindFlipbookAction(IList actions) {
            if (actions == null) return null;
            foreach (var a in actions) {
                if (a == null) continue;
                if (a.GetType() == Reflection.FlipbookBuilderActionType) return a;
            }
            return null;
        }

        /// <summary>
        /// Hierarchy path of a GameObject, e.g. "Root/Avatar/Body/Toggles".
        /// </summary>
        internal static string GetGameObjectPath(GameObject go) {
            if (go == null) return "(null)";
            var parts = new List<string>();
            var t = go.transform;
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
