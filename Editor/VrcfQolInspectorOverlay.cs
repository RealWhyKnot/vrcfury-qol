// VrcfQolInspectorOverlay.cs
// Best-effort: watches the Unity Inspector and enriches recognisable VRCFury
// elements with inline UI.
//
// Currently:
//   - Each "Page #N" label inside a Flipbook Builder gets a row of buttons
//     sourced from the VrcfQol.InlinePageButtons registry. Tools register the
//     button; the overlay handles placement.
//   - Each State Action row with a bound SerializedProperty path gets a
//     "Duplicate item" button that duplicates only that one action, plus a
//     "Copy to page" picker when the action lives inside a flipbook page.
//   - Each Toggle inspector (the VRCFury component with a Toggle content) gets
//     a status banner pinned to the top of the window explaining that the
//     Global Parameter is being auto-managed, with inline Preview and opt-in/out
//     buttons.
//
// This is intentionally defensive: if VRCFury restructures its inspector, the
// overlay silently finds nothing and does nothing - the right-click context
// menu still works as the authoritative entry point.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UmeVrcfQol.Tools;

namespace UmeVrcfQol {

    [InitializeOnLoad]
    internal static class VrcfQolInspectorOverlay {
        private const string InjectedClass = "vrcfqol-injected";
        private const string ButtonBarClass = "vrcfqol-buttons";
        private const string ActionToolsClass = "vrcfqol-action-tools";
        private const string ActionToolsInjectedClass = "vrcfqol-action-tools-injected";
        private const string ToggleBannerClass = "vrcfqol-toggle-banner";

        private static readonly Regex PageLabelRegex = new Regex(@"^Page #(\d+)$", RegexOptions.Compiled);
        private static readonly Regex ActionPathRegex = new Regex(
            @"(^|\.)actions\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);
        private static readonly Regex FlipbookActionPathRegex = new Regex(
            @"\.pages\.Array\.data\[(\d+)\]\.state\.actions\.Array\.data\[(\d+)\]$",
            RegexOptions.Compiled);

        private const double ScanIntervalSeconds = 0.25;
        private static double _nextScan;

        static VrcfQolInspectorOverlay() {
            EditorApplication.update += Tick;
        }

        private static void Tick() {
            if (EditorApplication.timeSinceStartup < _nextScan) return;
            _nextScan = EditorApplication.timeSinceStartup + ScanIntervalSeconds;
            try { Scan(); } catch { /* defensive */ }
        }

        private static void Scan() {
            if (!VrcfQol.Reflection.TryEnsure(out _)) return;

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in allWindows) {
                if (w == null) continue;
                if (w.GetType().Name != "InspectorWindow") continue;
                var root = w.rootVisualElement;
                if (root == null) continue;
                ScanRoot(root);
            }
        }

        private static void ScanRoot(VisualElement root) {
            EnsureToggleBanner(root);

            // Capture the inspector's scroll position so we can restore it
            // if our injection causes the layout to shift. VRCFury rebuilds
            // its own visual tree on drag operations and other state changes,
            // and our overlay re-injects buttons in response — without a
            // restore the user is dropped at the top of the page list every
            // time, which is exactly the "scroll gets messed up after dragging"
            // symptom the user reported.
            var scroll = root.Q<ScrollView>();
            Vector2 savedScrollOffset = scroll != null ? scroll.scrollOffset : Vector2.zero;

            int injectedThisScan = 0;
            var labels = root.Query<Label>().ToList();
            foreach (var label in labels) {
                var match = PageLabelRegex.Match(label.text ?? "");
                if (!match.Success) continue;
                if (TryInjectPageButtons(label)) injectedThisScan++;
            }
            injectedThisScan += InjectActionButtons(root);

            if (injectedThisScan > 0 && scroll != null) {
                // Defer the restore one frame so any layout pass triggered by
                // our insertion has a chance to finish first; otherwise we'd
                // set scrollOffset to a value that gets re-clamped immediately.
                var capturedScroll = scroll;
                var capturedOffset = savedScrollOffset;
                scroll.schedule
                    .Execute(() => capturedScroll.scrollOffset = capturedOffset)
                    .ExecuteLater(0);
            }
        }

