using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KioskTerm;

/// <summary>
/// Runs a command inside a Windows pseudo-console (ConPTY) and streams its raw
/// VT output back to the caller. Input is intentionally never written — this is a
/// display-only terminal.
/// </summary>
internal sealed class ConPty : IDisposable
{
    public event Action<byte[]>? Output;   // raw bytes from the child (VT sequences + UTF-8 text)
    public event Action<int>? Exited;      // child exit code

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _inputWrite = IntPtr.Zero;
    private SafeFileHandle? _outputRead;
    private IntPtr _attrList = IntPtr.Zero;
    private PROCESS_INFORMATION _pi;
    private Thread? _readThread;
    private Thread? _waitThread;
    private volatile bool _disposed;

    public void Start(string commandLine, short cols, short rows)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 25;

        // Two anonymous pipes: one feeds the console's input, one drains its output.
        if (!CreatePipe(out IntPtr inputRead, out IntPtr inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) failed");
        if (!CreatePipe(out IntPtr outputRead, out IntPtr outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) failed");

        _inputWrite = inputWrite;
        _outputRead = new SafeFileHandle(outputRead, ownsHandle: true);

        int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out _hPC);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // The pseudo-console duplicated the ends it needs; close our copies so the
        // read side reports EOF once the console is closed.
        CloseHandle(inputRead);
        CloseHandle(outputWrite);

        var startupInfo = ConfigureProcessThread();
        try
        {
            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

            // CreateProcessW may write into the command-line buffer, so hand it a
            // writable copy rather than a marshalled immutable string.
            var cmdBuffer = new StringBuilder(commandLine, commandLine.Length + 1);

            bool ok = CreateProcess(
                null,
                cmdBuffer,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out _pi);

            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        }
        finally
        {
            startupInfo.StartupInfo.cb = 0;
        }

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" };
        _readThread.Start();

        _waitThread = new Thread(WaitLoop) { IsBackground = true, Name = "conpty-wait" };
        _waitThread.Start();
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC != IntPtr.Zero && cols > 0 && rows > 0)
            ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    /// <summary>Writes user input (xterm-encoded key sequences) to the console's stdin.</summary>
    public void WriteInput(string data)
    {
        if (_inputWrite == IntPtr.Zero || string.IsNullOrEmpty(data)) return;
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        WriteFile(_inputWrite, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
    }

    private STARTUPINFOEX ConfigureProcessThread()
    {
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

        const IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        if (!UpdateProcThreadAttribute(
                _attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

        si.lpAttributeList = _attrList;
        return si;
    }

    private void ReadLoop()
    {
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read);
            var buffer = new byte[8192];
            int n;
            while (!_disposed && (n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[n];
                Array.Copy(buffer, chunk, n);
                Output?.Invoke(chunk);
            }
        }
        catch
        {
            // pipe closed — treated as end of output
        }
    }

    private void WaitLoop()
    {
        if (_pi.hProcess == IntPtr.Zero) return;
        WaitForSingleObject(_pi.hProcess, INFINITE);
        GetExitCodeProcess(_pi.hProcess, out uint code);

        // Closing the pseudo-console flushes remaining output then EOFs the read loop.
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

        Exited?.Invoke(unchecked((int)code));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_inputWrite != IntPtr.Zero) { CloseHandle(_inputWrite); _inputWrite = IntPtr.Zero; }
        _outputRead?.Dispose();

        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (_pi.hProcess != IntPtr.Zero) { CloseHandle(_pi.hProcess); _pi.hProcess = IntPtr.Zero; }
        if (_pi.hThread != IntPtr.Zero) { CloseHandle(_pi.hThread); _pi.hThread = IntPtr.Zero; }
    }

    // ---- native interop -------------------------------------------------------

    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
}
