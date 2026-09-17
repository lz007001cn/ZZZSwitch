# ZZZSwitch 在线更新设计

状态：2026-09-16，先审查设计，再实施；正式版本保持 1.3.7。不发布服务器、版本、提交或标签。实施结果和验证记录见 docs/update/IMPLEMENTATION-REPORT.md。

## 1. 当前架构

- .NET SDK 8.0.201；WPF 主项目、Core、ManifestTool、演示工具和四个控制台测试项目。MainWindow 直接组合服务，无 DI 容器。
- App.OnStartup 初始化语言/主题、命名互斥和 application.lock；StartupUri 创建主窗口，Loaded 执行初始化/恢复/识别；关闭默认进托盘，RequestExit 显式退出。
- csproj Version 为 1.3.7；UI 使用 AssemblyInformationalVersion。UiSettingsService 在 AppPaths.DataRoot 原子保存 ui-settings.json。
- MainWindowViewModel 统一 Busy/Progress/Result；OperationCoordinator 持有 operation.lock 并拒绝未完成游戏事务。
- OperationLogger 属于游戏存储体系。应用更新日志独立保存在控制数据根 Updates，不触发游戏存储初始化或迁移。
- Core 的 VerifiedFileTransfer、PathSafety、OrdinaryPathGuard 与游戏文件操作耦合。更新模块采用同一 BCL SHA256/同卷替换原则，不引用 Core，不修改其工具或事务。
- 正式与测试脚本发布 win-x64 self-contained single-file；现有发布物仍含九个 config JSON、README、ManifestTool runtimeconfig sidecar。这些 config 是发行配置，不是用户 ui-settings/state。

## 2. 更新架构

独立 BCL 项目 ZZZSwitch.Update：版本、协议模型/解析/验证、HTTP 服务、包边界和更新事务。主程序与独立 ZZZSwitch.Updater 引用该库；Updater 不引用主程序或 Core，发布为自包含单文件，复制到 Updates/jobs/<随机ID> 后运行。

WPF 增加一个更新窗口与 MainWindow 更新适配 partial；设置提供检查入口、自动检查开关、endpoint 与 channel。更新配置使用独立 update-settings.json，避免已有 UiSettings 的复制路径丢失新字段。endpoint 默认空，手动检查说明未配置；正式仅 HTTPS，本地测试允许 HTTP loopback，禁止降级/不受控重定向。

检查期间不占游戏操作锁，检测成功后只在主界面空闲时提示；窗口会话使用共享 Busy 与 OperationCoordinator 阻止并发切服、重复更新和未完成事务。退出在更新下载/交接期间受到保护；取消等待流关闭后清理。后台仅启动成功后延迟一次，失败写日志。

## 3. Manifest schema v1

必须字段：schemaVersion=1、version、channel(stable/beta)、mandatory(bool)、minSupportedVersion、publishedAt(有时区 ISO8601)、releaseNotes(语言到文本字典)、package(url/size/sha256)。未知字段忽略，已知重复字段拒绝。manifest 最大 256 KiB；包 1..1 GiB；解压总量最多 2 GiB，文件数严格限制。

版本为 SemVer 三段非负整数及可选 prerelease/build；数值比较主次修订，稳定版高于同核心预发布；预发布按 SemVer 标识符规则。stable 不接受预发布；beta 可接受预发布/正式版。当前>=远端返回无更新；低于 minSupportedVersion 提示手动升级，不执行不兼容的原位更新；mandatory 仅强调推荐，不强制安装或阻断切服。

示例见 docs/update/latest.example.json。SHA-256 来自 manifest；HTTPS 保护来源，不声称哈希代替数字签名。

## 4. 客户端状态机

Idle → Checking → UpToDate / Available / ManualUpgradeRequired / Failed。
Available → 用户确认 → Downloading → Verifying → Ready → 用户确认重启 → Handoff → Exit。
Downloading/Verifying → Cancelled / Failed，清理本次随机任务的临时包。

IUpdateService 提供 CheckForUpdatesAsync / DownloadUpdateAsync / VerifyPackageAsync。静态复用 HttpClient，禁止自动重定向；检查超时 20 秒、下载总时限 30 分钟且每次读 30 秒超时。CancellationToken 全链传递；HTTP/网络/超时/JSON/协议/大小/哈希/磁盘错误有独立分类。流式下载 .part，校验后重命名 package.zip，服务端 filename 不参与路径。

## 5. Updater 状态机

接收固定 job 路径，校验请求、PID+启动时间+EXE路径 → Ready 握手 → 等主程序退出（不杀主程序） → 获取安装目录更新锁 → 再校验包 → Staging → Prepared → Applying → AwaitingHealth → Committed。

任何 Applying/AwaitingHealth 失败 → RollingBack → RolledBack；回滚失败 → RecoveryRequired，保留备份和日志。重启主程序携带随机更新会话标识，完成基础 UI 初始化后确认；超时则先确保所启动进程已退出再回滚，不在活动新版进程下写文件。

启动最早阶段（在 Theme、AppPaths、主窗口之前）检查本安装 .zzzswitch-update/journal.json；未完成日志交给独立 updater 恢复，禁止进入游戏工作流。恢复命令可手动重试。损坏日志失败关闭，不猜测路径。已提交日志允许幂等清理。

## 6. 目录与边界

