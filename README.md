# ZZZSwitch

English | [简体中文](README.zh-CN.md)

🌐 [Project website](https://lz007001cn.github.io/ZZZSwitch/) · [Download the latest release](https://github.com/lz007001cn/ZZZSwitch/releases/latest)

ZZZSwitch is a Windows server switcher for **Zenless Zone Zero**, supporting **Global**, **CN Official**, and **Bilibili** clients.

Global ↔ CN Official switching uses the official Sophon manifests for the installed game version. ZZZSwitch reuses files already available in the current client, downloads only missing target files, and preserves the source-region differences before replacement so the reverse switch can reuse local data instead of downloading the original client again. Bilibili remains a CN-based channel overlay; starting with 1.3.4, the channel components for the supported game version are included in the app.

![ZZZSwitch main window](docs/images/zzzswitch-main-window.png)

![ZZZSwitch compact window](docs/images/zzzswitch-compact-window.png)

## Highlights

- Detects the game installation automatically or accepts a manually selected directory.
- Supports Chinese and English, light and dark themes, and a first-run setup guide.
- Provides a full window for status, package, cache, backup, and settings management.
- Provides a compact window with three server buttons, current-server highlighting, and inline switch progress.
- Runs in the system tray by default. A single left click restores the configured window; the right-click menu opens the full window, compact window, or exits.
- Lets you choose whether closing a window hides the application to the tray or exits it.
- Retrieves and browses Global/CN Sophon manifests for the exact installed game version.
- Reuses verified local files and completed version packages, including resumable file and chunk downloads.
- Preserves each server's hot-update cache automatically before switching.
- Supports custom cache and backup locations, old-version cache cleanup, startup behavior, and log retention.
- Checks GitHub Releases silently from Settings, shows the current and latest versions with release notes, and verifies update packages with GitHub's SHA-256 before installation.
- Uses MD5/SHA-256 verification, transactional backups, rollback journals, process checks, and path-safety validation.

## Requirements

- Windows x64.
- An installed PC version of Zenless Zone Zero.
- Internet access for Global ↔ CN Official manifests and any target files not already available locally.
- Direct Global ↔ Bilibili switching requires the matching Global/CN base difference, which can be downloaded automatically or reused locally. The Bilibili channel components themselves no longer require a separate download.

## Usage

1. Download `ZZZSwitch-win-x64-v1.4.0.zip` from the [latest release](https://github.com/lz007001cn/ZZZSwitch/releases/latest), extract it to any folder, and run `ZZZSwitch.exe`.

2. On first launch, complete the setup guide:
   - choose Chinese or English and the interface theme;
   - detect or select the Zenless Zone Zero game directory;
   - choose the startup window and close behavior.

3. Completely close Zenless Zone Zero and HoYoPlay before switching.

4. Select the target server:
   - **Global ↔ CN Official:** ZZZSwitch checks both directions, reads the matching manifests when needed, preserves reusable source files, and downloads only missing target files.
   - **Bilibili:** On the first inspection of a supported game version, ZZZSwitch verifies and installs its bundled channel components under `.zzzswitch\packages\<game-version>\bilibili`. CN ↔ Bilibili uses only that small overlay; Global ↔ Bilibili combines it with the online Global/CN base difference in one recoverable transaction. Bilibili shares CN Blocks resources while retaining a separate channel identity.

5. In the full window, review and confirm the switch summary. In the compact window, selecting a server starts the switch directly and reports progress in the lower-left corner without extra confirmation or completion dialogs.

6. ZZZSwitch backs up affected files and saves the source server's hot-update cache before applying changes. Verified downloads and chunks are retained after cancellation or network failure, so retrying does not restart from zero.

7. After switching, start the game and complete any target-server resource download. The first entry into another server may still require approximately **3–10 GB** of in-game resources.

8. Closing the application hides it in the system tray by default. Left-click the tray icon once to reopen the configured window, or use the right-click menu. Enable **Exit when closing a window** in Settings if preferred.

## Application updates

Starting with v1.4.0, opening **Settings** silently checks the latest stable GitHub Release and displays the current version, latest version, publication date, and release notes. The check does not interrupt normal use or show a separate prompt.

Selecting **Install update** downloads the official Windows x64 ZIP, verifies its declared size and the SHA-256 digest supplied by GitHub, and only then hands it to the standalone updater. ZZZSwitch exits, replaces the program files, restarts automatically, and records the installation result. An incomplete package or failed digest is never installed.

The public v1.3.7 release does not contain the updater, so upgrading from that public build to v1.4.0 requires one manual download. After v1.4.0 is installed, later compatible versions can use the in-app updater. A republished package with the same version number does not trigger another update because version comparison treats it as the installed version.

## Packages, Manifests, and caches

Open **Manage packages** to view saved versions, update or browse manifests, preview and verify completed packages, resume package updates, and remove selected local data.

`%LOCALAPPDATA%\ZZZSwitch` stores settings, state, locks, logs, transaction-control files, and temporary application-update jobs. Managed game-switch payloads use explicit names under the current installation: transaction backups default to `.zzzswitch\app-data\backup-records`, automatic Global/CN downloads live in `.zzzswitch\app-data\downloads`, metadata lives in `snapshots`, `sophon-manifests`, and `blocks-manifests`, and the large server-specific Blocks store is `.zzzswitch\blocks-cache`. `.zzzswitch\packages` is separate because it contains read-only offline inputs: the bundled Bilibili overlay by default, plus Global/CN files only when the user deliberately imports a unified offline package. Legacy `data` / `cache` layouts and AppData payloads are migrated before use. The short-lived `backup-content` object layout remains readable for existing backups but is no longer created. Backup location, cache location, startup mode, close behavior, language, theme, and log retention are available in **Settings**.

Packages and caches are isolated by game installation and version. After a game update, ZZZSwitch creates new manifest, package, and cache records instead of applying older-version files. Previous-version Blocks, owned manifests, and metadata snapshots can be cleaned together, including read-only leftovers. Legacy fixed `global` / `cn_official` package directories are accepted for compatibility but are no longer required by automatic switching and are not deleted without an explicit cleanup action.

## Safety notes

> [!IMPORTANT]
> Do not switch while Zenless Zone Zero or HoYoPlay is running. Keep both closed until ZZZSwitch reports that the operation has completed.

- ZZZSwitch does not apply a package when the game version, required file size, MD5, or SHA-256 validation fails.
- Global/CN online switching never falls back to an old `.zzzswitch\packages` replacement package.
- `Persistent\Blocks` is managed as a server cache; `Persistent\Video` is not moved, copied, verified, or deleted.
- File replacement, Blocks exchange, state updates, and backups participate in the same recoverable transaction.
- Startup recovery uses saved transaction journals and rollback backups after an interrupted operation.

## Developer documentation

- [Sophon Manifest retrieval, classification, download, and cross-region diff](docs/manifest-analysis.md)
- [Architecture and transaction design](docs/design.md)
- [Automated test coverage](docs/testing.md)
