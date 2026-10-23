# Repository agent guidance

## macOS build and launch

- Use `macos/build-app.sh` after changing macOS code. It builds and installs the
  current source at `/Applications/Hollow Knight Vision.app`, refreshes Launch
  Services, and updates an existing Dock pin.
- Launch only `/Applications/Hollow Knight Vision.app`. Never launch or pin the
  disposable `macos/build/Hollow Knight Vision.app` package.
- Use `macos/build-app.sh --package-only` only for CI or artifact packaging when
  installation is intentionally unwanted.
- `macos/run-live.sh` is the canonical build, install, and launch workflow.

## Windows build and launch

- Use `windows/build-windows.ps1` after changing Windows code. A successful
  build publishes, installs, and hash-verifies the current desktop app at
  `%LOCALAPPDATA%\Programs\Hollow Knight Vision`, then refreshes the Start Menu
  shortcut.
- Close the installed Hollow Knight Vision before building. The build must fail
  rather than leave the Start Menu installation on stale binaries.
- Do not treat `windows/artifacts/app-win-x64` as the installed app or hand off
  a Windows build until `windows/install-app-windows.ps1` has verified the
  installed copy.
- Launch and test the installed app through the Start Menu target, not directly
  from the disposable artifact directory.
