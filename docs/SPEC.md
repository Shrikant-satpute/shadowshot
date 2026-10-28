# Shadowshot — Implementation Spec (contract for all implementers)

Shadowshot is an original, lightweight Windows screenshot + annotation tool with the classic
"press PrtScr → screen freezes and dims → drag a region → annotate → copy/save" workflow, plus a
**Copy for Claude** action that saves the annotated PNG locally and puts it on the clipboard so
**Ctrl+V in the Claude Code CLI** (Windows Terminal) attaches it as `[Image #N]`.

Feature parity reference: Lightshot 5.5 (studied, not copied). Do not copy any other product's code,
icons, strings, names or branding. All icons are drawn with GDI+ vectors.

## Stack (fixed, verified on this machine)

- C# (LangVersion `latest` = C# 13), **WinForms on .NET Framework 4.8** (`net48`; preinstalled on Win10 1903+/Win11) → tiny single exe, no runtime install, **no NuGet packages**.
- Build: `dotnet build Shadowshot.sln -c Release` (SDK 9.0.305). Common props live in `Directory.Build.props` (AnyCPU, not 32-bit preferred, Version 1.0.0).
- Process is **Per-Monitor-V2 DPI aware** via `app.manifest` → every coordinate is a physical pixel.
- Settings use a plain `key=value` file (no JSON libraries).
- **net48 C# limits (compile errors):** no ranges/indices (`^1`, `a..b`), no `Span<T>`/`stackalloc` spans, no default interface methods, no records/`init`/`required` (no shims — don't use them), no `HashCode`, no `string.Contains(char)` / `string.Join(char, …)` (use the string overloads), no `System.Threading.Lock`. Fine: switch expressions, patterns, target-typed `new()`, local functions, interpolation, `using var`, tuples, `??=`.
- `Nullable` disabled, `ImplicitUsings` disabled (write your `using`s). Namespace `Shadowshot` (app, tests use `Shadowshot.Tests`), `ShadowshotSetup` (installer).
- **P/Invoke rule:** there is no shared Native.cs. Each file declares the Win32 functions it needs in a private nested `static class Native`. Duplicates across files are fine.
- Keep code simple and direct (Karpathy guidelines): no speculative abstractions, no options not listed here, no dead code, comments only where they explain *why*.
- Never shell out to PowerShell from the app.

## Repository layout

```
Shadowshot/
  Shadowshot.sln, Directory.Build.props                          [lead — exists]
  build.ps1, build.cmd           build everything → dist/        [platform]
  README.md                                                      [lead, last]
  docs/SPEC.md                   this file
  tools/IconGen/                 net48 console exe → src/Shadowshot/Assets/shadowshot.ico   [platform]
  src/Shadowshot/                the app
    Shadowshot.csproj, app.manifest, Contracts.cs                [lead — exist, do not edit]
    Assets/shadowshot.ico        (embedded as Win32 icon AND as resource "Shadowshot.ico")
    Program.cs, TrayApp.cs, HotkeyManager.cs, Settings.cs,
    ScreenCapture.cs, SettingsForm.cs, Uninstaller.cs            [platform]
    CaptureOverlay.cs, Editor.cs, Toolbar.cs, Icons.cs           [editor]
    Annotations.cs, Exporter.cs                                  [annotate]
  src/ShadowshotSetup/           installer exe; csproj + setup.manifest exist   [setup]
                                 (app exe is embedded as resource "Payload.Shadowshot.exe")
  tests/Shadowshot.Tests/        console test harness; csproj exists (InternalsVisibleTo set)  [tests, later]
  dist/                          Shadowshot.exe, ShadowshotSetup.exe, Shadowshot-portable.zip
```

`[x]` = owning agent. **Only create/edit files you own.** If you need something from another
owner's file, use exactly the API in this spec; if the spec is missing something, add a private
helper to YOUR file instead of editing theirs. Other owners write their files at the same time as
you, so they may not exist yet — code against this spec.

## Shared contracts (src/Shadowshot/Contracts.cs — exists, read it, do not change)

`ToolKind { None, Pen, Line, Arrow, Rectangle, Ellipse, Marker, Text, Step, Pixelate }`
`EditorCommand { CopyForClaude, Copy, QuickSave, SaveAs, Print, Close, PickCustomColor, ContextMenu }`
`IOverlayHost { void Notify(string title, string text, string openPath); }`
`AppInfo` constants: `Name`, `MutexName`, `EventCapture`, `EventSettings`, `EventExit`, `UninstallKey` (HKCU), `AppKey` (`Software\Shadowshot`, HKCU), `RunKey`, `Version`, `DataDir` (`%APPDATA%\Shadowshot`).

## Coordinate system

All editor/annotation geometry is in **capture space**: physical pixels with (0,0) = top-left of the
virtual desktop. The overlay form covers exactly the virtual desktop, so capture space == overlay
client coordinates.

---

## Platform (owner: platform)

