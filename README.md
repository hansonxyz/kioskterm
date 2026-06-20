# KioskTerm

A fullscreen, borderless, lockdown terminal overlay for **unattended Windows
provisioning**. It runs a setup command, shows its live output in an `xterm.js`
terminal (the Windows-Terminal look), and stops curious users from interrupting
the process — without requiring kiosk licensing, Group Policy, or a custom shell.

It exists to solve one annoyance: during an unattended setup script, people open
the Start menu, Alt+F4 the window, or let the machine sleep mid-install.
KioskTerm covers the screen, swallows the escape keys, and keeps the box awake
until the script finishes — then cleans up after itself.

> It is a deterrent for ordinary users, not a security boundary. `Ctrl+Alt+Del`
> is deliberately left working (Windows reserves it), so a determined user can
> still reach Task Manager. That's by design.

## Download

**[Download `kioskterm.exe` (v1.0)](https://github.com/hansonxyz/kioskterm/releases/download/v1.0/kioskterm.exe)** —
a single self-contained executable for Windows 10/11 (x64); no .NET install
required on the target. All builds are on the
[releases page](https://github.com/hansonxyz/kioskterm/releases).

## What it does

- **Borderless fullscreen** overlay on the primary monitor.
- **Rear-most z-order** — stays behind any window spawned *after* it, so an
  unattended installer's own UI is visible on top while it runs.
- **Hides the taskbar** (primary + secondary monitors) and restores it on exit.
- **Blocks Start-menu keys** (Win, Ctrl+Esc) and window-close (Alt+F4) via a
  low-level keyboard hook.
- **Keeps the machine awake** and the display on for the duration (auto-cleared
  even on a hard kill).
- **Header band** — an optional caption (multi-line, centered) and an optional
  logo pinned top-right, sized to the header height.
- **Auto-exits** when the wrapped command exits, restoring the environment.
- **Display-only by default**, or a focusable **input mode** for interactive
  scripts (see [Modes](#modes)).
- Ships as a **single self-contained `.exe`** — no .NET or other dependency on
  the target machine (only the WebView2 runtime, which is present on current
  Windows 10/11).

## Usage

```
kioskterm.exe [options] -- <command> [args...]
```

Everything after `--` is the command run inside the pseudo-console (ConPTY).

| Option | Description |
|---|---|
| `--header`, `-h <text>` | Caption in the header band. Literal `\n` becomes a line break; each line is centered. Rendered as 2 blank lines + message + 2 blank lines. |
| `--logo`, `-l <path>` | Image shown top-right (PNG/JPG/GIF/WebP/SVG), aspect-preserved and capped to the header-band height. |
| `--minimize-others`, `-m` | Minimize all other windows on launch (useful for testing/demo). |
| `--allow-sleep` | Permit normal sleep/display timeout. Default is to keep the machine awake. |
| `--allow-input`, `--input` | Make the terminal focusable and accept typing (see [Modes](#modes)). Blocks all `Ctrl` combos and `Alt+Tab`. |
| `--test` | Safe testing harness — titled resizable window, taskbar visible, **no key blocking**, so you can always recover the session. |
| `--` | Separator; everything after it is the command and its arguments. |

### Examples

Locked provisioning overlay with a multi-line caption and a logo:

```powershell
kioskterm.exe --header "Configuring Windows for first use...\n\nPlease do not turn off your computer" `
              --logo C:\branding\acme.png `
              -- powershell -NoProfile -ExecutionPolicy Bypass -File C:\setup.ps1
```

Interactive script that prompts for input:

```powershell
kioskterm.exe --allow-input --header "Account setup" -- powershell -NoProfile -File C:\enroll.ps1
```

Develop/test safely (window has a close button, taskbar stays, keys aren't blocked):

```powershell
kioskterm.exe --allow-input --test -- powershell -NoProfile -File C:\enroll.ps1
```

> **PowerShell argument quoting:** call the exe directly (`& kioskterm.exe ...`)
> or pass `-ArgumentList` as a single pre-quoted string. `Start-Process
> -ArgumentList @(...)` silently drops the quotes around multi-word values like
> `--header`, which mis-parses the command line.

## Modes

`--allow-input` switches the entire window strategy. The two modes are mutually
exclusive in behaviour:

| | Default (locked) | `--allow-input` |
|---|---|---|
| Window | Rear-most, never activates | Focusable; grabs foreground on start |
| Focus | Never takes focus | Holds focus when alone; **reclaims it** when a window opened after it closes |
| Keyboard | Display-only; nothing typed reaches the command | Keystrokes forwarded to the command's stdin (real typing) |
| Blocked keys | Win, Ctrl+Esc, Alt+F4 | Same **+ every Ctrl combo** (no Ctrl+C kill) **+ Alt+Tab** |
| Minimize | N/A (never activates) | Refused — can't be minimized out of the way |

Other windows (e.g. an installer) may still come to the front in input mode;
KioskTerm yields, then pulls itself back to the front and refocuses the terminal
when that window closes.

## Safety & recovery

- **`--test`** removes the lockdowns (titled window with a close button, visible
  taskbar, no key hook) — use it while iterating so a bug can't trap you.
- **Watchdog:** if the terminal fails to start within 20 s, KioskTerm exits
  (code `2`) rather than sitting on a frozen fullscreen window.
- **Launch failure:** if the command can't be started, the error is shown
  briefly, written to `%TEMP%\kioskterm-error.log`, and the app exits (code `3`).
- **`Ctrl+Alt+Del`** always works — the intended last-resort escape hatch.

The exit code mirrors the wrapped command's exit code, except for the special
cases above (and `1` for invalid usage / no command).

## Requirements

- Windows 10/11 (x64).
- WebView2 runtime — present on current Windows 10/11; only the run target needs it.

## Limitations

- Covers the **primary monitor** only (other monitors' taskbars are hidden, but
  their screens aren't covered).
- Foreground reclaim works around Windows' focus-stealing protection with an
  `AttachThreadInput` trick; behaviour can vary in unusual shell configurations.

## Building

See [BUILDING.md](BUILDING.md). In short: install the .NET 8 SDK and run
`dotnet publish -c Release -o .\dist`.

## License

MIT &copy; 2026 [HansonXyz](https://github.com/hansonxyz) — see [LICENSE.md](LICENSE.md).
