# 在线更新实施与验收报告

日期：2026-09-16。正式版本仍为1.3.7；本次没有提交、推送、打标签、发布或部署服务器。

## 修改摘要与新架构

在既有WPF设置中增加应用更新入口。支持可配置endpoint、stable/beta、延迟后台检查、双语更新说明、版本/日期、流式下载进度与速度、取消、SHA-256、明确的重启确认。

`ZZZSwitch.Update`只依赖.NET BCL，包含协议模型、解析/验证、SemVer、IUpdateService/UpdateService、边界校验、更新事务与进程交接。`ZZZSwitch.Updater`是独立self-contained single-file WinExe，不引用主程序或Core；从下载任务目录运行，等待旧进程退出后才安装。

程序包使用精确allowlist，不递归覆盖整个程序目录。先解压到同卷staging、验证版本/内容、复制并验证原文件备份、持久化日志，再逐文件原子替换，主EXE最后替换。正常新版在主窗口构造完成后、游戏初始化前确认；失败回滚，中断后下次启动首先恢复。已提交后清理异常不回滚健康新版。取消交接会停止仍在等待旧进程的本次Updater。

更新窗口复用MainWindowViewModel Busy/Progress和OperationCoordinator，不引入新的游戏事务体系。独立update-settings.json避免更改原UiSettings存储/复制路径。Core目录没有源码差异；游戏识别、切服、Sophon、B服、缓存、游戏备份恢复代码均未修改。

## 修改文件列表

新增：

- `ONLINE-UPDATE-DESIGN.md`
- `docs/update/README.md`
- `docs/update/latest.example.json`
- `docs/update/IMPLEMENTATION-REPORT.md`
- `src/ZZZSwitch.Update/ZZZSwitch.Update.csproj`
- `src/ZZZSwitch.Update/UpdateVersion.cs`
- `src/ZZZSwitch.Update/UpdateManifest.cs`
- `src/ZZZSwitch.Update/UpdateService.cs`
- `src/ZZZSwitch.Update/UpdatePaths.cs`
- `src/ZZZSwitch.Update/UpdatePayload.cs`
- `src/ZZZSwitch.Update/UpdateTransaction.cs`
- `src/ZZZSwitch.Update/UpdateHandoff.cs`
- `src/ZZZSwitch.Updater/ZZZSwitch.Updater.csproj`
- `src/ZZZSwitch.Updater/Program.cs`
- `src/ZZZSwitch/MainWindow.Update.cs`
- `src/ZZZSwitch/UpdateWindow.xaml`
- `src/ZZZSwitch/UpdateWindow.xaml.cs`
- `tests/ZZZSwitch.Update.Tests/ZZZSwitch.Update.Tests.csproj`
- `tests/ZZZSwitch.Update.Tests/Program.cs`
- `tests/ZZZSwitch.Update.Tests/Integration.cs`
- `tests/ZZZSwitch.Ui.Smoke/UpdateUiTests.cs`
- `tools/publish-updater.ps1`
- `tools/test-online-update.ps1`

修改：

- `ZZZSwitch.sln`：新增三个项目。
- `src/ZZZSwitch/ZZZSwitch.csproj`：引用独立更新库。
- `src/ZZZSwitch/App.xaml.cs`：启动恢复/健康确认和更新会话退出保护。
- `src/ZZZSwitch/MainWindow.xaml.cs`：设置入口连接、延迟检查、更新重启确认门控。
- `src/ZZZSwitch/LocalizationManager.cs`：设置入口双语资源。
- `src/ZZZSwitch/SettingsWindow.xaml`、`SettingsWindow.xaml.cs`、`Workflows/SettingsWorkflow.cs`：更新入口。
- `tests/ZZZSwitch.Ui.Smoke/Program.cs`：注册更新UI测试。
- `build-runnable-test.ps1`：新增测试、打包独立Updater、记录验证状态。
- `publish-win-x64.ps1`：打包Updater，拒绝覆盖已有正式输出。
- `.github/workflows/release.yml`：加入更新测试；保留原有发布触发方式。

原先两个README的用户修改及未跟踪的CODEX-HANDOFF保持原状，不属于本次实现。

## 测试结果

最终完整流水线：`_verification/online-update-reviewed-pipeline.log`。

| 项目 | 通过组数 |
|---|---:|
| Core（既有） | 145 |
| ManifestTool（既有） | 26 |
| UI | 21（原20+更新UI1） |
| Demo（既有） | 4 |
| Update（新增） | 38 |
| 合计 | 234 |