### Settings.cs
```csharp
public sealed class Settings {
    public static string FilePath { get; }            // %APPDATA%\Shadowshot\settings.ini
    public static Settings Load();                    // never throws; missing/corrupt values → defaults
    public void Save();                               // never throws (log + ignore IO errors); creates the folder

    public string RegionHotkey   = "PrintScreen";              // Hotkey.Parse format; "" = disabled
    public string FullSaveHotkey = "Shift+PrintScreen";        // whole virtual desktop → auto-saved file
    public string ClaudeHotkey   = "Ctrl+Alt+PrintScreen";     // monitor under cursor → Copy for Claude (no editor)
    public string SaveFolder;       // default %USERPROFILE%\Pictures\Shadowshot
    public string ClaudeFolder;     // default %USERPROFILE%\Pictures\Shadowshot\Claude
    public string LastSaveDir = ""; // last folder used by Save As; "" → SaveFolder. Ctrl+S quick-saves here.
    public string Format = "png";   // png | jpg | bmp   (Save As default filter + quick save + full-screen save)
    public int JpegQuality = 90;    // 50–100
    public bool ShowNotifications = true;
    public bool CaptureCursor = false;
    public bool KeepSelection = false;
    public Rectangle LastSelection;      // capture space
    public bool ClaudeIncludeNotes = true;   // Copy for Claude also pastes numbered-step labels as text
    public bool FirstRunDone = false;
    public Color Color = Color.FromArgb(255, 0, 0);          // current annotation color
    public Color MarkerColor = Color.FromArgb(255, 204, 0);  // highlighter has its own color (#FFCC00 = a palette swatch)
    public int GetToolSize(ToolKind t);  public void SetToolSize(ToolKind t, int size);  // defaults + clamping per Tools table

    // Registry-backed (not stored in the ini):
    public static bool StartWithWindows { get; set; }         // HKCU Run value "Shadowshot" = "\"<exe>\" --tray"
}
```

### HotkeyManager.cs
```csharp
public struct Hotkey {           // e.g. "Ctrl+Shift+PrintScreen", "Alt+F1", "" (disabled)
    public Keys Key; public bool Ctrl, Shift, Alt, Win;
    public bool IsEmpty { get; }
    public static Hotkey Parse(string s);   // tolerant; unknown → empty
    public override string ToString();      // canonical "Ctrl+Alt+Shift+Win+<Keys name>" e.g. "Shift+PrintScreen"
    public string ToDisplayString();        // pretty: "Shift+PrtScr", "Ctrl+Alt+PrtScr"
}
public sealed class HotkeyManager : IDisposable {   // NativeWindow message-only window; MOD_NOREPEAT
    public event Action<int> Pressed;               // id
    public bool Register(int id, Hotkey hk, out string error);  // false + human text, e.g. "Shift+PrtScr is already used by another app (Lightshot?)"
    public void UnregisterAll();
}
```
Gotcha: WinForms never delivers KeyDown for PrintScreen — only KeyUp. Hotkey input boxes must handle KeyUp for it.

**Bare PrtScr (verified on Windows 11 25H2):** the system hands a bare PrtScr to Snipping Tool (or swallows it) before any `WM_HOTKEY`, and `RegisterHotKey` even reports success for it. So a hotkey that is exactly `PrintScreen` is claimed with a `WH_KEYBOARD_LL` hook on a dedicated thread (so a busy UI thread never delays system-wide typing): key-down with no modifier held → post `WM_HOTKEY` to the message window and swallow the press (repeats + key-up included). While suspended (Options open) the press is forwarded as `WM_KEYUP` to our focused control if one of our windows is in front, otherwise passed on. All other combos use `RegisterHotKey`. No Windows setting is changed.

### ScreenCapture.cs
```csharp
public sealed class MonitorSnapshot { public Rectangle Bounds; public float Scale; public bool Primary; } // capture space; Scale = effective dpi/96 (GetDpiForMonitor MDT_EFFECTIVE_DPI)
public sealed class CaptureResult : IDisposable {
    public Bitmap Image;                 // Format32bppPArgb, size == virtual desktop
    public Rectangle VirtualBounds;      // screen coords of the virtual desktop
    public List<MonitorSnapshot> Monitors;
    public List<Rectangle> Windows;      // visible top-level window rects (capture space, DWMWA_EXTENDED_FRAME_BOUNDS), z-order top-first; excludes cloaked/minimized/zero-size windows, the desktop (classes Progman, WorkerW) and windows of this process; keeps the taskbar
    public Point Cursor;                 // capture space
    public MonitorSnapshot MonitorAt(Point p);   // monitor containing p, else nearest
}
public static class ScreenCapture {
    public static CaptureResult CaptureAll(bool includeCursor, bool collectWindows);  // one BitBlt SRCCOPY|CAPTUREBLT of the virtual desktop
    public static Bitmap Crop(Bitmap src, Rectangle r);                                // new Format32bppArgb bitmap
}
```
Machine for testing: two monitors, 1920×1080 @125 % (primary, 0,0) and 1920×1080 @100 % (1920,0) → mixed DPI. Must be correct on both.

