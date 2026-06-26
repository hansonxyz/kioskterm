using System.Text;

namespace KioskTerm;

/// <summary>
/// Captures the wrapped command's full terminal output to disk.
///
/// The processed log is human-readable plain text: terminal escape sequences are
/// stripped and in-place redraws (carriage-return progress bars, line erases) are
/// collapsed to their final state, so the file is greppable. An optional raw log
/// records the unmodified byte stream.
///
/// Durability is the priority: completed lines are written and flushed to disk
/// immediately, and the in-progress line is re-snapshotted and flushed on every
/// chunk, so a mid-run reboot/crash still leaves everything emitted up to the cut.
/// </summary>
internal sealed class SessionLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly bool _timestamps;
    private FileStream? _text;   // processed, human-readable
    private FileStream? _raw;    // optional unmodified byte stream
    private bool _closed;

    // Streaming UTF-8 decode (handles multibyte sequences split across chunks).
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    // Current (not-yet-finalized) line and cursor column.
    private readonly StringBuilder _line = new();
    private int _col;
    private long _committedLen;      // bytes of finalized lines already on disk
    private DateTime _lineStamp;     // wall-clock when the current line began
    private bool _lineStarted;
    private bool _sawContent;        // suppress ConPTY's leading blank screen-paint lines

    private enum VtState { Normal, Esc, Csi, Osc, OscEsc }
    private VtState _state = VtState.Normal;
    private readonly StringBuilder _csi = new();

    /// <summary>Opens the requested logs, or returns null if none requested / on failure.</summary>
    public static SessionLogger? TryCreate(string? textPath, string? rawPath, bool timestamps)
    {
        if (string.IsNullOrEmpty(textPath) && string.IsNullOrEmpty(rawPath)) return null;
        try
        {
            var log = new SessionLogger(timestamps);
            if (!string.IsNullOrEmpty(textPath)) log._text = Open(textPath);
            if (!string.IsNullOrEmpty(rawPath)) log._raw = Open(rawPath);
            return log;
        }
        catch (Exception ex)
        {
            // Logging must never abort the run; record the failure and carry on.
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "kioskterm-error.log"),
                    "log open failed: " + ex + "\r\n");
            }
            catch { /* best effort */ }
            return null;
        }
    }

    private SessionLogger(bool timestamps) => _timestamps = timestamps;

    private static FileStream Open(string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Overwrite; FileShare.Read lets the log be tailed while running. UTF-8, no BOM.
        return new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Append(byte[] data)
    {
        lock (_sync)
        {
            if (_closed) return;

            if (_raw != null)
            {
                try { _raw.Write(data, 0, data.Length); _raw.Flush(); }
                catch { /* best effort */ }
            }

            if (_text != null)
            {
                try
                {
                    var chars = new char[data.Length + 4];
                    int n = _decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
                    for (int i = 0; i < n; i++) Process(chars[i]);
                    SnapshotPartial();   // persist the in-progress line every chunk
                }
                catch { /* best effort */ }
            }
        }
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case VtState.Normal:
                switch (c)
                {
                    case '\x1b': _state = VtState.Esc; break;
                    case '\n': FinalizeLine(); break;
                    case '\r': _col = 0; break;                                   // return to col 0 -> overwrite
                    case '\b': if (_col > 0) _col--; break;
                    case '\t': { int t = (_col / 8 + 1) * 8; while (_col < t) Put(' '); } break;
                    default:
                        if (c >= ' ' && c != '\x7f') Put(c);                      // printable; other C0 ignored
                        break;
                }
                break;

            case VtState.Esc:
                if (c == '[') { _csi.Clear(); _state = VtState.Csi; }
                else if (c == ']') _state = VtState.Osc;
                else _state = VtState.Normal;                                     // 2-byte / charset escape -> drop
                break;

            case VtState.Csi:
                if (c >= '@' && c <= '~') { ApplyCsi(c); _state = VtState.Normal; }
                else _csi.Append(c);
                break;

            case VtState.Osc:
                if (c == '\x07') _state = VtState.Normal;                         // BEL ends OSC (e.g. title set)
                else if (c == '\x1b') _state = VtState.OscEsc;                    // maybe ST (ESC \)
                break;

            case VtState.OscEsc:
                _state = (c == '\\') ? VtState.Normal : VtState.Osc;
                break;
        }
    }

    private void ApplyCsi(char final)
    {
        if (final != 'K') return;   // only EL (erase-in-line) affects plain text; SGR/cursor/ED ignored
        string p = _csi.ToString();
        if (p.Length == 0 || p == "0") { if (_col < _line.Length) _line.Length = _col; }     // cursor -> end
        else if (p == "2") { _line.Clear(); _col = 0; }                                       // whole line
        else if (p == "1") { for (int i = 0; i < _col && i < _line.Length; i++) _line[i] = ' '; } // start -> cursor
    }

    private void Put(char c)
    {
        if (!_lineStarted) { _lineStarted = true; _lineStamp = DateTime.Now; }
        if (_col < _line.Length) _line[_col] = c;
        else { while (_line.Length < _col) _line.Append(' '); _line.Append(c); }
        _col++;
    }

    private string Stamp() => _timestamps ? $"[{_lineStamp:HH:mm:ss}] " : "";

    private void FinalizeLine()
    {
        string body = _line.ToString().TrimEnd();
        bool started = _lineStarted;
        _line.Clear(); _col = 0; _lineStarted = false;

        // Drop the blank lines ConPTY emits to paint the initial empty screen
        // (everything before the first real output). Blank lines after that are kept.
        if (body.Length == 0 && !_sawContent) return;
        if (body.Length > 0) _sawContent = true;

        if (!started) _lineStamp = DateTime.Now;   // blank line still gets a timestamp
        byte[] bytes = Encoding.UTF8.GetBytes(Stamp() + body + "\r\n");
        _text!.Position = _committedLen;
        _text.Write(bytes, 0, bytes.Length);
        _committedLen = _text.Position;
        _text.SetLength(_committedLen);            // drop any partial snapshot beyond
        try { _text.Flush(flushToDisk: true); } catch { _text.Flush(); }
    }

    // Rewrite the in-progress line just past the committed content and flush, so the
    // file always reflects the latest output even without a trailing newline.
    private void SnapshotPartial()
    {
        if (_text == null) return;
        _text.Position = _committedLen;
        if (_lineStarted && _line.Length > 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Stamp() + _line.ToString().TrimEnd());
            _text.Write(bytes, 0, bytes.Length);
        }
        _text.SetLength(_text.Position);
        _text.Flush();
    }

    /// <summary>Writes a KioskTerm status line (e.g. a headless-fallback notice) into the log.</summary>
    public void WriteBanner(string text)
    {
        lock (_sync)
        {
            if (_closed || _text == null) return;
            _sawContent = true;             // a banner is real content, not leading paint
            _lineStamp = DateTime.Now;
            byte[] bytes = Encoding.UTF8.GetBytes(Stamp() + text + "\r\n");
            _text.Position = _committedLen;
            _text.Write(bytes, 0, bytes.Length);
            _committedLen = _text.Position;
            _text.SetLength(_committedLen);
            try { _text.Flush(flushToDisk: true); } catch { _text.Flush(); }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;

            if (_text != null)
            {
                try
                {
                    if (_lineStarted && _line.Length > 0) FinalizeLine();   // commit a trailing partial line
                    else _text.SetLength(_committedLen);
                    _text.Flush(flushToDisk: true);
                }
                catch { /* best effort */ }
                try { _text.Dispose(); } catch { }
                _text = null;
            }

            if (_raw != null)
            {
                try { _raw.Flush(); _raw.Dispose(); } catch { }
                _raw = null;
            }
        }
    }

    public void Dispose() => Close();
}
