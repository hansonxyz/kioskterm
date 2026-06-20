using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KioskTerm;

internal sealed class MainForm : Form
{
    private readonly string? _header;
    private readonly string _commandLine;
    private readonly bool _keepAwake;
    private readonly string? _logoPath;
    private readonly bool _allowInput;
    private readonly bool _testMode;
    private string? _logoServedName;
    private readonly WebView2 _web = new();
    private ConPty? _pty;
    private bool _ptyStarted;
    private string? _tempDir;
    private System.Windows.Forms.Timer? _watchdog;

    // Fail-safe: if the WebView/terminal never gets far enough to start the
    // command within this window, tear down rather than sit fullscreen forever.
    private const int WatchdogMs = 20000;

    public int ExitCode { get; private set; }

    public MainForm(string? header, string commandLine, bool keepAwake, string? logoPath,
                    bool allowInput, bool testMode)
    {
        _header = header;
        _commandLine = commandLine;
        _keepAwake = keepAwake;
        _logoPath = logoPath;
        _allowInput = allowInput;
        _testMode = testMode;

        TopMost = false;
        BackColor = System.Drawing.Color.Black;
        KeyPreview = false;

        if (_testMode)
        {
            // Safety harness: a normal titled, taskbar-visible, windowed form so
            // there's always an obvious escape hatch (the close button, Alt+Tab,
            // the taskbar) while we test input handling.
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(1100, 720);
            Text = "KioskTerm (TEST MODE)";
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "KioskTerm";
        }

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = System.Drawing.Color.Black;
        _web.TabStop = _allowInput;
        Controls.Add(_web);
    }