### TrayApp.cs (ApplicationContext, implements IOverlayHost)
- `public TrayApp(bool startCapture, bool openSettings, bool silent)`.
- NotifyIcon with the app icon at small-icon size (load via `LoadIconWithScaleDown(GetModuleHandle(null), (IntPtr)32512, cx, cy)` — 32512 is the Win32 icon group id — fallback to the embedded "Shadowshot.ico" resource). Tooltip `Shadowshot — press PrtScr to capture` (actual hotkey text; ≤ 63 chars).
- Left click on the tray icon → region capture. Context menu: `Capture area` (with hotkey text), `Capture full screen (save)`, `Screen → Copy for Claude`, separator, `Open screenshots folder`, `Open Claude folder`, separator, `Options…`, `About Shadowshot`, `Exit`.
- Registers the 3 hotkeys (ids 1,2,3). Failures → one balloon "Some hotkeys could not be registered" listing each with the reason; clicking that balloon opens Options.
- Hotkeys are unregistered while the Options dialog is open (so its hotkey boxes can capture PrtScr) and re-registered after it closes.
- IPC: creates the three named auto-reset `EventWaitHandle`s (AppInfo.Event*) and waits with `ThreadPool.RegisterWaitForSingleObject`; marshal to the UI thread.
- Region capture: if an overlay is open, activate it; else `ScreenCapture.CaptureAll(settings.CaptureCursor, true)` → `new CaptureOverlay(capture, settings, this).Show()`. On overlay `FormClosed`: dispose capture, `GC.Collect()` (keeps idle memory small).
- Full save: capture → `Exporter.SaveAuto(image, folder: LastSaveDir or SaveFolder, s.Format, s.JpegQuality)` → Notify("Screenshot saved", "Saved to <path>. Click to open the folder.", path).
- Claude hotkey: capture (with cursor setting) → crop the monitor under the cursor → `Exporter.CopyForClaude(img, settings, null)` → Notify("Copied for Claude", "Press Ctrl+V in Claude Code to attach it.", path).
- First run (`FirstRunDone` false) or launch without `--tray`: balloon "Shadowshot is running. Press PrtScr (or click this icon) to take a screenshot." then set FirstRunDone.
- `Notify` respects `ShowNotifications`. Clicking a balloon with openPath → `explorer.exe /select,"<path>"`.
- Global exception handlers → append to `%APPDATA%\Shadowshot\error.log` + short MessageBox; keep running when possible.

### Program.cs
Args: `--tray` (start silently), `--capture`, `--settings`, `--exit`, `--uninstall [--silent]`.
- `--uninstall` → `Uninstaller.Run(silent)`, return.
- Single instance via `AppInfo.MutexName`. If another instance runs: `AllowSetForegroundWindow(ASFW_ANY)`, then set EventExit for `--exit`, EventSettings for `--settings`, nothing for `--tray`, else EventCapture (launching again = capture). Exit.
- First instance: `--exit` → exit; else `Application.Run(new TrayApp(...))`.
- `Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);`

### SettingsForm.cs (Options dialog, standard controls, AutoScaleMode.Dpi, FixedDialog)
- **Hotkeys**: three rows (Capture area / Full screen → save / Screen → Copy for Claude): read-only box that records the pressed combo (KeyDown + KeyUp for PrintScreen; Backspace/Delete clears; Ctrl, Alt or Win is required unless the key is PrintScreen, F1–F24, Pause, Scroll Lock or Insert), a clear "×" button, and a status label ("OK" / reason) from a test registration on Apply. Reject duplicates.
- **General**: Start with Windows, Show notifications, Capture mouse cursor, Remember last selection.
- **Saving**: Screenshots folder (+Browse), Default format (PNG/JPG/BMP), JPEG quality 50–100.
- **Copy for Claude**: Claude folder (+Browse, Open), checkbox "Also paste numbered-step labels as text (Claude reads them exactly)".
- OK / Cancel / Apply. Title "Shadowshot Options — v1.0.0".
- `public SettingsForm(Settings settings)`; TrayApp re-registers hotkeys and saves settings after OK/Apply.

### Uninstaller.cs
`public static void Run(bool silent)`:
1. Confirm unless silent: "Uninstall Shadowshot? Your saved screenshots will be kept."
2. Signal `AppInfo.EventExit` (if it exists); wait up to 3 s for other Shadowshot processes (not this one) to exit, then kill those whose exe path equals ours.
3. Remove: Run value; Start-menu shortcut `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Shadowshot.lnk`; Desktop shortcut `Desktop\Shadowshot.lnk` (only these exact files); delete `HKCU\Software\Shadowshot`, the Uninstall key and `%APPDATA%\Shadowshot`.
4. **Safety:** delete program files ONLY if this exe's directory equals the `InstallLocation` recorded in the Uninstall key (full-path normalized, case-insensitive) — never for a portable copy. Then start hidden `cmd.exe /c ping -n 3 127.0.0.1 >nul & del /f /q "<dir>\Shadowshot.exe" & rmdir "<dir>"` (NO `/s`: a non-empty folder is never wiped). Never touch screenshot folders.
5. "Shadowshot has been uninstalled." unless silent.

