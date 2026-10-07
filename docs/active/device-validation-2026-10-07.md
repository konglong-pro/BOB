# Android 真机验收与优化补充（2026-10-07）

状态：本轮优化与收尾完成，手机已覆盖升级，Windows 日常版本已启动。下表区分实际执行结果与未验证边界。

本轮衔接 [2026-10-06 优化](optimization-2026-10-06.md)。测试从台北时间 10 月 6 日晚持续到 10 月 7 日，使用已连接的 OnePlus PJX110、Android 14（SDK 34），覆盖安装同签名 Debug APK，保留应用数据、历史和既有证书 pin。

## 环境与数据隔离

- 大文件测试时 Android 与 Windows 分别位于 `192.168.10.0/24` 和 `192.168.1.0/24`，使用可路由的 Wi-Fi HTTPS/WSS 和手动 IP，未用 ADB 端口转发。最终复测改为手机热点，Windows 地址 `192.168.148.244`；原生 NSD 发现同一电脑，原证书 pin 连接成功。跨广播子网的 mDNS 不计为通过。
- Windows 真机测试宿主复用生产端点、会话、传输与维护服务，以及原有服务身份和证书；业务日志、收件与测试文件隔离在忽略目录 `artifacts/device-qa`。不迁移或覆盖 Windows 用户历史、收件设置和私钥。
- 原生 instrumentation 使用独立的私有日志、连接设置与每轮 UUID 媒体项，只访问并清理本轮对象，不读取现有用户媒体、信任或传输日志。
- 手机通过 USB 调试连接并充电。用户拒绝的通知权限保持原样；前台服务与传输可检查，未把通知展示计为通过。

## 本轮发现与修改

1. Android 快速输入曾被异步界面状态回写，导致 IP/文字丢字。改用同步 `TextFieldState` 保存文字、选区和 IME 组合状态；界面流仅观察，不回写旧值。
2. 手动 IP 会话原先未跨进程保留，空闲后台断开后返回页面也未自动恢复。保存成功握手后的 IP、已认证 `serverId` 和连接意愿，前台严格使用原 pin 恢复；Disconnect 跨重启暂停自动连接，损坏设置拒绝恢复，缺少 pin 等待显式 Connect。离线仍显示已知电脑历史。
3. 真机在发送时取消、接收时取消均复现先关闭 HTTPS 正文导致对端固定 `network_interrupted`。双端现在先持久化并发送 cancel，最多等待 60 秒再关闭正文。等待期间占用 FIFO、禁止重试；接收内容只写未发布对象，不能发布。canonical 终态、断线或超时关闭流并清理未发布对象，未确认请求用于重连协调。同步更新协议与规格，不增加线级字段。
4. 真机前台 WSS 被强制中断时复现 `NetworkOnMainThreadException`：OkHttp 连接池关闭 TLS socket 在主线程执行，导致应用崩溃。清理改在 IO 上下文完成，捕获旧会话资源后等待清理，再尝试下一个端点；取消协程也不跳过清理。安装该修复后，连续三次强制中断 WSS 均在同一手机进程自动恢复，没有再产生应用崩溃。
5. 系统文件选择器返回时遇到短暂断线，原先会在排队前释放连接保护，并因当前会话离线丢弃 URI。现在打开选择器时捕获一次性认证身份，返回后先把文件排队到原电脑，再释放保护；发送等待原身份连接与快照同步。真机在选择器内中断 WSS 后，64 MiB 文件仍为原身份入队并完成完整摘要验证；取消选择没有新增记录，主动离线仍不能打开选择器。
6. Windows HTTP 清理和 WSS 取消并发关闭同一 FileStream，真机复现 `ObjectDisposedException` 并断开控制会话。内容流现在用原子门确保底层文件只释放一次，先移除活动引用再取消；异常清理仍释放协调信号。真实 Kestrel 检查连续取消 8 个活动 PUT 后，在同一 WSS 会话继续文字和新文件，未发布临时文件均清理。
7. 当前进程内显式选择的发现目标在后台停止发现时被空快照清空，返回前台无法自动连接。保留显式选中的服务名与已认证身份，等待同一电脑重新解析；前台恢复必须具有原 pin，不能因同名广播改投另一身份。

Windows 保留真实非终态并使用本地 `CancelPending`，其日志 `Canceled` 仅保存首次请求时间，确认前不作为线级 canceled；Android 保留已有 `Canceled + cancelPending` 表达方式。双方同时取消时仍由内容接收端固定首次请求时间，已经发布的 completed 与先前真实 failed 的优先级保持契约。

## 实际传输与恢复结果

