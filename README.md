# ZZZSwitch

ZZZSwitch 是一款 Windows x64 本地桌面工具，用于在同一个《绝区零》游戏目录中安全切换：

- 国服
- 国际服
- B服（哔哩哔哩服）

软件只使用本地差异包，不会在线下载差异文件，不会修改 HoYoPlay 配置，也不会代替启动器更新或修复游戏。

## 当前版本

| 项目 | 版本 |
|---|---|
| ZZZSwitch | 1.2.2 |
| 支持的游戏版本 | 3.1.0 |
| 系统 | Windows 10/11 x64 |
| 运行环境 | 自包含，无需单独安装 .NET |

软件版本和游戏差异包版本是两套独立版本号：

- `ZZZSwitch 1.2.2` 表示工具本身的版本。
- `ZZZSwitch-Packages-3.1.0` 表示差异包适用于游戏 3.1.0。

不同游戏版本的差异包不能混用。

## 主要功能

- 自动检测 HoYoPlay 安装记录和常见游戏目录。
- 自动识别当前是国服、国际服还是 B服。
- 使用六个独立方向清单切换服务器文件。
- 分别管理国服与国际服的 `Persistent\Blocks` 热更新缓存；B服复用国服缓存，不重复占用一整套空间。
- 可在“缓存管理”中清理旧游戏版本缓存，或把缓存迁移到自定义目录/其他磁盘。
- 切换前检查游戏版本、差异包逐文件长度与 SHA-256、进程占用和缓存状态。
- 切换过程中保留事务备份，失败时自动回滚。
- 保存并恢复各服务器的 `version/revision` 元数据。
- 检测 `.zzzswitch` 存储目录、差异包和缓存异常，并可安全重建标准目录结构。
- 提供详细检查、运行日志、备份历史和恢复功能。
- 适配 Windows 深色模式。

当前 3.1.0 差异规则：

- 国际服 → 国服：替换 32 个文件。
- 国服 → 国际服：替换 24 个文件。
- 国服 → B服：叠加 72 个 B服文件并按键更新 `config.ini`。
- 国际服 → B服：恢复国服核心、叠加 B服文件并按键更新 `config.ini`。
- B服 → 国服/国际服：恢复目标配置并清理 B服叠加文件。
- 六个方向均没有必需删除文件；B服叠加文件离开 B服时按“存在则删除”处理。

## 下载文件

所有服务器用户只需要：

```text
ZZZSwitch-win-x64-v1.2.2.zip
ZZZSwitch-Packages-3.1.0.zip
```

`ZZZSwitch-Packages-3.1.0.zip` 已同时包含 `global`、`cn_official` 和
`bilibili` 三个差异目录，不再单独分发 B服扩展压缩包。

同时提供：

```text
SHA256SUMS-v1.2.2.txt
```

可使用 SHA-256 校验压缩包是否完整。

热更新缓存不会随发布包分发。每台电脑都需要在本机初始化自己的国服/国际服资源缓存；B服与国服共用同一份缓存。

## 安装

### 1. 解压软件本体

将 `ZZZSwitch-win-x64-v1.2.2.zip` 完整解压到普通文件夹，例如：

```text
D:\Tools\ZZZSwitch
```

然后运行：

```text
ZZZSwitch.exe
```

不要直接在 ZIP 压缩包中运行，否则 Windows 可能提示需要先提取其他文件。

### 2. 解压差异包

假设游戏目录是：

```text
E:\HoYoPlay\games\ZenlessZoneZero Game
```

将差异包中的 `.zzzswitch` 文件夹解压到游戏目录的上一级：

```text
E:\HoYoPlay\games\
├─ .zzzswitch\
│  └─ packages\
│     └─ 3.1.0\
│        ├─ global\
│        ├─ cn_official\
│        ├─ bilibili\
│        └─ version.ini
└─ ZenlessZoneZero Game\
```

不要放成：

```text
E:\HoYoPlay\games\ZenlessZoneZero Game\.zzzswitch
```

## 首次使用

