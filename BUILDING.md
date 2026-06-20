# Building KioskTerm

KioskTerm is a Windows-only .NET 8 / WinForms app that hosts an `xterm.js`
terminal in WebView2 and runs a command inside a Windows pseudo-console
(ConPTY). It builds to a single self-contained `.exe` with no runtime
dependency on the target machine.

## Prerequisites

| Requirement | Notes |
|---|---|
| **.NET 8 SDK** | The only build dependency. Compiles C# and provides full Win32 P/Invoke — no separate Visual C++ build tools needed. Install: `winget install Microsoft.DotNet.SDK.8` |
| **Windows 10/11 (x64)** | Target and build platform. |
| **WebView2 Runtime** | Required at **run** time, not build time. Ships with Windows 11 and current Windows 10; the "Evergreen" runtime is present on essentially all supported machines. Only relevant for the boxes you run the exe on. |

No Node.js / npm toolchain is required — the `xterm.js` assets are vendored
(see [Frontend assets](#frontend-assets)).

## Build

From the project root (`kioskterm.csproj` lives here):

```powershell
# Debug build (fast; framework-dependent, for local iteration)
dotnet build -c Debug

# Release: single self-contained exe -> .\dist\kioskterm.exe (~69 MB, bundles the .NET runtime)
dotnet publish -c Release -o .\dist
```

The publish settings (single-file, self-contained, win-x64, compressed) are
baked into `kioskterm.csproj`, so `dotnet publish -c Release -o .\dist` is all
that's needed. The resulting `dist\kioskterm.exe` runs on a clean Windows box
with no .NET installed.

> **Smaller exe (optional):** a framework-dependent build is ~200 KB instead of
> ~69 MB, but requires the **.NET 8 Desktop Runtime** on the target. Freshly
> provisioned machines usually don't have it, which is why self-contained is the
> default. To produce one: `dotnet publish -c Release -o .\dist-fd -p:SelfContained=false -p:PublishSingleFile=false`.

## Run / smoke test

```powershell
.\dist\kioskterm.exe --header "Configuring Windows for first use...\n\nPlease do not turn off your computer" `
    --logo C:\path\to\logo.png `
    -- powershell -NoProfile -ExecutionPolicy Bypass -File C:\setup.ps1
```

> **Argument quoting:** when launching from PowerShell, prefer calling the exe
> directly (`& kioskterm.exe ...`) or pass `-ArgumentList` as a single,
> pre-quoted string. `Start-Process -ArgumentList @(...)` silently drops quotes
> around multi-word values (e.g. the `--header` text), which mis-parses the
> command line.

## Project layout

```
kioskterm.csproj      Project + single-file publish settings; embeds web\ as resources
app.manifest         asInvoker + DPI awareness (PerMonitorV2 via project property)
Program.cs           Entry point: CLI parsing (--header/--logo/--minimize-others/--allow-sleep/--allow-input/--test, -- command)
MainForm.cs          WinForms host (locked vs input mode); WebView2 init, IPC, input forwarding, watchdog, temp asset extraction
ConPty.cs            ConPTY pseudo-console: spawn the command, stream VT output, write stdin, resize, exit handling
Native.cs            Win32: taskbar hide/restore, HWND_BOTTOM rear-most, keyboard hook, keep-awake, minimize-all, foreground grab/reclaim, Start-menu dismiss
web\index.html       Terminal page: header band (caption + logo) and #term; CSP; scrollbar hidden
web\boot.js          Frontend bootstrap: xterm + fit addon, header/logo layout, host<->page messaging
web\xterm.min.js     Vendored xterm.js (see below)
web\xterm.min.css    Vendored xterm.js stylesheet
web\addon-fit.min.js Vendored xterm fit addon
```

The `web\` folder is embedded into the exe as resources (`<EmbeddedResource>` in
the csproj) and extracted to a per-process temp directory at runtime, where it's
served to WebView2 via a virtual host (`https://kioskterm.local/`).

## Frontend assets

The `xterm.js` files in `web\` are vendored (no package manager). They were
fetched from jsDelivr:

```powershell
$web = '.\web'
Invoke-WebRequest 'https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js'        -OutFile "$web\xterm.min.js"
Invoke-WebRequest 'https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css'        -OutFile "$web\xterm.min.css"
Invoke-WebRequest 'https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js' -OutFile "$web\addon-fit.min.js"
```

To upgrade, re-fetch newer versions and rebuild. The page's Content-Security-Policy
in `index.html` only allows scripts/styles from the same origin (`'self'`), so
keep any new assets as separate files referenced from `index.html` rather than
inlining `<script>` blocks.

## Clean

```powershell
dotnet clean
Remove-Item -Recurse -Force .\bin, .\obj, .\dist -ErrorAction SilentlyContinue
```