        // ----------------------------------------------------------------------
        // Per-Toggle auto-update + Preview banner (one banner per Toggle
        // component, sitting inside that Toggle's editor subtree so the Preview
        // button belongs to exactly the Toggle it visually accompanies -- not
        // a global banner that can only address the first Toggle on a
        // multi-Toggle GameObject).
        // ----------------------------------------------------------------------

        private static void EnsureToggleBanner(VisualElement root) {
            if (!VrcfQol.Reflection.TryEnsure(out _)) {
                RemoveAllBanners(root);
                return;
            }

            var selected = Selection.activeGameObject;
            if (selected == null) {
                RemoveAllBanners(root);
                return;
            }

            var r = VrcfQol.Reflection;
            var togglesOnSelection = new HashSet<Component>();
            foreach (Component c in selected.GetComponents(r.VRCFuryType)) {
                if (c == null) continue;
                var content = r.ContentField.GetValue(c);
                if (content == null || content.GetType() != r.ToggleType) continue;
                togglesOnSelection.Add(c);
            }

            if (togglesOnSelection.Count == 0) {
                RemoveAllBanners(root);
                return;
            }

            var anchored = new HashSet<Component>();
            foreach (var wrapper in EnumerateEditorWrappers(root)) {
                if (wrapper.GetType().Name != "EditorElement") continue;
                if (!TryGetEditorTarget(wrapper, out var comp)) continue;
                if (!togglesOnSelection.Contains(comp)) continue;
                if (anchored.Contains(comp)) continue;
                BuildBannerInside(wrapper, comp);
                anchored.Add(comp);
            }

            foreach (var banner in root.Query<VisualElement>(className: ToggleBannerClass).ToList()) {
                var owner = banner.userData as Component;
                if (owner == null || !anchored.Contains(owner)) {
                    banner.RemoveFromHierarchy();
                }
            }
        }

        private static void RemoveAllBanners(VisualElement root) {
            foreach (var banner in root.Query<VisualElement>(className: ToggleBannerClass).ToList()) {
                banner.RemoveFromHierarchy();
            }
        }

        private static void BuildBannerInside(VisualElement editorElement, Component target) {
            // Anchor the banner inside the InspectorElement (the editor's
            // content host) so it sits BELOW the component header rather
            // than above it, integrating with the VRCFury inspector body.
            var host = FindInspectorContent(editorElement);
            var banner = host.Q<VisualElement>(className: ToggleBannerClass);
            if (banner == null || !ReferenceEquals(banner.parent, host)) {
                if (banner != null) banner.RemoveFromHierarchy();
                banner = new VisualElement();
                banner.AddToClassList(ToggleBannerClass);
                ApplyBannerChromeStyle(banner);
                host.Insert(0, banner);
            }
            banner.userData = target;
            PopulateBanner(banner, target);
        }

        private static VisualElement FindInspectorContent(VisualElement editorElement) {
            foreach (var child in editorElement.Children()) {
                if (child.GetType().Name == "InspectorElement") return child;
            }
            foreach (var desc in editorElement.Query<VisualElement>().ToList()) {
                if (desc != editorElement && desc.GetType().Name == "InspectorElement") return desc;
            }
            return editorElement;
        }

        // Visual language: VRCFury action headers are dark grey one-line rows
        // with a label on the left and a button on the right. The banner adopts
        // the same chrome (no big colored bar competing with VRCFury's palette);
        // a thin colored left edge conveys sync state without staining the row.
        private static void ApplyBannerChromeStyle(VisualElement banner) {
            banner.style.flexDirection = FlexDirection.Row;
            banner.style.alignItems = Align.Center;
            banner.style.paddingLeft = 6;
            banner.style.paddingRight = 4;
            banner.style.paddingTop = 2;
            banner.style.paddingBottom = 2;
            banner.style.marginTop = 0;
            banner.style.marginBottom = 1;
            banner.style.backgroundColor = new StyleColor(new Color(0.20f, 0.20f, 0.20f, 1f));
            banner.style.borderLeftWidth = 3;
            banner.style.borderBottomWidth = 1;
            banner.style.borderBottomColor = new StyleColor(new Color(0, 0, 0, 0.35f));
        }