### build.ps1 / build.cmd
`build.cmd` → `powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"` (for humans).
`build.ps1`: `dotnet build Shadowshot.sln -c Release` (fail on error) → copy `Shadowshot.exe` and `ShadowshotSetup.exe` to `dist\` → `dist\Shadowshot-portable.zip` (Shadowshot.exe + README.md) → print sizes. IconGen is run only if the .ico is missing (`-Icon` switch forces it).

### tools/IconGen
Exists (copied from the verified prototype). Improve the artwork: dark rounded square, violet `#7C5CFF` selection-corner brackets, and a small Claude-orange `#D97757` 4-point sparkle in the middle (no camera/feather). Keep frames 16/20/24/32/40/48/64 (32bpp BMP) + 256 (PNG). Usage: `IconGen.exe <out.ico> [previewDir]`. Regenerate `src/Shadowshot/Assets/shadowshot.ico`.

---

## Editor (owner: editor)

```csharp
public sealed class CaptureOverlay : Form {
    public CaptureOverlay(CaptureResult capture, Settings settings, IOverlayHost host);
}
public sealed class Editor : IDisposable {            // no Form dependency: tests drive it headless
    public Editor(CaptureResult capture, Settings settings);
    public Rectangle Selection { get; set; }          // capture space; Empty = none
    public List<Annotation> Annotations { get; }
    public ToolKind Tool { get; set; }
    public Color Color { get; }                       // color for the active tool (MarkerColor when Marker)
    public void SetColor(Color c);                    // sets Color or MarkerColor (if Marker active) and recolors the text being edited
    public Point LastMouse { get; }
    public bool ToolbarsVisible { get; }
    public event Action<Rectangle> Invalidate;        // capture-space rect to repaint
    public event Action<EditorCommand> Command;
    public Cursor Cursor { get; }
    public void MouseDown(Point p, MouseButtons b, Keys modifiers, int clicks);
    public void MouseMove(Point p, MouseButtons b, Keys modifiers);
    public void MouseUp(Point p, MouseButtons b, Keys modifiers);
    public void MouseWheel(Point p, int delta, Keys modifiers);
    public bool KeyDown(Keys keyData);                // returns handled
    public void KeyPress(char c);
    public void Tick();                               // ~every 250 ms from the form: caret blink, HUD expiry
    public void Render(Graphics g, Rectangle clip);
    public void CommitText();                         // finish an in-progress text/label edit
    public void ToggleFullScreen();                   // Ctrl+A / double-click / context menu
    public void ClearSelection();                     // context menu "Clear selection"
    public Bitmap RenderResult();                     // CommitText + Exporter.Compose(capture.Image, Selection, Annotations)
    public string BuildClaudeNotes();                 // "" or "Screenshot notes: (1) label; (2) label" from Step labels, single line
}
```

### CaptureOverlay (form shell)
- `FormBorderStyle.None`, `ShowInTaskbar=false`, `TopMost=true`, `StartPosition=Manual`, `AutoScaleMode.None`, `KeyPreview=true`, styles `UserPaint|AllPaintingInWmPaint|OptimizedDoubleBuffer|Opaque`.
- Bounds: `SetWindowPos(HWND_TOPMOST, VirtualBounds…)` after the handle exists; swallow `WM_DPICHANGED` (0x02E0) so the form never self-resizes; close on `WM_DISPLAYCHANGE` (0x007E). After show: `Activate()` + `SetForegroundWindow(Handle)`.
- Route input to the Editor. Override `ProcessCmdKey` (arrows/Enter/Tab/Esc are otherwise eaten as dialog keys) → `editor.KeyDown(keyData)`; `OnKeyPress` → `editor.KeyPress`. Pass `e.Clicks` to MouseDown.
- `OnPaint` → `editor.Render(e.Graphics, e.ClipRectangle)`. `editor.Invalidate` → `Invalidate(rect)` (never full-screen invalidation on mouse move). `Cursor = editor.Cursor` after each input.
- 250 ms Timer → `editor.Tick()`.
- Commands:
  - `CopyForClaude` → `img = editor.RenderResult()`; `path = Exporter.CopyForClaude(img, settings, settings.ClaudeIncludeNotes ? editor.BuildClaudeNotes() : null)`; Notify("Copied for Claude", "Press Ctrl+V in Claude Code to attach it.", path); close.
  - `Copy` → `Exporter.CopyImage(img)`; Notify("Copied", "Your screenshot is copied to the clipboard.", null); close.
  - `QuickSave` → `Exporter.SaveAuto(img, LastSaveDir or SaveFolder, s.Format, s.JpegQuality)`; Notify("Screenshot saved", …, path); close.
  - `SaveAs` → `Exporter.SaveAs(this, img, settings)`; null → stay open; else Notify + close.
  - `Print` → `Exporter.Print(this, img)`; false → stay open; else close.
  - `Close` → close. `PickCustomColor` → `ColorDialog` (owner = overlay, FullOpen) → `editor.SetColor`.
  - `ContextMenu` → `ContextMenuStrip` at `editor.LastMouse`: `Copy for Claude  Enter`, `Copy  Ctrl+C`, `Save  Ctrl+S`, `Save as…`, `Print  Ctrl+P`, separator, `Select full screen  Ctrl+A`, `Clear selection`, separator, `Cancel  Esc`. Items needing a selection are disabled without one.
  - Before closing after an action: persist `settings` (Color, MarkerColor, tool sizes, LastSelection) with `settings.Save()`. Exceptions in actions → MessageBox owned by the overlay, stay open. Dispose bitmaps you create.
