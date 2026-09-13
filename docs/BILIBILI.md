# ZZZSwitch B服支持

ZZZSwitch 1.2.0 已将原 B服实验能力纳入普通 Release 构建。1.3.4 起，当前支持版本的 B服渠道组件也嵌入正式程序。当前源码只保留一个正式构建和一套 `config` 配置，国服、国际服与 B服共用核心切换、备份、恢复和测试逻辑。旧的 v1.2.0-preview.3 程序和实验包仅作为历史发布物保留。

## 资源与缓存模型

`tools/build-bilibili-package.ps1` 将候选配置写入 `_release-staging/bilibili-package/config`，不直接覆盖正式 `config`。生成时保留国服核心基线 `gameVersion`、跨版本复用开关及兼容版本列表；请求版本必须匹配核心基线，不能把旧核心哈希直接标成新版。候选配置必须通过运行时配置契约检查后才打包；审核后再单独纳入正式配置。

- B服 = 国服核心文件 + `PCGameSDK.dll` + `BLPlatform64` 登录窗 + `sdk_pkg_version`。
- B服与国服共用国服 `Persistent\Blocks` 和 version/revision 快照，不创建第三套热更新缓存。
- 进入 B服时只按键修改 `config.ini` 的 `General` 段，保留游戏版本、插件和其他配置。
- 离开 B服时删除已安装的 B服叠加文件，并恢复目标服配置值。
- `config.ini`、被替换文件和待清理叠加文件在执行前进入同一份事务备份。

## 安装与启动

程序首次识别到受支持的游戏版本后，会从软件本体解压 B服渠道组件，逐文件验证长度和 SHA-256，并以同盘目录移动原子提交到：

```text
.zzzswitch\packages\3.1.0\bilibili
```

因此从国服官服切换 B服不再需要手动解压 B服组件。国际服与 B服之间直接切换时，仍需要对应版本的国际服/国服基础差异文件；内置内容只覆盖 B服渠道叠加层。

切换成功后，通过游戏目录中的以下程序打开 B服登录窗：

```text
ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\PCGamePlatform.exe
```

切换前必须完全退出游戏、HoYoPlay、`PCGamePlatform.exe` 和 `game_security_protection.exe`。若登录失败，不要立即使用官方启动器修复覆盖文件；先在 ZZZSwitch 中切回原服，或按程序提示使用最近一次可恢复备份。

## 差异包维护

维护新游戏版本的 B服叠加包时使用：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-bilibili-package.ps1 -GameVersion <游戏版本>
```

脚本会更新统一 `config` 中的 B服 profile/四条相关切换清单，运行正式核心测试，并把待审核的内部叠加包和 SHA-256 文件放入 `_release-staging\bilibili-package`。它不会生成独立的 B服应用，也不会覆盖 `dist` 中的历史发布物。

审核基础包和 B服叠加包后，用统一打包脚本生成三服合一的正式候选包：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-unified-package.ps1 -GameVersion <游戏版本> -BasePackageZip <基础包路径> -BilibiliPackageZip <B服叠加包路径>
```

产物写入 `_release-staging\unified-package`，不会覆盖 `dist` 中已有的发布物。
