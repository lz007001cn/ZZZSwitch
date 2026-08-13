# Bilibili SDK build asset

`PCGameSDK-5.0.4.0.dll` is a signed Shanghai Kuanyu Digital Technology
(`上海宽娱数码科技有限公司`) binary extracted from the user-supplied ZZZ
three-server switcher package for local package generation.

- File version: `5.0.4.0`
- Size: `5,905,408` bytes
- SHA-256: `28CDC496571B194924EA23398810B4F128D9FF390FDCC00C789EC628F2B73D57`
- Authenticode: valid when imported on 2026-08-06

The package builder uses this asset to assemble the separate Bilibili overlay
archive. It is never embedded in the ZZZSwitch application or the base 3.1.0
global/CN package archive. The binary itself is excluded from Git by default;
public redistribution requires a separate license review.
