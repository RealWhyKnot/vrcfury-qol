# Troubleshooting

Common failure modes and what to check first. If your problem isn't here, file a [bug report](https://github.com/RealWhyKnot/vrcfury-qol/issues/new?template=bug_report.yml) — include the Unity version, VRCFury version, and the affected tool.

## "VRCFury runtime assembly ('VRCFury') not found."

You see this in a dialog when invoking a tool. It means the reflection cache couldn't find the VRCFury assembly in the current AppDomain.

**Check:**
- VRCFury is actually imported into the project. The package shows up in `Packages/com.vrcfury.vrcfury/` (VCC install) or `Assets/VRCFury/` (manual install).
- The VRCFury assembly name is still `"VRCFury"` (case-sensitive). If a future VRCFury release renames the assembly, this lookup will fail until [`Editor/VrcfQol.cs`](https://github.com/RealWhyKnot/vrcfury-qol/blob/main/Editor/VrcfQol.cs) is updated.

## "Could not locate one or more VRCFury internal types."

The assembly was found but a specific type (`VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, etc.) couldn't be resolved.

**Check:**
- VRCFury version: this project is tested against **1.1303.x**. Significantly older or newer versions may rename internals. If your version moved a type, please [open a `vrcfury: compat` issue](https://github.com/RealWhyKnot/vrcfury-qol/issues) with the new path and we'll update the cache.

## Right-click menu items don't appear

The framework hooks `EditorApplication.contextualPropertyMenu`, which runs when Unity right-clicks a serialized property. If items aren't showing:

- **Are you right-clicking the right thing?** Most items target a specific property: a Toggle component, a flipbook page row, the Flipbook Builder action header. Right-clicking the inspector background or a label *outside* a serialized property won't trigger the menu.
- **Has the framework loaded?** Tools register in their static constructor (`[InitializeOnLoad]`). Focus Unity once after install so it compiles and runs the constructors. Look for any `[VRCF QoL]` log lines on startup as a sanity check.
- **A tool's `match()` may be returning false.** If you're trying to invoke a specific action (e.g. *Migrate child toggles as pages*), the trigger has to be on the corresponding type. The Migrate tool's match expects a `[SerializeReference]` of `VF.Model.StateAction.FlipBookBuilderAction`; right-clicking the page rows below it won't match.

## Inline buttons (e.g. *Duplicate → End*) don't appear

The buttons are injected by `VrcfQolInspectorOverlay.cs`, which scans inspector windows every ~250 ms looking for labels matching `^Page #\d+$`. If buttons don't show up:

- **VRCFury restyled the inspector.** This is best-effort UI injection. The right-click menu on a page row still works (`VRCF QoL/Duplicate page to end`).
- **The page label format changed.** Future VRCFury versions might localise or restyle "Page #N". The overlay can be updated to recognise the new format.

## Auto Global Parameter banner missing

The green banner at the top of every Toggle inspector confirms auto-sync is active. If it's absent:

- Check the inspector overlay didn't fail silently. Console logs from the inspector overlay's try/catch may surface there.
- The Toggle has a name set, right? The sync skips toggles with empty/whitespace names — there's nothing useful to set `globalParam` to. Once you name the toggle, the next 0.5 s tick syncs it and the banner appears.
- The component might be in a closed prefab stage. The sync only operates on the open scene + open prefab stage.

## Replace-References window finds zero matches

- **The From object isn't actually referenced in any selected GameObject's VRCFury components.** Try right-clicking a known reference target in the hierarchy → *VRCFury QoL → Replace references in selection…* — the window pre-fills the search list and you can verify by setting From to that target.
- **You picked a different instance.** For scene objects, `==` is identity-based — two different GameObjects with the same name aren't equal. Drag the actual referenced object into the From field, not a same-named sibling.
- **The reference is a string path, not an Object reference.** This tool only handles `ObjectReference` properties. Some VRCFury fields store paths as strings (e.g. animation curve target paths) — those aren't covered. See [issue #5](https://github.com/RealWhyKnot/vrcfury-qol/issues) for ongoing work.

## Replace-References "skipped N stale entries"

A match's underlying ref drifted between *Find* and *Apply*. Causes include:
- The user manually edited the field in another inspector after Find.
- A different tool (or another Apply) changed the ref first.

The stale match is skipped to avoid blindly overwriting a value the user didn't see. Click *Refresh* to re-scan.

## Move tool: "Merge into one component" is greyed out

The installed VRCFury version doesn't expose the legacy `VRCFuryConfig.features` list (or the lookup failed). Use **Move whole components** instead — same end result for most workflows, just preserves the original component count.

If you need merge behaviour and the field has been renamed, please [file a `vrcfury: compat` issue](https://github.com/RealWhyKnot/vrcfury-qol/issues) with the version you're on.

## Hot-reload log not updating

`<ProjectRoot>/Logs/VrcfQolHotReload.log` is written by `VrcfQolHotReload.cs`. If the file isn't there or isn't growing on script saves:

- **First-time install.** Focus Unity once so it compiles `VrcfQolHotReload.cs` itself; from then on the watcher runs.
- **`FileSystemWatcher` failed to start.** This shows up as an exception in the Unity console at editor startup. Causes: anti-virus, sandboxing, or unusual filesystem types. The tool degrades gracefully — Unity's built-in focus-based refresh still works, the log just doesn't get written.

## Undo doesn't revert everything

Each tool collapses its operation into a single Undo group, but a few caveats:

- **Direct edits between tool runs.** If you ran a tool, then made manual edits, then `Ctrl+Z`-ed, you're undoing the manual edits first.
- **Auto Global Parameter ticks aren't undoable.** The polling sync deliberately doesn't register Undo (would flood the stack). If you don't want it touching your toggle, opt out via the inspector banner.
- **`ComponentUtility.PasteComponentAsNew`** registers its own Undo entry; the Move tool collapses these into one group, but if Unity itself fails the paste mid-operation the partial result is reverted (the tool's catch block calls `Undo.RevertAllInCurrentGroup`).
