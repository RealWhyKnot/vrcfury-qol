// VrcfQol.cs
// Core framework for VRCFury QoL tools.
//
// This file provides three things to tool authors:
//
//   1. VrcfQol.Reflection   - lazily-resolved reflection handles for VRCFury's
//                             internal types (VRCFury component, Toggle feature,
//                             State, FlipBookBuilderAction, FlipBookPage).
//
//   2. Registration API     - small, typed helpers so a new tool is usually one
//                             file with one [InitializeOnLoad] registration call:
//                                RegisterPropertyTool         (generic, by SerializedProperty)
//                                RegisterFlipbookPageTool     (page right-click)
//                                RegisterFlipbookPageButton   (page inline button)
//                                RegisterFlipbookBuilderTool  (builder right-click)
//                                RegisterToggleTool           (VRCFury Toggle right-click)
//                                RegisterActionTool           (generic action right-click)
//
//   3. Helpers              - page clipboard for copy/paste, path formatting,
//                             flipbook resolution from a SerializedProperty,
//                             deep-clone of a FlipBookPage.
//
// The inspector overlay (VrcfQolInspectorOverlay.cs) reads from the page-button
// registry to render inline buttons next to each page row. Nothing else touches
// the inspector's visual tree directly - tools just register and let the overlay
// handle placement.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol {

    internal static class VrcfQol {

        // ============================== Property tools =========================

        internal delegate bool PropertyMatcher(SerializedProperty prop);
        internal delegate void PropertyToolAction(SerializedProperty prop);

        private sealed class PropEntry {
            public string Label;
            public PropertyMatcher Match;
            public PropertyToolAction Action;
            public int Priority;
            public Func<SerializedProperty, bool> Enabled; // optional, greys out when false
        }

        private static readonly List<PropEntry> _propEntries = new List<PropEntry>();
        private static bool _contextHookInstalled;

        public static void RegisterPropertyTool(
            string label,
            PropertyMatcher match,
            PropertyToolAction action,
            int priority = 0,
            Func<SerializedProperty, bool> enabled = null) {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label is required");
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (action == null) throw new ArgumentNullException(nameof(action));
            EnsureContextHook();
            _propEntries.Add(new PropEntry {
                Label = label, Match = match, Action = action,
                Priority = priority, Enabled = enabled,
            });
        }

        private static void EnsureContextHook() {
            if (_contextHookInstalled) return;
            _contextHookInstalled = true;
            EditorApplication.contextualPropertyMenu += OnContextMenu;
        }

        private static void OnContextMenu(GenericMenu menu, SerializedProperty property) {
            if (property == null) return;
            bool addedSeparator = false;
            foreach (var e in _propEntries.OrderByDescending(x => x.Priority)) {
                bool matched;
                try { matched = e.Match(property); } catch { matched = false; }
                if (!matched) continue;

                bool enabled = true;
                if (e.Enabled != null) {
                    try { enabled = e.Enabled(property); } catch { enabled = false; }
                }

                if (!addedSeparator) {
                    menu.AddSeparator(string.Empty);
                    addedSeparator = true;
                }

                var captured = property.Copy();
                var act = e.Action;
                if (enabled) {
                    menu.AddItem(new GUIContent(e.Label), false, () => {
                        try { act(captured); } catch (Exception ex) { Debug.LogException(ex); }
                    });
                } else {
                    menu.AddDisabledItem(new GUIContent(e.Label));
                }
            }
        }

        // =========================== Typed convenience ========================

        // ---- FlipBookPage ----

        private static readonly Regex PagePathRegex = new Regex(
            @"\.pages\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);

        public static bool IsFlipbookPageProperty(SerializedProperty prop) {
            if (prop == null) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            return PagePathRegex.IsMatch(prop.propertyPath ?? "");
        }

        public static int GetFlipbookPageIndex(SerializedProperty prop) {
            if (prop == null) return -1;
            var m = PagePathRegex.Match(prop.propertyPath ?? "");
            return m.Success && int.TryParse(m.Groups[1].Value, out var i) ? i : -1;
        }

        public static void RegisterFlipbookPageTool(
            string label,
            Action<FlipbookContext> action,
            int priority = 0,
            Func<FlipbookContext, bool> enabled = null) {
            RegisterPropertyTool(
                label,
                IsFlipbookPageProperty,
                prop => {
                    if (!TryResolveFlipbookFromPage(prop, out var ctx)) return;
                    action(ctx);
                },
                priority,
                enabled == null ? null : new Func<SerializedProperty, bool>(prop => {
                    return TryResolveFlipbookFromPage(prop, out var ctx) && enabled(ctx);
                }));
        }

        // ---- FlipBookBuilderAction ----

        public static bool IsFlipbookBuilderProperty(SerializedProperty prop) {
            if (prop == null) return false;
            if (prop.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var t = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(t)) return false;
            return t.EndsWith(" " + Reflection.FlipbookBuilderActionType.FullName);
        }

        public static void RegisterFlipbookBuilderTool(
            string label,
            Action<FlipbookContext> action,
            int priority = 0,
            Func<FlipbookContext, bool> enabled = null) {
            RegisterPropertyTool(
                label,
                IsFlipbookBuilderProperty,
                prop => {
                    if (!TryResolveFlipbookFromBuilder(prop, out var ctx)) return;
                    action(ctx);
                },
                priority,
                enabled == null ? null : new Func<SerializedProperty, bool>(prop => {
                    return TryResolveFlipbookFromBuilder(prop, out var ctx) && enabled(ctx);
                }));
        }

        // ---- VRCFury Toggle component (right-click anywhere on the component) ----

        public static bool IsToggleContentProperty(SerializedProperty prop) {
            if (prop == null || prop.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var t = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(t)) return false;
            return t.EndsWith(" " + Reflection.ToggleType.FullName);
        }

        public static void RegisterToggleTool(
            string label,
            Action<ToggleContext> action,
            int priority = 0,
            Func<ToggleContext, bool> enabled = null) {
            RegisterPropertyTool(
                label,
                IsToggleContentProperty,
                prop => {
                    if (!TryResolveToggle(prop, out var ctx)) return;
                    action(ctx);
                },
                priority,
                enabled == null ? null : new Func<SerializedProperty, bool>(prop => {
                    return TryResolveToggle(prop, out var ctx) && enabled(ctx);
                }));
        }

        // ---- Action (right-click a specific VF.Model.StateAction.* instance) ----

        public static bool IsActionProperty(SerializedProperty prop, Type actionType) {
            if (prop == null || prop.propertyType != SerializedPropertyType.ManagedReference) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            if (actionType == null) return false;
            var t = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(t)) return false;
            return t.EndsWith(" " + actionType.FullName);
        }

        public static void RegisterActionTool(
            string vrcfActionFullName,
            string label,
            Action<SerializedProperty, object> action,
            int priority = 0) {
            RegisterPropertyTool(
                label,
                prop => {
                    if (!Reflection.TryEnsure(out _)) return false;
                    var actionType = Reflection.VrcfuryAsm.GetType(vrcfActionFullName, false);
                    return actionType != null && IsActionProperty(prop, actionType);
                },
                prop => {
                    var value = prop.managedReferenceValue;
                    if (value == null) return;
                    action(prop, value);
                },
                priority);
        }

        // =========================== Inline button registry ===================

        internal sealed class InlineButtonSpec {
            public string Text;
            public string Tooltip;
            public Action<FlipbookContext> OnClick;
            public Func<FlipbookContext, bool> Visible;
            public int Order;
        }

        private static readonly List<InlineButtonSpec> _inlinePageButtons = new List<InlineButtonSpec>();

        internal static IReadOnlyList<InlineButtonSpec> InlinePageButtons => _inlinePageButtons;

        public static void RegisterFlipbookPageButton(
            string text,
            string tooltip,
            Action<FlipbookContext> onClick,
            int order = 0,
            Func<FlipbookContext, bool> visible = null) {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("text is required");
            if (onClick == null) throw new ArgumentNullException(nameof(onClick));
            _inlinePageButtons.Add(new InlineButtonSpec {
                Text = text, Tooltip = tooltip, OnClick = onClick,
                Visible = visible, Order = order,
            });
            _inlinePageButtons.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        // =========================== Context resolvers ========================

        internal struct FlipbookContext {
            public Component vrcfComponent;
            public object toggle;
            public string toggleName;
            public object flipbookAction;
            public IList pages;
            public int pageIndex;
        }

        internal struct ToggleContext {
            public Component vrcfComponent;
            public object toggle;
            public string toggleName;
            public object state;
            public IList actions;
            public object flipbookAction;
            public bool slider;
        }

        internal static bool TryResolveFlipbookFromPage(SerializedProperty pageProp, out FlipbookContext ctx) {
            ctx = default;
            if (pageProp == null) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var r = Reflection;

            var m = PagePathRegex.Match(pageProp.propertyPath ?? "");
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, out var pageIndex)) return false;

            var comp = pageProp.serializedObject?.targetObject as Component;
            if (comp == null || comp.GetType() != r.VRCFuryType) return false;

            var content = r.ContentField.GetValue(comp);
            if (content == null || content.GetType() != r.ToggleType) return false;

            var state = r.ToggleStateField.GetValue(content);
            var toggleActions = r.StateActionsField.GetValue(state) as IList;
            var fb = FindFlipbookAction(toggleActions);
            if (fb == null) return false;
            var pages = r.PagesField.GetValue(fb) as IList;
            if (pages == null) return false;

            ctx = new FlipbookContext {
                vrcfComponent = comp,
                toggle = content,
                toggleName = (string)r.ToggleNameField.GetValue(content) ?? "",
                flipbookAction = fb,
                pages = pages,
                pageIndex = pageIndex,
            };
            return true;
        }

        internal static bool TryResolveFlipbookFromBuilder(SerializedProperty builderProp, out FlipbookContext ctx) {
            ctx = default;
            if (builderProp == null) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var r = Reflection;

            var comp = builderProp.serializedObject?.targetObject as Component;
            if (comp == null || comp.GetType() != r.VRCFuryType) return false;
            var content = r.ContentField.GetValue(comp);
            if (content == null || content.GetType() != r.ToggleType) return false;
            var state = r.ToggleStateField.GetValue(content);
            var toggleActions = r.StateActionsField.GetValue(state) as IList;
            var fb = builderProp.managedReferenceValue;
            if (fb == null || fb.GetType() != r.FlipbookBuilderActionType) {
                fb = FindFlipbookAction(toggleActions);
            }
            if (fb == null) return false;
            var pages = r.PagesField.GetValue(fb) as IList;
            ctx = new FlipbookContext {
                vrcfComponent = comp,
                toggle = content,
                toggleName = (string)r.ToggleNameField.GetValue(content) ?? "",
                flipbookAction = fb,
                pages = pages,
                pageIndex = -1,
            };
            return true;
        }

        internal static bool TryResolveToggle(SerializedProperty contentProp, out ToggleContext ctx) {
            ctx = default;
            if (contentProp == null) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var r = Reflection;

            var comp = contentProp.serializedObject?.targetObject as Component;
            if (comp == null || comp.GetType() != r.VRCFuryType) return false;
            var content = r.ContentField.GetValue(comp);
            if (content == null || content.GetType() != r.ToggleType) return false;

            var state = r.ToggleStateField.GetValue(content);
            var actions = r.StateActionsField.GetValue(state) as IList;
            var fb = FindFlipbookAction(actions);
            bool slider = false;
            try { if (r.ToggleSliderField != null) slider = (bool)r.ToggleSliderField.GetValue(content); } catch { slider = false; }

            ctx = new ToggleContext {
                vrcfComponent = comp,
                toggle = content,
                toggleName = (string)r.ToggleNameField.GetValue(content) ?? "",
                state = state,
                actions = actions,
                flipbookAction = fb,
                slider = slider,
            };
            return true;
        }

        internal static bool TryResolveFlipbookFromComponent(Component vrcf, out FlipbookContext ctx) {
            ctx = default;
            if (vrcf == null) return false;
            if (!Reflection.TryEnsure(out _)) return false;
            var r = Reflection;
            if (vrcf.GetType() != r.VRCFuryType) return false;
            var content = r.ContentField.GetValue(vrcf);
            if (content == null || content.GetType() != r.ToggleType) return false;
            var state = r.ToggleStateField.GetValue(content);
            var actions = r.StateActionsField.GetValue(state) as IList;
            var fb = FindFlipbookAction(actions);
            if (fb == null) return false;
            var pages = r.PagesField.GetValue(fb) as IList;
            ctx = new FlipbookContext {
                vrcfComponent = vrcf,
                toggle = content,
                toggleName = (string)r.ToggleNameField.GetValue(content) ?? "",
                flipbookAction = fb,
                pages = pages,
                pageIndex = -1,
            };
            return true;
        }

        // =========================== Clone / clipboard ========================

        internal static object DeepClonePage(object sourcePage) {
            var r = Reflection;
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

        internal static class PageClipboard {
            private static object _clone;
            private static string _sourceDescription;

            public static bool HasValue => _clone != null;
            public static string SourceDescription => _sourceDescription ?? "";

            public static void CopyFrom(FlipbookContext ctx) {
                if (ctx.pages == null || ctx.pageIndex < 0 || ctx.pageIndex >= ctx.pages.Count) return;
                _clone = DeepClonePage(ctx.pages[ctx.pageIndex]);
                _sourceDescription = $"Page #{ctx.pageIndex + 1} of \"{ctx.toggleName}\"";
            }

            public static object TakeClone() {
                if (_clone == null) return null;
                return DeepClonePage(_clone);
            }
        }

        // =========================== Helpers ==================================

        internal static object FindFlipbookAction(IList actions) {
            if (actions == null) return null;
            foreach (var a in actions) {
                if (a == null) continue;
                if (a.GetType() == Reflection.FlipbookBuilderActionType) return a;
            }
            return null;
        }

        internal static string GetGameObjectPath(GameObject go) {
            if (go == null) return "(null)";
            var parts = new List<string>();
            var t = go.transform;
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // =========================== Reflection cache =========================
        //
        // Exposed as an instance singleton (via the static `Reflection` property)
        // so tools can write `var r = VrcfQol.Reflection;` and then read `r.X`.
        // A purely static class would force `VrcfQol.Reflection.X` everywhere,
        // and would also prevent `var r = ...` captures at all.

        internal static ReflectionCache Reflection { get; } = new ReflectionCache();

        internal sealed class ReflectionCache {
            public Assembly VrcfuryAsm { get; private set; }
            public Type VRCFuryType { get; private set; }
            public Type ToggleType { get; private set; }
            public Type StateType { get; private set; }
            public Type FlipbookBuilderActionType { get; private set; }
            public Type FlipbookPageType { get; private set; }

            public FieldInfo ContentField { get; private set; }
            public FieldInfo ToggleNameField { get; private set; }
            public FieldInfo ToggleStateField { get; private set; }
            public FieldInfo ToggleSliderField { get; private set; }
            public FieldInfo ToggleUseGlobalParamField { get; private set; }
            public FieldInfo ToggleGlobalParamField { get; private set; }
            public FieldInfo StateActionsField { get; private set; }
            public FieldInfo PagesField { get; private set; }
            public FieldInfo PageStateField { get; private set; }

            public bool TryEnsure(out string error) {
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
                FlipbookPageType = FlipbookBuilderActionType.GetNestedType("FlipBookPage",
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (FlipbookPageType == null) {
                    error = "FlipBookBuilderAction.FlipBookPage not found.";
                    return false;
                }

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                ContentField = VRCFuryType.GetField("content", any);
                ToggleNameField = ToggleType.GetField("name", any);
                ToggleStateField = ToggleType.GetField("state", any);
                ToggleSliderField = ToggleType.GetField("slider", any);
                ToggleUseGlobalParamField = ToggleType.GetField("useGlobalParam", any);
                ToggleGlobalParamField = ToggleType.GetField("globalParam", any);
                StateActionsField = StateType.GetField("actions", any);
                PagesField = FlipbookBuilderActionType.GetField("pages", any);
                PageStateField = FlipbookPageType.GetField("state", any);
                if (ContentField == null || ToggleNameField == null || ToggleStateField == null ||
                    StateActionsField == null || PagesField == null || PageStateField == null) {
                    error = "One or more expected fields on VRCFury model types were not found.";
                    VRCFuryType = null;
                    return false;
                }
                // ToggleSliderField, ToggleUseGlobalParamField and ToggleGlobalParamField are
                // optional - older VRCFury versions may not expose them. Tools that depend on
                // them should null-check.
                return true;
            }
        }
    }
}