以下以“当前安装的是国际服”为例。

### 1. 准备当前服务器

1. 使用国际服启动器完成游戏更新。
2. 启动游戏，确认能够正常登录并进入游戏。
3. 等待游戏内资源下载完成。
4. 完全退出游戏。
5. 完全退出 HoYoPlay，包括后台托盘进程。

### 2. 选择游戏目录

启动 ZZZSwitch 后点击“自动检测”。

自动检测会依次检查：

- 上次保存的有效目录
- HoYoPlay/米哈游启动器安装记录
- 所有固定磁盘的常见安装位置

如果发现多个有效目录，软件会要求用户选择。如果未找到，可以点击“选择”，手动指定包含 `ZenlessZoneZero.exe` 的游戏根目录。

确认主页显示：

```text
当前客户端：国际服
游戏版本：3.1.0
国服差异包：可用
国际服差异包：可用
```

### 3. 初始化当前服缓存

点击“初始化当前服缓存”。

初始化只会登记当前：

```text
ZenlessZoneZero_Data\Persistent\Blocks
```

的文件数量、大小和清单，不会移动或修改当前游戏内容。

国服和国际服每个游戏版本通常只需要手动初始化一次。B服映射到国服资源缓存，不要为 B服再建立第三份 Blocks。

### 4. 首次切换到另一服务器

1. 确认游戏与启动器已经完全退出。
2. 在 ZZZSwitch 中点击“国服”。
3. 检查确认窗口中的来源服、目标服和文件数量。
4. 点击“确认切换”。
5. 等待软件提示切换完成。
6. 打开国服对应的启动器。
7. 完成国服首次热更新下载。
8. 成功进入游戏。
9. 完全退出游戏与启动器。
10. 重新打开 ZZZSwitch。
11. 点击“初始化当前服缓存”。

此时国服与国际服资源缓存均已建立。切换国服 ↔ B服不会搬运 Blocks。

首次进入另一服务器时下载数 GB 或十几 GB 热更新属于正常现象，因为发布包不包含热更新缓存。

## 日常切换

国服与国际服资源缓存都初始化后：

1. 完全退出游戏。
2. 完全退出国服和国际服启动器。
3. 打开 ZZZSwitch。
4. 选择目标服务器。
5. 检查确认窗口。
6. 点击“确认切换”。
7. 等待切换成功。
8. 打开目标服务器对应的启动器；B服使用切换成功提示中的 `PCGamePlatform.exe` 路径。

软件会自动：

- 重新检查当前服是否下载了新热更新。
- 更新当前服 Blocks 缓存清单。
- 保存当前服 Blocks。
- 恢复目标服 Blocks。
- 应用目标服差异文件。
- 恢复目标服 `version/revision` 元数据。
- 校验结果并提交状态。

后续游戏下载了新的热更新时，不需要再次手动初始化。新内容会在下一次切走当前服务器时自动保存。

## 缓存与备份空间管理

主页点击“缓存管理”可以：

- 查看当前安装的已存储缓存和旧版本缓存占用；
- 只删除非当前游戏版本的缓存和对应清单；
- 把缓存迁移到自定义目录，复制与清单校验完成后才启用新位置；
- 恢复默认的游戏同级缓存位置。

同一磁盘内切换仍使用快速目录移动；自定义目录位于另一磁盘时，程序使用可恢复的临时复制和清单校验，因此需要足够的临时可用空间，耗时也会明显增加。游戏、启动器或未完成事务存在时不会执行迁移/清理。

成功切换后，每份游戏安装按来源服分别保留一份最新的可恢复事务备份：国服、国际服、B服各一个槽位，因此最多三份。再次从同一来源服切出时，新备份会在切换完全提交后替代该服旧备份；切换目标不同也不会增加槽位。已完整回滚的失败备份、启动时已完整恢复的中断备份和被替代的成功备份会自动轮换清理。损坏或回滚未完成的记录会保留供排查，避免误删唯一恢复依据。

