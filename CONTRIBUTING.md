# 贡献指南

感谢你愿意改进 WinToolbox。下面是保持项目可维护的一些约定。

## 开发环境

- Windows 10/11 x64
- [.NET SDK 10](https://dotnet.microsoft.com/download)
- 可选：Visual Studio 2022 / Rider / VS Code + C# Dev Kit

```powershell
dotnet build -c Release      # 应保持 0 warning / 0 error
dotnet run                   # 直接启动
```

## 代码约定

- **分层**：`Core/` 只放系统能力与工具，**不能引用 `Views/` 或 `MainWindow`**；`Views/` 可以依赖 `Core/`。
- **一个模块一个文件**：新增页面 = `Views/XxxView.xaml(.cs)` + 在 `MainWindow` 的页面表与侧栏里注册。
- **样式走 DynamicResource**：颜色一律用 `Themes/Palette.*.xaml` 里的语义键，不要写死 `#RRGGBB`，否则深色模式会花。
- **UI 线程**：耗时操作（WMI、进程枚举、HID 读取）放在 `Task.Run` 里，回到 UI 线程再更新控件。
- **失败要说话**：捕获异常后给用户一句能看懂的原因，不要静默失败，也不要用假数据填充。
- **不引入新的重型依赖**：UI 手写样式，尽量只用 BCL + `System.Management`。

## 提 PR 前

1. `dotnet build -c Release` 通过（0 warning / 0 error）。
2. 如果是 UI 改动，附一张浅色 + 一张深色截图（可以在 `docs/` 下用离屏渲染工具生成）。
3. PR 描述包含：**改了什么 / 为什么 / 怎么验证**。
4. 不要提交 `publish/`、`dist/`、`bin/`、`obj/`（已在 `.gitignore` 里）。

## 提交信息

采用 `type: subject` 形式，例如：

```
feat: 电量页支持罗技 HID++ 协议
fix: 修复深色模式下清理列表文字对比度不足
docs: 补充 MSI 打包说明
```

## 新增设备电量支持

如果你有一个非雷蛇的设备并希望支持它：

1. 用 `tests/UiCheck` 里的 `--batterytest` 或 USB 抓包工具确认该设备的控制接口与协议。
2. 在 `Core/Battery.cs` 里新增一个 `QueryXxx` 方法，并在 `Scan()` 里按 VID 分发。
3. 在 README 的「已知限制」里更新支持列表。

欢迎附上设备型号、VID/PID 与抓到的报文（注意不要泄露个人信息）。
