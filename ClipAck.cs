using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("ClipAck")]
[assembly: AssemblyDescription("复制或剪切后在屏幕左下角给出确认提示")]
[assembly: AssemblyProduct("ClipAck")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ClipAck
{
    internal static class Program
    {
        private const string AppName = "ClipAck";
        private const string MutexName = "Local\\ClipAck.SingleInstance";

        [STAThread]
        private static void Main()
        {
            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string installedPath = GetInstalledPath();
            string currentPath = Application.ExecutablePath;

            try
            {
                // 第一次从任意位置双击运行时，把自己复制到固定目录并设置开机自启。
                if (!SamePath(currentPath, installedPath))
                {
                    InstallAndStart(currentPath, installedPath);
                    return;
                }

                bool ownsMutex;
                using (Mutex mutex = new Mutex(true, MutexName, out ownsMutex))
                {
                    if (!ownsMutex)
                    {
                        return; // 已经有一个实例在后台运行。
                    }

                    TryEnableAutostart(installedPath);

                    using (TrayContext context = new TrayContext())
                    {
                        Application.Run(context);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "ClipAck 启动失败：\r\n" + ex.Message,
                    AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static string GetInstalledPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipAck");
            return Path.Combine(dir, "ClipAck.exe");
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void InstallAndStart(string currentPath, string installedPath)
        {
            string installDir = Path.GetDirectoryName(installedPath);
            Directory.CreateDirectory(installDir);

            bool installedExists = File.Exists(installedPath);
            try
            {
                File.Copy(currentPath, installedPath, true);
                installedExists = true;
            }
            catch (IOException)
            {
                // 旧版本正在运行时无法覆盖，继续启动已有版本。
                if (!installedExists)
                {
                    throw;
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (!installedExists)
                {
                    throw;
                }
            }

            TryEnableAutostart(installedPath);

            ProcessStartInfo info = new ProcessStartInfo(installedPath);
            info.WorkingDirectory = installDir;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        private static void TryEnableAutostart(string installedPath)
        {
            try
            {
                // 从旧名字版本升级时，移除旧的启动项，避免出现两个托盘程序。
                Autostart.RemoveLegacy();
            }
            catch
            {
            }

            try
            {
                Autostart.Enable(installedPath);
            }
            catch
            {
                // 开机自启写入失败时仍然保留本次运行能力；
                // 正常用户权限下写入 HKCU 不会失败。
            }
        }

        private static void EnableDpiAwareness()
        {
            // 优先使用 Per-Monitor V2，保证高缩放屏幕下提示框位置和字体清晰。
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
            }
        }
    }

    internal static class Autostart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "ClipAck";
        private const string LegacyRunValueName = "CtrlC-Feedback";

        public static void RemoveLegacy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue(LegacyRunValueName, false);
                }
            }
        }

        public static void Enable(string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("无法写入开机自启设置。");
                }

                key.SetValue(RunValueName, "\"" + exePath + "\"", RegistryValueKind.String);
            }
        }

        public static void Disable()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }
    }

    internal enum ClipboardOperation
    {
        Unknown,
        Copy,
        Cut
    }

    /// <summary>
    /// 低级键盘钩子只观察 Ctrl+C / Ctrl+X 两个组合键，
    /// 用来给紧随其后的剪贴板更新打上“复制”或“剪切”的标记。
    /// </summary>
    internal sealed class ShortcutMonitor : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_MENU = 0x12;
        private const int VK_C = 0x43;
        private const int VK_X = 0x58;
        private const int PendingLifetimeMilliseconds = 900;

        private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
        private IntPtr _hook;
        private ClipboardOperation _pending;
        private DateTime _pendingAtUtc;
        private bool _disposed;

        public ShortcutMonitor()
        {
            _hookProc = HookCallback;

            try
            {
                _hook = NativeMethods.SetWindowsHookEx(
                    WH_KEYBOARD_LL,
                    _hookProc,
                    NativeMethods.GetModuleHandle(null),
                    0);
            }
            catch
            {
                // 如果系统禁止全局键盘钩子，程序仍按普通剪贴板更新工作，
                // 只是无法区分文本的“已剪切”和“已复制”。
                _hook = IntPtr.Zero;
            }
        }

        public ClipboardOperation Consume()
        {
            ClipboardOperation result = _pending;
            _pending = ClipboardOperation.Unknown;

            if (result == ClipboardOperation.Unknown)
            {
                return ClipboardOperation.Unknown;
            }

            if ((DateTime.UtcNow - _pendingAtUtc).TotalMilliseconds > PendingLifetimeMilliseconds)
            {
                return ClipboardOperation.Unknown;
            }

            return result;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_disposed)
            {
                int message = wParam.ToInt32();

                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    NativeMethods.KBDLLHOOKSTRUCT info =
                        (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam,
                            typeof(NativeMethods.KBDLLHOOKSTRUCT));

                    if ((info.vkCode == VK_C || info.vkCode == VK_X) &&
                        IsControlDown() &&
                        !IsKeyDown(VK_MENU))
                    {
                        _pending = info.vkCode == VK_X
                            ? ClipboardOperation.Cut
                            : ClipboardOperation.Copy;
                        _pendingAtUtc = DateTime.UtcNow;
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsControlDown()
        {
            return IsKeyDown(VK_CONTROL) ||
                   IsKeyDown(VK_LCONTROL) ||
                   IsKeyDown(VK_RCONTROL);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 资源管理器的“剪切/复制”会在剪贴板中写入 Preferred DropEffect。
    /// 这里只读取这个 4 字节标志，不读取具体文字、图片或文件。
    /// </summary>
    internal static class ClipboardInspector
    {
        private const string PreferredDropEffectFormat = "Preferred DropEffect";
        private const int DropEffectMove = 2;

        public static bool IsFileMove()
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null ||
                    !data.GetDataPresent(PreferredDropEffectFormat, false))
                {
                    return false;
                }

                object value = data.GetData(PreferredDropEffectFormat);
                MemoryStream stream = value as MemoryStream;
                if (stream == null)
                {
                    return false;
                }

                long position = stream.Position;
                stream.Position = 0;

                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, buffer.Length);
                stream.Position = position;

                if (read < 4)
                {
                    return false;
                }

                int effect = BitConverter.ToInt32(buffer, 0);
                return (effect & DropEffectMove) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 一个不可见的顶层窗口，只负责接收剪贴板变化通知。
    /// 除资源管理器的文件操作标志外，不读取剪贴板内容，
    /// 因此文本、图片、文件以及任何自定义格式都能触发提示。
    /// </summary>
    internal sealed class ClipboardListener : NativeWindow, IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly Action _onChanged;
        private readonly Timer _debounce;
        private bool _disposed;

        public ClipboardListener(Action onChanged)
        {
            _onChanged = onChanged;

            CreateParams cp = new CreateParams();
            cp.Caption = "ClipAck.ClipboardListener";
            cp.Style = 0;
            cp.ExStyle = WS_EX_TOOLWINDOW;
            CreateHandle(cp);

            if (!NativeMethods.AddClipboardFormatListener(Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _debounce = new Timer();
            _debounce.Interval = 80;
            _debounce.Tick += DebounceTick;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                // 一次复制可能触发多条系统通知，合并成一次提示。
                _debounce.Stop();
                _debounce.Start();
            }

            base.WndProc(ref m);
        }

        private void DebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (!_disposed)
            {
                _onChanged();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_debounce != null)
            {
                _debounce.Stop();
                _debounce.Tick -= DebounceTick;
                _debounce.Dispose();
            }

            if (Handle != IntPtr.Zero)
            {
                NativeMethods.RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ShortcutMonitor _shortcutMonitor;
        private readonly ClipboardListener _listener;
        private readonly ToastForm _toast;
        private bool _closed;
        private bool _uiDisposed;

        public TrayContext()
        {
            _toast = new ToastForm();
            _shortcutMonitor = new ShortcutMonitor();
            _listener = new ClipboardListener(OnClipboardChanged);

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("关闭复制反馈");
            exitItem.Click += ExitClick;
            _menu.Items.Add(exitItem);

            Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon == null)
            {
                icon = SystemIcons.Application;
            }

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = icon;
            _trayIcon.Text = "ClipAck（右键关闭）";
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Visible = true;
        }

        private void OnClipboardChanged()
        {
            ClipboardOperation operation = _shortcutMonitor.Consume();
            bool isCut = operation == ClipboardOperation.Cut;

            // 右键菜单剪切文件时没有键盘事件，用资源管理器的
            // Preferred DropEffect 标志补足文件剪切场景。
            if (operation == ClipboardOperation.Unknown)
            {
                isCut = ClipboardInspector.IsFileMove();
            }

            _toast.ShowToast(isCut ? "已剪切" : "已复制");
        }

        private void ExitClick(object sender, EventArgs e)
        {
            CloseApp();
        }

        private void CloseApp()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            // 用户主动关闭后不再开机自启；以后重新双击 exe 即可再次启用。
            try
            {
                Autostart.Disable();
            }
            catch
            {
            }

            // 先移除托盘图标，等菜单点击处理结束、消息循环退出后再清理对象。
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
            }

            ExitThread();
        }

        private void DisposeUi()
        {
            if (_uiDisposed)
            {
                return;
            }

            _uiDisposed = true;

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_menu != null)
            {
                _menu.Dispose();
            }

            if (_listener != null)
            {
                _listener.Dispose();
            }

            if (_shortcutMonitor != null)
            {
                _shortcutMonitor.Dispose();
            }

            if (_toast != null)
            {
                _toast.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeUi();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class ToastForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly Timer _hideTimer;
        private Font _textFont;
        private float _scale = 1f;
        private string _message = "已复制";

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(30, 32, 38);
            DoubleBuffered = true;

            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            _textFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);

            _hideTimer = new Timer();
            _hideTimer.Interval = 1000;
            _hideTimer.Tick += HideTick;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            uint dpi = NativeMethods.GetDpiForWindow(Handle);
            if (dpi > 0)
            {
                _scale = dpi / 96f;
            }
            else
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    _scale = g.DpiX / 96f;
                }
            }

            Size newSize = new Size(S(132), S(44));
            if (ClientSize != newSize)
            {
                ClientSize = newSize;
            }

            UpdateRoundedRegion();
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = CreateRoundRect(new Rectangle(0, 0, Width, Height), S(10)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                {
                    old.Dispose();
                }
            }
        }

        private static GraphicsPath CreateRoundRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();

            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = radius * 2;
            if (diameter > bounds.Width)
            {
                diameter = bounds.Width;
            }
            if (diameter > bounds.Height)
            {
                diameter = bounds.Height;
            }

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void ShowToast(string message)
        {
            if (IsDisposed)
            {
                return;
            }

            if (Handle == IntPtr.Zero)
            {
                return;
            }

            if (_message != message)
            {
                _message = message;
                Invalidate();
            }

            Reposition();

            if (!Visible)
            {
                Show();
            }

            // 连续复制/剪切时，让本次提示重新完整显示 1 秒。
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void Reposition()
        {
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            int margin = S(16);

            Location = new Point(
                workArea.Left + margin,
                workArea.Bottom - Height - margin);
        }

        private void HideTick(object sender, EventArgs e)
        {
            _hideTimer.Stop();
            Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            float scale = _scale;
            float diameter = 20f * scale;
            float centerX = 23f * scale;
            float centerY = Height / 2f;

            using (SolidBrush circleBrush = new SolidBrush(Color.FromArgb(52, 199, 89)))
            {
                g.FillEllipse(
                    circleBrush,
                    centerX - diameter / 2f,
                    centerY - diameter / 2f,
                    diameter,
                    diameter);
            }

            using (Pen checkPen = new Pen(Color.White, Math.Max(1.4f, 2.2f * scale)))
            {
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                checkPen.LineJoin = LineJoin.Round;

                PointF[] check =
                {
                    new PointF(centerX - diameter * 0.22f, centerY),
                    new PointF(centerX - diameter * 0.05f, centerY + diameter * 0.16f),
                    new PointF(centerX + diameter * 0.24f, centerY - diameter * 0.17f)
                };

                g.DrawLines(checkPen, check);
            }

            Rectangle textRect = new Rectangle(
                S(41),
                0,
                Width - S(49),
                Height);

            TextRenderer.DrawText(
                g,
                _message,
                _textFont,
                textRect,
                Color.FromArgb(247, 248, 250),
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hideTimer != null)
                {
                    _hideTimer.Stop();
                    _hideTimer.Tick -= HideTick;
                    _hideTimer.Dispose();
                }

                if (_textFont != null)
                {
                    _textFont.Dispose();
                    _textFont = null;
                }
            }

            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelKeyboardProc callback,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int nCode,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }
}