        private static void PopulateBanner(VisualElement banner, Component target) {
            banner.Clear();

            var mutedText = new StyleColor(new Color(0.78f, 0.78f, 0.78f, 1f));
            var supported = VrcfQol.Reflection.ToggleUseGlobalParamField != null
                         && VrcfQol.Reflection.ToggleGlobalParamField != null;

            if (!supported) {
                banner.style.borderLeftColor = new StyleColor(new Color(0.55f, 0.55f, 0.55f, 1f));
                var msg = new Label("vrcfury-qol: this VRCFury version does not expose Global Parameter fields. Auto-sync disabled.");
                msg.style.color = mutedText;
                msg.style.flexGrow = 1;
                msg.style.whiteSpace = WhiteSpace.Normal;
                banner.Add(msg);
                return;
            }

            var optedOut = AutoGlobalParameterTool.IsOptedOut(target);
            banner.style.borderLeftColor = new StyleColor(optedOut
                ? new Color(0.78f, 0.42f, 0.32f, 1f)
                : new Color(0.42f, 0.70f, 0.45f, 1f));

            var label = new Label(optedOut
                ? "vrcfury-qol: Global Parameter sync off"
                : "vrcfury-qol: Global Parameter auto-synced with Menu Path");
            label.style.color = mutedText;
            label.style.flexGrow = 1;
            label.tooltip = optedOut
                ? "Global Parameter is NOT being kept in sync with the Menu Path for this toggle. Click Turn on to resume syncing."
                : "Keeps 'Use Global Parameter' checked and 'globalParam' equal to the Menu Path so VRCFury cannot wipe custom work during avatar updates.";
            banner.Add(label);

            var capturedTarget = target;
            var capturedOptedOut = optedOut;
            var previewing = PreviewTool.IsPreviewingComponent(capturedTarget);
            var previewBtn = new Button(() => PreviewTool.ToggleTogglePreview(capturedTarget)) {
                text = previewing ? "Stop Previewing" : "Preview"
            };
            previewBtn.tooltip = previewing
                ? "Destroy the temporary preview copy and return to the original avatar."
                : "Create a temporary avatar copy with this toggle applied. Does not move the Scene camera.";
            ApplyInlineButtonStyle(previewBtn);
            ApplyDangerButtonStyle(previewBtn, previewing);
            banner.Add(previewBtn);

            var optBtn = new Button(() => {
                AutoGlobalParameterTool.SetOptedOut(capturedTarget, !capturedOptedOut);
                if (capturedOptedOut) {
                    AutoGlobalParameterTool.ApplyTo(capturedTarget, force: true);
                }
            }) {
                text = optedOut ? "Turn on" : "Turn off"
            };
            optBtn.tooltip = optedOut
                ? "Resume auto-syncing the Global Parameter with the Menu Path."
                : "Stop auto-syncing the Global Parameter. The current value will not be touched.";
            ApplyInlineButtonStyle(optBtn);
            banner.Add(optBtn);
        }

        // Shared inline-button chrome so the banner, page bar, and action
        // tool buttons all read as the same control type.
        private static void ApplyInlineButtonStyle(Button btn) {
            btn.style.marginLeft = 4;
            btn.style.marginTop = 0;
            btn.style.marginBottom = 0;
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.style.paddingTop = 1;
            btn.style.paddingBottom = 1;
            btn.style.flexShrink = 0;
        }

        // ----------------------------------------------------------------------
        // Per-page inline buttons
        // ----------------------------------------------------------------------

