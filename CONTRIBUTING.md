# Contributing

Welcome, and thanks for taking an interest. Bug reports, feature requests, and pull requests are all welcome — open an issue or PR against this repo.

## Before you start

A bit of orientation goes a long way:

- Skim the [Architecture](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Architecture) wiki page to see how tools, the reflection cache, and the inspector overlay fit together.
- For a bug report, the [Troubleshooting](https://github.com/RealWhyKnot/vrcfury-qol/wiki/Troubleshooting) wiki page covers the common scenarios — please check it first.

## Setting up the dev loop

There's no build system here — `vrcfury-qol` is a flat folder of `.cs` files compiled by Unity itself.

**Prerequisites:**

- Unity 2022.3.x (matching the avatar projects we test against)
- A Unity project with VRCFury already imported
- git

**Recommended layout:** clone the repo somewhere outside your Unity project, then symlink the `Editor/` folder into the project under any `Assets/...Editor/` path. That way edits to the repo apply to the live project without copy-pasting.

Windows (PowerShell, run as admin):
```powershell
New-Item -ItemType Junction -Path "C:\Path\To\YourProject\Assets\VrcfQol" -Target "C:\Path\To\vrcfury-qol\Editor"
```

Linux / macOS:
```sh
ln -s /path/to/vrcfury-qol/Editor /path/to/YourProject/Assets/VrcfQol
```

Once linked, `VrcfQolHotReload.cs` will pick up `.cs` saves and trigger `AssetDatabase.Refresh()` even when Unity isn't focused. Tail `<ProjectRoot>/Logs/VrcfQolHotReload.log` to watch compiles in real time. **Bootstrap step:** focus Unity once after the first install so it compiles the scripts; from then on the watcher takes over.

## Editing the wiki

The wiki is **source-controlled at `wiki/`** in this repo. That means:

- Edits go through normal PR review, same as code.
- **Do not edit on the github.com Wiki UI.** Web edits get overwritten the next time the sync workflow runs.
- On every push to `main` that touches `wiki/**`, the [wiki-sync workflow](.github/workflows/wiki-sync.yml) mirrors the changes to the GitHub Wiki repo.

**One-time wiki bootstrap.** GitHub doesn't create the wiki repo until a maintainer creates the first page through the web UI. If you see a `wiki repo doesn't exist yet` warning in the wiki-sync workflow, visit `https://github.com/RealWhyKnot/vrcfury-qol/wiki` and click **Create the first page** with any content (it'll be overwritten on the next sync). After that, every push that touches `wiki/**` syncs automatically.

## Submitting a PR

- Branch from `main`. Open the PR against `main`.
- The [PR template](.github/PULL_REQUEST_TEMPLATE.md) auto-populates the description. Fill the checklist honestly — particularly the "compiles in Unity 2022.3.x with no console errors" item.
- **Touched a tool?** Update or add the corresponding entry in [`wiki/Tools-Overview.md`](wiki/Tools-Overview.md). UI changes deserve a screenshot.
- **Added new reflection-cache fields?** Verify they degrade gracefully when missing (see existing optional fields in `Editor/VrcfQol.cs` for the pattern).
- Keep PRs focused. Mixing unrelated changes makes review harder for everyone.

## Code review expectations

- Be ready to iterate. Expect at least one review pass on anything non-trivial.
- VRCFury internals change between releases; if your PR depends on a new field, mention the minimum VRCFury version it was tested against.

## Commit message style

- Conventional-ish prefixes are appreciated but not enforced: `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `ci:`.
- Keep the subject ≤72 characters.
- The body is for the *why*. The diff already shows the *what*; explain reasoning, alternatives rejected, and any VRCFury-version gotcha you're working around.

## Reporting security issues

Please don't file a public issue for a security vulnerability. Use GitHub's **Security tab → Report a vulnerability** for a private disclosure. See [SECURITY.md](.github/SECURITY.md) for details.
