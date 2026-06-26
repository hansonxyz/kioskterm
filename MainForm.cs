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
    private readonly bool _deferReveal;   // --show-on-output-only: stay parked off-screen until real output
    private readonly bool _hidden;        // --hidden: run the command but never show anything
    private readonly string? _logPath;
    private readonly string? _logRawPath;
    private readonly bool _logTimestamps;
    private SessionLogger? _logger;
    private System.Drawing.Rectangle _targetBounds;
    private bool _revealed;
    private readonly ContentScanner _contentScanner = new();
    private string? _logoServedName;
    private readonly WebView2 _web = new();
    private ConPty? _pty;
    private bool _ptyStarted;
    private string? _tempDir;
    private System.Windows.Forms.Timer? _watchdog;
    private System.Windows.Forms.Timer? _shellWatch;

    // Fail-safe: if the renderer comes up but the terminal never starts within this
    // window, run the command headless rather than hang.
    private const int WatchdogMs = 20000;

    // How long to keep retrying WebView2 environment/controller creation before
    // giving up and running headless (rides out the transient 0x80070490 seen mid
    // Edge/WebView2 update or very early in a post-update auto-logon).
    private const int WebView2InitBudgetSec = 30;

    // Distinct exit codes for KioskTerm's own failures (vs. the wrapped command's
    // pass-through code). 64/66 chosen from the conventional sysexits range.
    public const int ExitUsage = 64;
    public const int ExitCommandLaunchFailed = 66;

    private string? _startupError;   // WebView2 init failure detail (for the headless banner)

    public int ExitCode { get; private set; }

    public MainForm(string? header, string commandLine, bool keepAwake, string? logoPath,
                    bool allowInput, bool testMode, bool showOnOutputOnly, bool hidden,
                    string? logPath, string? logRawPath, bool logTimestamps)
    {
        _header = header;
        _commandLine = commandLine;
        _keepAwake = keepAwake;
        _logoPath = logoPath;
        _allowInput = allowInput;
        _testMode = testMode;
        _hidden = hidden;
        _logPath = logPath;
        _logRawPath = logRawPath;
        _logTimestamps = logTimestamps;
        _deferReveal = showOnOutputOnly && !testMode && !hidden;   // --test/--hidden override

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
        _targetBounds = _testMode ? Bounds : Screen.PrimaryScreen!.Bounds;
        // Park off-screen during init for every mode; we only come on-screen / lock
        // down once the renderer is actually ready. That way a long WebView2 retry
        // never leaves a black locked screen, and a fatal failure falls back cleanly.
        Bounds = OffScreen(_targetBounds.Size);
        await InitWebViewAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Keep the machine awake while provisioning (not for silent --hidden runs).
        // Reveal/lockdown and the watchdog are wired once WebView2 is confirmed ready
        // (see InitWebViewAsync), so a long WebView2 retry doesn't lock a black screen.
        if (_keepAwake && !_hidden) Native.PreventSleepAndDisplayOff();
    }

    // Brings the overlay on-screen and engages the lockdown. Runs immediately on
    // show, or is deferred to the first real output under --show-on-output-only.
    private void Reveal()
    {
        if (_hidden || _revealed) return;   // --hidden never shows, even on errors
        _revealed = true;

        Bounds = _targetBounds;   // on-screen (a no-op unless we were parked off-screen)

        // --test leaves the taskbar and keys alone so the environment stays recoverable.
        if (!_testMode)
        {
            Native.HideTaskbar();
            Native.InstallKeyboardHook(blockCtrlCombos: _allowInput);

            // Once a second, keep the shell suppressed: dismiss the Start/Search
            // menu if Windows opened it, and re-hide the taskbar if explorer.exe
            // restarted (which spawns a fresh, visible one).
            _shellWatch = new System.Windows.Forms.Timer { Interval = 1000 };
            _shellWatch.Tick += (_, _) =>
            {
                Native.DismissStartMenuIfOpen();
                Native.EnsureTaskbarsHidden();
            };
            _shellWatch.Start();
        }

        if (_allowInput)
        {
            // Take and hold the foreground; reclaim it when a spawned window closes.
            Native.ForceForeground(Handle);
            Native.StartForegroundWatch(Handle, () =>
            {
                if (!IsDisposed) { _web.Focus(); PostToWeb("focus"); }
            });
            if (_ptyStarted) EnableInputUi();   // deferred reveal: page is already up; else the ready handler does it
        }
        else if (!_testMode)
        {
            SinkToBottom();
        }
    }

    private void EnableInputUi()
    {
        _web.Focus();
        PostToWeb("input:1");   // tell the page to accept keystrokes and focus the terminal
    }

    private static System.Drawing.Rectangle OffScreen(System.Drawing.Size size)
    {
        var vs = SystemInformation.VirtualScreen;
        return new System.Drawing.Rectangle(vs.Right + 1000, vs.Top, size.Width, size.Height);
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
        // --hidden never renders, so skip WebView2 entirely: no dependency on it, no
        // startup wait, and robust to early-boot / non-interactive (e.g. SSH) sessions.
        if (_hidden) { RunHeadless(null); return; }

        _tempDir = ExtractWebAssets();
        _logoServedName = CopyLogo(_tempDir);

        if (!await TryInitWebView2())
        {
            // WebView2 could not initialize even after retrying. Run the command
            // anyway (no overlay) so provisioning is never blocked.
            RunHeadless(_startupError);
            return;
        }

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

        // Renderer is up; guard that the page actually starts the terminal.
        StartWatchdog();
    }

    // Creates the WebView2 environment + controller, retrying briefly to ride out the
    // transient "element not found" (0x80070490) seen mid Edge/WebView2 update or very
    // early in a post-update auto-logon. Returns false (and records the error) if it
    // never succeeds within the budget.
    private async Task<bool> TryInitWebView2()
    {
        string userData = Path.Combine(Path.GetTempPath(), "kioskterm-wv2-" +
            Environment.ProcessId.ToString());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);
                return true;
            }
            catch (Exception ex) { last = ex; }

            if (IsDisposed || sw.Elapsed.TotalSeconds >= WebView2InitBudgetSec) break;
            try { await Task.Delay(2000); } catch { }
        }
        RecordStartupFailure(last, attempts);
        return false;
    }

    private void StartWatchdog()
    {
        _watchdog = new System.Windows.Forms.Timer { Interval = WatchdogMs };
        _watchdog.Tick += (_, _) =>
        {
            _watchdog!.Stop();
            // Renderer came up but the page never started the terminal: run the
            // command headless rather than hang or exit silently.
            if (!_ptyStarted) RunHeadless("kioskterm: terminal did not start — running headless");
        };
        _watchdog.Start();
    }

    // Runs the wrapped command with no overlay (WebView2 bypassed for --hidden,
    // failed to initialize, or the terminal never handshook). The command still runs
    // and --log still captures it; only the on-screen overlay is absent.
    private void RunHeadless(string? reason)
    {
        if (_ptyStarted) return;
        _ptyStarted = true;
        _watchdog?.Stop();
        StartPty(120, 40, reason);   // default size; output still captured by --log
    }

    // Surfaces a WebView2 startup failure everywhere useful (the bug being that this
    // was previously swallowed): stderr, the dedicated error log, and — via the
    // headless banner — the --log file.
    private void RecordStartupFailure(Exception? ex, int attempts)
    {
        string hr = ex != null ? $"0x{(uint)ex.HResult:X8}" : "n/a";
        _startupError = $"kioskterm: WebView2 init failed after {attempts} attempt(s) over " +
                        $"{WebView2InitBudgetSec}s: {hr} {ex?.GetType().Name}: {ex?.Message}".Trim();
        try { Console.Error.WriteLine(_startupError); } catch { }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "kioskterm-error.log"), _startupError + "\r\n"); } catch { }
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
            // Renderer is up: reveal the overlay now (deferred/hidden modes don't).
            if (!_deferReveal && !_hidden) Reveal();

            if (!string.IsNullOrEmpty(_header))
                PostToWeb("h:" + NormalizeHeader(_header));
            else if (_logoServedName != null)
                PostToWeb("h:");   // empty band so the logo has a height to fit within

            if (_logoServedName != null)
                PostToWeb("l:" + _logoServedName);

            // If already revealed (the normal case), enable input now; under a
            // deferred reveal, Reveal() enables it once we come on-screen.
            if (_allowInput && _revealed)
                EnableInputUi();
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

    private void StartPty(short cols, short rows, string? banner = null)
    {
        // Full-session logging is orthogonal to display mode (works with --hidden,
        // --show-on-output-only, --test, etc.) and never changes on-screen behavior.
        _logger = SessionLogger.TryCreate(_logPath, _logRawPath, _logTimestamps);
        if (!string.IsNullOrEmpty(banner)) _logger?.WriteBanner(banner);

        _pty = new ConPty();
        _pty.Output += chunk =>
        {
            _logger?.Append(chunk);   // capture on the read thread, independent of the UI

            // Marshal to the UI thread; WebView2 is single-threaded.
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    // --show-on-output-only: reveal on the first non-whitespace,
                    // non-escape-sequence byte.
                    if (_deferReveal && !_revealed && _contentScanner.HasContent(chunk))
                        Reveal();
                    PostToWeb("o:" + Convert.ToBase64String(chunk));
                });
            }
            catch (InvalidOperationException) { /* handle gone */ }
        };
        _pty.Exited += code =>
        {
            _logger?.Close();   // ConPty joined the read thread, so the full tail is captured

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
            string detail = $"kioskterm: failed to start command (win32={code}): {ex.Message}\r\ncommand line: {_commandLine}";

            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "kioskterm-error.log"), detail + "\r\n" + ex + "\r\n"); }
            catch { /* best effort */ }
            try { Console.Error.WriteLine(detail); } catch { }
            _logger?.WriteBanner(detail);

            // Show the error only if there's actually a renderer; headless runs just log it.
            if (_web.CoreWebView2 != null)
            {
                Reveal();
                PostToWeb("o:" + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("\r\n\x1b[31m" + detail + "\x1b[0m\r\n")));
                // Don't sit on a dead command — surface briefly then exit.
                var bail = new System.Windows.Forms.Timer { Interval = 4000 };
                bail.Tick += (_, _) => { bail.Stop(); ExitCode = ExitCommandLaunchFailed; Close(); };
                bail.Start();
            }
            else
            {
                ExitCode = ExitCommandLaunchFailed;
                Close();
            }
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
            _watchdog?.Dispose();
            _shellWatch?.Dispose();
            _pty?.Dispose();
            _logger?.Dispose();   // idempotent; flushes a trailing partial line if the command was killed
            try { if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
            catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Scans the raw PTY byte stream for the first "real" output — a printable,
    /// non-whitespace character that isn't part of a terminal escape sequence
    /// (colors, cursor moves, window-title sets, etc.). Stateful across chunks.
    /// </summary>
    private sealed class ContentScanner
    {
        private enum S { Normal, Esc, Csi, Osc, OscEsc }
        private S _s = S.Normal;

        public bool HasContent(byte[] data)
        {
            foreach (byte b in data)
            {
                switch (_s)
                {
                    case S.Normal:
                        if (b == 0x1B) _s = S.Esc;               // start of an escape sequence
                        else if (b > 0x20 && b != 0x7F) return true;  // printable, non-space, non-DEL
                        break;                                    // (<=0x20 control/space and DEL are ignored)
                    case S.Esc:
                        if (b == (byte)'[') _s = S.Csi;           // CSI: ESC [
                        else if (b == (byte)']') _s = S.Osc;      // OSC: ESC ]
                        else _s = S.Normal;                       // 2-byte / unsupported escape — consume one byte
                        break;
                    case S.Csi:
                        if (b >= 0x40 && b <= 0x7E) _s = S.Normal; // final byte ends the CSI sequence
                        break;
                    case S.Osc:
                        if (b == 0x07) _s = S.Normal;             // BEL terminates OSC
                        else if (b == 0x1B) _s = S.OscEsc;        // maybe ST (ESC \)
                        break;
                    case S.OscEsc:
                        _s = (b == (byte)'\\') ? S.Normal : S.Osc;
                        break;
                }
            }
            return false;
        }
    }
}
