# BOB

BOB 是一个自用的 Android 14 与 Windows 10/11 局域网双向传输工具。第一阶段聚焦文字、图片和文件，通过 HTTPS/WSS 通信，Windows 作为服务端，Android 作为客户端。

## 当前状态

连接、文字与前台在线文件切片，以及双端聊天与分类记录界面已经落地：

- 双端保留紫色背景和像素手形，提供 `Chat` 混合时间线，以及独立的 `Text`、`Images`、`Files` 记录视图。更早记录可继续加载；文字、图片与文件历史持久保存，归档损坏会保留原文件并提示。文字可直接选中复制、长文字气泡内展开；图片点击查看，通过右键/长按菜单复制，无常驻 `View`、`Copy` 按钮。
- Windows：全英文 WPF GUI、HTTPS/WSS 服务、原生 DNS-SD、图片/文件多选、流式 PUT/GET、SHA-256、`.part` 与禁止覆盖发布。主窗口顶部仅保留紧凑的 `Choose`、`Open` 收件目录操作；最小化留任务栏，关闭隐藏到托盘。托盘 `Keyboard shortcuts...` 打开独立的快捷键编辑与说明窗口。
- Android：全英文 Compose GUI、多地址局域网发现、手动 IP、受限 TOFU、严格完整证书 pin、图片/文件多选、流式 PUT/GET 与 pending MediaStore 发布。连接失败会显示候选地址和安全分类后的网络/TLS/WSS 原因；普通文件记录可点击交给系统应用打开。
- 本轮根目录构建与检查通过：Windows 25 项检查（含原生托盘/热键生命周期）、Android lint 和 6 项真机归档检查通过。`versionCode = 2` APK 已覆盖升级并保留数据，实机 PC→手机长文字、手机→PC 图片/文件与归档重启保留通过。物理键盘/输入法和跨应用剪贴板兼容性仍需补充验收，详见当前阶段记录。
- 当前文件传输只承诺双方在线且 Android 位于前台。历史归档不等于协议队列恢复：进程重启后中断记录显示失败，离线/跨重启协议恢复、锁屏前台服务、取消/重试、系统分享入口与 Windows 拖放尚未实现。

## Windows 快捷操作

| 快捷键 | 操作 |
| --- | --- |
| `Enter` / `Shift+Enter` | 发送文字 / 换行；输入法候选确认不发送 |
| `Ctrl+Alt+B` | 全局唤出并聚焦输入；已在前台时隐藏到托盘 |
| `Ctrl+L` | 聚焦输入框 |
| `Ctrl+O` / `Ctrl+Shift+O` | 选择文件 / 图片 |
| `Ctrl+Shift+R` / `Ctrl+Shift+E` | 选择 / 打开收件目录 |
| `F1` | 打开独立快捷键说明与全局唤出键设置窗口 |
| `Ctrl+C` | 复制选中文字；图片聚焦或预览时复制完整图片 |
| `Esc` | 关闭预览；主界面中隐藏到托盘 |

全局快捷键可从托盘 `Keyboard shortcuts...` 修改，冲突时会提示。托盘 `Exit BOB` 才真正退出并停止网络服务；隐藏和最小化不停止传输，不提供开机自启。

## 本地工具

仓库使用 `.tools/` 中固定的 .NET 10.0.301、JDK 17、Android SDK 和 Gradle 9.4.1，不要求把这些工具安装到系统路径。Android 构建基于 AGP 9.2.1。

## 常用命令

在仓库根目录运行：

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-toolchain.ps1 -ToolsRoot .\.tools
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-windows.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows-launcher.ps1
```

首次命令会下载项目内便携工具链并要求接受 Android SDK 许可；已经存在时会复用缓存。

也可用 `-Target Windows` 或 `-Target Android` 只运行一端。

`publish-windows-launcher.ps1` 会构建 Windows Release 版本，并在仓库根目录生成可直接双击的 `BOB.exe`。快捷启动器没有控制台窗口，使用仓库自带的 `.tools/dotnet`，因此需要和完整的 BOB 仓库目录一起保留。普通 `build.ps1` 默认只更新 Debug；准备通过根目录 `BOB.exe` 联调时必须重新运行发布脚本，避免启动旧 Release。

只有 Android 显示 `Connected` 且 Windows 顶部显示 `Phone connected · <手机名>`，才表示 HTTPS、证书 pin、WSS hello/welcome 和最终 snapshot 已全部完成。Windows 等待、握手和断开原因保留在连接状态中。

Android 调试 APK 构建在 `src/android/app/build/outputs/apk/debug/app-debug.apk`。连接 Android 14 手机并开启 USB 调试后，可用项目内的 `adb` 安装：

```powershell
.\.tools\android-sdk\platform-tools\adb.exe install -r .\src\android\app\build\outputs\apk\debug\app-debug.apk
```

## 文档入口

- [当前阶段](docs/active/current.md)
- [阶段清单](docs/phase-manifest.yaml)
- [BOB v1 主规格](docs/planning/active/bob-v1-spec.md)
- [传输协议](docs/contracts/transport-v1.md)
- [架构决策](docs/adr/0001-lan-topology-and-trust.md)
- [Android 网络客户端决策](docs/adr/0002-android-okhttp-client.md)