        // Returns true if buttons were freshly injected (caller uses this to
        // decide whether the inspector's scroll position should be restored).
        private static bool TryInjectPageButtons(Label pageLabel) {
            pageLabel.AddToClassList(InjectedClass);

            var parent = pageLabel.parent;
            if (parent == null) return false;

            var specs = VrcfQol.InlinePageButtons;
            if (specs == null || specs.Count == 0) return false;

            var buttons = FindPageButtonBar(parent, pageLabel);
            var inserted = false;
            if (buttons == null) {
                // Sibling-insert approach: keep the label exactly where it is and
                // just append a button container as the next sibling. Doing it this
                // way (rather than removing the label and re-parenting it under a
                // wrapper row) avoids two extra mutations to the visual tree, which
                // is what was triggering scroll resets on every overlay tick during
                // a flipbook page drag.
                buttons = new VisualElement();
                buttons.AddToClassList(ButtonBarClass);
                buttons.userData = pageLabel;
                buttons.style.flexDirection = FlexDirection.Row;
                buttons.style.alignItems = Align.Center;
                buttons.style.justifyContent = Justify.FlexEnd;
                buttons.style.flexShrink = 0;
                buttons.style.marginLeft = 8;
                buttons.style.marginTop = 1;
                buttons.style.marginBottom = 1;

                foreach (var spec in specs) {
                    var capturedSpec = spec;
                    var btn = new Button(() => OnInlineButtonClicked(pageLabel, capturedSpec)) {
                        text = spec.Text,
                    };
                    btn.userData = spec;
                    btn.tooltip = spec.Tooltip ?? string.Empty;
                    ApplyInlineButtonStyle(btn);
                    buttons.Add(btn);
                }

                int labelIndex = parent.IndexOf(pageLabel);
                parent.Insert(labelIndex + 1, buttons);
                inserted = true;
            }

            UpdatePageButtons(pageLabel, buttons);
            return inserted;
        }

        private static void OnInlineButtonClicked(Label pageLabel, VrcfQol.InlineButtonSpec spec) {
            if (!TryResolvePageContext(pageLabel, out var ctx, out var error)) {
                EditorUtility.DisplayDialog("WhyKnot vrcfury-qol", error, "OK");
                return;
            }

            if (spec.Visible != null) {
                bool vis;
                try { vis = spec.Visible(ctx); } catch { vis = true; }
                if (!vis) {
                    EditorUtility.DisplayDialog("WhyKnot vrcfury-qol",
                        "This action is not available for this page right now.", "OK");
                    return;
                }
            }

            try {
                spec.OnClick(ctx);
            } catch (System.Exception ex) {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("WhyKnot vrcfury-qol",
                    "Action failed. See Console.\n\n" + ex.Message, "OK");
            }
        }

        private static bool TryResolvePageContext(Label pageLabel, out VrcfQol.FlipbookContext ctx, out string error) {
            ctx = default;
            error = null;
            var match = PageLabelRegex.Match(pageLabel.text ?? "");
            if (!match.Success) {
                error = "Could not determine which flipbook page this button belongs to.";
                return false;
            }
            if (!int.TryParse(match.Groups[1].Value, out var oneBasedIndex)) {
                error = "Could not determine which flipbook page this button belongs to.";
                return false;
            }
            var sourceIndex = oneBasedIndex - 1;

            var selection = Selection.activeGameObject;
            if (selection == null) {
                error = "Could not determine which GameObject is inspected. Select it in the Hierarchy and try again.";
                return false;
            }
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                error = err;
                return false;
            }
            var r = VrcfQol.Reflection;

            foreach (Component c in selection.GetComponents(r.VRCFuryType)) {
                if (c == null) continue;
                if (!VrcfQol.TryResolveFlipbookFromComponent(c, out var resolved)) continue;
                if (resolved.pages == null || sourceIndex < 0 || sourceIndex >= resolved.pages.Count) continue;
                resolved.pageIndex = sourceIndex;
                ctx = resolved;
                return true;
            }

            error =
                "Could not find a flipbook toggle on the selected GameObject that contains this page. " +
                "If the flipbook is on a different object than the current selection, use right-click WhyKnot / vrcfury-qol instead.";
            return false;
        }

        private static VisualElement FindPageButtonBar(VisualElement parent, Label pageLabel) {
            for (int i = 0; i < parent.childCount; i++) {
                var child = parent[i];
                if (child != null &&
                    child.ClassListContains(ButtonBarClass) &&
                    ReferenceEquals(child.userData, pageLabel)) {
                    return child;
                }
            }
            return null;
        }

