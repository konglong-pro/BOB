# 当前阶段：BOB v1 实现

状态：`active`
阶段 ID：`bob-v1-implementation`
实现授权：`true`

## 当前目标

P0/P1 已进入联调，在线 P2/P3 tracer 已落地。双端聊天交互、分类历史与 Windows 托盘/快捷键升级已实现，并通过下列自动检查与实机验收。持久协议队列、Android 后台传输及跨重启协议恢复仍是独立的未完成工作。

## 本轮升级（2026-10-29，已实现）

- 保留紫色背景、共享像素手形与全英文 UI；提供 `Chat`、`Text`、`Images`、`Files` 记录视图，聊天视图按时间混排，分类视图分别显示对应类型。
- 文字可直接选择复制，长文字气泡内展开；图片点击预览，Windows 通过右键菜单或图片焦点内 `Ctrl+C` 复制，Android 通过长按菜单复制；移除常驻查看/复制按钮。
- 开放更早的文字记录，并将图片/文件历史原子归档以供重启后查看。归档不等于协议队列恢复；中断记录应明确显示失败，不伪造续传或完成。
- Windows 使用 `Enter` 发送、`Shift+Enter` 换行并避开输入法确认键；最小化留任务栏、关闭隐藏托盘、显式退出才停服。默认 `Ctrl+Alt+B` 全局唤出/隐藏，可修改并提示冲突；重复启动唤出已有实例。
- Windows 主窗口已移除大块设置与快捷键面板，顶部仅保留紧凑的 `Choose`、`Open` 目录操作。`Ctrl+Shift+R` 选择收件目录，`Ctrl+Shift+E` 打开收件目录；全局快捷键编辑与说明移至托盘 `Keyboard shortcuts...` 独立窗口，也可通过 `F1` 打开。
- 普通文件记录可点击打开可用的本地文件；历史归档损坏时保留原文件并显示警告，不静默覆盖。
- 以下完成情况区分代码实现与已执行实测；历史验证保留在文末，不作为本轮通过的依据。

## 规范入口

- 主规格：`../planning/active/bob-v1-spec.md`
- 协议契约：`../contracts/transport-v1.md`
- 架构决策：`../adr/0001-lan-topology-and-trust.md`
- Android 网络客户端：`../adr/0002-android-okhttp-client.md`
- 阶段清单：`../phase-manifest.yaml`

## 允许修改

`src/`、`tests/`、`docs/`、`scripts/`，以及实现所需的最小仓库根配置。

