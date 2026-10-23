# 维护与发版指南

这份文档写给「以后的我」：改完代码怎么发版、怎么让已经装过的用户升级、哪些坑不能踩。

---

## 一、日常改动流程

```powershell
# 1. 改代码
# 2. 本地跑一下
dotnet run

# 3. 确认能编译（0 warning / 0 error 是硬要求）
dotnet build -c Release

# 4. 提交
git add -A
git commit -m "feat: 电量页支持罗技 HID++"
git push
```

提交信息用 `type: subject`：

| 前缀 | 用途 |
| --- | --- |
| `feat:` | 新功能 |
| `fix:` | 修 bug |
| `docs:` | 只改文档 |
| `refactor:` | 重构，不改行为 |
| `chore:` | 构建、CI、依赖 |

**不要提交的目录**：`bin/`、`obj/`、`publish/`、`dist/`（已在 `.gitignore` 里，别用 `git add -f` 强塞）。

---

## 二、版本号规则

采用语义化版本 `主版本.次版本.修订号`：

| 变化 | 例子 |
| --- | --- |
| 加了用户能感知的新功能 | `1.0.0 → 1.1.0` |
| 只修 bug / 改样式 | `1.1.0 → 1.1.1` |
| 有破坏性改动（配置格式、安装路径变了） | `1.1.1 → 2.0.0` |

**版本号只有一个源头**：`WinToolbox.csproj` 里的 `<Version>`。
打包脚本会把它同时写进 MSI 与程序集版本，不要手动改 `Package.wxs`。

---

## 三、发版（一条命令）

```powershell
# 示例：发 1.1.0
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0
```

这条命令会：

1. 把 `WinToolbox.csproj` 的版本改成 `1.1.0`
2. 跑 `build.ps1`（自包含发布到 `publish\`）
3. 跑 `installer/build-installer.ps1`（重新生成 `Components.wxs` → 出 MSI 和便携 ZIP）
4. 输出 `dist\` 下的两个产物

然后自己检查一下 `dist\` 里的文件大小是否正常，再发 Release：

```powershell
# 提交版本号变更
git add WinToolbox.csproj
git commit -m "chore: bump version to 1.1.0"
git push

# 打 tag（CI 会自动构建并创建 Release）
git tag v1.1.0
git push origin v1.1.0
```

推到 tag 后，GitHub Actions（`.github/workflows/build.yml`）会自动：
编译 → 自包含发布 → 生成 MSI + ZIP → 创建 Release 并附上这两个文件。

**如果你不想用 CI 自动发**，就手动在 GitHub 网页上新建 Release，把 `dist\` 里两个文件拖进去。

---

## 四、让老用户能升级（最重要）

已安装用户点「新版本 MSI」就能覆盖升级，依赖三件事：

1. **`UpgradeCode` 永远不要改**
   `installer/Package.wxs` 里的 `UpgradeCode="8B1D8C13-..."` 是升级识别码。改了它，新版本会变成「并存安装」，用户机器上会出现两个 WinToolbox。

2. **新版本号必须比旧版本大**
   WiX 的 `MajorUpgrade` 靠 `Version` 判断。如果版本号没升，安装会提示「已安装更新版本」。

3. **不要改安装目录与产品名**
   `INSTALLFOLDER`（`%LOCALAPPDATA%\Programs\WinToolbox`）与 `Package/@Name` 变了会被当成新产品。

用户数据（`%LOCALAPPDATA%\WinToolbox\settings.ini`）不在安装目录里，升级不会丢设置。

---

## 五、改了这些东西要格外小心

| 改动 | 影响 | 处理方式 |
| --- | --- | --- |
| `AssemblyName` / exe 名 | 快捷方式、自启动注册表项、配置目录 | 需要写迁移逻辑，尽量不改 |
| `AppSettings` 的键名 | 老用户设置丢失 | 新增键兼容读旧键，或写迁移 |
| 配置文件路径 | 用户设置「消失」 | 启动时检测旧路径并迁移 |
| 注册表 `HKCU\...\Run` 值名 | 开机自启失效 | 保留原名 `WinToolbox` |
| `tools/` 里的第三方工具 | 打包体积、授权说明 | 同步更新 README 致谢 |
| `docs/` 截图 | README 展示 | 改完界面重新生成（见下） |

### 重新生成截图

```powershell
# 浅色
dotnet run --project tests\UiCheck -c Release -- docs --theme light
# 深色
dotnet run --project tests\UiCheck -c Release -- docs\dark --theme dark
```

> 跑之前先关掉正在运行的 WinToolbox：程序有单实例互斥锁，会打断离屏渲染。

---

## 六、发版前检查清单

- [ ] `dotnet build -c Release` → 0 warning / 0 error
- [ ] 手动跑一遍改动涉及的功能（尤其管理员相关、HID 电量）
- [ ] 更新 `CHANGELOG.md`（写了改了什么）
- [ ] 更新 `README.md` 里受影响的说明与截图
- [ ] `release.ps1 -Version x.y.z` 成功，`dist\` 里 MSI 与 ZIP 大小正常
- [ ] 本机装一次新 MSI（会覆盖旧版），确认能启动、设置还在
- [ ] 打 tag 推送，等 CI 出 Release
- [ ] Release 说明里写清楚「升级方式」与「已知问题」

---

## 七、常见问题

**Q：MSI 装完控制面板里有两个 WinToolbox？**
A：说明 `UpgradeCode` 被改过。改回去，并让用户手动卸载多余的旧版。

**Q：新 MSI 提示「已安装更新版本」？**
A：`<Version>` 没升，或和已装的相同。把版本号 +1 重新打包。

**Q：CI 构建失败在 WiX 步骤？**
A：本地先确认 `wix build` 能过；CI 里需要 `dotnet tool install --global wix --version 5.0.2` 与 `wix extension add -g WixToolset.UI.wixext/5.0.2`（workflow 里已包含）。

**Q：想退回旧版？**
A：安装旧版 MSI 会被拒（版本更低）。先卸载当前版本再装旧版；或把旧版版本号调高重打包（不推荐）。

**Q：Release 附件太大？**
A：MSI 约 48 MB、ZIP 约 58 MB，属正常（含完整 .NET 运行时）。想更小就发 `-FrameworkDependent` 版本，但那要求用户装 .NET 10 Desktop Runtime。