- Dispose the editor on close (TrayApp disposes the capture).

### Interaction model (Editor)
**Before a selection exists**
- Whole screen dimmed to **50 %** (frozen screenshot drawn over black at half intensity — pre-render this dimmed copy once).
- Hovered window (top-most of `capture.Windows` containing the cursor, clipped to the virtual desktop; none → the monitor under the cursor) is previewed undimmed with a 2 px `#7C5CFF` border.
- Hint pill centered in the upper third of the monitor under the cursor: `Drag to select an area  •  Click to grab a window  •  Esc to cancel`.
- Left drag → rubber-band selection with live size label. Mouse-up with < 4 px movement → selection = hovered window/monitor rect. **Ctrl held when the drag starts → auto `Copy` on release** (classic power-user shortcut).
- Right-click / Esc → Close.

**With a selection**
- Outside dimmed 50 %; inside: original pixels + annotations (clipped to the selection).
- Frame: 1 px, white/black 3-3 dashes (visible on any background). 8 handles (corners + edge midpoints): square ±3 px·scale (≈7×7), black fill, white 1 px outline.
- Size label `594x613` (no spaces) — bold Segoe UI ~10pt·scale, text `#DEDEDE`, on a black α200 rounded pill, just above the top-left corner (inside the top-left corner when there's no room).
- **Tool bar** (vertical) right of the selection with a 5 px·scale gap, bottom-aligned with the selection (fallbacks: left side; then inside the right edge). **Action bar** (horizontal) below the selection, right-aligned (fallbacks: above; then inside the bottom edge). Both clamped to the selection's monitor. **Hidden while dragging a selection/handle or drawing; shown again on mouse-up.**
- Mouse-down priority: toolbar buttons → palette popup → resize handles (always active) → inside selection: active tool draws, no tool moves the selection → outside selection: no tool = start a new selection (annotations stay), tool active = ignore.
- **Double-click** inside the selection with no tool = `ToggleFullScreen()`. With a drawing tool, a double-click is just another mouse-down.
- `ToggleFullScreen()`: if the selection is not the monitor under the cursor → remember the current rect and select that monitor; else if it is → select the whole virtual desktop if multi-monitor, otherwise restore the remembered rect.
- **Right-click** → `Command(ContextMenu)` (commits text first).
- **Keys** (when not typing text):
  `Esc` → if a tool is active: commit + deselect the tool; else Close · `Ctrl+X` Close · `Enter` / `Ctrl+Shift+C` Copy for Claude · `Ctrl+C` Copy · `Ctrl+S` QuickSave · `Ctrl+Shift+S` SaveAs · `Ctrl+P` Print · `Ctrl+Z` / `Delete` undo · `Ctrl+Y` / `Ctrl+Shift+Z` redo · `Ctrl+A` ToggleFullScreen · arrows move the selection 1 px (Ctrl: 10 px) · Shift+arrows resize (move right/bottom edge) 1 px (Ctrl+Shift: 10 px) · `Ctrl+Numpad+` / `Ctrl+Numpad−` (and `Ctrl+=`/`Ctrl+-`) size up/down · tool letters `P L A R E H T N B` toggle tools. Selection is clamped to the virtual desktop and never smaller than 1×1.
- **Typing text** (Text tool, or a Step label right after placing a badge): printable chars append, Backspace deletes, Enter = newline (Text) / commit (Step label), Ctrl+V pastes clipboard text (single line for Step labels), `Ctrl+Enter`/`Esc`/clicking elsewhere commits. Empty text is discarded (a Step badge with no label stays). Every Command commits text first.
- **Mouse wheel** (deltas accumulate to 120 per step, so touchpads behave): up = bigger, down = smaller for the active tool (and live for the text being edited); shows a HUD pill `Size 6` near the cursor for ~1 s. Persist via `settings.SetToolSize`.
- **Shift while drawing**: Line/Arrow snap to 45°, Rectangle/Ellipse/Pixelate square.
- **Pen**: a click without moving leaves a dot (diameter = size).
- Undo removes the last annotation (pushes to redo); any new annotation clears redo. Step number at creation = (number of Step annotations currently in the list) + 1, so undo/redo keep numbering consistent.
- **Step tool**: mouse-down places the badge at the point; dragging ≥ 8 px sets `Tip` (leader arrow pointing at the target) and the badge stays where the press started; on mouse-up enter label-typing mode (caret right of the badge). Typing is optional — clicking elsewhere / Enter / Esc commits.
- `Cursor`: Cross before/while selecting and for drawing tools, IBeam for Text, SizeAll inside the selection with no tool, resize cursors on handles (NS/WE/NWSE/NESW), Default over toolbars/popups.

### Tools (vertical bar, top → bottom)

| Tool | Key | Tooltip | Size meaning | Default | Range |
|---|---|---|---|---|---|
| Pen | P | Pen (P) | stroke px | 3 | 1–30 |
| Line | L | Line (L) | stroke px | 3 | 1–30 |
| Arrow | A | Arrow (A) | stroke px | 4 | 2–30 |
| Rectangle | R | Rectangle (R) | stroke px | 3 | 1–30 |
| Ellipse | E | Ellipse (E) | stroke px | 3 | 1–30 |
| Marker | H | Highlighter (H) | stroke px | 18 | 6–60 |
| Text | T | Text (T) | font px | 22 | 10–96 |
| Step | N | Numbered step (N) — click, or drag to point | badge diameter | 30 | 18–80 |
| Pixelate | B | Blur / redact secrets (B) | block px (floor: ⌈min side/3⌉ for rects ≤ 120 px, so a redacted text line is never readable) | 10 | 4–40 |
| — separator — | | | | | |
| Color | | Color | swatch of the current color (marker color when Marker is active); click → palette popup | | |
| Undo | | Undo (Ctrl+Z) | | | |
| Redo | | Redo (Ctrl+Y) | | | |

Clicking the active tool deselects it (back to move/resize). Palette popup beside the color button:
10 swatches `#FF0000 #FF9500 #FFCC00 #34C759 #32ADE6 #007AFF #AF52DE #FF2D55 #FFFFFF #000000` + a "Custom…" cell → `Command(PickCustomColor)` (the one command that does NOT commit text, so the picked color applies to the text being typed).
Sizes are in **image pixels** (not DPI-scaled) because they end up in the PNG.

### Actions (horizontal bar, left → right)

| Action | Tooltip | Command |
|---|---|---|
| **Claude** | Copy for Claude (Enter) — then press Ctrl+V in Claude Code | CopyForClaude — wider pill: sparkle icon + text "Claude", background `#D97757` |
| Print | Print (Ctrl+P) | Print |
| Copy | Copy (Ctrl+C) | Copy |
| Save | Save as… (quick save: Ctrl+S) | SaveAs |
| — separator — | | |
| Close | Close (Esc) | Close |

### Look
- Bars: rounded (radius 6·scale), background `ARGB(240, 32, 33, 38)`, buttons 32×32·scale (icons ~18·scale), hover `ARGB(255, 62, 64, 72)`, active tool `#7C5CFF`, separators `ARGB(60,255,255,255)`, icon color `#ECECEC`, disabled (undo/redo with empty stacks) α 90.
- UI metrics multiply by the selection's monitor `Scale` (cursor's monitor before a selection).
- Tooltip: dark pill beside the hovered button on the side away from the selection, clamped to the monitor.
- UI text: Segoe UI, pixel units, `TextRenderingHint.AntiAliasGridFit`.
- Paint performance: draw only the clip rect of the pre-rendered dimmed image (`CompositingMode.SourceCopy`, `InterpolationMode.NearestNeighbor`, `PixelOffsetMode.Half`, 1:1), then the undimmed selection part, then annotations (AntiAlias, clipped to selection), then chrome. Invalidate only the union of the old and new "UI bounds" (selection inflated by handles + label + bars + tooltip + popup + HUD + hover rect + hint). Cache fonts/brushes; dispose them in `Dispose`.