## 构建与检查

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-windows.ps1
```

上述命令使用 `.tools/` 中的固定工具链。命令是否在当前提交上通过，必须由执行者实际运行后报告；文档本身不代表已验证。

## 当前切片边界

- Windows 已实现全英文 WPF GUI、共享像素手形图标、`GET /bob/v1/info`、`bob.v1` WSS 握手、空快照、单活动会话、心跳超时和文字收发；原生 Windows DNS-SD 广播 `_bob._tcp.local`。仓库根目录可生成无控制台的 `BOB.exe` 快捷入口，应用用命名 Mutex 阻止并发实例，重复启动可激活已有实例。顶部显示紧凑连接状态与手机名。
- Android 已实现 Android 14 起的全英文 Compose GUI、共享 adaptive/legacy 像素手形图标、`NsdManager` 发现、延迟自动连接、多机选择与手动 IP。发现会保留全部 A/AAAA，优先同链路地址并顺序尝试，避免多网卡 Windows 的首地址误选。
- Android 已实现 `/info` 同连接证书观察、受限 TOFU、应用私有完整 DER pin、全新 strict TLS 复核、`bob.v1` WSS hello/welcome/snapshot、双向文字与 ack。只有完整 snapshot 后才显示已连接；连接失败提示保留候选地址、WSS 升级 HTTP 状态和安全分类后的网络/TLS/WSS 原因，不显示正文或文件路径。
- Android 文字使用原子 JSON 存储：出站先落盘、单一串行 drain、断线沿用原 `textId`；入站落盘后才 ack，并在重连时重发幂等 ack。信任变化绝不覆盖，只有确认 pin 冲突才开放“重置信任”。
- 双端已实现当前前台在线会话内的图片/文件多选、每方向 FIFO、HTTPS 流式 PUT/GET、增量 SHA-256、progress、digest、completed 与 terminalAck。Windows 接收先写 `Downloads\BOB\.bob-<id>.part` 并禁止覆盖发布；Android 接收先写 MediaStore `IS_PENDING=1`，验证后发布到 `Pictures/BOB` 或 `Download/BOB`。
- 双端文字支持直接选择复制与长文字气泡内展开；图片点击预览，复制通过右键/长按菜单或 Windows 图片焦点内 `Ctrl+C`，无常驻查看/复制按钮。Android 通过原始 `content://` URI 写入系统剪贴板，Windows 复制完整解码图像。Android 接收图片只在 MediaStore 发布后开放操作，Windows 接收图片只在摘要验证与原子发布后开放最终路径，不读取 pending 内容或 `.part`。Windows 大图由应用内有界解码器显示，不把图片路径交给系统 shell；解码失败时不开放查看或复制操作。
- Windows 顶部 `Choose`、`Open` 与 `Ctrl+Shift+R`、`Ctrl+Shift+E` 允许选择和打开统一收件目录；新目录通过临时探针验证可创建、写入和删除后，选择才持久化到 `%LocalAppData%\BOB\settings.json`。活动接收在 accepted 时冻结原收件根，目录变更只影响后续 offer，保证 staging 与最终发布仍在同一目录。
- 图片、文件记录已持久归档，重启时中断的非终态显示为失败；归档不恢复活动协议队列。文件队列与终态重放协调目前仍仅在进程内；Android 系统分享入口、前台传输服务、锁屏、离线/跨重启协议恢复、取消/重试与 Windows 拖放尚未实现。Android 进入后台后当前仍会延迟断开，因此本切片只承诺双方在线且 Android 前台。
- Windows 已实现托盘生命周期、全局唤出与快捷键窗口；关闭主窗口隐藏到托盘，最小化保留任务栏，托盘显式退出才停止服务。隔离的原生窗口检查已验证真实热键注册与冲突回退、关闭隐藏、命名事件唤出、最小化恢复、窗口消息处理及释放资源。
- Windows 已从持久文字历史恢复入站 `textId` 去重：同 ID、同正文与 `createdAt` 只重发 ack，不重复加入时间线；同 ID、不可变字段冲突时返回 `invalid_metadata`。
- 双端主界面提供 `Chat`、`Text`、`Images`、`Files` 分类与更早记录加载；文字不再限制只能查看最近 20 条，用户阅读旧记录时保留浏览位置。Windows 隐藏去重 tombstone 与待确认出站文字的自动重放仍未完整实现，因此重启后的端到端离线重放尚未达到 P1 退出门槛。

## 本轮已执行验证（2026-10-29）

