# Shadowshot

A lightweight Windows screenshot & annotation tool — built for sharing screens with **Claude Code**.

Press **PrtScr**, drag over what matters, mark it up, and hit **Claude** (or Enter). The annotated PNG is
saved locally and put on the clipboard, so **Ctrl+V in the Claude Code CLI attaches it as an image**.

- ~200 KB single exe, .NET Framework 4.8 (already part of Windows 10/11) — no runtime to install
- Per-user install, no admin rights, no network access, no telemetry, no cloud uploads
- Multi-monitor and mixed-DPI aware (every pixel is a real screen pixel)

## Install

1. Run **`ShadowshotSetup.exe`** → Install. It installs to `%LOCALAPPDATA%\Programs\Shadowshot`, adds a
   Start-menu shortcut, starts with Windows (optional) and appears in *Settings → Apps* for uninstalling.
2. Shadowshot lives in the tray. Press **PrtScr** (or click the tray icon).

Portable: unzip `Shadowshot-portable.zip` anywhere and run `Shadowshot.exe`.

> **Windows SmartScreen** may warn because the exe isn't code-signed: *More info → Run anyway*.
>
> **PrtScr just works on Windows 11** — Shadowshot takes the key before Snipping Tool does, without
> changing any Windows setting (quit Shadowshot and PrtScr goes back to Snipping Tool).
> **Coming from Lightshot?** Quit it (tray → Exit) or change its hotkeys, otherwise it keeps
> Shift+PrtScr / Ctrl+PrtScr for itself (Options shows which hotkeys are taken).

## Global hotkeys (change them in Options)

| Hotkey | Action |
|---|---|
| **PrtScr** | Capture an area (opens the editor) |
| **Shift+PrtScr** | Save the whole desktop to the screenshots folder instantly |
| **Ctrl+Alt+PrtScr** | Copy the screen under the mouse **for Claude** instantly |

## The editor

Drag to select an area — or just **click a window** to grab it (click the desktop to grab a monitor).
Drag the 8 handles to resize, drag inside to move. Hold **Ctrl while dragging** a new area to copy it
the moment you release.

**Tools** (right of the selection):

| Tool | Key | Notes |
|---|---|---|
| Pen | P | freehand; click for a dot |
| Line | L | Shift = snap to 45° |
| Arrow | A | bold head; Shift = snap to 45° |
| Rectangle / Ellipse | R / E | Shift = square / circle |
| Highlighter | H | translucent, own color (yellow) |
| Text | T | Enter = new line; Ctrl+Enter / Esc / click elsewhere = done |
| **Numbered step** | N | click to drop ①②③…; **drag to point at the target**; then type an optional label |
| **Redact** | B | true pixelation — use it on API keys, tokens, emails before sharing |
| Color · Undo · Redo | | palette + custom color; Ctrl+Z / Ctrl+Y |

Mouse wheel changes the size of the current tool (also while typing). Every mark gets a subtle
contrasting outline, so it stays readable on light *and* dark UI.

**Actions** (below the selection): **Claude** · Print · Copy · Save · Close.

| Key | Action |
|---|---|
| **Enter** / Ctrl+Shift+C | **Copy for Claude** |
| Ctrl+C | Copy image |
| Ctrl+S | Quick save (no dialog, to the last folder) |
| Ctrl+Shift+S | Save as… |
| Ctrl+P | Print |
| Ctrl+A / double-click | Select the whole monitor (again: whole desktop) |
| Arrows / Shift+Arrows | Move / resize the selection by 1 px (add Ctrl for 10 px) |
| Esc | Leave the current tool, or close |
| Right-click | Menu with all actions |

## Copy for Claude — how it works

1. The annotated image is saved as PNG to `Pictures\Shadowshot\Claude\claude_<date>_<time>.png`
   (files are kept, so Claude can re-open the `source:` path later).
2. The clipboard gets **the file path as text** and **the image itself**:
   - **Ctrl+V** in Claude Code (Windows Terminal / VS Code terminal) pastes the path, which Claude Code
     turns into **`[Image #1]`**.
   - **Alt+V** (Claude Code's own image-paste key on Windows) reads the image directly.
   - Paint, Slack, Office, browsers… get a normal image paste.
3. If you labelled numbered steps, their text comes along: the prompt gets
   `[Image #1] Screenshot notes: (1) Login button misaligned; (2) Wrong color` — Claude reads your
   labels exactly instead of guessing from pixels. (Turn off in *Options → Copy for Claude*.)

Tip: number the problems on the screenshot, then just type "fix 1 and 2".

## Options

Tray icon → Options: hotkeys, start with Windows, notifications, capture mouse cursor, remember last
selection, screenshots folder, default format (PNG/JPG/BMP) and JPEG quality, Claude folder, step notes.
Settings live in `%APPDATA%\Shadowshot\settings.ini`.

## Uninstall

*Settings → Apps → Shadowshot → Uninstall* (or `Shadowshot.exe --uninstall`). Your screenshots are
never deleted.

## Build from source

Requirements: Windows 10/11, .NET SDK 6+ (tested with 9.0), .NET Framework 4.8 targeting pack
(ships with Visual Studio; no NuGet packages are needed).

```
build.cmd            # builds everything → dist\Shadowshot.exe, dist\ShadowshotSetup.exe, dist\Shadowshot-portable.zip
dotnet build Shadowshot.sln -c Release
tests\Shadowshot.Tests\bin\Release\net48\Shadowshot.Tests.exe [outDir]   # headless test suite + PNG snapshots
```

Command line: `Shadowshot.exe [--tray | --capture | --settings | --exit | --uninstall [--silent]]`.
Silent install: `ShadowshotSetup.exe /S [/D=<folder>] [/NOSTARTUP] [/NOLAUNCH]`.

```
src/Shadowshot/        the app (tray, hotkeys, capture, editor, annotations, export)
src/ShadowshotSetup/   per-user installer (embeds the app exe)
tests/Shadowshot.Tests headless tests that drive the editor and render snapshots
tools/IconGen/         draws the app icon
docs/SPEC.md           design spec
```

## Known limits

- Protected/DRM video and some hardware-accelerated overlays may capture as black (Windows GDI capture).
- Cloud upload / share links are intentionally not included — everything stays on your PC.

## License

MIT © 2026 Shrikant Satpute — see [LICENSE](LICENSE).
