# VRCFury Flipbook Tools

A small set of Unity editor tools for working with **VRCFury Flipbook Builder** toggles. Useful when you've got a bunch of independent VRCFury toggles (outfit pieces, hair styles, preset configurations…) and want to collapse them into a single radial slider — or tweak an existing flipbook without clicking through the Inspector.

All tools live under **Tools → VRCFury →** in the main Unity menu.

## Tools

### Migrate Toggles Into Flipbook

`VrcfMigrateTogglesToFlipbook.cs`

Takes every non-flipbook VRCFury Toggle in the selected subtree and folds it into a destination flipbook toggle as a new page. Source VRCFury components are deleted afterward.

**Use when:** you have many independent toggles and want to turn them into a single radial selector.

**Usage:**

1. Select the GameObject holding your destination flipbook toggle (or an ancestor).
2. **Tools → VRCFury → Migrate Toggles Into Flipbook**.
3. Review the confirmation dialog (destination + ordered list of sources) and click **Migrate**.
4. Save the scene. `Ctrl+Z` reverts the whole migration.

**Matching rules:**

- **Destination:** exactly one VRCFury Toggle in the subtree whose state contains a `FlipBookBuilderAction`. Zero or multiple → the tool aborts with a clear dialog.
- **Sources:** every other VRCFury Toggle in the same subtree whose state does not already have a `FlipBookBuilderAction`.
- **Order:** hierarchy / sibling order.
- **Existing pages:** preserved. Migrated sources are appended after.
- **Dropped settings:** source toggles' menu path, `saved`, `defaultOn`, exclusive tags, icon, transitions, etc. are not carried over — a flipbook page is just a set of actions.

### Duplicate Flipbook Page

`VrcfDuplicateFlipbookPage.cs`

Opens a small window that lists every page of the selected flipbook and gives each row a **Duplicate to end** button. Clicking one deep-copies that page and appends the copy to the end of the flipbook.

**Use when:** you want to clone page #7 to page #8 as a starting point, without rebuilding the actions by hand.

**Usage:**

1. Select the GameObject holding your flipbook toggle (or an ancestor).
2. **Tools → VRCFury → Duplicate Flipbook Page…**.
3. In the window, click **Duplicate to end** on any row.
4. Save the scene. `Ctrl+Z` reverts.

**Deep copy guarantee:** the new page is independent of the source — editing page #8's actions after duplication will not affect page #7. Each action is cloned via `JsonUtility` round-trip, which preserves Unity `Object` references (GameObjects, Renderers, animation clips) by instance id while creating fresh instances of the value-type fields.

## Installation

1. Copy the `.cs` files into any `Assets/Editor/` folder in your Unity project. (Create the folder if you don't have one — Unity compiles anything under a folder named `Editor` as an editor-only assembly.)
2. Let Unity compile. Done.

No asmdef, no dependencies beyond VRCFury itself.

## How it works under the hood

The VRCFury runtime types (`VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, `VF.Model.StateAction.FlipBookBuilderAction`, etc.) are marked `internal`, so an editor script in `Assets/Editor/` can't reference them directly. The scripts use reflection to locate the types and their fields by name. That means:

- No asmdef changes or `InternalsVisibleTo` required.
- The scripts keep working across VRCFury upgrades as long as the field names (`content`, `state`, `actions`, `pages`, `name`) stay stable.
- If VRCFury renames those fields in a future version, the scripts show a clean error dialog rather than crashing.

Tested against VRCFury `1.1303.x`.

## Not a replacement for review

These tools do real, destructive changes to your scene (deleting source VRCFury components in the migrator). Always:

1. Commit your project to version control first, or duplicate the scene/avatar.
2. Try it on one small group before pointing it at anything large.

## Contributing

Issues and PRs welcome. If you add features (page names, per-source filters, different matching rules), please keep the core behavior opt-in behind menu items or dialog options so existing users aren't surprised.

## License

MIT — see [LICENSE](LICENSE).
