# Tools Overview

Every shipping tool, where to find it, and what it does. All tools wrap destructive changes in a single Undo group, so `Ctrl+Z` reverts them.

## Move VRCFury Components

**Where:** Right-click a GameObject in the hierarchy → *VRCFury QoL → Move all VRCFury components to…*. Also available under *GameObject → VRCFury QoL → Move all VRCFury components to…* in the menu bar.

**What it does:** Moves every VRCFury MonoBehaviour from a source GameObject to a destination GameObject in one Undo step. Two modes:

- **Move whole components** *(default)* — Each VRCFury MonoBehaviour is recreated on the destination as its own component. Preserves serialization shape exactly via `ComponentUtility.CopyComponent` + `PasteComponentAsNew` (so `[SerializeReference]` graphs round-trip correctly through Unity's own pipeline).
- **Merge into one component** — All features get appended into a single VRCFury component on the destination, using the legacy `config.features` list. Useful when you want everything authored on one carrier object. Greyed out if the installed VRCFury version doesn't expose `VRCFuryConfig.features`.

The dialog enforces same-scene-as-source and disables itself on identical source/destination.

> _Screenshot: TODO_

## Replace References

**Where:** *Window → VRCFury QoL → Replace References…*, or right-click a hierarchy selection → *VRCFury QoL → Replace references in selection…* (which pre-fills the search list).

**What it does:** Finds every `Object` reference inside any VRCFury component on the selected hierarchy that matches a `From` object, and lets you opt-in per match to replace them with a `To` object.

How it walks: for each VRCFury component on the search roots and their descendants, the tool builds a `SerializedObject` and iterates with `SerializedProperty.NextVisible(true)` — this descends into `[SerializeReference]` polymorphic graphs (Toggle, ArmatureLink, FullController, etc.) automatically. Every `ObjectReference` property whose value `==` the From object is recorded with its property path and the type of the enclosing `[SerializeReference]` parent (e.g. *ArmatureLink*, *Toggle*).

You can:
- Tick / untick matches individually before applying.
- *All* / *None* buttons toggle every match at once.
- *Ping* a row to highlight the underlying VRCFury component in the hierarchy.
- *Refresh* re-runs the search (matches against the current From should drop to zero after Apply).

Apply is grouped into one Undo step. If a match's current value drifted between Find and Apply, that match is skipped (logged as "stale"); the rest still apply.

**Type-mismatch warning:** If From and To are different concrete types (e.g. `Transform` and `GameObject`), the tool warns — Unity's typed fields will reject the assignment and that match will be skipped.

> _Screenshot: TODO_

## Auto Global Parameter

**Where:** Always-on background sync; visible as a colored banner at the top of every VRCFury Toggle inspector. Right-click anywhere on a Toggle for the manual control menu.

**What it does:** Every ~0.5 s, for every VRCFury Toggle in the open scene / prefab stage, this tool enforces:

- `Use Global Parameter` = **true**
- `Global Parameter`     = the toggle's Menu Path

The Toggle inspector shows a green banner confirming auto-sync is active. The banner has an inline **Disable** button to opt out on a per-toggle basis (or right-click → *VRCF QoL → Disable auto-update of global parameter*). Opt-outs are stored in `EditorPrefs` keyed by the component's `GlobalObjectId`, so the preference follows the scene/prefab on this machine but doesn't travel cross-machine.

**Why bother?** When VRCFury regenerates your avatar it can rename the internal parameter it picked for each toggle. Anything you've pinned to those names — animator constraints, external OSC clients, VRChat's per-avatar parameter memory — gets silently wiped. Explicitly setting `Global Parameter = MenuPath` forces VRCFury to keep the same name forever, so customisations survive a rebuild.

The sync deliberately **does not** wrap each tick in Undo (that would flood the Undo stack); it just `SetDirty`s when something actually changes. Direct user edits to either field still undo normally.

## Migrate Child Toggles into Flipbook

**Where:** Right-click a Flipbook Builder action → *VRCFury QoL → Migrate child toggles as pages*.

**What it does:** Scans the Flipbook's GameObject + descendants for non-flipbook VRCFury Toggles. Folds each into the flipbook as a new page (reusing the source's `State` directly — no clone, so actions stay byte-identical). Source VRCFury components are deleted afterward. A confirmation dialog shows the exact list of toggles that will be migrated and deleted.

Single Undo step reverts everything.

## Duplicate Flipbook Page

**Where:** Right-click a `Page #N` row → *VRCFury QoL → Duplicate page to end*. Also injected as an inline **Duplicate → End** button next to every `Page #N` label.

**What it does:** Deep-clones the page (every action is round-tripped via `JsonUtility.ToJson` / `FromJson` so the new state is independent) and appends the copy to the end of the same flipbook.

The inline button is best-effort UI injection: if a future VRCFury version changes the page layout, the button silently disappears and the right-click menu still works.

## Hot Reload + Compile Log

**Where:** Always-on background tool; output at `<ProjectRoot>/Logs/VrcfQolHotReload.log`.

**What it does:** A `FileSystemWatcher` on `Assets/**/*.cs` triggers `AssetDatabase.Refresh()` shortly after a script file changes — so saving from an external editor (with Unity unfocused) still picks up the edit. Subscribes to `CompilationPipeline.assemblyCompilationFinished` and writes a one-line summary per assembly + one line per error to the log file. The log rolls over at 512 KB (old copy kept as `.log.old`). Errors come out as `[Error] <file>(<line>,<col>): <message>` so they're easy to grep.

Bootstrap: focus Unity once after the first install so it compiles the scripts. From then on the watcher runs whenever Unity is open.
