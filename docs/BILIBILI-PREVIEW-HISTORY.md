# ZZZSwitch B服 Preview 历史记录

> 历史文档：记录 v1.2.0-preview.3 的实验验证前提。B服能力已在正式版 1.2.0 中启用，当前说明见 `BILIBILI.md`；旧实验发布产物仍保留。

该版本用于验证 3.1.0 国服/B服切换，不属于正式版。正式版 v1.1.3 的程序与发布包不会被覆盖。

## 已实现的实验逻辑

- B服资源层 = 国服核心文件 + `PCGameSDK.dll` + `BLPlatform64` 登录窗 + `sdk_pkg_version`。
- B服与国服共用国服 `Persistent\Blocks` 及 version/revision 快照；二者互切不搬运 Blocks。
- 进入 B服时只按键修改 `config.ini` 的 `General` 段，保留游戏版本、插件和其他配置。
- 离开 B服时删除 B服专用叠加文件，并恢复目标服务器的配置值。
- `config.ini`、所有被替换文件、所有待清理 B服文件都在执行前进入同一份事务备份。
- 复制、配置修改、文件删除或最终校验任一步失败，自动恢复原文件；异常退出后下次启动继续恢复。

## 测试前提

1. 游戏版本必须为 3.1.0。
2. 正式版的国服和国际服差异包必须已完整安装。
3. B服实验包必须位于 `.zzzswitch\packages\3.1.0\bilibili`。
4. 游戏、HoYoPlay、`PCGamePlatform.exe` 与 `game_security_protection.exe` 必须完全退出。
5. 第一次测试前请确认当前服热更新缓存已经初始化。

切到 B服后，通过游戏目录中的
`ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\PCGamePlatform.exe`
打开 B服登录窗。若实机登录失败，先不要使用官方启动器“修复”覆盖文件；直接在 ZZZSwitch 中切回原服，或从备份历史恢复刚才的事务。