        private static void UpdatePageButtons(Label pageLabel, VisualElement buttons) {
            var hasContext = TryResolvePageContext(pageLabel, out var ctx, out _);
            for (int i = 0; i < buttons.childCount; i++) {
                var btn = buttons[i] as Button;
                var spec = btn?.userData as VrcfQol.InlineButtonSpec;
                if (btn == null || spec == null) continue;

                var visible = true;
                if (hasContext && spec.Visible != null) {
                    try { visible = spec.Visible(ctx); } catch { visible = true; }
                }
                btn.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

                btn.text = spec.Text;
                if (hasContext && spec.TextProvider != null) {
                    try {
                        var text = spec.TextProvider(ctx);
                        if (!string.IsNullOrEmpty(text)) btn.text = text;
                    } catch {
                        btn.text = spec.Text;
                    }
                }

                btn.tooltip = spec.Tooltip ?? string.Empty;
                if (hasContext && spec.TooltipProvider != null) {
                    try {
                        var tooltip = spec.TooltipProvider(ctx);
                        if (!string.IsNullOrEmpty(tooltip)) btn.tooltip = tooltip;
                    } catch {
                        btn.tooltip = spec.Tooltip ?? string.Empty;
                    }
                }

                var danger = false;
                if (hasContext && spec.Danger != null) {
                    try { danger = spec.Danger(ctx); } catch { danger = false; }
                }
                ApplyDangerButtonStyle(btn, danger);
            }
        }

        private static void ApplyDangerButtonStyle(Button btn, bool danger) {
            if (danger) {
                btn.style.backgroundColor = new StyleColor(new Color(0.64f, 0.10f, 0.10f, 1f));
                btn.style.color = new StyleColor(Color.white);
                btn.style.unityFontStyleAndWeight = FontStyle.Bold;
                return;
            }

            btn.style.backgroundColor = new StyleColor(StyleKeyword.Null);
            btn.style.color = new StyleColor(StyleKeyword.Null);
            btn.style.unityFontStyleAndWeight = FontStyle.Normal;
        }

        // ----------------------------------------------------------------------
        // Per-action inline duplicate button
        // ----------------------------------------------------------------------

        private static int InjectActionButtons(VisualElement root) {
            int injected = 0;
            var fields = root.Query<PropertyField>().ToList();
            foreach (var field in fields) {
                if (field == null) continue;
                var bindingPath = field.bindingPath ?? "";
                if (!ActionPathRegex.IsMatch(bindingPath)) {
                    if (field.ClassListContains(ActionToolsInjectedClass)) {
                        var stale = field.Q<VisualElement>(className: ActionToolsClass);
                        if (stale != null) stale.RemoveFromHierarchy();
                        field.RemoveFromClassList(ActionToolsInjectedClass);
                    }
                    continue;
                }

                var existing = field.Q<VisualElement>(className: ActionToolsClass);
                var toolsKey = bindingPath + "|action-tools-v2";
                if (field.ClassListContains(ActionToolsInjectedClass)) {
                    if (existing != null && (existing.userData as string) == toolsKey) continue;
                    if (existing != null) existing.RemoveFromHierarchy();
                }
                field.AddToClassList(ActionToolsInjectedClass);

                var tools = new VisualElement();
                tools.AddToClassList(ActionToolsClass);
                tools.userData = toolsKey;
                tools.style.flexDirection = FlexDirection.Row;
                tools.style.alignItems = Align.Center;
                tools.style.justifyContent = Justify.FlexEnd;
                tools.style.marginTop = 2;
                tools.style.marginBottom = 2;
                tools.style.paddingRight = 2;

                var capturedPath = bindingPath;
                var capturedField = field;
                var btn = new Button(() => InvokeWithResolvedOwner(capturedField, capturedPath, "Duplicate item", OnDuplicateActionClicked)) {
                    text = "Duplicate item"
                };
                btn.tooltip =
                    "Clone only this one VRCFury action, such as a single BlendShape or Material Swap, and insert the copy directly below it.";
                ApplyInlineButtonStyle(btn);
                tools.Add(btn);

                if (FlipbookActionPathRegex.IsMatch(bindingPath)) {
                    var copyBtn = new Button(() => InvokeWithResolvedOwner(capturedField, capturedPath, "Copy to page", OnDuplicateActionToPageClicked)) {
                        text = "Copy to page"
                    };
                    copyBtn.tooltip =
                        "Clone only this one action and append the copy to another page in this flipbook.";
                    ApplyInlineButtonStyle(copyBtn);
                    tools.Add(copyBtn);
                }

                field.Insert(0, tools);
                injected++;
            }
            return injected;
        }