### Icons.cs
`public static class Icons { public static void Draw(Graphics g, string name, RectangleF box, Color color); }`
Names: `pen line arrow rectangle ellipse marker text step pixelate undo redo claude print copy save close`.
Pure GDI+ vector drawings (paths/lines/arcs), anti-aliased, scale with `box`. `claude` = a 4- or 8-ray sparkle. Original designs only.

---

## Annotations & export (owner: annotate)

```csharp
public abstract class Annotation : IDisposable {
    public Color Color;
    public int Size;                                        // meaning per tool (see Tools table)
    public abstract void Draw(Graphics g, Bitmap source);   // g in capture space; source = full screenshot (Pixelate reads it)
    public abstract Rectangle GetBounds();                  // capture space INCLUDING halo, arrow head, label, caret, editing frame — used for invalidation
    public virtual bool IsEmpty => false;                   // zero-length line, empty text, < 2 px rect → editor discards it
    public virtual void Dispose() { }
}
public sealed class PenAnnotation : Annotation { public List<Point> Points = new List<Point>(); }   // 1 point = dot
public sealed class MarkerAnnotation : Annotation { public List<Point> Points = new List<Point>(); }
public sealed class LineAnnotation : Annotation { public Point Start, End; }
public sealed class ArrowAnnotation : Annotation { public Point Start, End; }      // head at End
public sealed class RectangleAnnotation : Annotation { public Rectangle Rect; }   // normalized by the editor
public sealed class EllipseAnnotation : Annotation { public Rectangle Rect; }
public sealed class PixelateAnnotation : Annotation { public Rectangle Rect; }    // caches its pixelated bitmap; rebuilds when Rect/Size change; disposes it
public sealed class TextAnnotation : Annotation { public Point Location; public string Text = ""; public bool Editing; public bool CaretVisible; }
public sealed class StepAnnotation : Annotation {
    public int Number; public Point Center; public Point? Tip;
    public string Label = ""; public bool Editing; public bool CaretVisible;
}
public static class AnnotationStyle { public static bool IsLight(Color c); public static Color Halo(Color c); }
```

