# 设计说明

## 分层

- `ZZZSwitch`：WPF 界面、只读报告、切换预览、用户确认、进度和备份管理。
- `ZZZSwitch.Core`：配置、识别、路径安全、预检、备份、切换、回滚、状态与日志。
- `ZZZSwitch.Core.Tests`：无第三方测试框架的控制台测试器，只使用系统临时目录和模拟文件。

核心库不依赖 WPF，可以独立测试。文件操作和进程检测通过接口注入，以便故障注入测试不会触碰真实游戏目录。

主窗口采用渐进式 MVVM：`MainWindowViewModel` 管理路径、扫描摘要、报告、忙碌状态、交互可用性和界面 Command，XAML 不再直接绑定主窗口 `Click` 事件；`RelayCommand` / `AsyncRelayCommand` 统一 `CanExecute`、重复执行保护和异常转交。`InspectionPresentationBuilder` 将核心扫描结果转换为只读 UI 摘要和详细报告；`ServerSwitchWorkflow`、`CacheManagementWorkflow`、`BackupManagementWorkflow` 分别编排切换、缓存与备份目录操作，并通过 `MainWindowWorkflowContext` 回调窗口状态、进度和目录打开行为。核心 `SwitchPlanner`、`SwitchEngine`、恢复策略、跨进程互斥与回滚顺序保持不变。重复的三服入口由 `ServerSwitchCard` 统一图标、标题、说明与交互样式，客户端/版本/差异包/缓存区域由独立的 `InspectionSummaryCard` 承载。

`MainWindowDialogCoordinator` 通过 `IMainWindowDialogs` 统一消息窗、游戏目录候选、系统文件夹选择、切换确认、缓存管理和备份窗口的 Owner/取消语义，也让工作流测试可替换交互边界；`StartupWorkflow` 仅编排既有的未完成事务恢复、状态警告传递和备份轮换。实际恢复仍由核心 `PendingTransactionRecoveryService` 执行，最后备份保护路径仍来自重新加载的 `state.json`，轮换失败继续采用不阻断启动的 best-effort 规则。

## 识别策略

1. 读取 `state.json` 作为提示，但不以其覆盖物理文件结果。
2. 对每个启用 profile 的少量稳定关键文件检查存在性与精确大小。`version` / `revision` 热更新元数据不作为固定大小签名，避免有效缓存快照造成误判。
3. 单个 profile 全部匹配时识别为该服；多个 profile 同时完整匹配时识别为混合状态。
4. 没有完整匹配但多个 profile 均出现部分证据时识别为混合状态，否则为未知状态。
5. 配置模型保留可选 SHA-256 字段，可针对少量关键文件启用，不要求全目录哈希。

当前关键文件大小来自 2026-07-30 对版本 3.1.0 差异包的只读盘点。

## 事务边界

切换顺序：预检 → 保存来源服元数据快照 → 创建外部事务备份 → 应用数据目录临时预复制 → 将活动 Blocks 移入来源服仓库 → 恢复目标服 Blocks（或创建首次初始化目录）→ 覆盖 → 删除 → 恢复有效目标服元数据快照 → 数量/哈希/状态校验 → 写审计日志 → 最后提交 `state.json`。缓存位于另一磁盘时，“移动”会展开为目标卷临时复制、清单校验、同卷原子落位、最后删除源目录。

任何提交状态之前的错误都会进入回滚。进度回调异常不会影响事务。状态写入是最后一个可能影响提交语义的步骤。

应用崩溃不是数据库事务，无法保证断电级原子性；但每次操作均留下独立备份和 `backup.json`，可从界面选择恢复。

事务备份默认写入 `%LOCALAPPDATA%\ZZZSwitch\Backups`。用户可从主界面“备份目录”迁移全部备份；实现先复制到目标暂存目录并逐文件校验 SHA-256，再原子提交 `backup-location.json`，最后清理旧目录。“恢复上次状态”属于备份历史窗口，并继续使用 `state.json` 中的精确操作关联。

## 按服缓存快照

快照只枚举以下两个目录的一级普通文件：

- `ZenlessZoneZero_Data\Persistent`
- `ZenlessZoneZero_Data\StreamingAssets`

文件名必须包含 `version` 或 `revision`（不区分大小写）。不递归，不复制 `Blocks` 等目录。每个文件记录长度和 SHA-256；读取目标快照时还会验证 profile、游戏版本、原游戏路径、路径边界、文件长度和哈希。损坏快照自动跳过并尝试较早的有效快照。

2026-07-22 的真实国际服只读盘点会纳入 20 个一级文件，共 4,471,666 字节。

## 资源服 Blocks 缓存

`ZenlessZoneZero_Data\Persistent\Blocks` 不再纳入小型元数据快照，而由 `HotUpdateCacheService` 单独管理：

- 初始化只登记当前活动 Blocks 的文件名、长度和总体清单 SHA-256，不移动游戏文件。
- 默认位置使用统一的 `.zzzswitch` 根目录；`cache` 按规范化游戏路径的身份哈希隔离每份安装并保存大体积 Blocks 仓库，`packages` 保存按版本分组的只读差异包。用户可以逐安装迁移 `cache` 根目录；同卷使用目录移动，跨卷使用可恢复的校验复制。
- 切走时重新生成来源服清单并移动整个 Blocks 目录；切入时恢复目标服目录。
- B服是国服资源上的登录组件叠加层，统一映射到 `cn_official`，不创建第三套 Blocks 或元数据快照。
- 原资源包中的“国服/国际服热更新存储链接”只作为首次初始化种子，只读复制，不回写。
- `Persistent\Video` 完全不在工具管理范围内：不保存、不交换、不校验、不复制、不删除。
- 首次目标服缓存不存在时进入初始化模式；启动游戏完成下载并退出后，由用户点击“初始化当前服缓存”。
- 每次交换写入应用私有事务日志。普通文件替换失败时，Blocks 与普通文件共同回滚。
- 事务日志在首次移动前已经落盘；恢复逻辑会区分“尚未移动”“跨卷复制形成安全副本”“目标已激活”和“回滚已完成”状态，避免删除唯一缓存。