        private static void OnDuplicateActionClicked(int owningComponentInstanceId, string actionPropertyPath) {
            if (!TryFindSelectedActionComponent(owningComponentInstanceId, actionPropertyPath, out var component, out var error)) {
                EditorUtility.DisplayDialog("Duplicate VRCFury Action", error, "OK");
                return;
            }

            if (!DuplicateActionTool.TryDuplicate(component, actionPropertyPath, out error)) {
                EditorUtility.DisplayDialog("Duplicate VRCFury Action", error, "OK");
            }
        }

        private static void OnDuplicateActionToPageClicked(int owningComponentInstanceId, string actionPropertyPath) {
            if (!TryFindSelectedActionComponent(owningComponentInstanceId, actionPropertyPath, out var component, out var error)) {
                EditorUtility.DisplayDialog("Copy VRCFury Action To Page", error, "OK");
                return;
            }
            if (!DuplicateActionTool.TryGetFlipbookActionCopyInfo(component, actionPropertyPath,
                    out var info, out error)) {
                EditorUtility.DisplayDialog("Copy VRCFury Action To Page", error, "OK");
                return;
            }
            if (info.PageCount <= 1) {
                EditorUtility.DisplayDialog("Copy VRCFury Action To Page",
                    "This flipbook only has one page. Use Duplicate item to copy the action on the same page.",
                    "OK");
                return;
            }

            var menu = new GenericMenu();
            for (int i = 0; i < info.PageCount; i++) {
                var label = i == info.SourcePageIndex
                    ? $"Page {i + 1} - current page"
                    : $"Page {i + 1}";
                if (i == info.SourcePageIndex) {
                    menu.AddDisabledItem(new GUIContent(label));
                    continue;
                }

                var targetPageIndex = i;
                menu.AddItem(new GUIContent(label), false, () => {
                    if (!DuplicateActionTool.TryDuplicateToFlipbookPage(
                            component, actionPropertyPath, targetPageIndex, out var copyError)) {
                        EditorUtility.DisplayDialog("Copy VRCFury Action To Page", copyError, "OK");
                    }
                });
            }
            menu.ShowAsContext();
        }

        // ----------------------------------------------------------------------
        // Editor-target resolution helpers
        //
        // Unity's inspector hosts each component's Editor inside an
        // EditorElement (and, for UIElements editors, a nested
        // InspectorElement). Both are internal types whose field/property names
        // are not part of the public contract. Rather than hardcode m_Editor /
        // boundEditor / etc. (which has caused real bugs when Unity renames
        // them between versions) we look for ANY Editor-typed member on the
        // wrapper, walking the class hierarchy.
        //
        // FindOwningComponent walks UP from a leaf VisualElement (e.g. a
        // PropertyField on an action row) to find the component its editor is
        // editing. EnumerateEditorWrappers walks DOWN from the inspector root
        // to yield every component-editor wrapper, used by the per-Toggle
        // banner placement.
        // ----------------------------------------------------------------------

        private static Component FindOwningComponent(VisualElement element) {
            var current = element;
            while (current != null) {
                if (TryGetEditorTarget(current, out var comp)) return comp;
                current = current.parent;
            }
            return null;
        }

