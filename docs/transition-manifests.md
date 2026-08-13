# 切换方向清单

清单位于 `config\transitions`。两个 profile 组成两个有向切换方向，共两个 JSON 文件。

每个清单包含：

- `sourceProfile` / `targetProfile`
- `gameVersion`
- `enabled` 与禁用原因
- `replaceFiles`、`deleteFiles`、`optionalDeleteFiles`
- `expectedReplaceCount`、`expectedDeleteCount`
- `notes`

`replaceFiles` 中每一项包含：

- `source`：相对于目标 profile 差异包目录的路径。
- `target`：相对于游戏根目录的路径。
- `length`：差异文件的精确字节数。
- `sha256`：差异文件内容的 SHA-256。

删除项相对于游戏根目录。路径经过只读检查和执行前预检；差异文件在详细检查、切换预检、临时复制后和最终写入后都会使用清单数据校验。旧清单缺少 `length`/`sha256` 时仍可解析，但会被报告为清单不完整并阻止切换。

更新差异包后，使用以下工具确定性更新或核对清单：

```powershell
python tools\update_package_hashes.py config <packages版本目录>
python tools\update_package_hashes.py config <packages版本目录> --check
```

## 当前 3.1.0 规则

| 方向 | 状态 | 替换 | 必需删除 | 原因 |
|---|---:|---:|---:|---|
| global → cn_official | 启用 | 32 | 0 | 国服包完整文件集 |
| cn_official → global | 启用 | 24 | 0 | 已验证国际服包完整文件集 |

国服包比国际服包多 8 个 `Persistent` 文件，但已确认的当前国际服目录中也存在这些路径，且规格明确把当前 24 个国际服关键文件完整匹配视为国际服。因此没有证据支持把这 8 个运行数据文件设为国际服方向的必需删除项。

## 新增删除规则的要求

只有在能够证明某文件属于来源服且目标服明确不需要时，才加入 `deleteFiles`。必需删除项在操作前不存在会导致预检失败；可不存在的项应进入 `optionalDeleteFiles`。禁止目录递归删除、通配符、绝对路径、`..` 和环境变量。
