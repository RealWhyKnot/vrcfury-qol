# VRCFury QoL

Small Unity editor tools that add convenience actions directly to the VRCFury component inspector. The goal: tools appear *where you're already working* (right-click a page, click a button on a flipbook row, see a green banner on a Toggle) instead of hiding in a separate window.

The framework is designed so adding a new tool — flipbook or otherwise — is usually a single small file with one `[InitializeOnLoad]` registration.

## What you get

### Every VRCFury Toggle → auto-synced Global Parameter

Every VRCFury Toggle in your open scene gets two fields automatically enforced:

- `Use Global Parameter` = **true**
- `Global Parameter`     = the toggle's Menu Path

The Toggle inspector shows a **green banner** up top confirming auto-sync is active, so you don't manually edit the Global Parameter field (that would just get overwritten). The banner has an inline **Disable** button to opt out on a per-toggle basis (or right-click anywhere on the Toggle for the same). Opt-outs are stored in `EditorPrefs` keyed by the component's `GlobalObjectId`, so the preference follows the scene/prefab on this machine.

**Why bother?** When VRCFury regenerates your avatar it is free to rename the internal parameter it picked for each toggle. Anything you pinned to those names — animator constraints, external OSC clients, VRChat's per-avatar parameter memory — gets silently wiped. Explicitly setting Global Parameter = Menu Path forces VRCFury to keep the same name forever, so your customisations survive an avatar rebuild.

### Flipbook Builder → right-click → *Migrate child toggles as pages*

Right-click anywhere on a Flipbook Builder action. Get a menu item that scans the same GameObject + descendants for other non-flipbook VRCFury toggles, and folds each into this flipbook as a new page. Source VRCFury components are deleted after migration. A confirmation dialog shows exactly what's going to happen; `Ctrl+Z` reverts the whole thing.

### Flipbook page → right-click → *Duplicate page to end*

Right-click on any page inside a Flipbook Builder (the row with the `Page #N` header). Get a menu item that deep-clones that page and appends the copy at the end of the flipbook. The new page is independent — editing it doesn't affect the original.

### Flipbook page → inline *Duplicate → End* button

For discoverability, a small **Duplicate → End** button is injected next to every `Page #N` label. Clicking it does the same thing as the right-click version. This is best-effort UI injection: if a future VRCFury version changes the page layout, the button silently disappears and the right-click menu still works.

### Hot reload + compile-error log

`VrcfQolHotReload.cs` watches `Assets/**/*.cs` via a `FileSystemWatcher` and calls `AssetDatabase.Refresh()` when it sees a change, so Unity picks up edits even when its window isn't focused. It also subscribes to `CompilationPipeline.assemblyCompilationFinished` and appends a one-line summary per assembly plus one line per error to:

```
<ProjectRoot>/Logs/VrcfQolHotReload.log
```

The log rolls over at 512 KB (old copy is kept as `VrcfQolHotReload.log.old`). Tail it from a terminal to watch compiles happen in real time; errors come out formatted as `[Error] <file>(<line>,<col>): <message>` so they're easy to grep.

Bootstrap: after the first time you install or update these scripts, focus Unity once so it compiles them. From then on, saving a script externally (for example via an editor on another monitor) will trigger a refresh + compile without tabbing into Unity.

## Installation

Copy the `Editor/` folder into your Unity project, under `Assets/`. Any path works as long as the folder is inside `Assets/` and is called `Editor` (or has an `Editor` folder as an ancestor) — Unity compiles it as an editor-only assembly.

No asmdef, no dependencies beyond VRCFury itself.

```
Assets/
  Editor/
    VrcfQol.cs
    VrcfQolInspectorOverlay.cs
    VrcfQolHotReload.cs
    Tools/
      AutoGlobalParameterTool.cs
      DuplicateFlipbookPageTool.cs
      MigrateIntoFlipbookTool.cs
```

Tested against VRCFury `1.1303.x`.

## Adding your own tool

A tool is a small `[InitializeOnLoad]` static class that registers itself with `VrcfQol`. The framework provides typed helpers so your tool never has to walk the reflection cache by hand.

### Right-click on a flipbook page

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

### Inline button next to every `Page #N` label

```csharp
VrcfQol.RegisterFlipbookPageButton(
    text: "⇅",
    tooltip: "Move this page down.",
    onClick: ctx => { /* reorder ctx.pages */ },
    order: 5
);
```

### Right-click on the Flipbook Builder itself

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

### Right-click on any VRCFury Toggle

```csharp
VrcfQol.RegisterToggleTool(
    label: "VRCF QoL/Print my name",
    action: ctx => Debug.Log($"Toggle '{ctx.toggleName}' has slider={ctx.slider}")
);
```

### Right-click on a specific VRCFury action type

```csharp
VrcfQol.RegisterActionTool(
    vrcfActionFullName: "VF.Model.StateAction.ObjectToggleAction",
    label: "VRCF QoL/Ping target",
    action: (prop, action) => {
        // reflect into `action` to read its fields without referencing the internal type.
    }
);
```

### Generic fallback

If none of the typed helpers fit, `RegisterPropertyTool(label, match, action, priority, enabled)` lets you match any `SerializedProperty` directly.

Rules of thumb:

- **`match`** decides whether the right-click menu item appears. Keep it cheap — it runs on every right-click. Use `propertyPath` for positional matches, or `managedReferenceFullTypename` for `[SerializeReference]` types.
- **`enabled`** (optional) greys out the menu item without hiding it — useful for mutually-exclusive "Enable X" / "Disable X" pairs.
- **`action`** does the work. Wrap destructive changes in `Undo.RegisterCompleteObjectUndo` / `Undo.DestroyObjectImmediate` so users can `Ctrl+Z` the change. Call `EditorUtility.SetDirty(target)` so Unity knows to save.
- **Reflection helpers.** VRCFury's runtime types are `internal`, so the framework ships a resolved-by-name reflection cache at `VrcfQol.Reflection.ToggleType`, `.PagesField`, etc. Use it instead of rolling your own.

The existing tools in `Editor/Tools/` are short — borrow freely.

## How it works under the hood

VRCFury's `VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, and `VF.Model.StateAction.FlipBookBuilderAction` types are marked `internal`. An editor script in a user's `Assets/Editor/` folder can't reference them directly. This project uses reflection to resolve the types by name at runtime, and if VRCFury ever renames a field, each tool surfaces a clean error dialog (or silently no-ops, for the background sync) rather than crashing.

The right-click menu items ride on Unity's `EditorApplication.contextualPropertyMenu` — the exact same hook Unity itself uses for Copy/Paste on fields. The inline buttons and inspector banner use a light UIElements overlay that scans open inspector windows every ~250 ms and attaches its widgets next to recognisable labels. No `[CustomPropertyDrawer]` overrides, no fighting with VRCFury's existing drawers.

The auto-global-parameter sync polls every 500 ms via `EditorApplication.update`, walks `Resources.FindObjectsOfTypeAll(VRCFuryType)`, filters to scene/prefab-stage components, and only calls `SetDirty` when a field actually needed to change. It deliberately does NOT register an Undo step per tick — that would flood the Undo stack with background noise — but the user's own edits to the name field still undo normally, and the next tick will re-sync.

## Not a replacement for review

These tools make real, destructive changes to your scene (deleting source VRCFury components during a migration, forcing `useGlobalParam` on by default, etc). Always:

1. Commit your project to version control first, or duplicate the avatar.
2. Try any tool on one small group before pointing it at anything large.

## License

MIT — see [LICENSE](LICENSE).
