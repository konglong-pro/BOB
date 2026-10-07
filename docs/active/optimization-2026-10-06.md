# 2026-10-06 项目优化交付记录

状态：本轮实现与本机自动化验证已完成。真实 Wi-Fi 与 Android 设备验收待补。

后续已连接 Android 14 手机，真实 Wi-Fi、大文件、锁屏、恢复与取消修复的结果见 [2026-10-07 真机验收补充](device-validation-2026-10-07.md)。本文保留 10 月 6 日交付时的验证范围。

## 已实现

- 保留原有紫色配色；Android 与 Windows 均以接收左、发送右的文字、图片、文件气泡组成会话。连接设置移到独立面板，输入区固定，翻阅旧记录时不强制滚动；长文字保留完整查看与复制。
- Windows 增加文件拖放、离线文字/文件队列、取消、新 ID 从头重试和原生托盘；关闭隐藏，Open BOB 恢复，Exit BOB 停止服务。
- Windows 接收解除默认正文大小与最低速率限制；双方使用有界缓冲、64 位字节计数、增量 SHA-256 和 60 秒无进展超时，没有文件整体传输时长上限。
- 双端原子文件日志保存队列、摘要、发布意图与未确认终态；正文中断不恢复偏移，重试从头开始。写完正文后的摘要协调保留 24 小时；重连分页快照与同 ID 重放恢复确认。
- 修复摘要与正文跨通道先后顺序、丢失摘要/终态确认、同批单项失败、取消与完成/失败的竞态。双方独立失败时保留各自原始错误，并幂等确认对端错误。
- 文字先持久化再确认；存储故障中止当前会话，保留原 ID 待发项。坏日志保留原件，全部记录校验成功后才允许恢复清理。
- Android 发布、删除仅操作本应用在 BOB 目录的 pending MediaStore 对象；发布后的崩溃恢复重新核对完整性，已发布文件不被作为临时项删除。
- Android 使用原生 dataSync 前台服务维持活动传输，工作器独立于页面生命周期。闲时后台断开；已启动批次的队列在短暂重连期间继续运行，受系统后台预算约束。

## 验证

| 实际运行 | 结果 |
| --- | --- |
| 根目录 `pwsh.exe -File scripts/build.ps1`，默认 All | 通过；Windows Debug 0 警告/0 错误，Android 最新 `assembleDebug` 成功 |
| 根目录 `pwsh.exe -File scripts/check.ps1`，默认 All | 通过；Windows 43/43，Android 15/15 组 streamingChecks 与 lint 成功 |
| `publish-windows-launcher.ps1` | 通过；最终 Windows Release 0 警告/0 错误，根目录 BOB.exe 已更新 |
| 原生 HTTPS/WSS + 正确/错误证书 pin | 通过；唯一命名 CNG 测试 key 在正常/异常路径精确清理，生产私钥策略不变 |
| 真实 Kestrel 64 MiB PUT 与 GET | 通过；双向 SHA-256、状态与落盘闭环 |
| 真实 Kestrel 1 GiB chunked PUT | 通过；完整校验与发布，本次峰值工作集增量 5,550,080 B（约 5.3 MiB） |
| Windows 超过 2 GiB 的 sparse 源文件/HTTP 长度 | 通过；64 位长度及中断后的句柄释放与清理 |
| Android 2 GiB + 17 字节合成流 | 通过；Java 128 MB 堆，有界缓冲、64 位计数和独立 SHA 核对 |
| Windows UI 与托盘检查 | 通过；1080×800、760×600、有界长文字展开、关闭隐藏/打开恢复/退出清理 |
| APK 签名校验 | 通过；与 `E:\BOB` 现有 Debug APK 签名一致，未更换签名 key |
| `git diff --check` | 通过；无空白错误 |

文件流的负载检查使用本机 HTTP Kestrel 测试宿主，TLS/pin 使用单独的真实 HTTPS/WSS 检查；不据此声明手机网络吞吐量。Windows 故障回归还覆盖分页快照、离线文字、队列跨重启、丢失 digest/terminalAck、提前 digest、单项失败推进 FIFO、双方同时失败/取消、取消遇已存在失败、错误消息不回声和坏日志清理保护。

Android 15 组检查由 `streamingChecks` 执行并接入 `check`，包含空内容/未知长度、大小不符、取消、写失败、异常零字节读取、Unicode 文件名、24 小时摘要窗口、日志不变式、自有 MediaStore URI 规则、终态一致性与后台 60 秒窗口。当前没有 JUnit 测试；只有本纯 main harness 时允许 Gradle 的零 JUnit 发现结果，增加其他测试源会恢复严格检查。

构建与检查原始记录：[build.log](../../artifacts/optimization/build.log)、[check.log](../../artifacts/optimization/check.log)、[publish.log](../../artifacts/optimization/publish.log)。产物哈希：[build-output-hashes.json](../../artifacts/optimization/build-output-hashes.json)。

Windows UI 已实际渲染 1080×800 与 760×600，并检查有界长文字展开和托盘关闭、打开、退出。截图包含模拟消息与进度，用于布局验收。

[桌面截图](../../artifacts/ui-qa/chat-desktop.png) · [紧凑窗口截图](../../artifacts/ui-qa/chat-compact.png)

## 持久化与兼容性

