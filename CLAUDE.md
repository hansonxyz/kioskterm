# CLAUDE.md — working notes for this repo

KioskTerm is a Windows-only .NET 8 / WinForms app (xterm.js in WebView2 + ConPTY).
Local directory is `bareterm`; the project/repo is `kioskterm`. See `README.md` for
behaviour and `BUILDING.md` for the full build reference.

## Environment

- `dotnet` is not on PATH by default in fresh shells. Prefix commands with:
  `$env:Path = 'C:\Program Files\dotnet;' + $env:Path`
- GitHub CLI (`gh`) is authenticated as `hansonxyz` over SSH (`repo` scope).
- Remote: `git@github.com:hansonxyz/kioskterm.git` (`origin`).

## Build

```powershell
$env:Path = 'C:\Program Files\dotnet;' + $env:Path
# fast compile-check
dotnet build kioskterm.csproj -c Debug -p:PublishSingleFile=false -p:SelfContained=false
# release single-file self-contained exe -> dist\kioskterm.exe (~69 MB)
dotnet publish kioskterm.csproj -c Release -o .\dist
```

`bin/`, `obj/`, `dist/` are git-ignored. The binary ships via GitHub Releases, not git.

## Performing a GitHub release

The README download link points at **`releases/latest`**, so whatever release is
newest is what users download. Cut releases with `gh`:

```powershell
# new versioned release with the freshly built binary
gh release create v1.1 ".\dist\kioskterm.exe" --title "KioskTerm v1.1" --notes "..."

# inspect
gh release view v1.1 --json url,tagName,assets --jq '.url, .tagName, (.assets[].name)'
```

Re-releasing / replacing the binary on an existing tag:

```powershell
# replace just the asset on an existing release (tag stays where it is)
gh release upload v1.0 ".\dist\kioskterm.exe" --clobber

# OR fully re-cut a tag at the current commit (deletes release AND tag, then recreates):
gh release delete v1.0 --yes --cleanup-tag
git push origin main            # make sure the commit is pushed first
gh release create v1.0 ".\dist\kioskterm.exe" --title "KioskTerm v1.0" --notes "..."
```

Notes:
- Always `dotnet publish` a fresh `dist\kioskterm.exe` and push `main` before
  creating/replacing a release, so the tag, code, and binary line up.
- The single-file exe is ~69 MB (bundled .NET runtime); well under GitHub's asset limit.
- Deploys/releases are user-gated — only push or release when explicitly asked.

## Behavior notes

- WebView2 can be transiently unavailable early in a post-update auto-logon
  (`0x80070490`). Startup retries env/controller creation every 2s for 30s, then
  falls back to running the command **headless** (no overlay) so provisioning is
  never blocked. `--hidden` skips WebView2 entirely. Failures go to stderr,
  `%TEMP%\kioskterm-error.log`, and the `--log` file.
- To force a WebView2 failure for testing: set
  `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` to a nonexistent path before launching.
- Exit codes: wrapped command's code passes through; `64` = bad usage,
  `66` = command could not be launched.

## Manual testing patterns

- The locked overlay hides the taskbar and blocks keys, so testing it live is
  disruptive. Use `--test` (titled window, taskbar shown, no key hook) for
  anything interactive, and always wire a recovery net when scripting a launch
  (re-show `Shell_TrayWnd` via `ShowWindow` after the run).
- `Start-Process -ArgumentList @(...)` drops quotes around multi-word args; pass a
  single pre-quoted string or call the exe with `&`.
- Screenshot for visual checks via `Graphics.CopyFromScreen` (note: it does NOT
  capture the mouse cursor).