主页“备份目录”可将全部事务备份迁移到自定义位置；迁移会逐文件校验 SHA-256，写入新设置后才清理旧位置。选择的目录必须为空，且不能与游戏目录、`.zzzswitch` 或应用数据目录重叠。“恢复上次状态”位于“备份历史”窗口中，并继续只恢复与 `state.json` 精确对应的最后一次切换备份。

## 游戏版本更新

例如游戏从 3.1.0 更新到后续版本：

1. 暂停使用旧版切换功能。
2. 等待支持新游戏版本的切换配置和差异包。
3. 使用当前服务器启动器更新到新游戏版本。
4. 成功进入游戏并等待资源下载完成。
5. 完全退出游戏和启动器。
6. 安装支持新游戏版本的 ZZZSwitch 版本或配置。
7. 解压对应新游戏版本的差异包。
8. 初始化当前服的新版本缓存。
9. 切换到另一服务器并完成该服更新。
10. 初始化另一服的新版本缓存。
11. 完成至少一次双向切换验证。
12. 最后再清理 3.1.0 的旧缓存和旧差异包。

不要在升级前清空全部缓存。缓存和差异包按游戏版本隔离，保留旧版本直到新版本双向验证成功最安全。

只应删除明确属于旧版本的目录，例如：

```text
.zzzswitch\packages\3.1.0
.zzzswitch\cache\<游戏目录标识>\3.1.0
%LOCALAPPDATA%\ZZZSwitch\HotUpdateCaches\<游戏目录标识>\3.1.0
%LOCALAPPDATA%\ZZZSwitch\ProfileSnapshots\<服务器>\3.1.0
```

不要直接删除整个 `.zzzswitch` 或 `%LOCALAPPDATA%\ZZZSwitch`。

## 存储异常与目录修复

选择或自动检测到有效游戏目录后，ZZZSwitch 会检查同级存储目录，并在详细检查信息中区分：

- `.zzzswitch` 存储根目录未检测到。
- 当前版本差异包目录未检测到、为空或文件不完整。
- 国服/国际服热更新缓存尚未初始化。
- 已有缓存记录，但对应的 Blocks 缓存仓库丢失或不可用。

如果 `.zzzswitch` 或其标准子目录被删除，软件会弹出说明窗口。点击“修复目录”只会重新创建：

```text
.zzzswitch\
├─ packages\
│  └─ <游戏版本>\
│     ├─ global\
│     ├─ cn_official\
│     └─ bilibili\
└─ cache\
```

目录修复不会下载、生成或伪造差异文件，也不会修改当前游戏的 `Persistent\Blocks`。修复后仍需把对应版本的差异包手动解压到游戏目录上一级。

如果缓存仓库已经丢失，目录结构可以修复，但原缓存内容无法自动恢复。补齐差异包后，软件会把丢失的目标服缓存视为未初始化；切换并完成该服资源下载后，再按提示初始化缓存。

## 数据目录

默认缓存位置位于游戏同盘；用户在“缓存管理”中设置自定义位置后，`cache` 子树会迁移到所选目录：

```text
<游戏上级目录>\.zzzswitch\
├─ packages\                       # 按游戏版本保存的只读差异包
└─ cache\
   └─ <游戏目录名称-身份标识>\
      └─ <游戏版本>\
         ├─ global\Blocks          # 国际服非活动缓存
         └─ cn_official\Blocks     # 国服/B服共用的非活动缓存
```

当前正在使用的服务器缓存位于：

```text
ZenlessZoneZero Game\
└─ ZenlessZoneZero_Data\
   └─ Persistent\
      └─ Blocks
```

应用数据：

```text
%LOCALAPPDATA%\ZZZSwitch\
├─ Backups                         # 默认切换事务备份位置（可自定义）
├─ Logs                            # JSONL运行日志
├─ ProfileSnapshots                # version/revision元数据快照
├─ HotUpdateCaches                 # 按游戏目录、版本和服务器隔离的 Blocks 缓存清单
├─ Temp                            # 校验后的临时文件
├─ file-transaction.json           # 未完成普通文件事务的恢复记录（仅事务期间存在）
├─ application.lock                # 应用生命周期锁文件
├─ operation.lock                  # 写操作互斥锁文件
├─ backup-location.json            # 自定义备份位置（使用默认位置时内容为空）
├─ cache-locations.json            # 每份游戏安装的自定义缓存位置
└─ state.json                      # 最近一次成功状态
```

