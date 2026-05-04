# Changelog

All notable changes to this project will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-05-03

First release as a VRChat Package Manager (VPM) package, installable via the Creator Companion at `https://vpm.whyknot.dev/index.json`.

### Added
- VPM package metadata (`package.json`) declaring `dev.whyknot.vrcfury-qol` with a hard `vpmDependencies` on `com.vrcfury.vrcfury` (≥ 1.1300.0). VCC will refuse to install this package without VRCFury present.
- Editor assembly definition (`Editor/dev.whyknot.vrcfury-qol.Editor.asmdef`) scoping the tools to the Editor platform. The asmdef intentionally does **not** declare an assembly reference to `VRCFury` because all VRCFury access in this package goes through runtime reflection (`VrcfQol.Reflection.VRCFuryType`) — that lets the package compile against future VRCFury versions whose internal layout has shifted, and surface a friendly "VRCFury runtime assembly not found" message rather than a hard compile error.

### Changed
- **Breaking for loose-script users.** Prior to 1.0.0 the recommended install was to drop the `Editor/` folder anywhere under your `Assets/` tree; Unity compiled the scripts into the project's default editor assembly. With the new asmdef, code now compiles into a dedicated `dev.whyknot.vrcfury-qol.Editor` assembly. If you were previously importing as loose scripts and you upgrade by adding the asmdef in place, *internal* type references inside this package keep working, but any **external** code in your project that referenced these tools' types (e.g. `WhyKnot.VrcfQol.VrcfQol` from your own scripts) will need its asmdef to add `dev.whyknot.vrcfury-qol.Editor` to its `references`.
- Recommended migration: remove the old loose-script copy from `Assets/` and reinstall via VCC. Unity asset GUIDs are regenerated on import; nothing inside this package references its own files by GUID, so no project-side cleanup is required beyond removing the duplicate.
