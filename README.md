# BOB

BOB 是一个自用的 Android 14 与 Windows 10/11 局域网双向传输工具。第一阶段聚焦文字、图片和文件，通过 HTTPS/WSS 通信，Windows 作为服务端，Android 作为客户端。

## 当前状态

连接、文字与前台在线文件切片已经落地：

- Windows：全英文 WPF GUI、共享像素风图标、HTTPS/WSS 服务、原生 DNS-SD、进程内文字，以及图片/文件多选、流式 PUT/GET、SHA-256、`.part`、禁止覆盖发布、图片预览和可持久配置的收件目录。
- Android：全英文 Compose GUI、共享像素风图标、多地址局域网发现、手动 IP、受限 TOFU、严格完整证书 pin、持久文字，以及图片/文件多选、流式 PUT/GET、pending MediaStore 发布和图片预览；连接失败会显示候选地址和安全分类后的网络/TLS/WSS 原因。
- 当前文件传输只承诺双方在线且 Android 位于前台；离线/重启恢复、锁屏前台服务、取消/重试、系统分享入口与 Windows 拖放尚未实现。Android 14 真机的最终连接、双向正文和 MediaStore 验收仍需设备接入。

## 本地工具

仓库使用 `.tools/` 中固定的 .NET 10.0.301、JDK 17、Android SDK 和 Gradle 9.4.1，不要求把这些工具安装到系统路径。Android 构建基于 AGP 9.2.1。

## 常用命令

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-toolchain.ps1 -ToolsRoot .\.tools
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-windows.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows-launcher.ps1
```

首次命令会下载项目内便携工具链并要求接受 Android SDK 许可；已经存在时会复用缓存。

也可用 `-Target Windows` 或 `-Target Android` 只运行一端。

`publish-windows-launcher.ps1` 会构建 Windows Release 版本，并在仓库根目录生成可直接双击的 `BOB.exe`。快捷启动器没有控制台窗口，使用仓库自带的 `.tools/dotnet`，因此需要和完整的 BOB 仓库目录一起保留。普通 `build.ps1` 默认只更新 Debug；准备通过根目录 `BOB.exe` 联调时必须重新运行发布脚本，避免启动旧 Release。

只有 Android 显示 `Connected` 且 Windows 右上角显示 `Phone connected · <手机名>`，才表示 HTTPS、证书 pin、WSS hello/welcome 和最终 snapshot 已全部完成。Windows 等待、握手和断开原因会保留在同一状态框中。

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
