# VRCFury Toggles → Flipbook

A tiny Unity editor tool that migrates a bunch of independent **VRCFury Toggles** into pages of a single **Flipbook Builder** action on a destination toggle. Useful when you want to collapse many on/off toggles (outfit variants, hair styles, preset configurations…) into one radial slider.

## What it does

Given a selection in your Hierarchy:

- Finds exactly **one** VRCFury Toggle whose state contains a Flipbook Builder action — this is the **destination**.
- Takes every **other** VRCFury Toggle in the selected subtree (that does not already have a Flipbook Builder) and appends its entire action list as a new page in the destination's flipbook.
- Deletes the source VRCFury components (leaves the GameObjects alone in case they hold other things).
- All done in a single Undo group — `Ctrl+Z` reverts the whole migration.

Pages are appended in hierarchy / sibling order. Existing pages on the destination flipbook are preserved; migrated sources are added after.

## Installation

1. Copy `VrcfMigrateTogglesToFlipbook.cs` into any `Assets/Editor/` folder in your Unity project. (Create the folder if you don't have one — Unity compiles anything under a folder named `Editor` as an editor-only assembly.)
2. Let Unity compile. Done.

No asmdef, no dependencies beyond VRCFury itself.

## Usage

1. In the Hierarchy, select the GameObject that holds your destination flipbook toggle, or any ancestor of it. (The script scans the selection plus all descendants.)
2. Top menu: **Tools → VRCFury → Migrate Toggles Into Flipbook**.
3. A confirmation dialog shows which flipbook was identified and lists the source toggles in the order they'll be added. Click **Migrate** to proceed.
4. Save the scene.

If anything looks wrong, **Ctrl+Z** reverts everything.

## Matching rules

- **Destination:** a VRCFury Toggle whose `state.actions` contains a `FlipBookBuilderAction`. Typically this is a slider (radial) toggle you've set up with an empty Flipbook Builder.
- **Source:** any other VRCFury Toggle in the same subtree whose state does *not* contain a Flipbook Builder action.
- **Conflict:** if zero or multiple destinations are found, the tool aborts with a clear dialog listing what it saw. Narrow your selection and try again.

Source-toggle settings other than the action list (menu path, `saved`, `defaultOn`, exclusive tags, icon, transitions, etc.) are intentionally dropped — a flipbook page only needs the actions.

## How it works under the hood

The VRCFury runtime types (`VF.Model.VRCFury`, `VF.Model.Feature.Toggle`, `VF.Model.StateAction.FlipBookBuilderAction`, etc.) are marked `internal`, so an editor script in a user's `Assets/Editor/` folder can't reference them directly. The script uses reflection to locate the types and their fields by name. This means:

- No asmdef changes or `InternalsVisibleTo` required.
- The script keeps working across VRCFury upgrades as long as the field names (`content`, `state`, `actions`, `pages`, `name`, `slider`) stay stable.
- If VRCFury renames those fields in a future version, the script shows a clean error dialog rather than a mysterious crash.

Tested against VRCFury `1.1303.x`.

## Not a replacement for review

This tool does real, destructive changes to your scene (deleting source VRCFury components). Always:

1. Commit your project to version control first, or duplicate the scene/avatar.
2. Try it on one small group before pointing it at anything large.

## Contributing

Issues and PRs welcome. If you add features (page names, per-source filters, different matching rules), please keep the core behavior opt-in behind menu items or dialog options so existing users aren't surprised.

## License

MIT — see [LICENSE](LICENSE).
