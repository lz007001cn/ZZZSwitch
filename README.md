# ZZZSwitch

ZZZSwitch is a multi-region switcher for **Zenless Zone Zero** based on local differential packages. It runs directly on Windows.

It can automatically detect the game installation directory and provides **differential package deployment, region cache initialization**, and fast switching between the **CN / Global / other supported regions**.

---

## Usage

1. Download the **ZZZSwitch package** and the corresponding **differential package** for your current game version.

2. Extract ZZZSwitch to any location and run `ZZZSwitch.exe`. The program will automatically detect the game installation directory.

3. Select the corresponding differential package and wait for it to be extracted into the game directory.

4. Click **Initialize Current Region Cache**.

5. Switch to the target region.

   > The first switch may require downloading approximately **3–10 GB** of additional game resources.

6. Launch the game and make sure you can successfully enter the target region.

7. Close both the game and the launcher, then click **Initialize Current Region Cache** again.

Once the caches for the required regions have been initialized, you can switch between them directly.

---

## Notes

> [!IMPORTANT]
> Make sure both the **game and launcher processes are completely closed** before applying a differential package, initializing a cache, or switching regions.

- Differential packages must match the current game version.
- The first switch to a new region may require downloading additional resources.
- After a game update, existing differential packages or caches may need to be regenerated.