`Persistent\Video` 不由 ZZZSwitch 管理：

- 不保存
- 不交换
- 不校验
- 不复制
- 不删除

切换服务器或更换账号后，剧情视频等内容可能再次下载。

## 安全机制

- 六个方向分别使用独立 JSON 清单。
- 拒绝绝对路径、`..`、通配符和越界路径。
- 差异包版本必须与游戏版本一致。
- 每个差异文件必须与清单中的长度和 SHA-256 完全一致。
- 游戏或关键启动器进程运行时禁止切换。
- 同一用户只能运行一个 ZZZSwitch 实例；同一会话和跨会话分别使用命名锁与文件句柄锁保护。
- 切换、缓存初始化、恢复、目录修复和备份删除共用跨进程写操作锁。
- 文件被占用、缓存清单损坏或存在未完成事务时停止操作。
- 差异文件先复制到应用临时目录并校验，再写入游戏目录。
- 被替换和删除的文件会写入独立事务备份，备份和恢复均校验 SHA-256。
- Blocks 移动/跨磁盘校验复制和普通文件替换属于同一个联合事务。
- 任一步失败都会停止并尝试恢复原状态。
- 强制关闭后，下次启动会根据持久化事务记录继续判定提交或回滚。
- 状态文件只在完整切换成功后提交。

软件无法提供断电级原子性。切换过程中不要强制结束程序、关机、重启或拔出游戏磁盘。

## 常见问题

### 自动检测显示错误目录

1. 点击“自动检测”重新扫描。
2. 确认使用的是 ZZZSwitch 1.2.2 或与当前游戏版本配套的更新版本。
3. 如果仍未找到，点击“选择”手动指定游戏目录。

### 差异包显示不可用

检查：

- `.zzzswitch` 是否位于游戏目录上一级。
- 差异包版本是否与游戏版本一致。
- 解压是否完整。
- 文件是否通过长度和 SHA-256 校验。
- 安全软件是否隔离了 EXE、DLL 或 SYS 文件。

### 切换提示文件被占用

完全退出：

- 绝区零
- HoYoPlay
- 国服启动器
- 国际服启动器
- B服登录窗 `PCGamePlatform.exe`
- `game_security_protection.exe`
- 相关更新进程

必要时检查系统托盘或任务管理器。

### 首次切换为什么还要下载十几 GB

ZZZSwitch 不分发热更新缓存。第一次进入另一资源服时必须在本机下载并初始化一次。国服与国际服缓存都建立后，日常切换才会快速交换；B服直接复用国服缓存。

如果同一台电脑存在多份绝区零安装，ZZZSwitch 会按规范化游戏路径生成独立目录身份。不同安装的 Blocks 清单与实际缓存不会互相覆盖。升级自旧版后，身份匹配的旧清单会在首次检查时自动迁移；不匹配的旧清单不会被其他安装认领。

### 为什么另一台电脑的缓存大小不同

Blocks 大小会受到安装时间、补丁历史、启动器清理和账号资源影响。不能用另一台电脑的缓存大小作为标准。

判断缓存是否可用应以以下条件为准：

- 启动器更新完成
- 已成功进入游戏
- 游戏内没有继续下载
- 初始化没有报错
- 缓存清单校验有效

### 后续热更新需要再次点击初始化吗

不需要。当前服下载的新内容会在下一次切换离开该服务器时自动保存。

### 切换后又下载了少量视频资源

`Persistent\Video` 不在管理范围内。少量账号相关视频重新下载属于预期行为。

### 能否复制其他电脑的 Blocks 缓存

