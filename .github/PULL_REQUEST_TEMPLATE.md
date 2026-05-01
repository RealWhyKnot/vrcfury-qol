<!-- Linked issue (optional but appreciated): Closes #__ -->

## Summary

<!-- 1-3 sentences on the *why*, not just the what. The diff already shows the what. -->

## Checklist

- [ ] Compiles in Unity 2022.3.x with no console errors or warnings introduced by this change.
- [ ] Tested in the editor against a VRCFury-equipped avatar — including Undo (Ctrl+Z) for any destructive operation.
- [ ] If a tool was added or its UX changed, the corresponding section in [`wiki/Tools-Overview.md`](../wiki/Tools-Overview.md) was updated.
- [ ] If reflection-cache fields were added/changed, optional fields are null-checked at every call site.
- [ ] No `Debug.Log` debug noise left in production code paths.

## VRCFury version tested
<!-- e.g. 1.1303.0 -->
