---
doc_type: adr
adr_id: ADR-0002
title: Android HTTPS 与 WSS 使用 OkHttp
status: accepted
date: 2026-07-11
---

# ADR-0002：Android HTTPS 与 WSS 使用 OkHttp

## 背景

Android SDK 36 不提供 `java.net.http.WebSocket`。BOB 又需要同一个专用客户端同时支持 HTTPS、WSS、原生 ping/pong、关闭握手、连接取消和自定义 X.509 信任管理器。自行实现 RFC 6455 会把帧掩码、分片、控制帧、超时和竞态带入一期安全边界。

## 决策

- 使用 `com.squareup.okhttp3:okhttp:5.4.0`，仅用于 Android BOB 专用 HTTPS/WSS 流量。
- 不引入 Retrofit、日志拦截器、额外 JSON 库或 OkHttp coroutines artifact。
- TOFU probe 与 strict pin 使用不同的 `OkHttpClient` 和连接池。
- 自定义 hostname verifier 只存在于同时执行证书 profile 与完整 DER pin 校验的 BOB 客户端；不修改全局 TLS 默认值。
- JSON 使用 Android 平台 `org.json`，本地原子记录使用平台 `AtomicFile`/`SharedPreferences.commit()`。

## 依赖评估

- 平台缺口：Android 公共 SDK 没有可直接采用的 WebSocket 客户端；OkHttp 核心同时覆盖 HTTPS 与 RFC 6455。
- 版本与维护：5.4.0 是 2026-06-08 发布的稳定版，支持 Android API 21+；BOB 最低 API 为 34。
- 许可证：OkHttp 与 Okio 使用 Apache License 2.0。
- 传递依赖：Gradle 选择 `okhttp-android:5.4.0`、Okio 3.17.0 和 AndroidX Startup 1.2.0；Kotlin stdlib 继续由项目的 2.3.10 统一解析。
- 包体：OkHttp Android AAR 约 844 KiB；最终 APK 影响以发布构建为准，不以未压缩调试 APK 作为发布体积承诺。
- 替代方案：手写 WebSocket 被拒绝；`java.net.http` 在 Android SDK 中不可用；再引入完整网络框架没有必要。

## 后果

- WSS 的掩码、分片、ping/pong 与关闭握手由成熟实现负责。
- 证书信任仍完全由 BOB 的 profile、TOFU 记录和完整 DER pin 决定，不依赖系统 CA。
- 需要及时跟进 OkHttp 5.x 的安全与兼容更新，并在升级时重新执行真机 TLS/WSS 验证。

## 参考

- [OkHttp 官方概览与要求](https://square.github.io/okhttp/)
- [OkHttp 5.x 更新记录](https://square.github.io/okhttp/changelogs/changelog/)
- [OkHttp 许可证](https://github.com/square/okhttp/blob/master/LICENSE.txt)
