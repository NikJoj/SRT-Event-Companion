# Releasing SRT Event Companion to rigs

## One-time setup (your build PC)

1. .NET 10 SDK.
2. Inno Setup 6.3 or newer: `winget install JRSoftware.InnoSetup`.

## Build an installer

```powershell
.\installer\build-installer.ps1 -Version 1.0.0
```

The script runs the tests, publishes a self-contained build (rigs don't need .NET) and creates
`artifacts\installer\SRT-Event-Companion-Setup-1.0.0.exe`. It prints the file's **SHA-256**.

## First install on a rig

Copy the setup file to the rig and run it. No admin rights are needed. It installs to
`%LOCALAPPDATA%\Programs\SRT Event Companion` with a Start menu shortcut, an optional desktop shortcut and
**Start with Windows** (ticked by default). Then open the app and pair the rig as usual.

Only one copy runs at a time: opening it again brings the running window to the front.

## Roll out an update to every rig

1. Build with a higher version: `.\installer\build-installer.ps1 -Version 1.1.0`.
2. Upload the setup file to a public https address (for example a GitHub release on a public repository).
3. Admin → Kerala Race Wars → **Companion app release** → **Publish new version**: version, download link, SHA-256.

Each rig checks at start-up, after every End Session and every 30 minutes. It installs on its own only when
nobody is using it (on the rig-check screen, or between drivers). Otherwise staff see **Update to vX** in the header.
It never updates during a session. The download is refused unless its SHA-256 matches what you published.
The Rigs table in admin shows each rig's version and **Update pending** until it has updated.

**Withdraw** stops rigs that haven't updated from installing it. To roll back, publish the old build with a
higher version number.

## Code signing (removes the Windows warning)

Without a certificate, Windows SmartScreen shows "Windows protected your PC" on the first manual install
(staff choose **More info → Run anyway**). Automatic updates are not affected.

With a code-signing certificate in your Windows certificate store:

```powershell
.\installer\build-installer.ps1 -Version 1.1.0 -CertThumbprint <certificate thumbprint>
```

This signs the app and the installer (needs `signtool.exe` from the Windows SDK).
