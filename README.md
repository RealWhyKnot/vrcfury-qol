# VRCFury QoL

Small Unity editor tools that add convenience actions directly to the VRCFury component inspector. The goal: tools appear *where you're already working* (right-click a page, click a button on a flipbook row) instead of hiding in a separate window.

Currently ships with two flipbook tools. The framework is designed so adding a new tool — flipbook or otherwise — is a single small file.

## What you get

### Flipbook Builder → right-click → *Migrate child toggles as pages*

Right-click anywhere on a Flipbook Builder action. Get a menu item that scans the same GameObject + descendants for other non-flipbook VRCFury toggles, and folds each into this flipbook as a new page. Source VRCFury components are deleted after migration. A confirmation dialog shows exactly what's going to happen; `Ctrl+Z` reverts the whole thing.

### Flipbook page → right-click → *Duplicate page to end*

Right-click on any page inside a Flipbook Builder (the row with the `Page #N` header). Get a menu item that deep-clones that page and appends the copy at the end of the flipbook. The new page is independent — editing it doesn't affect the original.

### Flipbook page → inline *Duplicate → End* button

For discoverability, a small **Duplicate → End** button is also injected next to every `Page #N` label. Clicking it does the same thing as the right-click version. This is best-effort UI injection: if a future VRCFury version changes the page layout, the button silently disappears and the right-click menu still works.

## Installation

Copy the `Editor/` folder into your Unity project, under `Assets/`. Any path works as long as the folder is inside `Assets/` and is called `Editor` (or has an `Editor` folder as an ancestor) — Unity compiles it as an editor-only assembly.

No asmdef, no dependencies beyond VRCFury itself.

```
Assets/
  Editor/
    VrcfQol.cs
    VrcfQolInspectorOverlay.cs
    Tools/
      DuplicateFlipbookPageTool.cs
      MigrateIntoFlipbookTool.cs
```

Tested against VRCFury `1.1303.x`.

## Adding your own tool

A tool is a small `[InitializeOnLoad]` static class that registers itself with `VrcfQol`. Skeleton:

```csharp
using UnityEditor;
using UnityEngine;

namespace UmeVrcfQol.Tools {
    [InitializeOnLoad]
    internal static class MyCoolTool {
        static MyCoolTool() {
            VrcfQol.RegisterPropertyTool(
                label: "VRCF QoL/Do something cool",
                match: prop => prop.propertyPath.EndsWith(".mySpecialField"),
                action: prop => {
                    Debug.Log($"Hello from {prop.propertyPath}");
                    // ... mutate the prop, your SerializedObject, or the underlying target.
                }
            );
        }
    }
}
```

Rules of thumb:

- **`match`** decides whether the right-click menu item appears for a given property. Keep it cheap — it runs on every right-click. Use `propertyPath` for positional matches, or `managedReferenceFullTypename` for `[SerializeReference]` types.
- **`action`** does the work. Wrap destructive changes in `Undo.RegisterCompleteObjectUndo` / `Undo.DestroyObjectImmediate` so users can `Ctrl+Z` the change. Call `EditorUtility.SetDirty(target)` so Unity knows to save.
- **Reflection helpers.** VRCFury's runtime types are `internal`, so the framework ships a resolved-by-name reflection cache at `VrcfQol.Reflection.ToggleType`, `.PagesField`, etc. Use it instead of rolling your own.

The existing tools in `Editor/Tools/` are short — borrow freely.

## How it works under the hood

VRCFury's `VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, and `VF.Model.StateAction.FlipBookBuilderAction` types are marked `internal`. An editor script in a user's `Assets/Editor/` folder can't reference them directly. This project uses reflection to resolve the types by name at runtime, and if VRCFury ever renames a field, each tool surfaces a clean error dialog rather than crashing.

The right-click menu items ride on Unity's `EditorApplication.contextualPropertyMenu` — the exact same hook Unity itself uses for Copy/Paste on fields. The inline **Duplicate → End** button uses a light UIElements overlay that scans open inspector windows every ~250 ms and attaches a button next to each `Page #N` label it finds. No `[CustomPropertyDrawer]` overrides, no fighting with VRCFury's existing drawers.

## Not a replacement for review

These tools make real, destructive changes to your scene (deleting source VRCFury components during a migration). Always:

1. Commit your project to version control first, or duplicate the avatar.
2. Try any tool on one small group before pointing it at anything large.

## License

MIT — see [LICENSE](LICENSE).
