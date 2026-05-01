# VRCFury QoL Wiki

`vrcfury-qol` is a small framework + collection of Unity Editor tools that add convenience actions directly to the [VRCFury](https://vrcfury.com/) component inspector. Tools appear *where you're already working* — right-click a page, click a button on a flipbook row, drop in two objects to swap references — instead of hiding behind a separate window.

The [README](https://github.com/RealWhyKnot/vrcfury-qol/blob/main/README.md) is the quick-start; this wiki goes deeper.

## How it works (60 seconds)

VRCFury's runtime types (`VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, etc.) are marked `internal`, so a script in `Assets/Editor/` can't reference them directly. `vrcfury-qol` does two things:

1. **A reflection cache** (`Editor/VrcfQol.cs` → `ReflectionCache`) resolves VRCFury's types and fields by name on first use, caches them, and exposes typed handles to tools. If VRCFury renames a field in a future version, the cache returns null and tools degrade gracefully (banner explains what's missing, right-click menu silently drops affected items) — no crashes.
2. **A registration API** lets each tool plug in with a single `[InitializeOnLoad]` static class. Tools never touch the inspector's visual tree directly. Right-click items ride on `EditorApplication.contextualPropertyMenu`, and inline buttons / banners are injected by a small UIElements overlay (`VrcfQolInspectorOverlay.cs`) that scans inspector windows every ~250 ms and attaches widgets next to recognisable labels.

The full picture is on [[Architecture]].

## Read these first

- **[[Installation]]** — drop-in instructions and the hot-reload bootstrap step
- **[[Tools-Overview]]** — every shipping tool, what it does, where to find it
- **[[Architecture]]** — framework design: registration, reflection, overlay
- **[[Adding-a-Tool]]** — developer guide with worked examples for every `Register*` API
- **[[Troubleshooting]]** — common failure modes and what to check first

## What's in the box

- Move all VRCFury components between GameObjects (whole-component or merge-into-one mode)
- Replace object references in bulk across the selected hierarchy
- Auto-sync `useGlobalParam` / `globalParam` on every Toggle (with per-toggle opt-out)
- Migrate child VRCFury Toggles into a parent Flipbook
- Duplicate a flipbook page to the end (right-click or inline button)
- Hot-reload watcher + per-assembly compile log

See [[Tools-Overview]] for screenshots and details.
