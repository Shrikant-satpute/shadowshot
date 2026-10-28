using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Shadowshot
{
    public struct Hotkey
    {
        public Keys Key;
        public bool Ctrl, Shift, Alt, Win;

        public bool IsEmpty => Key == Keys.None;

        public static Hotkey Parse(string s)
        {
            var hotkey = new Hotkey();
            if (string.IsNullOrWhiteSpace(s)) return hotkey;
            foreach (string raw in s.Split('+'))
            {
                string part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": hotkey.Ctrl = true; break;
                    case "shift": hotkey.Shift = true; break;
                    case "alt": hotkey.Alt = true; break;
                    case "win": case "windows": hotkey.Win = true; break;
                    default:
                        Keys key = ParseKey(part);
                        if (key == Keys.None || !hotkey.IsEmpty) return new Hotkey();
                        hotkey.Key = key;
                        break;
                }
            }
            return hotkey.IsEmpty ? new Hotkey() : hotkey;
        }

        static Keys ParseKey(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "prtscr": case "prtsc": return Keys.PrintScreen;
            }
            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') return Keys.D0 + (name[0] - '0');
            // Single key names only: Enum.TryParse would also accept numbers and comma-separated flag lists.
            if (name.Length == 0 || !char.IsLetter(name[0]) || name.IndexOf(',') >= 0 || !Enum.TryParse(name, true, out Keys key)
                || !Enum.IsDefined(typeof(Keys), key) || (uint)key >= (uint)Keys.KeyCode || IsModifierKey(key))
                return Keys.None;
            return key;
        }

        internal static bool IsModifierKey(Keys key) =>
            key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey
            || key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey
            || key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu
            || key == Keys.LWin || key == Keys.RWin;

        public override string ToString() => IsEmpty ? "" : Modifiers() + Key;

        public string ToDisplayString()
        {
            if (IsEmpty) return "";
            string name;
            if (Key == Keys.PrintScreen) name = "PrtScr";
            else if (Key == Keys.Enter) name = "Enter";
            else if (Key == Keys.PageDown) name = "PageDown";
            else if (Key >= Keys.D0 && Key <= Keys.D9) name = ((char)('0' + (Key - Keys.D0))).ToString();
            else name = Key.ToString();
            return Modifiers() + name;
        }

        string Modifiers()
        {
            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            if (Win) sb.Append("Win+");
            return sb.ToString();
        }
    }

    public sealed class HotkeyManager : IDisposable
    {
        public event Action<int> Pressed;

        readonly MessageWindow window;
        readonly HashSet<int> registered = new HashSet<int>();
        PrintScreenHook printScreen;

        public HotkeyManager()
        {
            window = new MessageWindow(this);
        }

        public bool Register(int id, Hotkey hk, out string error)
        {
            if (registered.Remove(id)) Native.UnregisterHotKey(window.Handle, id);
            if (printScreen != null && printScreen.Id == id) printScreen.Id = 0;

            // Windows 11 hands a bare PrtScr to Snipping Tool (or swallows it) before RegisterHotKey sees it,
            // and RegisterHotKey even "succeeds" for it. A low-level keyboard hook gets the key first.
            if (hk.Key == Keys.PrintScreen && !hk.Ctrl && !hk.Alt && !hk.Shift && !hk.Win)
            {
                if (!EnsurePrintScreenHook())
                {
                    error = "PrtScr could not be claimed: the keyboard hook failed to install";
                    return false;
                }
                if (printScreen.Id != 0)
                {
                    error = "PrtScr is already used by another Shadowshot hotkey";
                    return false;
                }
                printScreen.Id = id;
                // The hook isn't called while an elevated window is in front (UIPI); the plain hotkey covers that
                // case. A press the hook swallows never reaches hotkey matching, so nothing fires twice.
                if (Native.RegisterHotKey(window.Handle, id, Native.MOD_NOREPEAT, (uint)Keys.PrintScreen)) registered.Add(id);
                error = null;
                return true;
            }

            uint modifiers = Native.MOD_NOREPEAT;
            if (hk.Alt) modifiers |= Native.MOD_ALT;
            if (hk.Ctrl) modifiers |= Native.MOD_CONTROL;
            if (hk.Shift) modifiers |= Native.MOD_SHIFT;
            if (hk.Win) modifiers |= Native.MOD_WIN;

            if (Native.RegisterHotKey(window.Handle, id, modifiers, (uint)hk.Key))
            {
                registered.Add(id);
                error = null;
                return true;
            }
            int code = Marshal.GetLastWin32Error();
            error = code == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                ? hk.ToDisplayString() + " is already used by another app (Lightshot?)"
                : hk.ToDisplayString() + " could not be registered: " + new Win32Exception(code).Message;
            return false;
        }

        public void UnregisterAll()
        {
            foreach (int id in registered) Native.UnregisterHotKey(window.Handle, id);
            registered.Clear();
            if (printScreen != null) printScreen.Id = 0;
        }

        // For the Options dialog: no hotkey fires, but the suspended hook still keeps a bare PrtScr away from
        // Snipping Tool and hands it to our focused hotkey box, even if no hotkey used PrtScr before.
        public void Suspend()
        {
            UnregisterAll();
            EnsurePrintScreenHook();
        }

        bool EnsurePrintScreenHook()
        {
            if (printScreen == null)
            {
                var hook = new PrintScreenHook(window.Handle);
                if (!hook.Installed)
                {
                    hook.Dispose();
                    return false;
                }
                printScreen = hook;
            }
            return true;
        }

        public void Dispose()
        {
            UnregisterAll();
            printScreen?.Dispose();
            window.DestroyHandle();
        }

        // Windows waits for low-level hook callbacks before delivering every keystroke system-wide, so the hook
        // lives on its own thread: a busy UI thread (capturing, a modal dialog) never delays anyone's typing.
        sealed class PrintScreenHook : IDisposable
        {
            public volatile int Id;   // hotkey id to post as WM_HOTKEY; 0 = suspended
            public readonly bool Installed;

            readonly IntPtr target;
            readonly Thread thread;
            readonly Native.HookProc callback;   // field: the hook must not outlive its delegate
            uint threadId;
            bool held;         // PrtScr is down: later key-downs are auto-repeats, not new presses
            bool swallowing;   // inside a PrtScr press we took, so its repeats and key-up are ours too

            public PrintScreenHook(IntPtr target)
            {
                this.target = target;
                callback = OnKey;
                bool installed = false;
                using (var ready = new ManualResetEventSlim())
                {
                    thread = new Thread(() =>
                    {
                        threadId = Native.GetCurrentThreadId();
                        IntPtr hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, callback, Native.GetModuleHandle(null), 0);
                        installed = hook != IntPtr.Zero;
                        ready.Set();
                        if (hook == IntPtr.Zero) return;
                        while (Native.GetMessage(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0) { }   // hook calls arrive in here
                        Native.UnhookWindowsHookEx(hook);
                    }) { IsBackground = true, Name = "PrtScr hook" };
                    thread.Start();
                    ready.Wait();
                }
                Installed = installed;
            }

            IntPtr OnKey(int code, IntPtr message, IntPtr data)
            {
                if (code >= 0 && Marshal.ReadInt32(data) == Native.VK_SNAPSHOT)
                {
                    int msg = message.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool newPress = down && !held;
                    held = down;
                    if (newPress && !ModifierDown())
                    {
                        int id = Id;
                        if (id != 0)
                            Native.PostMessage(target, Native.WM_HOTKEY, (IntPtr)id, IntPtr.Zero);
                        else if (!ForwardToOwnFocusedWindow())
                            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
                        swallowing = true;
                    }
                    if (swallowing)
                    {
                        if (!down) swallowing = false;
                        return (IntPtr)1;
                    }
                }
                return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
            }

            // While suspended (Options open), PrtScr still mustn't reach Snipping Tool when one of our windows has
            // focus: hand it to the focused control as the key-up WinForms reports for PrintScreen.
            static bool ForwardToOwnFocusedWindow()
            {
                IntPtr foreground = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(foreground, out uint pid);
                if (pid != (uint)Native.GetCurrentProcessId()) return false;
                var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                IntPtr focus = Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(foreground, out _), ref info) ? info.hwndFocus : IntPtr.Zero;
                Native.PostMessage(focus != IntPtr.Zero ? focus : foreground, Native.WM_KEYUP, (IntPtr)Native.VK_SNAPSHOT, (IntPtr)unchecked((int)0xC1370001));
                return true;
            }

            static bool ModifierDown() =>
                IsDown(Native.VK_CONTROL) || IsDown(Native.VK_MENU) || IsDown(Native.VK_SHIFT) || IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN);

            static bool IsDown(int vk) => Native.GetAsyncKeyState(vk) < 0;

            public void Dispose()
            {
                Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(1000);
            }
        }

        sealed class MessageWindow : NativeWindow
        {
            readonly HotkeyManager owner;

            public MessageWindow(HotkeyManager owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_HOTKEY) owner.Pressed?.Invoke(m.WParam.ToInt32());
                else base.WndProc(ref m);
            }
        }

        static class Native
        {
            public static readonly IntPtr HWND_MESSAGE = (IntPtr)(-3);
            public const int WM_HOTKEY = 0x0312;
            public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
            public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
            public const int WH_KEYBOARD_LL = 13, WM_QUIT = 0x0012, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104;
            public const int VK_SNAPSHOT = 0x2C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

            public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct GUITHREADINFO
            {
                public int cbSize, flags;
                public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
                public int left, top, right, bottom;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

            [DllImport("user32.dll")]
            public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            [DllImport("user32.dll")]
            public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

            [DllImport("user32.dll")]
            public static extern bool UnhookWindowsHookEx(IntPtr hook);

            [DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

            [DllImport("user32.dll")]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vk);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

            [DllImport("user32.dll")]
            public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("kernel32.dll")]
            public static extern int GetCurrentProcessId();

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string moduleName);
        }
    }
}