| 项目 | 已执行结果 |
| --- | --- |
| Wi-Fi 双向文字 | 中文、Emoji 和 44 字符快速输入交付，UTF-8 字节数与摘要一致 |
| 图片、零字节、普通文件 | 双向交付；原生文件多选与图片选择器返回 BOB；接收内容重新读取的完整 SHA-256 一致 |
| 2 GiB + 17 字节 Windows → Android | 完成，`f106b555-037d-494e-ab30-3f99691786e6`；最终 MediaStore 文件完整重新读取校验 |
| 2 GiB + 17 字节 Android → Windows | 完成，`2d3e0c22-5317-4783-9bf2-fb731c5092c8`；最终文件完整重新读取校验 |
| 熄屏、密码锁屏 | 双方向已有正文持续进展并完成，原生 dataSync 服务、WakeLock 与 Wi-Fi 锁运行；未测试拔线深度 Doze |
| 并发与 FIFO | 大文件下载期间手机多选 64 MiB 与空文件上传，两个方向并行，同方向按队列完成 |
| 取消与重试 | 手机发送/接收、电脑发送/接收时取消均确认。手机接收取消 `e8c49503-350f-4bfd-9cb3-10fae62a6ddc` 保持原连接、删除 pending 媒体项，下一空文件继续。重试 `9809b0c6-c287-40ff-aa44-dcb30e07c4ef` 为新 ID，64 MiB 全量 SHA 一致。电脑接收取消 `a2164821-aa5c-4a55-a016-99c54ba138cd` 与手机发送取消 `fd1e4e7e-7f73-46da-bc19-d7043ad64ede` 保持同一连接，临时文件被删除；手机原生 Retry `2f8c0106-9dd6-4a5d-b74e-d3e352fb2ff1` 保留原 `retryOf`，64 MiB 全量 SHA 一致 |
| 强制停止后重开 | 保存的手动目标使用原身份/pin 自动 Connected，已知电脑历史保留 |
| 主动 Disconnect 后重开 | 确认保存 `auto_connect=false`；跨进程保持 Offline，不自行恢复；显式 Connect 可连接 |
| Home 后返回前台 | 空闲后台断开，回到 BOB 恢复 Connected |
| 电脑离线补发 | 手机后台断开时排队的文字、空文件在返回前台后各交付一次，终态已确认 |
| 前台控制中断 | 两次复现同一 TLS 清理崩溃；安装 IO 清理修复后连续三次强制中断均在同一 PID 自动恢复，原身份与 pin 保留 |
| 选择器返回遇到断线 | 选择器内强制中断 WSS 后，只创建原电脑的一个文件项 `ade03897-bcd8-434f-94e1-8385bf4bd2b3`，重连后完成 64 MiB，完整 SHA 一致；取消选择不入队 |
| 热点原生发现 | 最终手机热点网络发现原 Windows 身份并完成 strict pin 与 WSS snapshot |
| 显式发现目标后台恢复 | 最后补丁安装后，选择原电脑、Home 7 秒、返回 BOB 连续两轮均恢复 Connected；使用日常 Windows 版本，原身份与 pin 保留 |

大文件是实际写入的 `2,147,483,665` 字节文件，没有使用稀疏文件代替。原始文件与两端最终内容 SHA-256 均为：

```text
035E212910EDC3D771691F2FB75DE57CBD1AACE2C2C1152710DDA7C0226F8221
```

64 MiB 测试文件实际为 `67,108,881` 字节，SHA-256 为：

```text
FE7F93C2F81B727C8813E45EA50476366A8FF877D5971CF1056C961F630C200A
```

大文件上传期间 Android PSS 采样约 86–88 MiB，Windows 工作集约 72 MiB；这些是时点采样，不代表连续测得的峰值。

双向 2 GiB 测试在 10 月 6 日 23:38–23:52 完成。后续修复围绕取消清理、选择器和前台恢复；取消/重试复测使用 64 MiB，Windows 真实流式检查另覆盖 1 GiB 已知长度与 64 MiB chunked 正文，未在每次 UI 修复后重复整轮 2 GiB 传送。

## 构建与检查

| 检查 | 已执行结果 |
| --- | --- |
| 根目录 `pwsh.exe -File scripts/build.ps1` | 全量通过；Windows 内容流释放修复后另跑 `-Target Windows`，0 警告、0 错误；最终发现目标补丁后另跑 `-Target Android`，通过 |
| 根目录 `pwsh.exe -File scripts/check.ps1` | 全量 Windows 45/45、Android 17/17 streamingChecks 与 lint 通过；最终 Windows 与 Android 补丁后分别重跑对应 Target，45/45 与 17/17、lint 通过 |
| Android 原生 instrumentation | 最终已安装版本 12/12 通过，随后前台恢复两轮通过 |
| Windows 真机宿主 | 已编译，0 警告、0 错误；所有测试宿主已停止 |
| Windows Release 与根 `BOB.exe` | 最终内容流修复已发布，0 警告、0 错误；根启动器拉起日常 WPF 版本，42424 监听与 `/info` 原身份验证通过 |

