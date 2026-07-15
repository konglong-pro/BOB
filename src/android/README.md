# BOB Android

Android 14+ 的 BOB 客户端。当前实现包括：

- Compose GUI 与 `_bob._tcp.` NSD 发现；一台设备延迟自动连接，多台设备手动选择，支持手动 IP。
- HTTPS `/bob/v1/info`、受限 TOFU、应用私有完整 DER pin 与全新 strict TLS 连接。
- `bob.v1` WSS hello/welcome、分页 snapshot、原生 ping/pong、有界关闭和自动重连。
- 连接状态只有在最终 snapshot 后才进入 `Connected`；失败提示包含候选地址以及安全分类后的网络、TLS、HTTPS 或 WSS 原因。
- 双向文字、先持久化后 ACK、原 `textId` 重放、送达状态与可复制时间线。
- DNS-SD 保留全部 A/AAAA、优先同链路地址并顺序尝试，避免 Windows 多网卡时盲选首地址。
- 前台在线会话内支持图片/文件多选、流式 PUT/GET、SHA-256、进度、pending MediaStore 发布，以及发送和已验证接收图片的缩略图与大图预览。
- 页面进入后台后仍会延迟停止发现和 WSS；手机重启不自动启动。

当前传输队列只在进程内；系统分享入口、前台传输服务、锁屏、离线/重启恢复、取消和重试尚未启用。

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Target Android
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1 -Target Android
```

调试 APK 位于 `src/android/app/build/outputs/apk/debug/app-debug.apk`。