        private static IEnumerable<VisualElement> EnumerateEditorWrappers(VisualElement root) {
            if (root == null) yield break;
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0) {
                var cur = stack.Pop();
                var n = cur.GetType().Name;
                if (n == "EditorElement" || n == "InspectorElement") yield return cur;
                for (int i = 0; i < cur.childCount; i++) stack.Push(cur[i]);
            }
        }

        private static bool TryGetEditorTarget(VisualElement element, out Component component) {
            component = null;
            if (element == null) return false;
            var n = element.GetType().Name;
            if (n != "EditorElement" && n != "InspectorElement") return false;

            var t = element.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public
                                     | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            while (t != null && t != typeof(VisualElement)) {
                foreach (var f in t.GetFields(flags)) {
                    if (!typeof(Editor).IsAssignableFrom(f.FieldType)) continue;
                    object value;
                    try { value = f.GetValue(element); } catch { continue; }
                    if (value is Editor ed && ed != null && ed.target is Component comp) {
                        component = comp;
                        return true;
                    }
                }
                foreach (var p in t.GetProperties(flags)) {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    if (!typeof(Editor).IsAssignableFrom(p.PropertyType)) continue;
                    object value;
                    try { value = p.GetValue(element); } catch { continue; }
                    if (value is Editor ed && ed != null && ed.target is Component comp) {
                        component = comp;
                        return true;
                    }
                }
                t = t.BaseType;
            }
            return false;
        }

        // Click-time resolver: defers the EditorElement walk until the user
        // actually clicks, when the visual tree is guaranteed to be fully
        // built and the editor's target field is populated. Captures and
        // surfaces the resolution via a Debug.Log so any "wrong row" report
        // immediately points to whether the resolver succeeded or fell back.
        private static void InvokeWithResolvedOwner(
            VisualElement field,
            string actionPath,
            string action,
            Action<int, string> next) {
            var owner = FindOwningComponent(field);
            var ownerId = owner != null ? owner.GetInstanceID() : 0;
            if (owner != null) {
                Debug.Log("[VRCF QoL] " + action + " click: path=" + actionPath +
                    " resolved to " + owner.gameObject.name + "/" + owner.GetType().Name +
                    " (instanceId=" + ownerId + ").");
            } else {
                Debug.LogWarning("[VRCF QoL] " + action + " click: path=" + actionPath +
                    " could NOT be tied to a specific VRCFury component via the editor wrapper walk. " +
                    "Falling back to first-match scan on the selected GameObject. " +
                    "If the wrong component gets operated on, the inspector's internal layout has changed.");
            }
            next(ownerId, actionPath);
        }

        // When owningComponentInstanceId is nonzero, looks up that exact
        // Component directly -- this is the path that prevents picking the
        // wrong VRCFury component when two are on the same GameObject and
        // both expose a valid SerializedProperty at the same actionPropertyPath
        // (each component's SerializedObject roots its own path namespace).
        // When zero, falls back to the legacy "first component on the selection
        // that has a valid property at this path" scan -- keeps any call sites
        // that don't (yet) have a visual context for disambiguation working.
        private static bool TryFindSelectedActionComponent(
            int owningComponentInstanceId,
            string actionPropertyPath,
            out Component component,
            out string error) {
            component = null;
            error = null;

            if (owningComponentInstanceId != 0) {
                var resolved = EditorUtility.InstanceIDToObject(owningComponentInstanceId) as Component;
                if (resolved != null) {
                    using (var so = new SerializedObject(resolved)) {
                        var prop = so.FindProperty(actionPropertyPath);
                        if (prop != null && prop.propertyType == SerializedPropertyType.ManagedReference) {
                            component = resolved;
                            return true;
                        }
                    }
                }
            }

            var selection = Selection.activeGameObject;
            if (selection == null) {
                error = "Select the GameObject that owns this VRCFury component, then try again.";
                return false;
            }
            if (!VrcfQol.Reflection.TryEnsure(out var err)) {
                error = err;
                return false;
            }

            Component firstMatch = null;
            foreach (Component c in selection.GetComponents(VrcfQol.Reflection.VRCFuryType)) {
                if (c == null) continue;
                using (var so = new SerializedObject(c)) {
                    var prop = so.FindProperty(actionPropertyPath);
                    if (prop == null || prop.propertyType != SerializedPropertyType.ManagedReference) continue;
                }
                firstMatch = c;
                break;
            }

            if (firstMatch != null) {
                if (owningComponentInstanceId != 0) {
                    Debug.LogWarning("[VRCF QoL] Inline action resolver fell back to first-match scan on '" +
                        selection.name + "' -- EditorElement lookup missed for instance id " +
                        owningComponentInstanceId + ". If the wrong VRCFury component was operated on, " +
                        "the Unity internal type layout may have changed; report this so the resolver can be updated.");
                }
                component = firstMatch;
                return true;
            }

            error = "Could not match this inspector row to a VRCFury action on the selected GameObject. " +
                "If the inspector is locked or showing a different object, select the object again and retry.";
            return false;
        }
    }
}
