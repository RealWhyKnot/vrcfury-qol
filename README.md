# VRCFury QoL

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![VRCFury](https://img.shields.io/badge/VRCFury-1.1303.x-7e57c2.svg)](https://vrcfury.com/)
[![Unity](https://img.shields.io/badge/Unity-2022.3-000000.svg?logo=unity)](https://unity.com/)

Quality-of-life Editor tools for [VRCFury](https://vrcfury.com/). The goal: features appear *where you're already working* — right-click a page, click a button on a flipbook row, see a banner on a Toggle, drop in two objects to swap references — instead of hiding behind a separate window.

The framework is designed so adding a new tool is usually a single small file with one `[InitializeOnLoad]` registration. See the [Adding a Tool](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Adding-a-Tool) wiki page.

## What you get

- **Move all VRCFury components** between GameObjects in one Undo step. Pick "Move whole components" to preserve serialization shape, or "Merge into one component" to consolidate features onto a single carrier object. *(Right-click a GameObject → VRCFury QoL → Move all VRCFury components to…)*
- **Replace references in selection.** Pick GameObjects, the window lists every distinct Object referenced by their VRCFury components — one row per unique value, with a count of how many places it's used. Drag a replacement onto a row and click Apply; every occurrence of that object across the scan is swapped in one Undo step. Per-selection *children* toggle controls whether each entry recurses into descendants. *(Tools → VRCFury QoL → Replace References…, or right-click a selection → VRCFury QoL → Replace references in selection…)*
- **Missing-reference warning.** On editor startup (and again every assembly reload), the tool scans every VRCFury component in the open scene for `Object` references whose target has been deleted, and pops a non-modal window listing each one with a Ping button. Dismiss it once and it stays dismissed until the next reload — so real problems can't linger silently, but you don't get nagged after you've acknowledged the situation. Can be re-run anytime via *Tools → VRCFury QoL → Check for missing references…*.
- **Auto-synced Global Parameter on every Toggle.** A green banner on the Toggle inspector confirms `useGlobalParam = true` and `globalParam = MenuPath` are kept in sync. Per-toggle opt-out via the banner button or right-click menu. Stops VRCFury from silently renaming parameters during avatar regen.
- **Migrate child toggles into a Flipbook.** Right-click a Flipbook Builder action; it scans the same GameObject + descendants for non-flipbook VRCFury Toggles and folds each into the flipbook as a new page, deleting the source components. Confirmation dialog shows exactly what will happen.
- **Duplicate a flipbook page** to the end via right-click or an inline `Duplicate → End` button next to every `Page #N` label.
- **Hot reload + compile-error log.** Watches `Assets/**/*.cs` and triggers `AssetDatabase.Refresh()` even when Unity is unfocused. Per-assembly compile summary + errors appended to `<ProjectRoot>/Logs/VrcfQolHotReload.log` (rolls over at 512 KB).

Detailed walkthroughs of every tool live in the [Tools Overview](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Tools-Overview) wiki page.

## Installation

### VCC (recommended)

Add the WhyKnot VPM listing to the [VRChat Creator Companion](https://creators.vrchat.com/), then this package shows up under **Manage Project → Add Package**.

1. Click [this `vcc://` link](vcc://vpm/addRepo?url=https://vpm.whyknot.dev/index.json) — VCC opens and pre-fills the listing URL.
2. Or, in VCC: **Settings → Packages → Add Repository**, paste `https://vpm.whyknot.dev/index.json`, click **I Understand, Add Repository**.
3. Open any project, click **Manage Project**, find **VRCFury QoL** in the package list, hit **Add**.

Unity compiles the package into a dedicated `dev.whyknot.vrcfury-qol.Editor` assembly (`Editor/` only — nothing leaks into runtime builds). Hard-depends on `com.vrcfury.vrcfury` (≥ 1.1300.0); VCC will refuse to install without VRCFury present.

### Manual install

For Unity projects not managed by VCC: download `dev.whyknot.vrcfury-qol-X.Y.Z.zip` from [the latest release](https://github.com/RealWhyKnot/vrcfury-qol/releases/latest), unzip into `Packages/dev.whyknot.vrcfury-qol/` (so `Packages/dev.whyknot.vrcfury-qol/package.json` exists), and Unity's Package Manager picks it up on next refresh. VRCFury must already be installed in the project.

Tested against VRCFury **1.1303.x** on Unity **2022.3**.

For per-clone setup steps (hot-reload bootstrap, etc.) see the [Installation](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Installation) wiki page.

## Adding your own tool

A tool is a small `[InitializeOnLoad]` static class that registers itself with `VrcfQol`. The framework provides typed helpers so your tool never has to walk the reflection cache by hand. The full developer guide with examples for every `Register*` method lives at [Adding a Tool](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Adding-a-Tool).

## Documentation

- [Wiki home](https://github.com/RealWhyKnot/vrcfury-qol/wiki) — long-form docs
- [Tools Overview](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Tools-Overview) — every shipping tool
- [Architecture](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Architecture) — how the framework hooks VRCFury via reflection + UI overlay
- [Adding a Tool](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Adding-a-Tool) — developer guide for new tools
- [Troubleshooting](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Troubleshooting) — common failure modes

## Not a replacement for review

These tools make real, destructive changes to your scene (deleting source VRCFury components during a migration, replacing object references in bulk, forcing `useGlobalParam` on by default, etc). Always:

1. Commit your project to version control first, or duplicate the avatar.
2. Try any tool on one small group before pointing it at anything large.

## Contributing

Bug reports, feature requests, and pull requests are all welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the dev loop and PR conventions.

## License

MIT — see [LICENSE](LICENSE).
