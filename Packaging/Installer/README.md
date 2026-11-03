# Installer assets

- `PRIVACY-NOTICE.txt` is shown by Inno Setup before installation. It is a
  short summary, not the full policy. The full policy is bundled in the app and
  accepted on first launch.
- `Indonesian.isl` is the Inno Setup Indonesian language file from the upstream
  Inno Setup source tree (`Files/Languages/Unofficial/Indonesian.isl`). Inno
  Setup 6.7 does not install this language file by default, so it is vendored
  here to keep `build-installer.ps1` reproducible.

The setup script is `build-installer.iss` in the repository root. It requires
Inno Setup 6.5 or newer (6.7.3 is tested). Build with:

```powershell
.\build.ps1
.\build-installer.ps1
```