不保证可用。缓存与游戏目录身份、游戏版本和资源修订相关。推荐每台电脑分别完成国服/国际服缓存初始化。

## 日志与排查

运行日志位于：

```text
%LOCALAPPDATA%\ZZZSwitch\Logs
```

寻求帮助时建议提供：

- 软件版本
- 游戏版本
- 当前服务器
- 错误提示截图
- “详细检查信息”
- 对应日期的 `.jsonl` 日志

日志可能包含本机游戏路径。公开发送前可以遮挡 Windows 用户名或其他个人目录信息。

## 开发与构建

需要：

- Windows 10/11
- Visual Studio 2022，或 .NET 8 SDK
- Windows Desktop/WPF 开发组件

打开：

```text
ZZZSwitch.sln
```

构建：

```powershell
dotnet build ZZZSwitch.sln -c Release
```

运行测试：

```powershell
dotnet run --project tests\ZZZSwitch.Core.Tests\ZZZSwitch.Core.Tests.csproj -c Release
```

运行不显示窗口、不触发应用单实例锁的 WPF 结构冒烟检查：

```powershell
dotnet run --project tests\ZZZSwitch.Ui.Smoke\ZZZSwitch.Ui.Smoke.csproj -c Release
```

当前自动测试数量：

```text
81/81
```

为新游戏版本生成或核对差异文件清单哈希：

```powershell
python tools\update_package_hashes.py config "E:\HoYoPlay\games\.zzzswitch\packages\<游戏版本>"
python tools\update_package_hashes.py config "E:\HoYoPlay\games\.zzzswitch\packages\<游戏版本>" --check
```

生成工具只读取差异包；第一条命令更新 `config\transitions`，第二条命令只核对、不写入。

生成 Windows x64 自包含单文件版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-win-x64.ps1
```

发布输出（版本号读取自 `src\ZZZSwitch\ZZZSwitch.csproj`）：

```text
publish\ZZZSwitch-win-x64-v<版本号>
```

### GitHub Actions 自动发布

`.github/workflows/release.yml` 会在推送 `v<主版本>.<次版本>.<修订号>` 标签时自动执行 Release 构建、核心测试、WPF UI 冒烟测试，并创建带 Windows x64 便携 ZIP 和 SHA-256 文件的 GitHub Release。标签版本必须与 `src\ZZZSwitch\ZZZSwitch.csproj` 中的 `Version` 完全一致，否则工作流会停止。

例如发布 `1.2.3`：先将 `src\ZZZSwitch\ZZZSwitch.csproj` 中唯一的 `<Version>` 改为 `1.2.3`，提交并推送 `main`，再执行：

```powershell
git tag -a v1.2.3 -m "ZZZSwitch v1.2.3"
git push origin v1.2.3
```

推送普通分支不会发布版本。发布工作流只打包应用程序，不上传游戏差异包或热更新缓存。

## 项目结构

```text
ZZZSwitch
├─ config                           # 国服、国际服、B服配置和六方向切换清单
├─ dist                             # 最终发布压缩包和校验文件
├─ docs                             # 设计、安全和测试文档
├─ publish                          # 未压缩发布输出
├─ src
│  ├─ ZZZSwitch                  # WPF界面程序
│  └─ ZZZSwitch.Core             # 检测、切换、缓存、备份与回滚
├─ tests                            # 核心自动测试与非破坏性 WPF 冒烟检查
├─ tools                            # 品牌资源与差异包清单维护工具
├─ publish-win-x64.ps1              # Windows x64发布脚本
└─ ZZZSwitch.sln                    # Visual Studio解决方案
```

`.vs`、`bin`、`obj`、`publish` 和 `_release-staging` 都是可重新生成的本机构建或发布数据，不属于核心源码。

## 说明

ZZZSwitch 不是米哈游、HoYoverse 或 Cognosphere 的官方工具，与上述公司不存在隶属或授权关系。

差异包可能包含官方游戏文件。公开分发前，请自行确认相关文件的再分发许可。使用前建议保留官方启动器及其修复能力，并确认重要数据已有备份。
