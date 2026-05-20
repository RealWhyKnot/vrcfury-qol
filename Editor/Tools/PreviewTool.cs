// PreviewTool.cs
//
// Non-destructive scene preview for VRCFury toggles and flipbook pages.
// Preview never mutates the original avatar. It creates a temporary clone,
// applies the selected VRCFury state actions to that clone, hides the source
// avatar while previewing, then restores it when the user stops previewing.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UmeVrcfQol.Tools {

    [InitializeOnLoad]
    internal static class PreviewTool {
        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string PreviewPrefix = "[VRCF QoL Preview]";
        private const string SessionPreviewId = "WhyKnot.VrcfQol.Preview.CloneId";
        private const string SessionSourceId = "WhyKnot.VrcfQol.Preview.SourceId";
        private const string SessionSourceWasHidden = "WhyKnot.VrcfQol.Preview.SourceWasHidden";
        private const string PrefsSourceGlobalId = "WhyKnot.VrcfQol.Preview.SourceGlobalId";
        private const string PrefsSourceWasHidden = "WhyKnot.VrcfQol.Preview.SourceWasHidden";

        private static PreviewSession _active;

        static PreviewTool() {
            RestoreAbandonedPreviewState();
            CleanupAbandonedPreviewClones();
            EditorApplication.delayCall += RestoreAbandonedPreviewState;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpened += (_, __) => RestoreAbandonedPreviewState();
            AssemblyReloadEvents.beforeAssemblyReload += StopActivePreview;
            EditorApplication.quitting += StopActivePreview;

            VrcfQol.RegisterFlipbookPageButton(
                text: "Preview",
                tooltip: "Create a temporary avatar copy with this flipbook page applied. This does not move the Scene camera.",
                onClick: TogglePagePreview,
                order: -10,
                textProvider: ctx => IsPreviewingPage(ctx) ? "Stop Previewing" : "Preview",
                tooltipProvider: ctx => IsPreviewingPage(ctx)
                    ? "Destroy the temporary preview copy and return to the original avatar."
                    : "Create a temporary avatar copy with this flipbook page applied. This does not move the Scene camera.",
                danger: IsPreviewingPage
            );
            VrcfQol.RegisterFlipbookPageTool(
                label: "WhyKnot/vrcfury-qol/Preview page",
                action: ShowPagePreview,
                priority: 35
            );
            VrcfQol.RegisterFlipbookBuilderTool(
                label: "WhyKnot/vrcfury-qol/Preview flipbook page...",
                action: ShowFlipbookPagePicker,
                priority: 35
            );
            VrcfQol.RegisterToggleTool(
                label: "WhyKnot/vrcfury-qol/Preview toggle",
                action: ShowTogglePreview,
                priority: 35
            );
        }

        internal static void ShowTogglePreview(Component component) {
            if (!TryResolveToggleFromComponent(component, out var ctx, out var error)) {
                EditorUtility.DisplayDialog("Preview", error, "OK");
                return;
            }
            ShowTogglePreview(ctx);
        }

        internal static void ToggleTogglePreview(Component component) {
            if (IsPreviewingComponent(component)) {
                StopActivePreview();
                return;
            }
            ShowTogglePreview(component);
        }

        internal static void StopActivePreview() {
            StopPreview("stopped");
        }

        [MenuItem("Tools/WhyKnot/vrcfury-qol/Stop previewing", priority = 36)]
        private static void StopPreviewFromMenu() {
            StopActivePreview();
        }

        [MenuItem("Tools/WhyKnot/vrcfury-qol/Stop previewing", true)]
        private static bool StopPreviewFromMenuValidate() {
            return _active != null && _active.CloneRoot != null;
        }

        internal static bool IsPreviewingComponent(Component component) {
            return _active != null &&
                _active.CloneRoot != null &&
                component != null &&
                _active.SourceComponent == component;
        }

        internal static bool IsPreviewingPage(VrcfQol.FlipbookContext ctx) {
            return IsPreviewing(ctx.vrcfComponent, ctx.pageIndex);
        }

        private static void ShowTogglePreview(VrcfQol.ToggleContext ctx) {
            if (ctx.flipbookAction != null) {
                var pages = VrcfQol.Reflection.PagesField.GetValue(ctx.flipbookAction) as IList;
                if (pages == null || pages.Count == 0) {
                    EditorUtility.DisplayDialog("Preview", "This flipbook has no pages to preview.", "OK");
                    return;
                }
                ShowPageMenu(ctx.vrcfComponent, ctx.toggleName, pages);
                return;
            }

            StartPreview(ctx.vrcfComponent, ctx.actions, PreviewName("Toggle", ctx.toggleName), "toggle", -1);
        }

        private static void ShowFlipbookPagePicker(VrcfQol.FlipbookContext ctx) {
            if (ctx.pages == null || ctx.pages.Count == 0) {
                EditorUtility.DisplayDialog("Preview", "This flipbook has no pages to preview.", "OK");
                return;
            }
            ShowPageMenu(ctx.vrcfComponent, ctx.toggleName, ctx.pages);
        }

        private static void ShowPagePreview(VrcfQol.FlipbookContext ctx) {
            if (ctx.pages == null || ctx.pageIndex < 0 || ctx.pageIndex >= ctx.pages.Count) {
                EditorUtility.DisplayDialog("Preview", $"Page #{ctx.pageIndex + 1} was not found.", "OK");
                return;
            }
            StartPreview(
                ctx.vrcfComponent,
                GetActionsFromPage(ctx.pages[ctx.pageIndex]),
                $"{PreviewName("Flipbook", ctx.toggleName)} - Page #{ctx.pageIndex + 1}",
                $"page #{ctx.pageIndex + 1}",
                ctx.pageIndex);
        }

        private static void TogglePagePreview(VrcfQol.FlipbookContext ctx) {
            if (IsPreviewingPage(ctx)) {
                StopActivePreview();
                return;
            }
            ShowPagePreview(ctx);
        }

        private static void ShowPageMenu(Component component, string toggleName, IList pages) {
            var menu = new GenericMenu();
            for (int i = 0; i < pages.Count; i++) {
                int pageIndex = i;
                menu.AddItem(new GUIContent($"Page {i + 1}"), false, () => {
                    StartPreview(
                        component,
                        GetActionsFromPage(pages[pageIndex]),
                        $"{PreviewName("Flipbook", toggleName)} - Page #{pageIndex + 1}",
                        $"page #{pageIndex + 1}",
                        pageIndex);
                });
            }
            menu.ShowAsContext();
        }

        private static void StartPreview(Component sourceComponent, IList actions, string title, string shortLabel, int pageIndex) {
            if (sourceComponent == null) {
                EditorUtility.DisplayDialog("Preview", "Could not resolve the VRCFury component.", "OK");
                return;
            }
            if (actions == null || actions.Count == 0) {
                EditorUtility.DisplayDialog("Preview", "There are no actions on this toggle/page to preview.", "OK");
                return;
            }

            StopPreview("replaced");

            var sourceRoot = FindAvatarRoot(sourceComponent);
            if (sourceRoot == null) {
                EditorUtility.DisplayDialog("Preview", "Could not find the avatar root to duplicate.", "OK");
                return;
            }

            GameObject clone = null;
            try {
                clone = Object.Instantiate(sourceRoot, sourceRoot.transform.parent);
                clone.name = $"{PreviewPrefix} {sourceRoot.name}";
                clone.transform.SetSiblingIndex(sourceRoot.transform.GetSiblingIndex() + 1);
                MarkHierarchyTemporary(clone);
                AlignCloneWithSource(sourceRoot, clone);
                StripPreviewComponents(clone);

                var session = new PreviewSession {
                    SourceComponent = sourceComponent,
                    SourceRoot = sourceRoot,
                    CloneRoot = clone,
                    Title = title,
                    ShortLabel = shortLabel,
                    PageIndex = pageIndex,
                    SourceWasHidden = IsSceneHidden(sourceRoot),
                    PreviousSelection = Selection.objects,
                };
                _active = session;
                RememberPreview(session);

                var applied = 0;
                foreach (var action in actions) {
                    if (action == null) continue;
                    if (ApplyActionToClone(action, session)) applied++;
                }

                if (applied == 0) {
                    StopPreview("empty");
                    EditorUtility.DisplayDialog("Preview",
                        "None of the actions on this toggle/page could be applied to a temporary preview copy.",
                        "OK");
                    return;
                }

                HideSourceAvatar(sourceRoot);
                Debug.Log($"[VRCF QoL] Started preview '{title}' on temporary clone '{clone.name}' ({applied} action(s) applied).");
            } catch (Exception ex) {
                Debug.LogException(ex);
                if (clone != null) Object.DestroyImmediate(clone);
                _active = null;
                ForgetPreview();
                EditorUtility.DisplayDialog("Preview", "Preview failed. See Console.\n\n" + ex.Message, "OK");
            }
        }

        private static bool IsPreviewing(Component component, int pageIndex) {
            return _active != null &&
                _active.CloneRoot != null &&
                component != null &&
                _active.SourceComponent == component &&
                _active.PageIndex == pageIndex;
        }

        private static bool ApplyActionToClone(object action, PreviewSession session) {
            try {
                switch (action.GetType().Name) {
                    case "ObjectToggleAction":
                        return ApplyObjectToggle(action, session);
                    case "BlendShapeAction":
                        return ApplyBlendShape(action, session);
                    case "MaterialAction":
                        return ApplyMaterialSwap(action, session);
                    case "MaterialPropertyAction":
                        return ApplyMaterialProperty(action, session);
                    case "AnimationClipAction":
                        return ApplyAnimationClip(action, session);
                    case "FlipbookAction":
                        return ApplyPoiyomiFlipbookFrame(action, session);
                    case "PoiyomiUVTileAction":
                        return ApplyPoiyomiUvTile(action, session);
                    case "ScaleAction":
                        return ApplyScale(action, session);
                    case "FlipBookBuilderAction":
                    case "FxFloatAction":
                    case "ResetPhysboneAction":
                    case "WorldDropAction":
                    case "SpsOnAction":
                    case "BlockBlinkingAction":
                    case "BlockVisemesAction":
                    case "DisableGesturesAction":
                        return false;
                    default:
                        return false;
                }
            } catch (Exception ex) {
                Debug.LogWarning($"[VRCF QoL] Preview skipped {action.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool ApplyObjectToggle(object action, PreviewSession session) {
            var obj = MapObject(GetObjectField<GameObject>(action, "obj"), session);
            if (obj == null) return false;
            var mode = GetField(action, "mode")?.GetValue(action)?.ToString() ?? "TurnOn";
            if (mode == "TurnOff") obj.SetActive(false);
            else if (mode == "Toggle") obj.SetActive(!obj.activeSelf);
            else obj.SetActive(true);
            AddFocus(session, obj);
            return true;
        }

        private static bool ApplyBlendShape(object action, PreviewSession session) {
            var shape = GetString(action, "blendShape");
            if (string.IsNullOrEmpty(shape)) return false;
            var value = GetFloat(action, "blendShapeValue");
            var targets = new List<SkinnedMeshRenderer>();
            if (GetBool(action, "allRenderers")) {
                targets.AddRange(session.CloneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            } else {
                var renderer = MapObject(GetObjectField<Renderer>(action, "renderer"), session) as SkinnedMeshRenderer;
                if (renderer != null) targets.Add(renderer);
            }

            var applied = false;
            foreach (var target in targets) {
                if (target == null || target.sharedMesh == null) continue;
                var idx = target.sharedMesh.GetBlendShapeIndex(shape);
                if (idx < 0) continue;
                target.SetBlendShapeWeight(idx, value);
                AddFocus(session, target);
                applied = true;
            }
            return applied;
        }

        private static bool ApplyMaterialSwap(object action, PreviewSession session) {
            var renderer = MapObject(GetObjectField<Renderer>(action, "renderer"), session);
            var mat = GetGuidObject(GetField(action, "mat")?.GetValue(action)) as Material;
            if (renderer == null || mat == null) return false;
            var index = GetInt(action, "materialIndex");
            var mats = renderer.sharedMaterials;
            if (index < 0 || index >= mats.Length) return false;
            mats[index] = mat;
            renderer.sharedMaterials = mats;
            AddFocus(session, renderer);
            return true;
        }

        private static bool ApplyMaterialProperty(object action, PreviewSession session) {
            var propertyName = GetString(action, "propertyName");
            if (string.IsNullOrEmpty(propertyName) || propertyName.Contains(".")) return false;

            var renderers = new List<Renderer>();
            if (GetBool(action, "affectAllMeshes")) {
                renderers.AddRange(session.CloneRoot.GetComponentsInChildren<Renderer>(true));
            } else {
                var target = MapObject(GetObjectField<GameObject>(action, "renderer2"), session);
                var renderer = target != null ? target.GetComponent<Renderer>() : null;
                if (renderer != null) renderers.Add(renderer);
            }

            var type = GetField(action, "propertyType")?.GetValue(action)?.ToString() ?? "Float";
            foreach (var renderer in renderers) {
                if (renderer == null) continue;
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                if (type == "Color") block.SetColor(propertyName, GetColor(action, "valueColor"));
                else if (type == "Vector" || type == "St") block.SetVector(propertyName, GetVector4(action, "valueVector"));
                else block.SetFloat(propertyName, GetFloat(action, "value"));
                renderer.SetPropertyBlock(block);
                AddFocus(session, renderer);
            }
            return renderers.Count > 0;
        }

        private static bool ApplyAnimationClip(object action, PreviewSession session) {
            var clip = GetGuidObject(GetField(action, "clip")?.GetValue(action)) as AnimationClip;
            if (clip == null) return false;
            clip.SampleAnimation(session.CloneRoot, Mathf.Max(0f, clip.length));
            AddFocus(session, session.CloneRoot);
            return true;
        }

        private static bool ApplyPoiyomiFlipbookFrame(object action, PreviewSession session) {
            var renderer = MapObject(GetObjectField<Renderer>(action, "renderer"), session);
            if (renderer == null) return false;
            SetRendererFloat(renderer, "_FlipbookCurrentFrame", Mathf.Floor(GetInt(action, "frame")) + 0.5f);
            AddFocus(session, renderer);
            return true;
        }

        private static bool ApplyPoiyomiUvTile(object action, PreviewSession session) {
            var renderer = MapObject(GetObjectField<Renderer>(action, "renderer"), session);
            if (renderer == null) return false;
            var prop = GetBool(action, "dissolve") ? "_UVTileDissolveAlpha_Row" : "_UDIMDiscardRow";
            prop += $"{GetInt(action, "row")}_{GetInt(action, "column")}";
            var renamed = GetString(action, "renamedMaterial");
            if (!string.IsNullOrEmpty(renamed)) prop += $"_{renamed}";
            SetRendererFloat(renderer, prop, 0f);
            AddFocus(session, renderer);
            return true;
        }

        private static bool ApplyScale(object action, PreviewSession session) {
            var obj = MapObject(GetObjectField<GameObject>(action, "obj"), session);
            if (obj == null) return false;
            obj.transform.localScale *= GetFloat(action, "scale");
            AddFocus(session, obj);
            return true;
        }

        private static void SetRendererFloat(Renderer renderer, string propertyName, float value) {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetFloat(propertyName, value);
            renderer.SetPropertyBlock(block);
        }

        private static void Tick() {
            if (_active == null) return;
            if (_active.CloneRoot == null) {
                RestoreSourceVisibility(_active.SourceRoot, _active.SourceWasHidden);
                _active = null;
                ForgetPreview();
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode) {
                StopPreview("stopped before play mode");
            }
        }

        private static void StopPreview(string reason) {
            if (_active == null) return;
            var clone = _active.CloneRoot;
            var source = _active.SourceRoot;
            var sourceWasHidden = _active.SourceWasHidden;
            var title = _active.Title;
            var previousSelection = _active.PreviousSelection;
            var restoreSelection = Selection.activeGameObject == null ||
                (clone != null && IsPreviewObject(Selection.activeGameObject, clone));
            _active = null;
            if (clone != null) Object.DestroyImmediate(clone);
            RestoreSourceVisibility(source, sourceWasHidden);
            if (restoreSelection) Selection.objects = previousSelection ?? Array.Empty<Object>();
            ForgetPreview();
            SceneView.RepaintAll();
            Debug.Log($"[VRCF QoL] Preview '{title}' {reason}; temporary clone destroyed.");
        }

        private static IList GetActionsFromPage(object page) {
            if (page == null || !VrcfQol.Reflection.TryEnsure(out _)) return null;
            var state = VrcfQol.Reflection.PageStateField.GetValue(page);
            return state == null ? null : VrcfQol.Reflection.StateActionsField.GetValue(state) as IList;
        }

        private static bool TryResolveToggleFromComponent(
            Component component,
            out VrcfQol.ToggleContext ctx,
            out string error) {
            ctx = default;
            error = null;
            if (!VrcfQol.Reflection.TryEnsure(out error)) return false;
            if (component == null || component.GetType() != VrcfQol.Reflection.VRCFuryType) {
                error = "Could not resolve the selected VRCFury component.";
                return false;
            }

            var r = VrcfQol.Reflection;
            var content = r.ContentField.GetValue(component);
            if (content == null || content.GetType() != r.ToggleType) {
                error = "This VRCFury component is not a Toggle.";
                return false;
            }

            var state = r.ToggleStateField.GetValue(content);
            var actions = r.StateActionsField.GetValue(state) as IList;
            var flipbook = VrcfQol.FindFlipbookAction(actions);
            var slider = false;
            try {
                if (r.ToggleSliderField != null) slider = (bool)r.ToggleSliderField.GetValue(content);
            } catch {
                slider = false;
            }

            ctx = new VrcfQol.ToggleContext {
                vrcfComponent = component,
                toggle = content,
                toggleName = (string)r.ToggleNameField.GetValue(content) ?? "",
                state = state,
                actions = actions,
                flipbookAction = flipbook,
                slider = slider,
            };
            return true;
        }

        private static GameObject FindAvatarRoot(Component component) {
            if (component == null) return null;
            var animator = component.GetComponentInParent<Animator>(true);
            if (animator != null) return animator.gameObject;
            var t = component.transform;
            while (t.parent != null) t = t.parent;
            return t.gameObject;
        }

        private static void AlignCloneWithSource(GameObject source, GameObject clone) {
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;
        }

        private static Bounds CalculateBounds(GameObject root, bool visibleOnly = false) {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var bounds = new Bounds(root.transform.position, Vector3.one);
            var hasBounds = false;
            foreach (var renderer in renderers) {
                if (renderer == null) continue;
                if (visibleOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy)) continue;
                if (!hasBounds) {
                    bounds = renderer.bounds;
                    hasBounds = true;
                } else {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }

        private static void MarkHierarchyTemporary(GameObject clone) {
            foreach (var transform in clone.GetComponentsInChildren<Transform>(true)) {
                transform.gameObject.hideFlags = HideFlags.DontSave;
                foreach (var component in transform.GetComponents<Component>()) {
                    if (component != null) component.hideFlags = HideFlags.DontSave;
                }
            }
        }

        private static void StripPreviewComponents(GameObject clone) {
            foreach (var component in clone.GetComponentsInChildren<Component>(true)) {
                if (component == null) continue;
                if (component.GetType().FullName == "VF.Model.VRCFury") {
                    Object.DestroyImmediate(component);
                }
            }
        }

        private static void AddFocus(PreviewSession session, Renderer renderer) {
            if (session == null || renderer == null) return;
            EncapsulateFocus(session, renderer.bounds);
        }

        private static void AddFocus(PreviewSession session, GameObject obj) {
            if (session == null || obj == null) return;
            EncapsulateFocus(session, CalculateBounds(obj));
        }

        private static void EncapsulateFocus(PreviewSession session, Bounds bounds) {
            if (!session.HasFocusBounds) {
                session.FocusBounds = bounds;
                session.HasFocusBounds = true;
            } else {
                session.FocusBounds.Encapsulate(bounds);
            }
        }

        private static T MapObject<T>(T source, PreviewSession session) where T : Object {
            if (source == null || session == null || session.SourceRoot == null || session.CloneRoot == null) return null;
            if (source is GameObject go) {
                var mapped = MapTransform(go.transform, session);
                return mapped != null ? mapped.gameObject as T : null;
            }
            if (source is Component component) {
                var mappedTransform = MapTransform(component.transform, session);
                if (mappedTransform == null) return null;
                var sourceComponents = component.gameObject.GetComponents(component.GetType());
                var index = Array.IndexOf(sourceComponents, component);
                var mappedComponents = mappedTransform.gameObject.GetComponents(component.GetType());
                if (index >= 0 && index < mappedComponents.Length) return mappedComponents[index] as T;
                return mappedTransform.GetComponent(component.GetType()) as T;
            }
            return source;
        }

        private static Transform MapTransform(Transform source, PreviewSession session) {
            if (source == null) return null;
            if (source == session.SourceRoot.transform) return session.CloneRoot.transform;
            var path = GetRelativePath(session.SourceRoot.transform, source);
            return string.IsNullOrEmpty(path) ? null : session.CloneRoot.transform.Find(path);
        }

        private static string GetRelativePath(Transform root, Transform target) {
            var parts = new Stack<string>();
            var cur = target;
            while (cur != null && cur != root) {
                parts.Push(cur.name);
                cur = cur.parent;
            }
            return cur == root ? string.Join("/", parts.ToArray()) : null;
        }

        private static bool IsPreviewObject(GameObject selected, GameObject cloneRoot) {
            if (selected == null || cloneRoot == null) return false;
            return selected == cloneRoot || selected.transform.IsChildOf(cloneRoot.transform);
        }

        private static T GetObjectField<T>(object target, string fieldName) where T : Object {
            return GetField(target, fieldName)?.GetValue(target) as T;
        }

        private static string GetString(object target, string fieldName) {
            return GetField(target, fieldName)?.GetValue(target) as string ?? "";
        }

        private static int GetInt(object target, string fieldName) {
            var value = GetField(target, fieldName)?.GetValue(target);
            try { return Convert.ToInt32(value); } catch { return 0; }
        }

        private static float GetFloat(object target, string fieldName) {
            var value = GetField(target, fieldName)?.GetValue(target);
            try { return Convert.ToSingle(value); } catch { return 0f; }
        }

        private static bool GetBool(object target, string fieldName) {
            var value = GetField(target, fieldName)?.GetValue(target);
            return value is bool b && b;
        }

        private static Color GetColor(object target, string fieldName) {
            var value = GetField(target, fieldName)?.GetValue(target);
            return value is Color color ? color : Color.white;
        }

        private static Vector4 GetVector4(object target, string fieldName) {
            var value = GetField(target, fieldName)?.GetValue(target);
            return value is Vector4 vector ? vector : Vector4.zero;
        }

        private static Object GetGuidObject(object wrapper) {
            return wrapper == null ? null : GetField(wrapper, "objRef")?.GetValue(wrapper) as Object;
        }

        private static FieldInfo GetField(object target, string fieldName) {
            return target == null ? null : FindFieldInHierarchy(target.GetType(), fieldName);
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string name) {
            while (type != null) {
                var field = type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        private static string PreviewName(string type, string toggleName) {
            return string.IsNullOrEmpty(toggleName) ? $"{type} Preview" : $"{type}: {toggleName}";
        }

        private static void CleanupAbandonedPreviewClones() {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>()) {
                if (go == null || string.IsNullOrEmpty(go.name)) continue;
                if (!go.name.StartsWith(PreviewPrefix, StringComparison.Ordinal)) continue;
                if (!EditorUtility.IsPersistent(go)) Object.DestroyImmediate(go);
            }
        }

        private static void RememberPreview(PreviewSession session) {
            SessionState.SetInt(SessionPreviewId, session.CloneRoot != null ? session.CloneRoot.GetInstanceID() : 0);
            SessionState.SetInt(SessionSourceId, session.SourceRoot != null ? session.SourceRoot.GetInstanceID() : 0);
            SessionState.SetBool(SessionSourceWasHidden, session.SourceWasHidden);
            StoreSourceForCrashRecovery(session.SourceRoot, session.SourceWasHidden);
        }

        private static void ForgetPreview() {
            SessionState.EraseInt(SessionPreviewId);
            SessionState.EraseInt(SessionSourceId);
            SessionState.EraseBool(SessionSourceWasHidden);
            EditorPrefs.DeleteKey(PrefsSourceGlobalId);
            EditorPrefs.DeleteKey(PrefsSourceWasHidden);
        }

        private static void RestoreAbandonedPreviewState() {
            var hasSourceState = SessionState.GetInt(SessionSourceId, 0) != 0 ||
                EditorPrefs.HasKey(PrefsSourceGlobalId);
            var source = ResolveRememberedSource();
            var clone = EditorUtility.InstanceIDToObject(SessionState.GetInt(SessionPreviewId, 0)) as GameObject;
            if (source == null && hasSourceState && EditorPrefs.HasKey(PrefsSourceGlobalId)) {
                CleanupAbandonedPreviewClones();
                return;
            }

            var sourceWasHidden = SessionState.GetBool(
                SessionSourceWasHidden,
                EditorPrefs.GetBool(PrefsSourceWasHidden, false));
            RestoreSourceVisibility(source, sourceWasHidden);
            if (clone != null && !EditorUtility.IsPersistent(clone)) Object.DestroyImmediate(clone);
            CleanupAbandonedPreviewClones();
            _active = null;
            ForgetPreview();
        }

        private static void HideSourceAvatar(GameObject source) {
            if (source == null) return;
            SceneVisibilityManager.instance.Hide(source, true);
        }

        private static void RestoreSourceVisibility(GameObject source, bool sourceWasHidden) {
            if (source == null || sourceWasHidden) return;
            SceneVisibilityManager.instance.Show(source, true);
        }

        private static bool IsSceneHidden(GameObject source) {
            return source != null && SceneVisibilityManager.instance.IsHidden(source);
        }

        private static void StoreSourceForCrashRecovery(GameObject source, bool sourceWasHidden) {
            if (source == null) return;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(source).ToString();
            if (string.IsNullOrEmpty(id)) return;
            EditorPrefs.SetString(PrefsSourceGlobalId, id);
            EditorPrefs.SetBool(PrefsSourceWasHidden, sourceWasHidden);
        }

        private static GameObject ResolveRememberedSource() {
            var source = EditorUtility.InstanceIDToObject(SessionState.GetInt(SessionSourceId, 0)) as GameObject;
            if (source != null) return source;

            var idText = EditorPrefs.GetString(PrefsSourceGlobalId, string.Empty);
            if (string.IsNullOrEmpty(idText)) return null;
            if (!GlobalObjectId.TryParse(idText, out var id)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
        }

        private sealed class PreviewSession {
            public Component SourceComponent;
            public GameObject SourceRoot;
            public GameObject CloneRoot;
            public string Title;
            public string ShortLabel;
            public int PageIndex;
            public bool SourceWasHidden;
            public bool HasFocusBounds;
            public Bounds FocusBounds;
            public Object[] PreviousSelection;
        }
    }
}
