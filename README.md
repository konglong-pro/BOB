# BOB

BOB 是一个自用的 Android 14 与 Windows 10/11 局域网双向传输工具。第一阶段聚焦文字、图片和文件，通过 HTTPS/WSS 通信，Windows 作为服务端，Android 作为客户端。

## 当前状态

双端聊天界面、连接和流式文件传输已经落地，保留原有紫色配色：

- Windows：接收在左、发送在右的文字/图片/文件会话，拖放与多选，离线文字和文件排队、原子日志恢复、取消与新 ID 从头重试、进度与速度/ETA。关闭窗口隐藏到托盘，托盘的 Exit BOB 才停止服务。收件目录可配置，已验证内容禁止覆盖发布。
- Android：同一聊天布局、多地址 NSD、手动 IP、受限 TOFU 与严格证书 pin、自动重连和文字幂等重放。成功的手动目标跨重启使用原 pin 恢复，主动 Disconnect 保留暂停连接的意愿；原生编辑状态避免快速输入丢字。活动传输使用原生前台服务和有界资源锁；接收先写 pending MediaStore，校验后发布，支持取消与重新选择/从头重试。
- 大文件取消了 Windows 默认约 30 MB 的正文限制，双端使用有界缓冲和 64 位计数，不设文件大小或整体传输时长限制。正文停滞 60 秒会关闭和清理；写完正文后的摘要协调保留 24 小时，重连沿用原 ID。
- Android 14 真机已完成真实 Wi-Fi 双向 2 GiB + 17 字节、完整 SHA-256、已有传输锁屏继续，以及 MediaStore 与日志恢复检查，具体版本和最终复测范围见设备验收记录。
- Android 临时 URI 在进程退出后不保证可读；传输中的正文重启后标记失败，需要重选或从头重试。Android 15+ 的后台 dataSync 受系统累计时限约束。系统分享入口、通知完善、清空历史与正式发布验收仍待完成；拔线深度 Doze 和其他 Android 版本尚未验收。

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

只有 Android 和 Windows 会话头均显示 `Connected`，才表示 HTTPS、证书 pin、WSS hello/welcome 和最终 snapshot 已全部完成。发现、手动 IP、等待、握手和断开原因位于 Connection 面板。

Android 调试 APK 构建在 `src/android/app/build/outputs/apk/debug/app-debug.apk`。连接 Android 14 手机并开启 USB 调试后，可用项目内的 `adb` 安装：

```powershell
.\.tools\android-sdk\platform-tools\adb.exe install -r .\src\android\app\build\outputs\apk\debug\app-debug.apk
```

本轮已连接手机的升级包为 [同签名 APK](artifacts/device-qa/app-debug-device.apk)，已覆盖安装并保留原数据与证书 pin。切换构建账户可能改变默认 Debug 签名，覆盖升级需保持原签名；具体产物摘要和检查记录见 [真机验收记录](docs/active/device-validation-2026-10-07.md)。

## 文档入口

- [当前阶段](docs/active/current.md)
- [本轮优化与验证记录](docs/active/optimization-2026-10-06.md)
- [Android 真机验收与优化补充](docs/active/device-validation-2026-10-07.md)
- [阶段清单](docs/phase-manifest.yaml)
- [BOB v1 主规格](docs/planning/active/bob-v1-spec.md)
- [传输协议](docs/contracts/transport-v1.md)
- [架构决策](docs/adr/0001-lan-topology-and-trust.md)
- [Android 网络客户端决策](docs/adr/0002-android-okhttp-client.md)
