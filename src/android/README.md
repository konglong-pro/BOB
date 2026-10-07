# BOB Android

Android 14+ 的 BOB 客户端。当前实现包括：

- 保留紫色配色的 Compose 聊天 GUI；电脑消息在左、手机消息在右，文字/图片/文件共享时间线，连接发现与手动 IP 位于 Connection 面板。
- HTTPS `/bob/v1/info`、受限 TOFU、应用私有完整 DER pin 与全新 strict TLS 连接。
- `bob.v1` WSS hello/welcome、分页 snapshot、原生 ping/pong、有界关闭和自动重连。
- TLS、连接池与取消响应的关闭在不可取消 IO 清理中执行；旧连接先隔离，新连接等待旧资源释放，避免断线时在主线程关闭网络 socket。
- 连接状态只有在最终 snapshot 后才进入 `Connected`；失败提示包含候选地址以及安全分类后的网络、TLS、HTTPS 或 WSS 原因。
- 双向文字、先持久化后 ACK、原 `textId` 重放、送达状态与可复制时间线。
- DNS-SD 保留全部 A/AAAA、优先同链路地址并顺序尝试，避免 Windows 多网卡时盲选首地址。
- 手动连接成功后保存已认证电脑身份和 IP；前台重开使用原 pin 严格恢复。Disconnect 跨重启暂停自动连接，离线仍显示已知电脑的历史。原生编辑状态避免快速输入和粘贴丢字。
- 当前进程内显式选择的 mDNS 目标在后台保留；返回前台等待同一服务名和身份重新解析，再用原 pin 恢复连接。选择器保留打开时的认证身份，短暂断线返回后先为原电脑排队，再释放连接保护。
- 图片/文件多选、64 位计数的有界流式 PUT/GET、SHA-256、进度/速度/ETA、取消与新 ID 从头重试。接收内容校验后才发布 MediaStore；图片提供有界预览和原图复制。
- 活动传输时原生 dataSync 前台服务维持连接、WakeLock 与 Wi-Fi 锁；批次收尾保留最多 60 秒控制/重连窗口，之后空闲后台断开，手机重启不自动启动。纯离线队列不启动服务。连接与工作器独立于页面 ViewModel 生命周期。
- 原子协调日志保留未开始队列、摘要和终态。正文停滞 60 秒中断并清理，正文写完后的摘要协调保留 24 小时；断线重放原 ID，不恢复字节偏移。

主动取消先等待对端固定结果，最多 60 秒，再关闭正文；发送和接收适用相同规则，接收端等待时不发布内容。待确认取消占住 FIFO，不能立即重试。控制断线时清理未发布内容并保留取消请求，重新连接后继续确认。

临时分享 URI 在进程退出后不保证可读，未完成正文需重新选择；系统分享入口仍未启用。Android 15+ 的后台 dataSync 有每 24 小时累计 6 小时的系统预算，超时时中断正文并保留摘要协调；回到前台后可继续。真机网络、锁屏和 MediaStore 验证范围见 [设备验收记录](../../docs/active/device-validation-2026-10-07.md)。

## 安全边界

首次 TOFU 只接受一张自签叶证书，并检查有效期、`CA=false`、`serverAuth` 和 `digitalSignature`。probe 只允许请求同一 TLS response 上的 `/info`；保存后关闭 probe，再以 strict pin 新建客户端。证书变化不会被静默覆盖。

BOB 不修改系统或应用的全局 TLS 信任，不提供 HTTP/WS 明文回退。

## 工具链

- JDK 17
- Android SDK 36；minSdk 34
- Android Gradle Plugin 9.2.1
- Gradle 9.4.1
- AGP built-in Kotlin 2.3.10
- Compose BOM 2026.06.00
- OkHttp 5.4.0

在仓库根目录执行：

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Target Android
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1 -Target Android
```

调试 APK 位于 `src/android/app/build/outputs/apk/debug/app-debug.apk`。

## 真机存储与恢复检查

项目使用 Android 原生 Instrumentation，不额外引入测试框架。以下命令在仓库根目录运行；先结束活动传输，因为 instrumentation 会重新启动 BOB 进程。

```powershell
$env:JAVA_HOME = (Get-ChildItem .\.tools\jdk-17 -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'bin\java.exe') } | Select-Object -First 1).FullName
$env:ANDROID_HOME = (Resolve-Path .\.tools\android-sdk).Path
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = Join-Path (Get-Location) '.gradle-user-home'
& .\.tools\gradle-9.4.1\bin\gradle.bat --project-dir .\src\android assembleDebug assembleDebugAndroidTest
& .\.tools\android-sdk\platform-tools\adb.exe install -r .\src\android\app\build\outputs\apk\debug\app-debug.apk
& .\.tools\android-sdk\platform-tools\adb.exe install -r .\src\android\app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk
& .\.tools\android-sdk\platform-tools\adb.exe shell am instrument -w -r com.bob.android.test/com.bob.android.device.BobDeviceChecks
```

检查使用独立的私有日志、连接设置和带本次 UUID 的 MediaStore 测试项；只允许访问并清理本次创建的项目，不读取现有用户媒体、信任记录或传输日志。覆盖真实 pending 发布、发布中断恢复、已发布内容被修改时保留原件、摘要不符清理，以及连接意愿保存和损坏设置拒绝。
