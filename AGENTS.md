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
