# Adding a Tool

A tool is a small `[InitializeOnLoad]` static class that registers itself with `VrcfQol`. The framework provides typed helpers so your tool never has to walk the reflection cache by hand.

The existing tools in `Editor/Tools/` are short — borrow freely. This page is the worked-example reference for every `Register*` API.

## Right-click on a flipbook page

```csharp
[InitializeOnLoad]
internal static class MyPageTool {
    static MyPageTool() {
        VrcfQol.RegisterFlipbookPageTool(
            label: "VRCF QoL/My page action",
            action: ctx => {
                // ctx.pages is the IList of FlipBookPage, ctx.pageIndex is the 0-based index.
                Debug.Log($"Page #{ctx.pageIndex + 1} of \"{ctx.toggleName}\"");
            },
            priority: 10
        );
    }
}
```

## Inline button next to every `Page #N` label

```csharp
VrcfQol.RegisterFlipbookPageButton(
    text: "⇅",
    tooltip: "Move this page down.",
    onClick: ctx => { /* reorder ctx.pages */ },
    order: 5
);
```

## Right-click on the Flipbook Builder itself

```csharp
VrcfQol.RegisterFlipbookBuilderTool(
    label: "VRCF QoL/Reverse all pages",
    action: ctx => {
        var reversed = new List<object>();
        foreach (var p in ctx.pages) reversed.Add(p);
        reversed.Reverse();
        ctx.pages.Clear();
        foreach (var p in reversed) ctx.pages.Add(p);
        EditorUtility.SetDirty(ctx.vrcfComponent);
    }
);
```

## Right-click on any VRCFury Toggle

```csharp
VrcfQol.RegisterToggleTool(
    label: "VRCF QoL/Print my name",
    action: ctx => Debug.Log($"Toggle '{ctx.toggleName}' has slider={ctx.slider}")
);
```

## Right-click on a specific VRCFury action type

```csharp
VrcfQol.RegisterActionTool(
    vrcfActionFullName: "VF.Model.StateAction.ObjectToggleAction",
    label: "VRCF QoL/Ping target",
    action: (prop, action) => {
        // reflect into `action` to read its fields without referencing the internal type.
    }
);
```

## Generic fallback

If none of the typed helpers fit, `RegisterPropertyTool(label, match, action, priority, enabled)` lets you match any `SerializedProperty` directly.

```csharp
VrcfQol.RegisterPropertyTool(
    label: "VRCF QoL/Inspect this property",
    match: prop => prop.propertyPath.EndsWith(".myField"),
    action: prop => { /* work with prop */ },
    priority: 0,
    enabled: prop => /* true to enable, false to grey out */ true
);
```

## Standalone EditorWindow tool (e.g. the Replace-References window)

For multi-object workflows that don't fit a right-click context, register a `[MenuItem]` directly and open an `EditorWindow`:

```csharp
internal static class MyTool {
    [MenuItem("Window/VRCFury QoL/My Tool...")]
    private static void Open() {
        if (!VrcfQol.Reflection.TryEnsure(out var error)) {
            EditorUtility.DisplayDialog("My Tool", error, "OK");
            return;
        }
        MyToolWindow.Open();
    }
}

internal sealed class MyToolWindow : EditorWindow {
    internal static void Open() {
        var w = GetWindow<MyToolWindow>(false, "My Tool", true);
        w.Show();
    }
    // ... OnGUI()
}
```

See `Editor/Tools/ReplaceReferencesTool.cs` and `ReplaceReferencesWindow.cs` for a complete example.

## Rules of thumb

- **`match`** decides whether the right-click menu item appears. Keep it cheap — it runs on every right-click. Use `propertyPath` for positional matches, or `managedReferenceFullTypename` for `[SerializeReference]` types.
- **`enabled`** (optional) greys out the menu item without hiding it — useful for mutually-exclusive "Enable X" / "Disable X" pairs.
- **`action`** does the work. Wrap destructive changes in `Undo.RegisterCompleteObjectUndo` / `Undo.DestroyObjectImmediate` so users can `Ctrl+Z` the change. Call `EditorUtility.SetDirty(target)` so Unity knows to save.
- **Reflection helpers.** VRCFury's runtime types are `internal`, so the framework ships a resolved-by-name reflection cache at `VrcfQol.Reflection.ToggleType`, `.PagesField`, etc. Use it instead of rolling your own.
- **Optional fields.** When you add a new reflected field that some VRCFury versions don't have, treat it as `null`-tolerant — call sites should null-check and either skip the work or surface a clean message. See `AutoGlobalParameterTool.SyncAll` for the pattern.
- **No `[CustomPropertyDrawer]`.** Drawer overrides fight VRCFury's own drawers and break across versions. Use the inline-button registry (`RegisterFlipbookPageButton`) or the inspector banner pattern in `VrcfQolInspectorOverlay` instead.

The framework is small (~600 LoC). Reading `Editor/VrcfQol.cs` end-to-end is the fastest way to internalise the API.
