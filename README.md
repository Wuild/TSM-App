# TSM Desktop for Linux, Windows, and macOS

[![License: MIT](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

**TSM Desktop is an unofficial TradeSkillMaster companion with first-class Linux support.** It provides a self-contained x86_64 AppImage for World of Warcraft players looking for a native TSM desktop app on Linux, alongside Windows and Apple Silicon macOS packages.

The app discovers local World of Warcraft clients, keeps TradeSkillMaster AppHelper realm data synchronized, manages addon updates, exports Accounting data, and provides an in-app Auction House browser using local TSM data.

> [!IMPORTANT]
> TSM Desktop is an independent, unofficial personal project. It is not affiliated with, authorized by, sponsored by, endorsed by, or supported by TradeSkillMaster LLC, Blizzard Entertainment, Wowhead, ZAM Network, or their affiliates. See the full [project notice](NOTICE.md).

The application supports:

- Windows x64
- macOS Apple Silicon (Arm64)
- Linux x64 through AppImage

Windows ARM, Linux ARM, and macOS Intel packages are not produced.

## Download

Download the installer for your system from [GitHub Releases](https://github.com/Wuild/TSM-App/releases/latest). Installers are self-contained, so the .NET runtime does not need to be installed separately. ZIP files attached to releases are portable update packages and are not the recommended installation method.

### TradeSkillMaster desktop app for Linux

Linux is a primary supported platform for this project. The x86_64 AppImage works across major desktop distributions without requiring the .NET runtime or a distribution-specific package. It includes the application icon, desktop metadata, automatic update support, system-tray integration, Linux Secret Service credential storage, and automatic detection of supported World of Warcraft installations.

Use the AppImage if you are searching for an unofficial TSM app on Ubuntu, Linux Mint, Fedora, Arch Linux, Manjaro, openSUSE, Debian, or another x86_64 Linux desktop distribution. Distribution-specific packages and ARM builds are not currently provided.

## Install

### Windows

1. Download `TSM-<version>-windows-x86_64-setup.exe`.
2. Run the installer and review the destination and optional desktop shortcut.
3. Launch **TSM Desktop** from the Start menu or the installer completion page.
4. If Windows displays a security prompt, verify that the installer came from this repository before allowing it to run.

The installer performs a per-user installation under `%LOCALAPPDATA%\Programs\TSM Desktop`, does not require administrator access, and registers a normal Windows uninstaller.

### macOS

Download `TSM-<version>-macos-arm64.dmg`. The macOS package requires an Apple Silicon Mac; Intel Macs are not supported.

Then:

1. Open the downloaded DMG.
2. Drag `TradeSkillMaster.app` onto the **Applications** shortcut in the disk image.
3. Eject the **TSM Desktop** disk image.
4. Open the application from Applications.
5. If Gatekeeper blocks the first launch, right-click the app and choose **Open**, or allow it from **System Settings → Privacy & Security** after verifying the download.

Automatic updates need write access to the `.app` bundle.

### Linux

1. Download `TSM-<version>-x86_64.AppImage`.
2. Make it executable:

   ```bash
   chmod +x TSM-<version>-x86_64.AppImage
   ```

3. Move it to a permanent writable location, for example `~/Applications`.
4. Run it:

   ```bash
   ./TSM-<version>-x86_64.AppImage
   ```

The AppImage must remain writable for automatic installation of updates. If the distribution does not provide FUSE support, run it with `--appimage-extract-and-run` or install the AppImage-compatible FUSE package supplied by the distribution.

Only one TSM Desktop process runs per operating-system user. Launching the app again restores and focuses the existing window, including when it is minimized or hidden in the system tray.

## First-time setup

1. Start TSM Desktop.
2. Select the base World of Warcraft directory. Choose the folder containing client directories such as `_retail_`, `_classic_`, or `_anniversary_`, not an individual addon folder.
3. Choose notification, startup, and tray preferences in onboarding.
4. Sign in with a TradeSkillMaster account. Authentication is required before the main application opens.
5. Make sure TradeSkillMaster and TradeSkillMaster AppHelper are installed in the desired WoW clients.

Credentials can be remembered through Windows Credential Manager, macOS Keychain, or Linux Secret Service. Passwords are not written to `settings.json`.

## Use

### Realm data

The start page lists locally detected and account-managed realms with their WoW flavor and installation state. Use the ellipsis menu on a realm to synchronize or remove it. While running, the app checks the configured WoW directory every 30 seconds for newly installed game flavors, accounts, addons, and changed local realm data. Detected changes refresh the UI and trigger an authenticated data sync. The regular authenticated refresh also runs every 30 minutes, and the top bar shows the freshness of the last successful sync.

### Addons

The Addons page shows each detected TradeSkillMaster addon separately for every installed WoW client. **Check for updates** compares installed versions with the selected TSM Release or Beta channel and installs newer packages. **Reinstall** replaces one selected addon with the current channel package. **Remove** deletes only that addon folder after confirmation; SavedVariables and other WoW account data are preserved.

### Auction House

The Auction House workspace reads local AppHelper AuctionDB snapshots. It supports:

- item-name search
- realm and faction filters
- sortable price and market-statistic columns
- flavor-aware Wowhead item details and icons
- favorites pinned above other results
- local price-history charts for favorited items

Favoriting an item begins collecting price snapshots from future realm synchronizations. Price history is local and is not retroactively available before tracking starts.

Releases include a compressed item-ID-to-icon-slug manifest, not item images or tooltip text. Visible rows use that manifest to request icons directly from Blizzard's media CDN. If an item is not yet mapped, its icon slug is learned and cached on demand. Full tooltip presentation is reserved for an opened item drawer.

If the Auction House is empty, verify that AppHelper is installed and synchronize the realm again. Online Wowhead enrichment can be disabled under **Settings → Advanced** without disabling local prices.

### Accounting export

Open **Accounting**, select an account and realm, choose the data types to include, and export. CSV files are written to the desktop when available, otherwise to the application data directory under `Exports`.

### Updates

Update behavior is configured under **Settings → Updates**.

- Automatic checks can be disabled.
- The check interval is configurable.
- **Check now** performs a manual check.
- Automatic installation is a separate opt-in option.
- Release mode ignores prereleases; Beta mode includes them.

Windows and macOS replace the published application through a rollback-capable helper. Linux replaces the running AppImage and restarts it. If the current installation cannot be updated automatically, the app opens the matching GitHub Release instead.

### Help and licenses

Open **Settings → Help** for setup guidance, troubleshooting, documentation, releases, and issue-reporting links. **Settings → Licenses** lists the direct, transitive, build-time, and runtime projects used by the app, including versions, license families, upstream projects, and license links. The packaged `THIRD_PARTY_NOTICES.md` file contains the corresponding notices.

## Reset the application

Open **Settings → Advanced → Reset app…** and confirm the reset. This action:

- restores all preferences and window placement to defaults
- clears cached Wowhead metadata and item icons
- clears local Auction House price history
- removes favorites and managed realm selections
- removes the remembered login from the operating system credential store
- disables launch-at-startup registration
- returns the app to onboarding

Reset does not modify the World of Warcraft installation, installed addons, addon configuration, or SavedVariables.

## Uninstall

For the cleanest uninstall, use **Settings → Advanced → Reset app…** first. This removes the remembered credential and startup registration. Then quit the application from the tray menu before running the platform uninstall steps.

### Windows

1. Open **Windows Settings → Apps → Installed apps**.
2. Find **TSM Desktop**, open its menu, and choose **Uninstall**.
3. Complete the uninstall wizard.
4. Delete `%LOCALAPPDATA%\TSM-CrossPlatform` only if remaining settings and cached data should also be removed.

### macOS

1. Quit TSM Desktop from its tray menu.
2. Move `TradeSkillMaster.app` from Applications to Trash.
3. Empty Trash when convenient.
4. Delete `~/Library/Application Support/TSM-CrossPlatform` only if remaining settings and cached data should also be removed.

### Linux

1. Delete the AppImage.
2. Delete `~/.local/share/TSM-CrossPlatform`.
3. If startup registration was not reset first, delete `~/.config/autostart/tradeskillmaster-crossplatform.desktop`.

If the app was removed before using Reset, remove any credential named for `TSM-CrossPlatform` from Windows Credential Manager, macOS Keychain Access, or the Linux password/keyring application. WoW addons and SavedVariables remain installed and must be removed separately through the addon manager or WoW directory if desired.

## Application data

| Platform | Default location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\TSM-CrossPlatform` |
| macOS | `~/Library/Application Support/TSM-CrossPlatform` |
| Linux | `~/.local/share/TSM-CrossPlatform` |

This directory contains settings, cached item data, local price history, and fallback exports. Login secrets are stored by the operating system credential service instead.

## Account-sync security

The initial identity password exchange uses HTTPS, but compatibility with the legacy private TSM session protocol includes plain HTTP requests. Use the application only on a trusted network. Access and session tokens stay in memory, and returned data-download URLs are upgraded to HTTPS.

## Linux FAQ

### Is this an official TradeSkillMaster Linux client?

No. TSM Desktop is an independent, unofficial compatibility project. The name TradeSkillMaster is used only to explain which addon data and services the app interoperates with.

### How do I run TradeSkillMaster desktop features on Linux?

Download the latest `TSM-<version>-x86_64.AppImage` from GitHub Releases, make it executable, and launch it from a writable location. Point onboarding at the World of Warcraft directory containing `_retail_`, `_classic_`, or another supported client folder.

### Which Linux distributions are supported?

The AppImage targets x86_64 Linux desktops rather than one specific distribution. It is intended for distributions including Ubuntu, Debian, Linux Mint, Fedora, Arch Linux, Manjaro, and openSUSE. Exact compatibility depends on the distribution's AppImage and desktop support.

### Does the Linux app update itself?

Yes. Automatic checks are configurable, manual checks are available in Settings, and automatic installation is optional. The AppImage must remain in a writable location for in-place updates.

### Does it manage the TradeSkillMaster addon too?

Yes. The app detects installed World of Warcraft flavors and TSM addons, checks the selected release channel, and can update, reinstall, or remove supported addon installations.

## Build from source

Install the .NET 10 SDK, then run:

```bash
dotnet run --project TSM/TSM.csproj
```

Run the verification suite with:

```bash
dotnet run --project TSM.Tests/TSM.Tests.csproj
```

Create a Linux AppImage after publishing:

```bash
dotnet publish TSM/TSM.csproj -c Release -r linux-x64 --self-contained true -o artifacts/publish/linux-x64
packaging/linux/build-appimage.sh artifacts/publish/linux-x64 artifacts/packages 0.1.0 /path/to/appimagetool x86_64
```

Maintainers can rebuild the bundled icon manifest from an existing local item cache with:

```bash
scripts/generate-item-icon-manifest.sh ~/.local/share/TSM-CrossPlatform/ItemIcons TSM/Assets/item-icons.json.br
```

Pushing a version tag such as `v0.2.0` runs the release workflow and publishes a Windows x64 setup executable, a macOS Apple Silicon DMG, a Linux x64 AppImage, and portable ZIPs used by the automatic updater. Older temporary workflow artifacts are removed after a successful release; GitHub Release downloads are retained.

## Project notice

This project is distributed under the [MIT License](LICENSE). It is a clean-room C#/Avalonia implementation based on the observable behavior required for compatibility with TradeSkillMaster data and services.

TradeSkillMaster, TSM, World of Warcraft, Warcraft, Blizzard, Wowhead, and associated names, logos, artwork, game content, data, trademarks, and copyrights belong to their respective owners. References to them describe compatibility and interoperability only; this project claims no ownership of third-party intellectual property. The project's MIT License does not grant rights to third-party trademarks, copyrighted material, services, APIs, or data.

Read the full [project notice](NOTICE.md) and [third-party notices](THIRD_PARTY_NOTICES.md).
