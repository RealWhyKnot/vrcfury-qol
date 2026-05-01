# Architecture

Three pieces:

1. **`VrcfQol.cs`** — the framework: reflection cache, registration API, helpers.
2. **`VrcfQolInspectorOverlay.cs`** — UIElements overlay that injects inline buttons and banners into VRCFury inspectors.
3. **`VrcfQolHotReload.cs`** — background watcher that triggers `AssetDatabase.Refresh()` on script changes when Unity is unfocused, plus a compile log.

Everything else under `Editor/Tools/` is a tool — usually a single `[InitializeOnLoad]` static class that registers itself with `VrcfQol`.

## The reflection cache

VRCFury's runtime types are marked `internal`. A user script in `Assets/Editor/` can't reference `VF.Model.VRCFury` directly. Instead we resolve types by name on first use:

```csharp
VrcfuryAsm = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "VRCFury");
VRCFuryType = VrcfuryAsm.GetType("VF.Model.VRCFury", false);
ToggleType  = VrcfuryAsm.GetType("VF.Model.Feature.Toggle", false);
// ...
ContentField     = VRCFuryType.GetField("content", any);
ToggleNameField  = ToggleType .GetField("name",    any);
// ...
```

Resolution is lazy and cached. Tools call `VrcfQol.Reflection.TryEnsure(out var error)` at the top of every entry point. The first call does the lookup; subsequent calls hit the cache. If anything fails to resolve, `TryEnsure` returns false with a human-readable error string and the tool either shows a dialog (for explicit user actions) or silently no-ops (for background polling).

**Optional fields.** Some VRCFury versions don't expose every field we'd like (`ToggleSliderField`, `ToggleUseGlobalParamField`, `ConfigField`, etc.). Those are `null`-tolerant — `TryEnsure` still succeeds when they're missing, and the tools that depend on them either degrade gracefully (banner explains, menu items disappear) or warn the user up-front (e.g. the Move-tool's *Merge into one* mode is greyed out when `VRCFuryConfig.features` isn't available).

The cache itself is an instance singleton (`VrcfQol.Reflection`) rather than a static class, so tools can write `var r = VrcfQol.Reflection;` once and read `r.X` everywhere. That captures the cache reference at function start and keeps call sites readable.

## Tool registration

The framework exposes typed registration helpers so tools never have to walk reflection by hand. Pick the one that fits your trigger:

| Trigger | Helper | Context type |
|---------|--------|--------------|
| Right-click any property in any inspector | `RegisterPropertyTool(label, match, action, priority, enabled)` | `SerializedProperty` |
| Right-click a Flipbook page row | `RegisterFlipbookPageTool(label, action, priority, enabled)` | `FlipbookContext` |
| Right-click a Flipbook Builder action | `RegisterFlipbookBuilderTool(label, action, priority, enabled)` | `FlipbookContext` |
| Right-click a VRCFury Toggle | `RegisterToggleTool(label, action, priority, enabled)` | `ToggleContext` |
| Right-click a specific `VF.Model.StateAction.*` | `RegisterActionTool(fullName, label, action, priority)` | `(SerializedProperty, object)` |
| Inline button next to every `Page #N` label | `RegisterFlipbookPageButton(text, tooltip, onClick, order, visible)` | `FlipbookContext` |

The first three flow through the same internal registry and ride on Unity's `EditorApplication.contextualPropertyMenu` — the same hook Unity itself uses for Copy/Paste on fields. The inline-button registry is read by `VrcfQolInspectorOverlay`.

`FlipbookContext` and `ToggleContext` are small structs that wrap the resolved component, the reflected feature/state, the actions list, etc. — anything a tool would otherwise have to re-resolve from a `SerializedProperty`.

## The inspector overlay

`VrcfQolInspectorOverlay.cs` runs every ~250 ms, scans every open `InspectorWindow`'s `rootVisualElement`, and:

1. **Injects inline buttons** next to every label whose text matches `^Page #(\d+)$`. Buttons come from the `RegisterFlipbookPageButton` registry.
2. **Injects a status banner** at the top of every VRCFury Toggle inspector — green when [auto-global-parameter sync](Tools-Overview#auto-global-parameter) is enabled for that component, brown when opted-out. The banner has an inline opt-in/out button.

This is **best-effort UI injection.** If a future VRCFury version restyles the inspector, the overlay finds nothing to attach to and the inline buttons silently disappear. The right-click menu remains the authoritative entry point — it still works because it rides on `contextualPropertyMenu`, not on inspector visual layout. We deliberately avoid `[CustomPropertyDrawer]` overrides: those would fight VRCFury's own drawers and are version-fragile.

The 250 ms poll is cheap (a couple of UQuery scans on the inspector tree). It's not on the hot path of any user interaction.

## Undo / Redo

Tools that mutate scene state follow this pattern:

```csharp
var group = Undo.GetCurrentGroup();
Undo.SetCurrentGroupName("VRCF QoL: ...");
try {
    Undo.RegisterCompleteObjectUndo(component, "...");
    // ... mutate ...
    Undo.DestroyObjectImmediate(otherComponent);   // not DestroyImmediate
    EditorUtility.SetDirty(component);
    Undo.CollapseUndoOperations(group);
} catch {
    Undo.RevertAllInCurrentGroup();
    throw;
}
```

The collapse means `Ctrl+Z` reverts the entire operation in one step. The catch block ensures partial failures don't leave the scene in a half-mutated state.

The Auto-Global-Parameter sync is the one exception: it polls every 500 ms and would flood the Undo stack if every tick registered. It uses `SetDirty` only — direct user edits to the relevant fields still undo normally, and the next tick re-syncs.

## Hot reload

`VrcfQolHotReload.cs` runs a `FileSystemWatcher` over `Assets/**/*.cs`, debounces events for 0.4 s, and calls `AssetDatabase.Refresh()` if Unity isn't already compiling. It also subscribes to `CompilationPipeline.assemblyCompilationFinished` and writes one line per assembly + one line per error to `<ProjectRoot>/Logs/VrcfQolHotReload.log`, with timestamp prefixes.

The log rolls over at 512 KB — old copy kept as `.log.old`. Tail it from a terminal to watch compiles in real time. Errors come out formatted as `[Error] <file>(<line>,<col>): <message>` so they're easy to grep.

If `FileSystemWatcher` fails to start (sandboxing, permissions, etc.), the tool logs the failure and silently degrades — Unity's normal focus-based refresh still works.
