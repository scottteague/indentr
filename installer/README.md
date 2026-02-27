# Indentr Windows Installer

This directory contains the Inno Setup script and build tooling that produces
`IndentrSetup-<version>.exe` — a self-contained Windows installer that bundles
the .NET runtime and optionally downloads and configures PostgreSQL automatically.

---

## Creating a release (recommended)

You do **not** need a Windows machine. Push a version tag and GitHub Actions
builds the installer and attaches it to a GitHub Release automatically.

```bash
git tag v1.0
git push --tags
```

The workflow (`.github/workflows/release.yml`) will:

1. Spin up a `windows-latest` runner
2. `dotnet publish` the UI project as a self-contained win-x64 binary
3. Install Inno Setup via Chocolatey
4. Compile `indentr.iss` with the version derived from the tag (`v1.0` → `1.0`)
5. Create a GitHub Release at `Releases → v1.0` with `IndentrSetup-1.0.exe` attached

The `GITHUB_TOKEN` needed to create the release is provided automatically — no
secrets need to be configured.

### Testing the build without creating a release

Go to **Actions → Build Windows Installer → Run workflow** on GitHub. The
installer is built and uploaded as a workflow artifact you can download from the
run page. No release is created.

---

## Building locally (Windows only)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Inno Setup 6.1+](https://jrsoftware.org/isdl.php)

### Usage

```bat
cd installer

:: Build with the default version defined in indentr.iss
build.bat

:: Build with an explicit version
build.bat 1.0
```

Output is written to `installer\output\IndentrSetup-<version>.exe`.

---

## What the installer does

1. **Detects PostgreSQL 14–17.** Scans `C:\Program Files\PostgreSQL\<version>\`.
2. **If not found:** downloads the PostgreSQL 17 installer from EDB (~330 MB)
   and runs it silently. You are asked to choose a superuser password, or leave
   it blank for trust authentication (no password — suitable for a
   single-user machine).
3. **If already installed:** asks for the existing `postgres` superuser password
   to connect and create the database (leave blank for trust auth).
4. **Creates the `indentr` database** using an idempotent `psql` script — safe
   to run on reinstalls or upgrades.
5. **Writes `%USERPROFILE%\.config\indentr\config.json`** with a ready-to-use
   local profile so the app opens directly without a profile-picker dialog.
   Skipped if the file already exists (upgrades preserve existing config).
6. **Installs the app** to `%ProgramFiles%\Indentr` and creates Start Menu
   shortcuts (optional desktop shortcut available during install).

---

## Versioning

The version is defined in `indentr.iss`:

```iss
#ifndef MyAppVersion
  #define MyAppVersion "0.001"
#endif
```

The `#ifndef` guard means the version can be overridden at compile time without
editing the file:

```bat
ISCC.exe /DMyAppVersion=1.0 indentr.iss
```

The GitHub Actions workflow passes the git tag as the version this way. Local
builds without an argument fall back to the `0.001` default.

---

## Known limitations

- **Code signing:** the installer is not signed. Windows Defender / SmartScreen
  will show an "Unknown publisher" warning on first run. Users can click
  "More info → Run anyway". Signing requires purchasing a code-signing
  certificate.
- **Trust auth `pg_hba.conf` path** is hardcoded to the default EDB install
  location. If PostgreSQL was installed elsewhere, the trust-auth rewrite step
  will silently fail and the user will need to configure authentication manually.
- **UAC elevation:** the installer requires admin rights (needed to install
  PostgreSQL). The config.json is written to the profile of the user who
  authenticated the UAC prompt, which is usually the same person but may differ
  on managed machines.