- 使用 PowerShell 7 从根目录执行 `scripts/build.ps1` 与 `scripts/check.ps1`：双端构建通过；最终 Windows 检查 25 项通过，Android `check`/lint 通过。Kotlin 编译使用进程内策略，以避免受限环境无法写入用户目录的 daemon 文件。
- Android 真机归档检查 6 项通过，覆盖归档往返、中断状态标记、URI 安全、原子回滚、有序检查点与损坏数据保护。
- `versionCode = 2` Debug APK 已覆盖安装到手机并保留应用数据。
- 实机 PC→手机传输 1,560 字符文字、手机→PC 传输 207 字节图片和 59 字节文件通过。手机选择器短暂断线不再被误判为切换电脑，已重连后完成文件发送。
- Windows 重启恢复图片记录；Android 强制停止并重新打开后保留完成图片、完成文件及明确标注的失败尝试，全部归档 ID 保留。已检查图片大图、长按复制菜单及长文字展开，未写入用户剪贴板。
- 原生桌面生命周期检查在独立进程创建透明测试窗口，使用临时目录和临时组合键，未启动生产服务、注入系统键盘或修改用户剪贴板。
- `scripts/publish-windows-launcher.ps1` 最终通过，Release 为 0 警告、0 错误，当前工作区根目录已生成 `BOB.exe`。交付检查时端口 42424 的实际拥有者来自 `E:\BOB`；该目录仍是原运行版本，未覆盖其代码或产物。使用本工作区新版前须退出该旧实例。
- 未实测：真实中文输入法候选确认、全部物理键盘组合、跨应用图片粘贴兼容性，以及最新 APK 的 PC→手机图片/文件完整实机矩阵。反向 GET 正文协议已通过真实 HTTPS/WSS 自动检查。协议离线重放与 Android 后台/锁屏传输仍不在已通过范围内。

## 本轮修改文件

以下路径相对仓库根目录；未新增生产依赖，未修改线级协议契约，也未提交工具链、证书或本机配置。

- `docs/active/current.md`
- `docs/planning/active/bob-v1-spec.md`
- `README.md`
- `src/android/app/build.gradle.kts`
- `src/android/app/src/androidTest/java/com/bob/android/HistoryInstrumentation.kt`
- `src/android/app/src/main/java/com/bob/android/application/BobConnectionCoordinator.kt`
- `src/android/app/src/main/java/com/bob/android/persistence/AtomicTransferHistoryStore.kt`
- `src/android/app/src/main/java/com/bob/android/persistence/TransferHistoryCheckpointWriter.kt`
- `src/android/app/src/main/java/com/bob/android/transfer/OnlineTransferCoordinator.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobScreen.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobUiState.kt`
- `src/android/app/src/main/java/com/bob/android/ui/BobViewModel.kt`
- `src/windows/Bob.Windows/App.xaml`
- `src/windows/Bob.Windows/App.xaml.cs`
- `src/windows/Bob.Windows/Bob.Windows.csproj`
- `src/windows/Bob.Windows/Domain/TransferModels.cs`
- `src/windows/Bob.Windows/MainWindow.xaml`
- `src/windows/Bob.Windows/MainWindow.xaml.cs`
- `src/windows/Bob.Windows/Persistence/TransferHistoryStore.cs`
- `src/windows/Bob.Windows/Platform/DesktopLifetime.cs`
- `src/windows/Bob.Windows/Platform/ShortcutSettingsWindow.cs`
- `src/windows/Bob.Windows/Transport/TransferCoordinator.cs`
- `src/windows/Bob.Windows/UI/ImagePreviewWindow.xaml`
- `src/windows/Bob.Windows/UI/ImagePreviewWindow.xaml.cs`
- `src/windows/Bob.Windows/UI/MainWindowViewModel.cs`
- `src/windows/Bob.Windows/UI/TimelineItemViewModel.cs`
- `tests/windows/Bob.Windows.Checks/DesktopInteractionChecks.cs`
- `tests/windows/Bob.Windows.Checks/DesktopLifetimeChecks.cs`
- `tests/windows/Bob.Windows.Checks/Program.cs`
- `tests/windows/Bob.Windows.Checks/RealTransportChecks.cs`
- `tests/windows/Bob.Windows.Checks/TransferHistoryChecks.cs`

## 已执行验证（历史记录）