Release解决方案构建0警告/0错误；全部既有195组保持通过。新增测试覆盖版本顺序、元数据、HTTP404/500/重定向、超时/取消/断流、大小/哈希、ZIP非法路径/链接/重复/缺文件/损坏、目录junction、缺源、备份失败、独占锁、替换中断、回滚失败后重试、损坏日志/备份、已提交清理中断和互斥。

WPF smoke覆盖双语更新说明与布局、窄窗口滚动、重复检查、取消传递，并继续验证既有主窗口/精简窗口与缩放。查看过`_verification/online-update-ui/update-Chinese.png`；下载按钮对比度问题已修正。开发过程中一次UI测试因测试自身未恢复语言资源导致既有测试失败，修正测试隔离后完整验证通过。

## 已验证端到端流程

最终脚本：`tools/test-online-update.ps1`。
日志：`_verification/online-update-e2e-reviewed.log`。
结果：`_verification/online-update-e2e-20260916-114231/run/results.json`。

1. 构建新增更新能力的隔离1.3.7源与1.3.8-test目标，不修改原正式发布物。
2. 本地HTTP listener提供真实latest.json与ZIP；真实HttpClient比较版本、流式下载、校验。
3. 独立Updater实际启动并握手，父进程退出，独立Updater再次校验并执行文件替换。
4. 启动实际打包的WPF主EXE，通过隔离健康探针确认版本/运行时/WPF资源，提交并清理。Updater退出码0，安装后的主EXE与目标哈希一致。
5. 另一轮使用具有正确产品/版本信息但缺少运行依赖的目标apphost，触发实际重启失败；Updater退出码1，原安装文件逐个哈希恢复，未完成日志清理完毕。
6. 两轮均核对7个隔离用户数据文件（设置、状态、缓存、Packages、备份、日志、游戏模拟文件）和安装目录额外用户文件，内容完全未变。

不访问真实游戏目录，不切服、不操作启动器、不恢复真实备份。

## 当前交付

- `_verification/runnable-test/current/ZZZSwitch.exe`
- 同目录`ZZZSwitch.Updater.exe`、config、BUILD-INFO.json与SHA256SUMS-TEST.txt。
- 测试版：`1.3.7-test.20260916-114335`。
- 主EXE SHA-256：`366DDD4F2621CCBA6F9AA93C02485FF892FB594421A6C3BDA1700B6EB5391D7E`。

不要只复制主EXE；独立Updater和config是便携应用组成部分。

## 未验证内容与已知限制

- 未部署或实测公网HTTPS更新源、CDN、证书运维、签名/密钥轮换。
- 端到端重启使用隔离健康探针；没有用真实用户配置执行真人点击升级验收，也没有验证真实游戏登录/切服。正常更新的主窗口确认路径由代码门控与UI smoke支撑，不能把探针结果声称为完整游戏健康检查。
- 故障注入和文件独占锁覆盖中断、权限式失败与重试；未实际断电、填满磁盘或安装不同杀毒软件重现实机干预。磁盘容量预检存在，但物理掉电持久性不作绝对保证。
- 初次启用必须手动安装带Updater的新构建；已发行的原1.3.7没有这个客户端。测试源的版本数字不意味着旧正式包已能自更新。
- 默认endpoint为空。第一次需填写可信地址，自动检查才会联网。没有静默安装或强制无人值守更新。
- SHA-256保证与manifest一致，不防被攻陷的发布源。信任HTTPS配置地址；没有发行签名。
- 不支持更新到allowlist以外的新文件结构；这类发行应先升级兼容客户端或提高最低版本要求并手动安装。
- 安装目录必须独立、可写、无重解析点；受限目录不自动提权。恢复输入损坏或无法写入时保留证据，需用户解除占用/权限问题或手动处理。
- 成功更新清理包和staging/backup，运行中的Updater副本在下次创建任务时删除；失败/未完成任务保留诊断。清理失败不会删用户文件或伪装为回滚成功。

## 下一阶段服务端最小接口

仅需两个静态HTTPS GET：`latest.json`与其`package.url`的原始ZIP。JSON采用schemaVersion1，包含version/channel/mandatory/minSupportedVersion/publishedAt/双语releaseNotes/package(url,size,sha256)。先上传不可变ZIP，再原子替换manifest，不使用重定向。无需新增客户端业务架构或绑定云厂商。详见README和latest.example.json。