    // In locked (display-only) mode, don't steal focus when shown — lets a spawned
    // installer keep the foreground. In input mode we do want to activate.
    protected override bool ShowWithoutActivation => !_allowInput;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Only the locked overlay is no-activate; input mode must be focusable.
            if (!_allowInput)
            {
                cp.ExStyle |= Native.WS_EX_NOACTIVATE;
                if (!_testMode) cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            }
            return cp;
        }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_testMode) Bounds = Screen.PrimaryScreen!.Bounds;   // borderless fullscreen on the primary monitor
        await InitWebViewAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // --test leaves the taskbar and keys alone so the environment stays recoverable.
        if (!_testMode)
        {
            Native.HideTaskbar();
            Native.InstallKeyboardHook(blockCtrlCombos: _allowInput);
        }
        if (_keepAwake) Native.PreventSleepAndDisplayOff();

        if (_allowInput)
        {
            // Take and hold the foreground; reclaim it when a spawned window closes.
            Native.ForceForeground(Handle);
            _web.Focus();
            Native.StartForegroundWatch(Handle, () =>
            {
                if (!IsDisposed) { _web.Focus(); PostToWeb("focus"); }
            });
        }
        else
        {
            SinkToBottom();
        }

        _watchdog = new System.Windows.Forms.Timer { Interval = WatchdogMs };
        _watchdog.Tick += (_, _) =>
        {
            _watchdog!.Stop();
            if (!_ptyStarted)
            {
                ExitCode = 2;   // never started — signal failure to the caller
                Close();
            }
        };
        _watchdog.Start();
    }

    private void SinkToBottom()
    {
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_WINDOWPOSCHANGING when !_allowInput:
                // Locked mode: re-assert rear-most on every z-order change.
                var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                pos.flags &= ~Native.SWP_NOZORDER;
                Marshal.StructureToPtr(pos, m.LParam, false);
                break;

            case Native.WM_MOUSEACTIVATE when !_allowInput:
                // Locked mode: never activate on click — stay input-inert and in the rear.
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;

            case Native.WM_SYSCOMMAND when _allowInput && !_testMode:
                // Input mode: refuse minimize so it can't be hidden out of the way.
                if ((m.WParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE) return;
                break;
        }
        base.WndProc(ref m);
    }

    private async Task InitWebViewAsync()
    {
        _tempDir = ExtractWebAssets();
        _logoServedName = CopyLogo(_tempDir);

        string userData = Path.Combine(Path.GetTempPath(), "kioskterm-wv2-" +
            Environment.ProcessId.ToString());
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;

        // Lock the embedded WebView down: no devtools, menus, or zoom.
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;

        core.WebMessageReceived += OnWebMessage;

        core.SetVirtualHostNameToFolderMapping(
            "kioskterm.local", _tempDir, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://kioskterm.local/index.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg = e.TryGetWebMessageAsString();

        // User keystrokes (xterm-encoded) forwarded to the console's stdin.
        if (msg.Length >= 2 && msg[0] == 'i' && msg[1] == ':')
        {
            _pty?.WriteInput(msg.Substring(2));
            return;
        }

        if (msg == "ready")
        {
            if (!string.IsNullOrEmpty(_header))
                PostToWeb("h:" + NormalizeHeader(_header));
            else if (_logoServedName != null)
                PostToWeb("h:");   // empty band so the logo has a height to fit within

            if (_logoServedName != null)
                PostToWeb("l:" + _logoServedName);

            if (_allowInput)
            {
                _web.Focus();
                PostToWeb("input:1");   // tell the page to accept keystrokes and focus the terminal
            }
            return;
        }

        if (msg.StartsWith("size:"))
        {
            // "size:<cols>x<rows>"
            var dims = msg.Substring(5).Split('x');
            if (dims.Length == 2 &&
                short.TryParse(dims[0], out short cols) &&
                short.TryParse(dims[1], out short rows))
            {
                if (!_ptyStarted)
                {
                    _ptyStarted = true;
                    _watchdog?.Stop();
                    StartPty(cols, rows);
                }
                else
                {
                    _pty?.Resize(cols, rows);
                }
            }
        }
    }

    private void StartPty(short cols, short rows)
    {
        _pty = new ConPty();
        _pty.Output += chunk =>
        {
            // Marshal to the UI thread; WebView2 is single-threaded.
            if (IsDisposed) return;
            try { BeginInvoke(() => PostToWeb("o:" + Convert.ToBase64String(chunk))); }
            catch (InvalidOperationException) { /* handle gone */ }
        };
        _pty.Exited += code =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    ExitCode = code;
                    Close();
                });
            }
            catch (InvalidOperationException) { /* already closing */ }
        };

        try
        {
            _pty.Start(_commandLine, cols, rows);
        }
        catch (Exception ex)
        {
            int code = ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : -1;
            string detail = $"Failed to start command (win32={code}): {ex.Message}\r\ncommand line: {_commandLine}";

            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "kioskterm-error.log"), detail + "\r\n" + ex); }
            catch { /* best effort */ }

            PostToWeb("o:" + Convert.ToBase64String(
                Encoding.UTF8.GetBytes("\r\n\x1b[31m" + detail.Replace("\r\n", "\r\n") + "\x1b[0m\r\n")));

            // Don't sit fullscreen on a dead command — surface briefly then exit.
            var bail = new System.Windows.Forms.Timer { Interval = 4000 };
            bail.Tick += (_, _) => { bail.Stop(); ExitCode = 3; Close(); };
            bail.Start();
        }
    }

    // Allow a multi-line caption to be passed on the command line as a single argv
    // token using literal "\n" (and "\r\n") escapes, since a real newline can't
    // easily be typed into an argument.
    private static string NormalizeHeader(string s) =>
        s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");

    private void PostToWeb(string message)
    {
        if (_web.CoreWebView2 != null && !IsDisposed)
            _web.CoreWebView2.PostWebMessageAsString(message);
    }

    private static string ExtractWebAssets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kioskterm-web-" + Environment.ProcessId.ToString());
        Directory.CreateDirectory(dir);

        var asm = Assembly.GetExecutingAssembly();
        const string prefix = "KioskTerm.web.";
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix)) continue;
            string fileName = name.Substring(prefix.Length);
            using Stream? rs = asm.GetManifestResourceStream(name);
            if (rs == null) continue;
            using var fs = File.Create(Path.Combine(dir, fileName));
            rs.CopyTo(fs);
        }
        return dir;
    }

    // Copies the supplied logo into the served folder and returns the filename to
    // reference from the page (served from https://kioskterm.local/, i.e. CSP 'self').
    private string? CopyLogo(string dir)
    {
        if (string.IsNullOrEmpty(_logoPath) || !File.Exists(_logoPath)) return null;
        try
        {
            string ext = Path.GetExtension(_logoPath);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            string name = "logo" + ext.ToLowerInvariant();
            File.Copy(_logoPath, Path.Combine(dir, name), overwrite: true);
            return name;
        }
        catch
        {
            return null;   // bad/locked file -> just render without a logo
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Always restore the environment, even on unexpected close.
        Native.StopForegroundWatch();
        Native.RemoveKeyboardHook();
        Native.RestoreTaskbar();
        Native.RestorePowerState();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pty?.Dispose();
            try { if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
            catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }
}