- Windows Debug 构建：通过，0 警告、0 错误。
- Windows 检查程序：20 项通过；除原有协议/会话/证书检查外，覆盖真实 Kestrel WebSocket 的 `AwaitingHello → Connected（含手机名）→ Offline（含断开原因）`、文字、结构化 PUT 错误、零字节/普通文件、SHA-256、路径约束、同名不覆盖、完整上传控制面、accepted/GET 跨通道竞态与下载终态闭环，以及文字历史持久化/最近 20 条窗口/跨重启去重、文字与图片复制动作、收件目录持久化/损坏回退、活动传输目录冻结、有界图片预览、非图片伪装拒绝和旧 transfer snapshot 丢弃。
- Android `assembleDebug`：通过；多地址连接、在线 PUT/GET 与基于本地 URI 的缩略图/大图预览代码编译成功，OkHttp 5.4.0 依赖解析成功。
- Android `check`/lint：通过；当前没有 Android 单元测试源，MediaStore、图片预览与真机正文流仍需设备验收。
- 2026-07-18 根目录 `build.ps1` 与 `check.ps1` 全量通过：Windows Debug 为 0 警告/0 错误且 20 项检查全部通过，Android `assembleDebug` 与 `check`/lint 通过。Windows Release 与根 `BOB.exe` 已重新发布并启动，`127.0.0.1:42424` TCP 监听成功；Debug APK 已通过 ADB 覆盖安装到 OnePlus PJX110（保留应用数据），真机前台 UI 确认 strict pin/WSS 已连接、最近 20 条标题和文字复制入口可见。为避免覆盖用户当前剪贴板，本轮未执行实际粘贴目标兼容性测试。
- 2026-07-15 图片预览与 Windows 自定义收件目录完成后，仓库根 `build.ps1` 与 `check.ps1` 全量通过。随后尝试重建根 `BOB.exe` 所用 Release，但当前正在运行的 BOB 锁定了 Release DLL；为避免中断现有双端连接，没有结束该进程。退出当前 BOB 后仍需重新运行 `publish-windows-launcher.ps1`，根启动器下次才会使用本切片。
- 便携工具链脚本在 `E:\BOB` 使用已有缓存重复执行通过；哈希、精确版本和中断 staging 清理均已验证。
- Windows 隐藏启动冒烟测试通过：真实 HTTPS `/bob/v1/info` 返回合法身份与协议范围，原生 mDNS 注册日志成功，退出后端口 `42424` 已释放。
- Windows 根目录 `BOB.exe` 启动—关闭回归通过：快捷入口拉起 WPF 与 `42424` 监听，关闭主窗口后 Host 在非 Dispatcher 上下文完成停止与释放，进程在 10 秒内退出且端口释放。
- 2026-07-13 真机复测排查发现根 `BOB.exe` 所用 Release 早于最新 Debug；重新执行发布脚本后，当前 Release、根启动器与 APK 已同步重建。新 Release 已启动，`127.0.0.1:42424` 可连接，Wi-Fi 地址 `192.168.10.112:42424` 的真实 `/bob/v1/info` 返回稳定服务身份。该地址由 DHCP 分配，只代表本次排查。
- 双端 `src/` 用户可见源码汉字扫描为零；Windows 多尺寸 ICO、WPF/launcher 图标嵌入及 Android adaptive/legacy 图标资源均通过构建验证。
- 本机真实 HTTPS/WSS 联调通过：证书 profile、strict pin、错误 pin 阻断、`bob.v1` 子协议、welcome、最终 snapshot 和 Android→Windows `text.ack` 均成功；测试结束后端口已释放。
- 2026-07-15 在 OnePlus PJX110（Android 14）完成真机 mDNS、首次 TOFU、strict pin 重连与 WSS welcome/snapshot 验收。真机复现确认 OkHttp 5.4.0 配合自定义 Android trust manager 时，`Handshake.peerCertificates` 的清理后视图可能为空；客户端已改为使用同一 TLS 握手中 trust manager 验证并记录的完整 DER pin，首次连接与强制停止后的重连均进入 `Connected`。错误 pin 阻断、双向文字、图片/文件与 MediaStore 落地仍需继续验收。

## 阶段完成条件

- Windows 与 Android 工程可以从干净工作区构建。
- Windows 检查程序和 Android `check` 通过。
- mDNS、TLS TOFU 与 WSS 文字闭环完成最小实机验证。
- 未决风险和下一阶段范围记录在主规格中。