- %LOCALAPPDATA%/ZZZSwitch/update-settings.json：用户更新偏好，不打包。
- %LOCALAPPDATA%/ZZZSwitch/Updates/jobs/<guid>：下载、请求、握手、独立 updater；未结束任务保留供诊断，成功后清理包，下一次维护可清理结束任务（运行中的 updater 本身不强删）。
- <app>/.zzzswitch-update：同卷 staging、backup、journal、锁；只处理自身明确记录的文件。
- 严格精确 allowlist：ZZZSwitch.exe、ZZZSwitch.Updater.exe、九个现有 config JSON；可选 README.md 与 ManifestTool runtimeconfig。所有必需文件均存在；拒绝未知 ZIP 文件、重复/大小写碰撞、目录穿越、绝对/ADS/设备路径、符号链接、重解析点和不完整文件集。
- 不删除未知旧文件，不替换整个安装目录。用户数据、日志、Packages、备份和游戏文件永不进入 allowlist。若安装目录与已知用户存储/游戏目录重叠则拒绝原位更新，建议另解压到独立目录。

## 7. 事务与失败恢复

先解压/验证完整 staging，再复制现有 allowlist 文件到 backup 并校验，持久化含旧/新哈希的 Prepared 日志之后才能写正式文件。每个文件先准备同卷副本，再 File.Replace（新增则 Move）；EXE 最后替换，保证旧/新主 EXE 始终有一个存在。日志原子写入并 Flush(true)。旧文件不先删除。

Applying 的崩溃恢复不依赖逐文件完成计数：对全体记录幂等恢复原文件，原本缺失的新增文件仅当匹配本次新哈希才删除。备份全部验证后才恢复；异常保留证据。安装器有有限 IO 重试与磁盘容量预检；备份或权限不足发生于正式写入前则旧文件保持原状。成功健康确认后先提交日志，再清理 staging/backup；清理失败不反转已提交结果。恢复后提示原位更新失败，可再次检查。

不承诺跨多文件硬件级原子性；系统掉电可能保留日志，必须恢复再继续。对恶意同用户并发改路径不提供句柄级沙箱保证；禁止提升权限。

## 8. 安全模型

服务端 endpoint 属于用户信任配置；下载地址须 HTTPS（本地 loopback 测试例外），不执行 releaseNotes，不相信 ZIP 路径。所有入口包括恢复都重新验证根目录、相对路径、重解析点、长度/哈希。主进程身份与握手绑定，Updater 不接受任意待执行程序名/额外命令行；重启仅固定 ZZZSwitch.exe。

SHA256 防传输损坏但不抵御被攻陷的 manifest 来源；签名与密钥轮换是后续可增补协议，第一版不能宣称签名更新。server 可自由实现 JSON，客户端不依赖 GitHub/云厂商 SDK。

## 9. 预计文件

新增 src/ZZZSwitch.Update/*、src/ZZZSwitch.Updater/*、tests/ZZZSwitch.Update.Tests/*、MainWindow.Update.cs、UpdateWindow.xaml(.cs)、更新配置窗口、文档/样例/本地端到端验证脚本。
修改 solution、主项目引用、App 更新启动恢复及退出门控、Loaded 延迟检查、SettingsWindow/SettingsWorkflow 检查入口、publish/test/release 脚本和 UI smoke。Core 与既有游戏功能源码不变。

## 10. 测试计划

版本比较/非法版本；JSON缺失、未知字段/schema、URL/hash/size；HTTP成功、404/500、超时/取消/中断/超长与短体；SHA匹配；ZIP边界/重复/链接/不完整；安装正常、占用、缺源、备份失败、注入替换中断、回滚、重复恢复、日志损坏与越界；用户数据字节不变。
原195组全部运行；新增独立 Update 测试加入正式/测试流水线。测试只操作临时夹具。
端到端使用本地 HTTP、隔离的自包含 1.3.7 与 1.3.8-test、真实 updater 进程与重启确认。专门健康探针模式在初始化游戏服务前退出，用于验证真实打包 EXE 版本/启动链而不接触真实用户或游戏；完整交互 UI 另由 WPF smoke 验证，报告不得混淆两者。

## 11. 自查结论与影响范围

已检查：运行中 updater 不自覆盖；无递归覆盖安装目录；新 EXE 不先删旧 EXE；崩溃日志先于正式写入；后台失败不干扰主功能；不将更新临时数据放游戏存储；默认 endpoint 不臆造。原正式包没有 updater，已发行 1.3.7 不能凭空获得本功能，用户需先手动安装包含更新器的新构建。本次 v1.3.7→1.3.8-test 演示源是加上更新能力的隔离测试构建，不修改既有正式发布物。

设计通过本地自查，按此范围实施。服务初始化/核心目录重构、游戏事务修改、静默安装、增量包、服务器部署均不在范围内。

实施复查：正常重启的健康确认放在 MainWindow.Loaded、游戏初始化之前，以验证主窗口已构造成功；隔离健康探针只验证运行时、WPF资源、版本与更新提交，不接触真实用户设置。取消交接会结束本次自己启动且仍在等待旧主进程的Updater，避免仅依赖可能写失败的abort标记。未存在更新日志时，普通启动不做更新路径资格检查，避免更新目录限制影响原有便携使用方式。已提交任务的Updater副本在下次更新创建任务时清理。
