# 当前阶段：BOB v1 实现

状态：`active`
阶段 ID：`bob-v1-implementation`
实现授权：`true`

## 当前目标

P0/P1 已进入联调。双端工程、Windows HTTPS/WSS 服务与 mDNS，以及 Android NSD、受限 TOFU、严格 pin、WSS 会话和持久文字客户端已经建立。下一步优先完成 Android 14 真机连接矩阵，再进入文件正文流。

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-windows.ps1
```

上述命令使用 `.tools/` 中的固定工具链。命令是否在当前提交上通过，必须由执行者实际运行后报告；文档本身不代表已验证。

## 当前切片边界

- Windows 已实现全英文 WPF GUI、共享像素手形图标、`GET /bob/v1/info`、`bob.v1` WSS 握手、空快照、单活动会话、心跳超时和进程内文字收发；原生 Windows DNS-SD 广播 `_bob._tcp.local`。仓库根目录可生成无控制台的 `BOB.exe` 快捷入口，应用用命名 Mutex 阻止并发实例。
- Android 已实现 Android 14 起的全英文 Compose GUI、共享 adaptive/legacy 像素手形图标、`NsdManager` 发现、延迟自动连接、多机选择与手动 IP；应用进入后台且没有活动文件传输时会停止发现、WSS 和重连。
- Android 已实现 `/info` 同连接证书观察、受限 TOFU、应用私有完整 DER pin、全新 strict TLS 复核、`bob.v1` WSS hello/welcome/snapshot、双向文字与 ack。只有完整 snapshot 后才显示已连接。
- Android 文字使用原子 JSON 存储：出站先落盘、单一串行 drain、断线沿用原 `textId`；入站落盘后才 ack，并在重连时重发幂等 ack。信任变化绝不覆盖，只有确认 pin 冲突才开放“重置信任”。
- Android 图片和文件的 PUT/GET 正文流、MediaStore 落地与前台传输服务尚未实现。
- Windows 托盘生命周期尚未实现；当前关闭主窗口会退出 BOB 并停止网络服务，不得把它误报为规格中的“隐藏到托盘”。
- Windows 已在单次进程生命周期内按 `textId` 去重：同 ID、同正文与 `createdAt` 只重发 ack，不重复加入时间线；同 ID、不可变字段冲突时返回 `invalid_metadata`。
- Windows 文字历史、去重 tombstone 与待确认 outbox 尚未持久化。Android 侧虽已恢复队列与 ack，但 Windows 重启后的端到端幂等和离线重放仍未达到 P1 退出门槛。

## 已执行验证

- Windows Debug 构建：通过，0 警告、0 错误。
- Windows 检查程序：7 项通过，包含协议 JSON、畸形信封、进程内幂等、会话状态机、证书配置、mDNS 描述符和有界 WebSocket 关闭。
- Android `assembleDebug`：通过；OkHttp 5.4.0 依赖解析成功。
- Android `check`/lint：通过；当前没有 Android 单元测试源。
- 便携工具链脚本在 `E:\BOB` 使用已有缓存重复执行通过；哈希、精确版本和中断 staging 清理均已验证。
- Windows 隐藏启动冒烟测试通过：真实 HTTPS `/bob/v1/info` 返回合法身份与协议范围，原生 mDNS 注册日志成功，退出后端口 `42424` 已释放。
- Windows 根目录 `BOB.exe` 启动—关闭回归通过：快捷入口拉起 WPF 与 `42424` 监听，关闭主窗口后 Host 在非 Dispatcher 上下文完成停止与释放，进程在 10 秒内退出且端口释放。
- 双端 `src/` 用户可见源码汉字扫描为零；Windows 多尺寸 ICO、WPF/launcher 图标嵌入及 Android adaptive/legacy 图标资源均通过构建验证。
- 本机真实 HTTPS/WSS 联调通过：证书 profile、strict pin、错误 pin 阻断、`bob.v1` 子协议、welcome、最终 snapshot 和 Android→Windows `text.ack` 均成功；测试结束后端口已释放。
- 当前 `adb devices` 没有连接设备；尚未执行 Android 14 真机的 mDNS、首次 TOFU、错误 pin 阻断和双向文字验收。

## 阶段完成条件

- Windows 与 Android 工程可以从干净工作区构建。
- Windows 检查程序和 Android `check` 通过。
- mDNS、TLS TOFU 与 WSS 文字闭环完成最小实机验证。
- 未决风险和下一阶段范围记录在主规格中。