Rendering rules — designed so humans **and Claude** read annotations unambiguously:
- **Halo**: every stroke/shape/arrow/text/leader is drawn first with a contrasting halo (`IsLight(color)` → black α≈140, else white α≈170; halo pen width = size + 2·max(1, size/3)), then in its color. Marks stay visible on any background. Marker has no halo.
- **Pen/Line**: round caps/joins; Pen draws the whole polyline as one path; a single point draws a filled dot.
- **Arrow**: solid filled head (length ≈ max(10, 3.5·size), half-width ≈ 0.45·length), the shaft stops at the head base; halo around shaft+head. Arrows shorter than the head shrink the head.
- **Marker**: the whole stroke as one `GraphicsPath` drawn once with α 125 (no darkening where it crosses itself), flat caps, round joins.
- **Rectangle/Ellipse**: outline only.
- **Pixelate**: downsample the `source` region by the block size and upscale nearest-neighbor (`PixelOffsetMode.Half`) — true redaction, unreadable. Clip to the rect.
- **Text**: `GraphicsPath.AddString` with "Segoe UI Semibold" (fallback Segoe UI bold) at `Size` px, line spacing ≈1.2; halo `DrawPath` (width ≈ max(1.5, Size/8), round joins — thinner than shapes so letter holes stay open), then `FillPath`. While `Editing`: thin dashed frame around the text box (min width ≈ Size) + a caret at the end when `CaretVisible`.
- **Step**: filled circle (diameter `Size`) in `Color` with a 2 px white ring (black ring if the color is light) and a soft halo; centered bold number (white, or black on light colors). `Tip` set → leader line + filled arrow head from the badge edge to `Tip`, drawn under the badge, with halo, stroke ≈ max(2, Size/10). `Label` non-empty or `Editing` → rounded callout box right of the badge (gap ≈ Size/5) — or above/below the badge when the leader heads right through that band, so the callout never hides its own leader, filled with `Color`, bold text white/black at ≈0.55·Size px, vertically centered on the badge; caret while editing.

```csharp
public static class Exporter {
    public static Bitmap Compose(Bitmap source, Rectangle selection, IEnumerable<Annotation> annotations);  // new 32bppArgb bitmap of the selection + annotations (clipped)
    public static string CopyForClaude(Bitmap image, Settings s, string notes);   // see below; returns saved path
    public static void CopyImage(Bitmap image);                      // clipboard: Bitmap + "PNG" stream
    public static string SaveAs(IWin32Window owner, Bitmap image, Settings s);    // SaveFileDialog (initial dir LastSaveDir/SaveFolder, filter "PNG|*.png|JPEG|*.jpg|BMP|*.bmp" preselected from s.Format, default name NewFileName), updates s.LastSaveDir + s.Format; null if cancelled
    public static string SaveAuto(Bitmap image, string folder, string format, int jpegQuality);  // NewFileName in folder (created), returns path
    public static bool Print(IWin32Window owner, Bitmap image);      // PrintDialog (UseEXDialog) → fit inside margins keeping aspect, centered, landscape when wider; false if cancelled
    public static string NewFileName(string folder, string prefix, string ext); // full path: prefix_yyyy-MM-dd_HH-mm-ss[_N].ext, unique, no spaces
}
```
Save names: `Screenshot_2026-10-27_22-16-30.png`; Claude names: `claude_2026-10-27_22-16-30.png`.
JPEG via the GDI+ JPEG encoder with `Encoder.Quality`; flatten to 24bpp RGB on white for JPG/BMP.

### Copy for Claude — clipboard payload (verified against Claude Code 2.1.285)
Claude Code facts: on native Windows **Ctrl+V** in Windows Terminal is the terminal's own text paste; Claude
Code attaches an image when the pasted text is an absolute path ending in `.png/.jpg/.jpeg/.gif/.webp`
whose bytes really are that type. It also splits pasted text at a space followed by a drive-letter path, so
`notes C:\x\shot.png` gives `[Image #1]` + `notes`. **Alt+V** reads the clipboard bitmap
(`Clipboard.ContainsImage()/GetImage()`; a "PNG" format alone or CF_HDROP is NOT detected). Images
> 2000 px per side are downscaled by Claude Code, > 512 KB re-encoded to JPEG — not our job.

