# Sophon Manifest 分析工具（测试版）

`ZZZSwitch.ManifestTool` 是与主程序隔离的开发 CLI。`branches`、`manifest`、`diff` 和 `classify` 默认只读取 HoYoverse / 米哈游 Sophon 元数据，不读取或修改游戏目录，不操作 `.zzzswitch`，也不覆盖 `config\transitions`。只有显式执行 `download` 并追加 `--accept-download` 时才下载所选文件的 chunk；输出目录必须与游戏和 `.zzzswitch` 隔离。

## Sophon manifest 与 chunk

Sophon build 返回多个 manifest category。manifest 是经过 Zstd 压缩的 protobuf 文件，记录逻辑文件路径、长度、文件 MD5 和对应 chunk 信息；chunk 是组成真实游戏文件的数据块。元数据命令只下载和解析 manifest；文件下载命令按清单选择显式文件，不会默认下载完整游戏。

测试版根据 [Escartem/MeiBrowser](https://github.com/Escartem/MeiBrowser) 的公开实现重新整理了接口流程与 protobuf 字段，并按本项目的安全边界独立实现。核对基准为 MeiBrowser commit `1c4091e2f7c07cd156a5e4b2f8da8699ebf01b7e`。

## 当前 CN / OS 配置

游戏标识为 `nap`。

| 区域 | game_id | getGameBranches | getBuild |
|---|---|---|---|
| OS | `U5hbdsT9W7` | `https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches` | `https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild` |
| CN | `x6znKlJ0xK` | `https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches` | `https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild` |

`launcher_id` 和 `plat_app` 集中定义在 `SophonRegionConfig`。password 只用于当前 `getBuild` 请求，不写入快照、报告、缓存或日志。`--verbose` 也会隐藏 password 和 URL 查询参数。

HTTP 层只对超时、连接/SSL 握手、429 和 5xx 做最多三次有限重试；确定性的 4xx 不重试，最终错误会保留已脱敏 URL 与最内层失败原因。

## 获取流程

1. `getGameBranches` 读取目标游戏的 main package、password、当前 tag，以及可选的 pre-download 数据。
2. `getBuild` 按 package、main branch、platform app 和显式版本获取 build。
3. CLI 列出每个 `category_id`、`matching_field` 和 `manifest.id`。
4. 未指定 category 时，只在存在唯一 `matching_field = game` 的情况下自动选择；否则停止并要求显式指定。
5. 组合 manifest URL，下载 Zstd 数据，解压并解析 protobuf。
6. 将 protobuf 资产转换为独立的 `ManifestSnapshot`，Diff 逻辑不依赖 protobuf 类型。

版本处理集中在 `SophonClient.GetVersionCandidates`。三段和四段数字版本都能输入；工具先尝试用户原值，再按需要尝试增加或移除末尾 `.0`，不会由调用方到处拼字符串。

## 路径与 Diff 规则

manifest 路径统一为反斜杠相对路径。绝对路径、UNC 路径、驱动器/数据流标记、`..` 和空路径会被拒绝。由于目标游戏运行在 Windows，索引和重复检测使用大小写不敏感比较；报告按稳定顺序输出。

同路径的长度和 MD5 都一致为 `Same`；目标独有为 `Added`；来源独有为 `Removed`；同路径但长度或 MD5 不同为 `Modified`。命令方向决定 Added/Removed 的含义，例如 OS→CN 中 CN 独有文件为 Added。

Sophon MD5 用于远程 manifest identity、候选差异判断、chunk 和重建文件校验。主程序在线切换还会对重建后的真实文件计算 SHA-256，并把 SHA-256 写入本次动态切换清单；不得用 MD5 替代切换引擎的 SHA-256 校验。

## 使用方法

```powershell
dotnet run --project tools\ZZZSwitch.ManifestTool -- branches --region OS
dotnet run --project tools\ZZZSwitch.ManifestTool -- manifest --region OS --version 3.1.0
dotnet run --project tools\ZZZSwitch.ManifestTool -- manifest --region CN --version 3.1.0
dotnet run --project tools\ZZZSwitch.ManifestTool -- diff --source OS --target CN --version 3.1.0
dotnet run --project tools\ZZZSwitch.ManifestTool -- diff --source CN --target OS --version 3.1.0
dotnet run --project tools\ZZZSwitch.ManifestTool -- classify --source OS --target CN --version 3.1.0
```

可使用 `--output <目录>` 指定输出，`--verbose` 查看已脱敏的诊断，`--no-cache` 强制重新下载 manifest。默认缓存位于：

```text
%LOCALAPPDATA%\ZZZSwitch\ManifestCache\nap\<region>\<version>\<category>\snapshot.json
```

缓存只保存已解析快照，不保存 branch password。

## 输出和候选清单

`manifest` 输出统一快照 JSON。`diff` 同时输出人类可读 TXT 与机器可读 JSON。追加 `--generate-candidate` 时，还会在输出目录生成：

```text
candidate-global-to-cn.json
candidate-cn-to-global.json
```

候选文件固定包含 `generatedCandidate: true`、`requiresManualReview: true`、`enabled: false` 和空 SHA-256。它们不会写入 `config\transitions`，必须人工缩小差异集合、下载真实目标文件并完成 SHA-256 校验后，才可能转化为正式清单。

## 基础文件与热更新分类

`classify` 对 `Modified`、`Added` 和 `Removed` 输出 TXT、JSON 与 CSV 分类报告。分类结果包含规则编号、置信度、建议动作和理由，分为：

| 分类 | 主要规则 | 建议动作 |
|---|---|---|
| `BaseClient` | EXE、DLL、SYS、原生插件、IL2CPP、Unity player 数据 | 差异包候选 |
| `BaseResource` | `StreamingAssets\Blocks` 或其他 StreamingAssets 资源 | 基础资源或 seed 候选 |
| `RuntimeHotUpdate` | `Persistent\Blocks` | 保留在按服热更新缓存 |
| `StateMetadata` | Persistent / StreamingAssets 顶层 version、revision、`base_version_hash` | 按服状态快照 |
| `NeedsObservation` | 路径证据不足 | 用进程写入归属实测 |

`StreamingAssets\Blocks` 与 `Persistent\Blocks` 被明确分开。分类是可审计规则，不会直接修改正式切换清单；`Removed` 一律标记为需要复核的删除候选。

```powershell
dotnet run --project tools\ZZZSwitch.ManifestTool -- classify `
  --source OS --target CN --version 3.1.0 --output .\manifest-output
```

## 真实文件下载与 SHA-256

`download` 根据目标区服的 manifest chunk 元数据下载一个明确路径，或读取分类报告选择指定类别。第一次不带确认参数运行时只做预检：

```powershell
dotnet run --project tools\ZZZSwitch.ManifestTool -- download `
  --region CN --version 3.1.0 `
  --path "UnityPlayer.dll" `
  --output .\manifest-download\cn
```

确认文件数、未压缩大小和输出位置后，追加：

```text
--accept-download
```

也可以从分类报告下载目标侧的 `BaseClient` 文件；默认类别就是 `BaseClient`：

```powershell
dotnet run --project tools\ZZZSwitch.ManifestTool -- download `
  --region CN --version 3.1.0 `
  --classification-report .\manifest-output\manifest-classification-OS-to-CN-3.1.0.json `
  --include-class BaseClient `
  --output .\manifest-download\cn `
  --accept-download
```

每个 chunk 都执行：压缩长度检查、Zstd 解压长度检查、解压后 MD5 校验，再按 manifest 偏移写入同目录临时文件。全部 chunk 完成后校验完整文件 MD5，成功才原子替换输出文件，并生成包含 SHA-256 的 `download-report-<region>-<version>.json`。

下载安全边界：

- 输出不能是磁盘根目录、真实游戏目录或 `.zzzswitch` 内部。
- 非空目录必须含本工具生成且区服、版本、category、manifest 全部匹配的标记文件。
- 路径逃逸、重解析点、chunk 缺口/重叠、长度或 MD5 不一致都会停止。
- 已存在且完整 MD5 正确的文件会复用并重新计算 SHA-256。
- 不自动写入差异包、游戏目录或 `config\transitions`。

## 主程序测试版在线切换

测试版主程序的 Global ↔ 国服官服切换不再从游戏目录下的 `.zzzswitch\packages` 读取文件。涉及 B 服的四个切换方向仍沿用旧版本地差异包、三服检测、事务备份与回滚逻辑；B 服不加入 Sophon Manifest 下载、浏览或自动差异包清单。点击国际服/国服切换后会：

1. 先按游戏版本查询目标方向和反向自动差异包；两者都是 `Ready` 时跳过 Sophon 分析和下载窗口，直接进入切换前 SHA-256 校验。
2. 缺少任一方向时，按当前游戏版本并行获取来源服与目标服 Sophon manifest，自动计算双向差异并分类。
3. 弹出下载窗口后，先按目标 Manifest 查找可直接复用的本地文件，再按来源 Manifest 校验当前客户端中即将被覆盖的差异文件，并保存为反向差异包。只有长度和 MD5 同时匹配的文件才会进入缓存。
4. 对目标方向仍缺失的文件显示逐文件/逐 chunk 实时网络进度、完整文件复用和分块断点命中状态；失败原因在独立的可换行区域显示。
5. 文件保存到 `%LOCALAPPDATA%\ZZZSwitch\OnlineDifferenceFiles\<version>\<target>\<manifest>\content`。下载器使用共享的最多四路分块并发；每个解压后 MD5 正确的 chunk 先写入同工作区的 `chunks` 断点缓存。取消或网络失败时只删除当前重建临时文件，重试会复用已完成文件、已保存的来源文件和已验证分块。单个 chunk 采用单层最多六次自动重试；完整文件提交后清理其冗余分块缓存。
6. 每个 chunk、完整文件 MD5 和最终 SHA-256 全部通过后，原子生成动态 `TransitionManifest`；目标与反向工作区分别成为对应版本的本地自动差异包。
7. 把目标自动差异包交给原有 `SwitchPlanner` / `SwitchEngine`，继续执行 SHA-256、进程占用、磁盘空间、备份、预复制、事务日志、Blocks 缓存处理和失败回滚。

主页启动检查不会枚举、哈希或要求 `.zzzswitch\packages`，也不再提供“导入差异包”入口。主页按当前游戏版本显示国际服/国服目标包的下载状态和大小；“客户端差异包管理”可查看各版本自动差异包、未完成断点与 Sophon Manifest 元数据。管理窗口打开后默认选中当前版本包，并提供本地刷新、当前版本 CN/OS Manifest 强制下载更新、Manifest 资源浏览、成品差异包清单预览、逐文件 SHA-256 校验、按方向更新差异包、打开目录和二次确认删除。Manifest 浏览器离线读取缓存，先选择全部资源/剧情与视频/音频/Streaming Blocks/状态元数据/客户端差异，再选择国际服→国服或国服→国际服，并可按路径搜索；当前只浏览，不下载选中的资源。Manifest 更新只替换元数据缓存；差异包更新继续使用现有断点与完整文件，成功后清理同版本同方向的旧工作区。程序包本身不预置替换文件。

“初始化当前服缓存”按钮已由“差异包管理”替代。若当前服务器尚无 Blocks 缓存清单，切换计划仍会从活动 `Persistent\Blocks` 自动建立来源身份；切换事务在替换客户端文件前重新盘点、写入来源清单并把当前 Blocks 保存到按游戏目录、版本、服务器隔离的缓存槽。目标服没有缓存时创建空活动目录，进入游戏后由客户端下载；下次切走时同样自动保存。客户端差异包管理与 Blocks 缓存管理保持独立。

自动范围仅包含 `BaseClient`、`StateMetadata` 和非 Blocks 的 `BaseResource`；所有 `Removed` 项都保留为人工复核，不会自动删除。3.1.0 两个方向都是 60 个文件，约 1.04 GiB。`StreamingAssets\Blocks` 在下载窗口单独列出但不纳入：OS→CN 为 2,067 个、9,995,031,492 字节，CN→OS 为 2,070 个、9,999,059,714 字节。`Persistent\Blocks` 继续由现有按服热更新缓存管理。

Bilibili 在可信 Sophon 渠道映射尚未确认前不进入在线差异服务。主程序会在调用 Sophon 前识别任一端为 B 服的方向，并改走旧版本地差异包；因此 Manifest 管理始终只显示国际服与国服官服。

## Bilibili 预留

枚举中保留 `Bilibili`，但当前会明确抛出：

```text
NotSupportedException: Bilibili manifest source has not been identified yet.
```

后续需要通过可信证据确认 API host、`launcher_id`、`game_id`、`plat_app`、`package_id`、是否复用 CN Sophon，以及是否存在额外渠道 manifest。当前不猜测或伪造任何 B 服 Sophon 参数。

## 测试

普通测试不访问真实网络。HTTP 被抽象为 `ISophonTransport`，`getGameBranches`、`getBuild` 和 manifest 下载均通过 fake transport 覆盖；文件下载测试还验证流式网络进度以及“首个分块成功、第二个分块取消、重试仅请求缺失分块”的断点行为。真实网络验证属于手动开发验证，不应默认在 CI 运行。

```powershell
dotnet run --project tests\ZZZSwitch.ManifestTool.Tests\ZZZSwitch.ManifestTool.Tests.csproj -c Release
```
