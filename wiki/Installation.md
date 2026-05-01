# Installation

`vrcfury-qol` ships as a flat folder of `.cs` files compiled by Unity itself. There's no asmdef, no package manifest, no native binaries.

## Option A — Drop into your project (simplest)

Copy the `Editor/` folder into your Unity project under any path that ends in (or contains) `Editor/`. Unity will pick it up as an editor-only assembly automatically.

```
Assets/
  YourFolder/
    Editor/
      VrcfQol.cs
      VrcfQolInspectorOverlay.cs
      VrcfQolHotReload.cs
      Tools/
        AutoGlobalParameterTool.cs
        DuplicateFlipbookPageTool.cs
        MigrateIntoFlipbookTool.cs
        MoveVrcfComponentsTool.cs
        ReplaceReferencesTool.cs
        ReplaceReferencesWindow.cs
```

## Option B — Symlink for live development

Clone the repo somewhere outside your Unity project, then symlink the `Editor/` folder into the project. Edits in the repo apply to the live project without copy-pasting.

**Windows (PowerShell, run as admin):**
```powershell
New-Item -ItemType Junction -Path "C:\Path\To\YourProject\Assets\VrcfQol" -Target "C:\Path\To\vrcfury-qol\Editor"
```

**Linux / macOS:**
```sh
ln -s /path/to/vrcfury-qol/Editor /path/to/YourProject/Assets/VrcfQol
```

## Hot-reload bootstrap

After installing for the first time, **focus Unity once** so it compiles the new scripts. From then on `VrcfQolHotReload.cs` watches `Assets/**/*.cs` and triggers `AssetDatabase.Refresh()` whenever a file changes — even when Unity isn't focused.

The hot-reload tool also writes a per-assembly compile log to `<ProjectRoot>/Logs/VrcfQolHotReload.log` (rolls over at 512 KB; old copy kept as `.log.old`). Tail it to watch compiles in real time:

```sh
tail -f Logs/VrcfQolHotReload.log
```

Errors come out as `[Error] <file>(<line>,<col>): <message>` so they're easy to grep.

## Compatibility

- **Unity 2022.3.x** — the version we test against on `D:\WhyKnot Stuff\VRChat\Avatars\Ume\` and similar avatar projects.
- **VRCFury 1.1303.x** — the latest version we explicitly verified. Older versions usually work; if a tool's reflection cache fails to resolve a field, the tool silently no-ops or shows a clean error dialog (see [[Troubleshooting]]).

## Uninstalling

Delete the folder you installed into. EditorPrefs entries (per-toggle opt-outs from [[Tools-Overview#auto-global-parameter|the Auto Global Parameter tool]]) persist after uninstall — they're harmless but if you want to clean them, search EditorPrefs for keys starting with `VrcfQol.AutoUpdateParam.OptOut.`.
