# DeltaUI
made it for fun
A shared UI library mod for KSP 1.12.5: windows with tabs, snap layouts, a themed look across every
window, and a built-in Chromium browser — all IMGUI, no uGUI/UI Toolkit assets to ship.

## What it does

- **Dock** — the window system every DeltaUI panel (and other mods, via `Dock.Register`) lives in.
  Tabs can be dragged out into their own window or back together, windows snap to screen edges and
  corners (quarter/half/full, like Windows 11), each window has its own fullscreen toggle and a
  flyout for snap layouts, and the whole layout is saved to `PluginData/layout.cfg`.
- **Theme** — a dark, flat ImGui-style skin (JetBrains Mono, square corners, blue accents) applied
  consistently across every DeltaUI window. Optionally (off by default — it's experimental) restyles
  *other* mods' IMGUI windows too: theme, fonts, a resize grip and a close button on windows that
  never had one.
- **Browser** — its own embedded Chromium (via CefSharp, running in a separate helper process
  outside `GameData` so KSP doesn't try to load its native DLLs), rendered to a `Texture2D` and
  streamed back mouse/keyboard/scroll. Multi-tab, persistent cookies/sessions, and a built-in
  network-level ad/tracker blocker (`tools/DeltaBrowser/adblock-hosts.txt`). A background tab keeps
  playing audio/video but stops rendering, like a real browser throttling hidden tabs.
- **Hub** — the ΔUI app-bar button: an empty window with every registered panel as a shortcut icon in
  its title bar. Minimizing a window sends it back to the hub as an inactive tab (the content keeps
  running — a minimized browser tab keeps playing); clicking the hub again restores it exactly where
  it was.
- **Perf** — a Harmony-based profiler that times `Update`/`FixedUpdate`/`OnGUI` per mod and writes a
  report.

## Build

Needs a local KSP 1.12.5 install (for the reference assemblies) with
[Harmony](https://github.com/pardeike/KSPHarmony) in `GameData`. Edit `KSPRoot` at the top of
`DeltaUI.csproj` to point at your install, then:

```bash
dotnet build DeltaUI.csproj -c Release
```

Output: `dist/GameData/DeltaUI/Plugins/DeltaUI.dll`.

The browser needs its own helper process, built separately (it's CEF/net48, not loadable inside
KSP's Mono runtime — that's also why it lives outside `GameData`):

```bash
dotnet build tools/DeltaBrowser/DeltaBrowser.csproj -c Release
```

Output: `dist/DeltaBrowser/`. Copy that whole folder next to your KSP executable (e.g.
`<KSP install>/DeltaBrowser/`), and `dist/GameData/DeltaUI` into `GameData`.

## Other mods that build against this

[OSMEarth](https://github.com/deltaspacesystems-ksp/OSMEarth) registers all of its windows (map
planner, roads panel, music player) as DeltaUI panels and references
`dist/GameData/DeltaUI/Plugins/DeltaUI.dll` directly — build DeltaUI first.

---

Built with [Claude Code](https://claude.com/claude-code).
