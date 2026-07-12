# BOB

BOB 是一个自用的 Android 14 与 Windows 10/11 局域网双向传输工具。第一阶段聚焦文字、图片和文件，通过 HTTPS/WSS 通信，Windows 作为服务端，Android 作为客户端。

## 当前状态

连接与文字切片已经落地：

- Windows：全英文 WPF GUI、共享像素风图标、HTTPS/WSS 服务、`/bob/v1/info`、会话握手、进程内文字收发，以及原生 Windows DNS-SD 广播。
- Android：全英文 Compose GUI、共享像素风图标、局域网发现、手动 IP、受限 TOFU、严格完整证书 pin、HTTPS `/info`、`bob.v1` WSS 会话和持久双向文字时间线。
- 图片和文件的 HTTPS 正文流尚未实现；Android 14 真机与真实 Windows 的最终连接验收仍需在设备接入后执行。

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

`publish-windows-launcher.ps1` 会构建 Windows Release 版本，并在仓库根目录生成可直接双击的 `BOB.exe`。快捷启动器没有控制台窗口，使用仓库自带的 `.tools/dotnet`，因此需要和完整的 BOB 仓库目录一起保留。

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
