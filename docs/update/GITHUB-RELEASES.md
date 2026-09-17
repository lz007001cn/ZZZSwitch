# GitHub Releases 发布与客户端约定

正式 endpoint：`https://api.github.com/repos/lz007001cn/ZZZSwitch/releases/latest`。使用公开 API、动态 `ZZZSwitch/{assemblyVersion}` User-Agent 和 GitHub JSON Accept/API-Version；不读取 HTML、不存 Token。20 秒检查总超时，下载 30 分钟总超时、30 秒响应/读超时，接受 CancellationToken。

## 发布资产

- tag：`v{major}.{minor}.{patch}`（正式版，不含 prerelease）。
- ZIP：`ZZZSwitch-win-x64-v{version}.zip`，根目录保持原 UpdatePayload 白名单，包括主 EXE、独立 Updater 和发行 config。
- 不需要独立校验文件。安装哈希来自同一 ZIP 资产的 GitHub API `digest` 字段（`sha256:64位十六进制`），传递给原有下载器和 Updater 验证。
- 不允许重复同名资产、错误版本/架构、Packages ZIP。缺 ZIP 或有效 digest 时仍返回版本/说明，仅自动安装不可用；不会选择数组首项或信任未验证下载。
- `.github/workflows/release.yml` 上传正式 ZIP，GitHub 提供资产 digest。无需生成或上传 `.sha256`。未触发发布。

2026-09-17 实际读取 latest API：v1.3.7，`ZZZSwitch-win-x64-v1.3.7.zip`（154855924 字节），API digest 为 `sha256:f232a7fe4b06a9294bf82ffe854d0644b0c2648e9fbaac1ef7610e931f34d138`。没有 sidecar 不会阻止读取版本或准备安装清单。历史 ZIP 不含 Updater，因此不应用于实际原位升级测试。

打开设置自动静默获取版本信息；当前版本立即展示，获取完成后展示最新版本和更新说明。启动延迟检查与设置使用同一状态，已有结果不重复请求。检查过程中无弹窗、无主操作按钮或下载进度；请求失败仅记日志，允许稍后重试。

本地 runnable-test 的程序集版本形如 `1.3.7-test.<timestamp>+<commit>`。稳定渠道检查只对这类项目测试构建按基础三段版本比较，因此它与正式 `1.3.7` 视为同版本，只有 `1.3.8` 及更高版本才提示更新。其他预发布版本仍遵守 SemVer。设置页仅显示 `1.3.7（测试版）`，完整时间戳和提交哈希保留在程序集与 BUILD-INFO 中。发现新版时，自动重启提示排在 Release Notes 之后。

## 调用边界

`IUpdateSource` → `UpdateRelease`（版本/name/body/date/html_url/可选安装 Manifest）→ `UpdateService`（UpdateVersion 数值比较、下载、size/SHA-256）→ 原 `UpdateHandoff` / Updater / staging / backup / rollback / 健康确认。更新源不负责安装。

GitHub ZIP 初始 URL 必须精确对应仓库/tag/资产名。最多三次显式 HTTPS 默认端口跳转，只允许 GitHub 仓库资产路径及 `release-assets.githubusercontent.com` / `objects.githubusercontent.com`；拒绝降级、其他域名、隐式跳转和循环。Manifest 源继续拒绝跳转。

SHA-256 验证文件完整性，信任边界仍为 HTTPS 发布源，不提供独立发行签名。API 403/404、403/429 限流、坏 JSON、超时和网络异常通过现有 UpdateException 映射并记录日志。SHA/size 失败不能进入交接。

## 自有云接入

提供原 schemaVersion=1 Manifest 和不可变 ZIP，修改 `update-settings.json` 的 Endpoint/Channel 即可复用 `ManifestUpdateSource`。不同协议可新增 `IUpdateSource` 并在 `UpdateService` 构造时注入；UI/下载器/校验器/Updater 无需改写。此次未部署或实现阿里云服务。

## 首次升级限制

历史正式 v1.3.7 不含更新客户端，需要先手动安装包含 Updater 的新版。不要将 runnable-test 的 BUILD-INFO/SHA256SUMS-TEST 等附属文件打入正式更新 ZIP。正式包发布前应运行全套测试和隔离 E2E。
