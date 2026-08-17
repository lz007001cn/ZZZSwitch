# ZZZSwitch

English | [简体中文](README.zh-CN.md)

ZZZSwitch is a Windows server switcher for **Zenless Zone Zero**, supporting **Global**, **CN Official**, and **Bilibili** clients.

Starting with **v1.3.0**, Global ↔ CN Official switching no longer depends on a separately distributed replacement package. ZZZSwitch reads the official Sophon manifests for the installed game version, calculates the required client differences, downloads and verifies the real files, and saves the completed result as a reusable local version package. Bilibili switching remains a CN-based channel overlay and continues to use the matching legacy local package.

![ZZZSwitch main window](docs/images/zzzswitch-main-window.png)

## Highlights

- Automatically detects the Zenless Zone Zero installation or lets you select it manually.
- Retrieves Global and CN Official Sophon manifests for the exact installed game version.
- Builds versioned Global ↔ CN Official client-difference packages on demand.
- Reuses local files that match the target manifest and preserves source-region differences before they are overwritten, allowing the reverse switch to avoid downloading the original client files again.
- Resumes verified file and chunk downloads after cancellation or network failure.
- Reuses a completed package for later switches in the same version and direction.
- Preserves each server's hot-update cache automatically before switching.
- Supports Bilibili as a separate login/channel overlay while sharing CN resource caches.
- Verifies switch files with MD5 and SHA-256 before applying them.
- Uses transactional backups, rollback journals, process checks, and path-safety validation.
- Provides package management, Manifest browsing, previews, updates, integrity checks, cache management, and backup history.

## Requirements

- Windows x64.
- An installed PC version of Zenless Zone Zero.
- Internet access to retrieve both manifests and any target files not already available locally for the first Global ↔ CN Official switch of a game version.
- A version-matched legacy local package for any switch involving Bilibili. Bilibili resources are not obtained through Sophon in v1.3.0.

## Usage

1. Download `ZZZSwitch-win-x64-v1.3.1.zip` from the [latest release](https://github.com/lz007001cn/ZZZSwitch/releases/latest), extract it to any folder, and run `ZZZSwitch.exe`.

2. Let ZZZSwitch detect the game directory, or choose the installation manually.

3. Completely close both the game and HoYoPlay before starting a switch.

4. Select the target server:

   - **Global ↔ CN Official:** ZZZSwitch checks both the target and reverse packages. If either is missing, it retrieves both Sophon manifests, preserves verified files from the installed source client, and downloads only target files that are not already available locally.
   - **Bilibili:** ZZZSwitch uses the matching legacy package under the game's `.zzzswitch\packages\<game-version>` directory. Bilibili is intentionally excluded from Manifest download and browsing.

5. For a first-time Global/CN download, review the file count and maximum download size, then start the download. Verified complete files and chunks are retained, so retrying does not restart from zero.

6. Review the switch summary and confirm. ZZZSwitch automatically backs up the current client state and saves the source server's hot-update cache before changing files.

7. Start the game and complete any target-server resource download. The first entry into a new server may still require approximately **3–10 GB** of in-game resources.

8. Before switching again, close the game and launcher. No manual “Initialize Current Region Cache” step is required; the current server cache is saved automatically during the next switch.

## Packages, Manifests, and caches

Open **Manage packages** from the main window to:

- view saved packages by game version and target server;
- download or refresh the current Global/CN Manifests;
- browse all resources, story/video files, audio, Streaming Blocks, state metadata, or client differences;
- preview and verify a completed difference package;
- update a package while reusing existing verified files and chunks;
- open or remove selected local package data.

Manifest metadata and automatic Global/CN packages are stored under `%LOCALAPPDATA%\ZZZSwitch`. Large server-specific Blocks caches remain independent from client-difference packages and can be moved or cleaned through **Cache management**.

Packages and caches are isolated by game installation and game version. After a game update, ZZZSwitch requests the new version's Manifests and creates new package/cache records instead of applying an older version's files. Previous versions remain available for manual cleanup.

## Safety notes

> [!IMPORTANT]
> Do not switch while Zenless Zone Zero or its launcher is running. Keep the game and HoYoPlay closed until ZZZSwitch reports that the operation has completed.

- ZZZSwitch will not apply a package whose game version, file count, size, MD5, or SHA-256 validation fails.
- Global/CN online switching never falls back to an old `.zzzswitch\packages` replacement package.
- `Persistent\Blocks` is managed as a server cache; `Persistent\Video` is not moved, copied, verified, or deleted.
- File replacement, Blocks exchange, state updates, and backups participate in the same recoverable transaction.
- If an operation is interrupted, startup recovery uses the saved transaction journals and rollback backup.

## Developer documentation

- [Sophon Manifest retrieval, classification, download, and cross-region diff](docs/manifest-analysis.md)
- [Architecture and transaction design](docs/design.md)
- [Automated test coverage](docs/testing.md)
