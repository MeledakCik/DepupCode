# Microsoft Store submission checklist

This document describes the packaging path that now exists in the repository. It
does not guarantee Store certification: Microsoft reviews the submitted package,
listing, privacy policy, and runtime behavior against current policy.

## What the repository can produce

| Artifact | Script | Purpose |
| --- | --- | --- |
| `DepupCode-v<version>-win-x64.zip` | `build.ps1` | Portable self-contained .NET publish |
| `DepupCode-Setup-v<version>-win-x64.exe` | `build-installer.ps1` | One-file per-user Windows setup (Inno Setup 6) |
| `DepupCode-v<version>-win-x64.msix` | `build-msix.ps1` | Full-trust MSIX package for Partner Center |

The MSIX package uses `Windows.FullTrustApplication` with the restricted
`runFullTrust` capability. It declares `internetClient` and the `depup://`
protocol. The WPF executable runs from the package install location, so all
writable data must stay in per-user locations. The app already stores settings
in `%APPDATA%\DepupCode`, runtimes in `%LOCALAPPDATA%\DepupCode`, and WebView2
cache in `%LOCALAPPDATA%\DepupCode\WebView2`.

## 1. Reserve the product in Partner Center

1. Create or select the Depup Code product in Microsoft Partner Center and
   reserve the name.
2. Copy the exact **Package/Identity Name** and **Publisher** values from
   Partner Center. Do not guess them and do not reuse another product's values.
3. Keep the package version in `DepupCode/DepupCode.csproj` newer than the last
   submitted version. MSIX requires four numeric parts; the script pads
   `2.5.0` to `2.5.0.0`.

## 2. Build and inspect the package

```powershell
.\build.ps1
.\build-msix.ps1 `
  -IdentityName "<Identity Name>" `
  -Publisher "CN=<Publisher ID>" `
  -PublisherDisplayName "Depup"
```

The script stages the publish output, generates the required Store logos from
`DepupCode/Assets/logo.png`, validates the substituted `AppxManifest.xml`, and
runs `makeappx.exe` from the installed Windows SDK. It writes a `.sha256`
checksum next to the package.

Before upload, confirm:

- `Identity` name, publisher, version, and architecture match Partner Center.
- The package contains `DepupCode\DepupCode.exe` and `PRIVACY.md`.
- The `depup://` protocol registration works from a packaged install.
- No user settings, tokens, logs, or project files are inside the package.
- Logos and screenshots meet the current Store image requirements.

## 3. Test as a packaged app

Sign with a test certificate whose subject matches the package Publisher, then
install the certificate in `Local Computer\Trusted People` on the test machine:

```powershell
.\build-msix.ps1 `
  -IdentityName "<Identity Name>" `
  -Publisher "CN=<Publisher ID>" `
  -CertificatePath .\cert\store-test.pfx `
  -CertificatePassword "<password>"
```

Run the Windows App Certification Kit against the installed package:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath .\artifacts\release\DepupCode-v2.5.0.0-win-x64.msix -reportoutputpath .\artifacts\wack-report.xml
```

Review every failure and warning. In particular, verify:

- First launch shows the privacy policy before any optional network feature is used.
- Declining the privacy policy exits without creating a workspace or account.
- The first-run workspace picker only reads folders the user selects.
- Settings, runtimes, logs, and WebView2 data remain writable after installation.
- Uninstall removes installed files and protocol/context-menu registration, while
  leaving user projects and settings intact.
- `depup://` sign-in returns to the packaged app and not to a portable copy.

## 4. Policy audit before submission

The following behaviors need explicit review against current Microsoft Store
policies. They are real product behavior, not packaging details.

- **Downloaded native runtimes.** Local PHP/Apache support downloads binaries
  from the network on user request and executes them. Store policy restricts
  downloading and executing code that is not part of the package. Either ship
  the reviewed runtimes in the package, keep the feature unavailable in the
  Store build, or obtain guidance from Microsoft before submission.
- **Extensions.** The app installs VS Code-compatible extensions from Open VSX
  and can execute their code. Confirm that the Store listing discloses this and
  that extension execution is gated by explicit user action.
- **Full trust and child processes.** The app launches terminals, debug adapters,
  language servers, Git, formatters, and external tools. Declare `runFullTrust`
  and explain the need in the submission notes.
- **AI and cloud providers.** Source code and context can be sent to a provider
  only when the user starts an AI, Git, deploy, or cloud action. The public
  privacy policy must name the categories of providers and data involved.
- **External WebView2 dependency.** The package depends on the Evergreen
  WebView2 Runtime. Confirm the current Store requirement for desktop apps and
  test on a clean Windows installation.
- **Auto-update.** Do not add an in-app updater that bypasses Store updates for
  the packaged build.

## 5. Listing and account requirements

Prepare:

- A publicly reachable privacy policy URL. The GitHub `PRIVACY.md` link is a
  source document; a stable product page is better for review.
- Support URL and contact details.
- Age/content rating questionnaire and Store listing text.
- Screenshots for desktop form factors, app icon, and description.
- Notes for testers explaining the privacy gate, optional sign-in, runtime
  downloads, and any feature that requires an external account or network.
- A complete `runFullTrust` justification.

## Known gaps

- No Partner Center identity or publisher certificate is stored in this
  repository. Those values are account-specific and must not be committed.
- The privacy policy URL and Store listing copy are not published by these
  scripts.
- Store certification remains subject to Microsoft's review. A successful MSIX
  build and WACK run do not guarantee approval.
