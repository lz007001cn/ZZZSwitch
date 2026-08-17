# ZZZSwitch

ZZZSwitch is a Windows multi-region switcher for **Zenless Zone Zero**. The current test build obtains Global ↔ CN Official client differences from Sophon when the user starts a switch; those two regions do not require a bundled local replacement package. Bilibili directions keep the legacy local-package workflow.

It can automatically detect the game installation directory and provides **verified online difference downloads, automatic current-region cache preservation**, and transactional switching between supported regions.

![ZZZSwitch main window](docs/images/zzzswitch-main-window.png)

> [!NOTE]
> The current runnable **test build** changes Global ↔ CN Official switching to an online Sophon workflow. The first download uses resumable, bounded-parallel chunk transfers and creates a versioned local automatic package; later switches within that version use the ready package without reopening the download workflow. The home page and Version Resources window show saved targets and disk usage. Every switch still verifies SHA-256 and uses the existing transactional backup/rollback engine. Global ↔ CN Official never reads or falls back to `.zzzswitch\packages`; any direction involving Bilibili deliberately keeps the legacy local-package workflow and is not shown in Sophon Manifest management. See [the test workflow and scope](docs/manifest-analysis.md#主程序测试版在线切换).

---

## Usage

1. Download and run the ZZZSwitch test build. Global ↔ CN Official needs no separate package; Bilibili switching requires the matching legacy package under the game's `.zzzswitch\packages` directory.

2. Let the program detect the game installation directory, or select it manually.

3. Choose Global or CN Official. ZZZSwitch analyzes the current-version manifests and shows the files and maximum download size.

4. Confirm the download. Completed files are verified and cached; cancellation never falls back to an old local package.

5. Confirm the switch. Before changing client files, ZZZSwitch automatically saves the current region's `Persistent\Blocks` and version/revision state inside the existing rollback transaction.

   > The first switch may require downloading approximately **3–10 GB** of additional game resources.

6. Launch the game and make sure you can successfully enter the target region.

7. Close both the game and launcher before switching again. No manual cache-initialization button is required.

---

## Notes

> [!IMPORTANT]
> Make sure both the **game and launcher processes are completely closed** before downloading for a switch or applying it.

- The online manifest and downloaded files must match the detected game version.
- The first switch to a new region may require downloading additional resources.
- After a game update, ZZZSwitch creates a new version-scoped online cache and saves the current region automatically.

## Developer tools

- [Sophon Manifest retrieval and cross-region diff test tool](docs/manifest-analysis.md)