`CopyForClaude(image, s, notes)`:
1. `path = NewFileName(s.ClaudeFolder, "claude", "png")` (create folder) — absolute, ASCII name, no spaces. Write the PNG with a `FileStream` and **close it before** touching the clipboard.
2. Text = `path`, or `notes + " " + path` when `notes` is non-empty. Notes are sanitized to ONE line: CR/LF/TAB → space, collapse spaces, strip `"`, cap at 600 chars. Then every space before `/` or a drive path `X:\` becomes U+00A0 (Claude Code splits pasted text only at U+0020 before a path), and notes ending in an image extension get a trailing `;`. Notes are omitted for UNC paths (Claude Code can't split them off). No quotes around the path, no trailing newline.
3. ```csharp
   var data = new DataObject();
   data.SetData(DataFormats.UnicodeText, false, text);     // Ctrl+V in Windows Terminal → [Image #N] (+ notes text)
   data.SetData(DataFormats.Bitmap, false, image);         // Alt+V in Claude Code, and Paint/Slack/Office
   data.SetData("PNG", false, new MemoryStream(pngBytes)); // lossless for browsers/Office
   Clipboard.SetDataObject(data, true, 10, 100);           // copy:true → survives app exit
   ```
   No CF_HDROP. Must run on the STA UI thread. Don't dispose `image` before this returns.
4. Return `path`. Never auto-delete Claude files (Claude may Read the `source:` path later).

`CopyImage(image)`: DataObject with `DataFormats.Bitmap` + `"PNG"` stream; `Clipboard.SetDataObject(data, true, 10, 100)`.

---

## Installer (owner: setup) — `src/ShadowshotSetup/`

Per-user installer, no admin, no external tools. `Program.cs` + `SetupForm.cs` (+ helpers you need).
- Payload: `Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.Shadowshot.exe")`. Version shown = payload's `AssemblyName.GetAssemblyName`-equivalent (read from the extracted file's FileVersionInfo) or the setup's own version (they are built together).
- **Window** (FixedDialog, AutoScaleMode.Dpi, ~520×380 @96 dpi): title "Shadowshot Setup", header with the app icon + "Shadowshot 1.0.0" + subtitle "Lightweight screenshot & annotation tool — built for Claude Code". Install folder (default `%LOCALAPPDATA%\Programs\Shadowshot`, + Browse). Checkboxes: `[x] Start menu shortcut` `[ ] Desktop shortcut` `[x] Start Shadowshot when Windows starts` `[x] Launch Shadowshot now`. If a `Lightshot.exe` process is running, show an amber note: "Lightshot is running and may hold the PrtScr hotkeys. Quit Lightshot (tray icon → Exit) so Shadowshot can use them." Buttons Install / Cancel; after success the panel shows "Shadowshot is installed. Press PrtScr to take a screenshot." + Finish.
- **Install steps**: (1) if Shadowshot is running: set `Local\Shadowshot.Cmd.Exit` (`EventWaitHandle.TryOpenExisting`), wait ≤ 3 s for processes named Shadowshot to exit, then kill those whose path is `<installDir>\Shadowshot.exe`; (2) create the folder, write the payload to `Shadowshot.exe.tmp`, then replace `Shadowshot.exe`; (3) shortcuts via `WScript.Shell` COM late-bound (`Type.GetTypeFromProgID("WScript.Shell")` + `InvokeMember`; no `dynamic`): Start menu `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Shadowshot.lnk`, optional Desktop `Shadowshot.lnk`, target the exe, working dir the folder, description; (4) Run value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Shadowshot = "\"<exe>\" --tray"` if checked (delete it if unchecked); (5) Uninstall key `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Shadowshot`: DisplayName "Shadowshot", DisplayVersion, Publisher "Shadowshot", DisplayIcon "<exe>,0", InstallLocation (full path, no trailing slash), UninstallString `"<exe>" --uninstall`, QuietUninstallString `"<exe>" --uninstall --silent`, EstimatedSize (KB, DWORD), NoModify=1, NoRepair=1, InstallDate yyyyMMdd; also `HKCU\Software\Shadowshot` `InstallDir`; (6) launch `<exe>` (no args) if checked.
- **Silent mode**: `ShadowshotSetup.exe /S [/D=<dir>] [/NOSTARTUP] [/NOLAUNCH]` → no UI; defaults = Start-menu shortcut, startup on, launch on; exit code 0 on success, 1 on failure (error text to stderr isn't visible for WinExe — also write `%TEMP%\ShadowshotSetup.log`).
- Errors → MessageBox with the reason; never leave a half-written `Shadowshot.exe` (use the .tmp + replace).
- Uninstall is handled by the app (`Shadowshot.exe --uninstall`), not by setup.