12 项原生检查覆盖原子日志读坏不覆盖、写入中断、已知/未知/空/图片 pending 发布、发布中断恢复、已发布内容被修改时保留原件、摘要不符清理、先完整验证日志再清理、正文中断恢复，以及连接身份/Disconnect 意愿保存与损坏设置拒绝。SHA 不符的断言等待失败日志和 pending 清理都完成，避免把异步终态观察当成已完成清理。

Windows 新增 2 项协调器回归覆盖两方向取消、延迟 accepted、EOF/摘要交叉、禁止发布、FIFO/重试阻塞、超时/断线清理、旧日志恢复和完成/真实失败优先级。Android 新增 2 组覆盖取消等待窗口与双方向完整正文/摘要仍不可发布，并接入已有 streamingChecks。

取消集成检查另外包含连续 8 轮真实部分 PUT、WSS canonical canceled、terminalAck、同会话文字 ACK、临时对象清理和后续新文件发布。最终 Windows 检查保留 45 项计数，该循环属于原有集成项。检查日志位于忽略目录 `artifacts/device-qa`，不提交本机工具、证书或签名密钥。

## 交付产物

- Windows：[根启动器 BOB.exe](../../BOB.exe)，使用本工作区最新 Release 和仓库工具链。原用户资料运行，42424 监听，原 `serverId` 为 `3b623512-99a5-4ebe-8627-398cbd969ae2`。
- Android：[已安装的同签名 APK](../../artifacts/device-qa/app-debug-device.apk)，SHA-256：`F05F9853B4FFB753AD755B5E3FFFCD07EF3D8C8BC159E3D70E54CDF48ED1B868`。
- 手机上的原 Debug 签名 SHA-256 为 `bfbe7f9f679bb47d57c237df9027c3f775ce121438a263dd017c11060b7ca35e`。构建账户切换会产生不同默认 Debug 签名，因此最终 APK 使用原有调试密钥重新签名后验证并 `install -r`，没有卸载、清空数据或导出密钥。标准构建目录的默认签名 APK 不作为本机覆盖升级包。
- 检查证据：`build-complete.log`、`check-complete.log`、`build-final-stream-owner.log`、`check-final-stream-owner.log`、`build-final-discovery.log`、`check-final-discovery.log`、`instrumentation-final-discovery.log`、`selected-discovery-reopen.json` 与 `ui/final-daily-connected.png`，均在 `artifacts/device-qa`。

## 修改文件

- Android UI/连接：`ui/BobScreen.kt`、`ui/BobViewModel.kt`、`persistence/SavedConnectionPreferences.kt`、网络连接清理文件。
- Android 传输/服务：`transfer/OnlineTransferCoordinator.kt`、`background/BobTransferRuntime.kt`；纯流式回归 `src/test/.../TransferStreamChecks.kt`。
- 原生真机检查：`app/build.gradle.kts`、`src/androidTest/.../BobDeviceChecks.kt`。
- Windows 取消协调：`Domain/TransferModels.cs`、`Transport/TransferCoordinator.cs`、`TransferContentStream.cs`、`SessionCoordinator.cs`、`BobEndpoints.cs`、`WebSocketSessionHandler.cs`、`TransferMaintenanceService.cs`、`UI/TimelineItemViewModel.cs`；`tests/windows/.../Program.cs`、`ReliabilityChecks.cs`、`RealReliabilityChecks.cs`。
- 规范与说明：协议、主规格、当前阶段、两端 README 和本记录。原始 UI/大文件优化的其他修改见上一轮记录。

## 未验证边界

本次未验证 Android 15+ 的 dataSync 累计预算、拔掉 USB 后长时深度 Doze、所有厂商省电策略、跨广播子网的 mDNS、拒绝通知权限时的通知展示、100 文件真机队列，以及正式 Release 签名/干净安装发布流程。Debug 覆盖升级保留数据不等于正式发布验收。

既有系统分享入口、清空历史等后续功能范围未在本次扩展。临时 URI 失效仍需重新选择；传输从头重试，不提供字节断点续传。测试生成的 `BOB_QA_*` 文件与记录用于复核，不清空用户历史。