Windows 文件日志为用户 BOB 数据目录内的 `file-transfers-v1.json`，Android 为应用私有目录的 `transfers.v1.json`，均使用版本 1 原子快照。正文不进日志；记录源路径/URI、元数据、计数与摘要、发布意图、取消待回应、终态待确认、本地失败来源与对端原始失败。恢复先完成全部校验再清理，不能把目录占用或读失败当空历史。已有文字记录格式不变。

协议 envelope、字段与端点保持 v1。明确了独立失败错误的幂等、尚待回应的取消请求、双方同时取消的接收端时间戳规则，以及 failed 不被迟到 completed 改写；见 [传输契约](../contracts/transport-v1.md)。

## 产物

- Windows：[BOB.exe](../../BOB.exe)，使用本工作区最新 Release 与 `.tools/dotnet`，须保留仓库目录。
- Android：[app-debug.apk](../../src/android/app/build/outputs/apk/debug/app-debug.apk)。本轮未安装到设备。

## 验证边界

本轮 ADB 未发现连接的 Android 设备。真实 Wi-Fi 双向大文件、MediaStore 发布/恢复、锁屏、省电策略及 Android 后台服务时限仍需真机验收；编译、lint 和 JVM 流测试不能代替这些检查。

Android 临时 URI 在进程退出后不保证可读；需要重新选择或从头重试。Android 15+ 的后台 dataSync 在每 24 小时累计 6 小时后受系统限制，返回前台可继续已保存的协调。传输未增加断点续传、文件夹、绑定、账号或自动启动。

系统分享入口、清空历史和通知完善仍是原有后续范围。没有新增生产依赖；没有修改生产证书的持久私钥策略。

## 修改文件

- `docs/active/current.md`
- `docs/active/optimization-2026-10-06.md`
- `docs/contracts/transport-v1.md`
- `docs/planning/active/bob-v1-spec.md`
- `README.md`
- `scripts/build.ps1`
- `scripts/check.ps1`
- `src/android/app/build.gradle.kts`
- `src/android/app/src/main/AndroidManifest.xml`
- `src/android/app/src/main/java/com/bob/android/application/BobConnectionCoordinator.kt`
- `src/android/app/src/main/java/com/bob/android/background/BobTransferRuntime.kt`
- `src/android/app/src/main/java/com/bob/android/background/QueuedReconnectPolicy.kt`
- `src/android/app/src/main/java/com/bob/android/background/TransferForegroundService.kt`
- `src/android/app/src/main/java/com/bob/android/MainActivity.kt`
- `src/android/app/src/main/java/com/bob/android/network/BobProtocol.kt`
- `src/android/app/src/main/java/com/bob/android/network/BobWebSocketSession.kt`
- `src/android/app/src/main/java/com/bob/android/persistence/AtomicTextMessageStore.kt`
- `src/android/app/src/main/java/com/bob/android/persistence/AtomicTransferJournal.kt`
- `src/android/app/src/main/java/com/bob/android/transfer/OnlineTransferCoordinator.kt`
- `src/android/app/src/main/java/com/bob/android/transfer/TransferFileNames.kt`
- `src/android/app/src/main/java/com/bob/android/transfer/TransferJournalRules.kt`
- `src/android/app/src/main/java/com/bob/android/transfer/TransferStreams.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobScreen.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobUiState.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobViewModel.kt`
- `src/android/app/src/test/java/com/bob/android/transfer/TransferStreamChecks.kt`
- `src/android/gradle.properties`
- `src/android/README.md`
- `src/windows/Bob.Windows/App.xaml.cs`
- `src/windows/Bob.Windows/Bob.Windows.csproj`
- `src/windows/Bob.Windows/Domain/TransferModels.cs`
- `src/windows/Bob.Windows/MainWindow.xaml`
- `src/windows/Bob.Windows/MainWindow.xaml.cs`
- `src/windows/Bob.Windows/Persistence/TextHistoryStore.cs`
- `src/windows/Bob.Windows/Persistence/TransferHistoryStore.cs`
- `src/windows/Bob.Windows/Platform/TrayController.cs`
- `src/windows/Bob.Windows/Security/ServerCertificateFactory.cs`
- `src/windows/Bob.Windows/Transport/BobEndpoints.cs`
- `src/windows/Bob.Windows/Transport/BobProtocol.cs`
- `src/windows/Bob.Windows/Transport/SessionCoordinator.cs`
- `src/windows/Bob.Windows/Transport/TransferCoordinator.cs`
- `src/windows/Bob.Windows/Transport/TransferMaintenanceService.cs`
- `src/windows/Bob.Windows/Transport/TransferProtocolMessages.cs`
- `src/windows/Bob.Windows/Transport/TransferProtocolParser.cs`
- `src/windows/Bob.Windows/Transport/WebSocketSessionHandler.cs`
- `src/windows/Bob.Windows/UI/MainWindowViewModel.cs`
- `src/windows/Bob.Windows/UI/TimelineItemViewModel.cs`
- `tests/windows/Bob.Windows.Checks/Program.cs`
- `tests/windows/Bob.Windows.Checks/RealReliabilityChecks.cs`
- `tests/windows/Bob.Windows.Checks/RealTransportChecks.cs`
- `tests/windows/Bob.Windows.Checks/ReliabilityChecks.cs`
