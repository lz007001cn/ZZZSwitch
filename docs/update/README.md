# 应用在线更新

设置 → 日志下方的独立「更新」卡片。默认使用 GitHub Releases latest API，版本来自程序集，打开设置立即静默获取版本与说明（无需校验文件），后台延迟 20 秒检查一次，失败仅记日志，不弹窗、不自动下载。用户点击「安装更新」后下载、校验并交接原有 Updater，随后退出重启。卡片内显示状态/进度/有限高度更新说明；关闭时可取消未完成操作。

正式源和资产规范见 [GITHUB-RELEASES.md](GITHUB-RELEASES.md)。配置仍保存在 `%LOCALAPPDATA%\ZZZSwitch\update-settings.json`，保留 AutomaticCheck 和自定义 Manifest endpoint；空 endpoint 迁移为 GitHub stable。高级源配置不放入日常设置卡片。

## 最小服务端接口

1. HTTPS GET latest.json，200返回 `latest.example.json` 同样结构，UTF-8，最大256 KiB；未知字段兼容，schemaVersion必须为1。
2. package.url 返回原始ZIP字节，200；若有Content-Length，必须与实际文件和manifest的size一致。SHA-256是完整ZIP的十六进制哈希。服务器不要重定向；先上传不可变ZIP，最后原子更新latest.json。
3. stable接受三段正式版本；beta也接受SemVer预发布，例如1.3.8-test。当前版本不低于远端时不更新；低于minSupportedVersion时要求手动升级。mandatory只强调建议，不强制安装。

保留的 Manifest 源无需身份认证、专用 SDK 或 ASP.NET；正式 GitHub 源使用公开 API，无需 Token。可使用Nginx加静态文件/对象存储。生产必须HTTPS；仅loopback允许HTTP做本地验证。SHA-256不替代发行签名，首版信任配置的HTTPS发布源，不具备离线签名/密钥轮换。

## 包格式与数据边界

ZIP根目录必须是便携发布目录的内容（不可多套一层文件夹）：两个self-contained单文件EXE和九个发行config JSON，README.md与ManifestTool runtimeconfig为可选文件。精确清单在UpdatePayload.cs。禁止自定义脚本、未知文件、删除指令、链接和额外用户数据。新增发行文件需要兼容客户端先扩展清单，或通过最低版本要求引导手动升级。

`publish-win-x64.ps1`及`build-runnable-test.ps1`都会打包独立Updater。不要把包含BUILD-INFO/SHA256SUMS等测试附属文件的整个测试目录直接作为正式更新ZIP。正式发布脚本拒绝覆盖已有版本输出。

用户ui-settings、state、更新偏好、游戏、Packages、日志和备份不在程序文件清单。安装目录若与已配置的用户存储/游戏目录重叠，则拒绝原位更新。发行config不是用户设置，不应在这些JSON里存用户偏好。

## 故障恢复

更新下载：Updates/jobs/随机ID/package.part，校验后package.zip。安装：应用目录/.zzzswitch-update保存staging、backup、journal。每个程序文件同卷原子替换，主EXE最后替换；未知文件不删。正常新进程在主窗口构造完成、游戏初始化之前确认版本并等待提交；这不是完整游戏功能健康探测。隔离探针更早确认，仅验证运行时/WPF资源与版本。

替换或启动失败会回滚。回滚输入损坏、锁定或权限不足时保留日志和副本；修复占用/权限后重新启动主程序，会在任何游戏服务初始化之前调用独立Updater恢复。不要删除未完成的`.zzzswitch-update`。Updater错误窗口显示原因，日志为Updates/update.log。不要通过启动器修复游戏来处理软件自身更新失败。

已提交更新清理失败不回滚健康新版。成功任务清除下载包和安装暂存/备份，独立Updater自身在下次创建更新任务时清理；未完成/失败任务保留作诊断。无管理员提升、不杀旧主程序；等待退出超时则停止。只在启动确认失败时终止本次Updater自己启动的新子进程，再恢复文件。

不能保证多文件硬件级原子性、恶意同用户并发替换路径防御或完全掉电持久性；通过日志、已校验备份和启动门控降低风险。无静默安装、增量更新或服务器部署。

## 本地验收

`tools/test-online-update.ps1`构建隔离的1.3.7源与1.3.8-test目标，启动loopback HTTP服务，执行真实检查/下载/哈希/独立Updater/PID退出/替换/真实WPF EXE重启探针；再使用不可启动的目标apphost验证真实启动失败与回滚。每轮使用新的_verification目录。

健康探针仅处理更新启动握手，在读取真实用户设置和初始化游戏服务前退出。普通更新使用同样握手，成功后正常启动。UI布局、取消/重复点击由WPF smoke独立验证；完整真人点击和真实游戏操作不由此探针证明。

首次部署必须手动安装带Updater的新构建：已发行1.3.7本身没有更新客户端，不可能通过服务器JSON自动获得本功能。示例manifest不是已发布1.3.8的声明。
