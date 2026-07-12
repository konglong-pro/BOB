---
title: BOB Transport Protocol v1
status: active
version: 1
updated: 2026-07-11
---

# BOB Transport Protocol v1

## 1. 契约范围

本文定义 BOB Android 客户端与 Windows 服务端之间可互操作的线级契约：发现、TLS 信任、WSS 消息、HTTPS 内容流、幂等、状态机和错误。

产品行为与界面不在本文重复定义，见 [BOB v1 主规格](../planning/active/bob-v1-spec.md)。

规范关键词“必须、不得、应、可以”表示实现约束。时间使用 UTC RFC 3339，UUID 使用小写标准格式，文本使用 UTF-8。

## 2. 拓扑与基础地址

- Windows 固定为 HTTPS/WSS 服务端；Android 固定为客户端，Android 不监听端口。
- 默认 TCP 端口：`42424`。
- 基础路径：`/bob/v1`。
- 只允许 TLS；不得提供 HTTP 或 WS 明文回退。
- WSS 子协议：`bob.v1`。

端点：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/bob/v1/info` | 服务实例与协议范围 |
| GET Upgrade | `/bob/v1/ws` | 控制、文字、状态与进度 |
| PUT | `/bob/v1/transfers/{transferId}/content` | Android 上传内容 |
| GET | `/bob/v1/transfers/{transferId}/content` | Android 下载内容 |

## 3. mDNS 发现

服务类型：

```text
_bob._tcp.local.
```

TXT 记录：

| 键 | 值 |
| --- | --- |
| `id` | Windows 安装实例 UUID |
| `name` | 面向用户的电脑名称 |
| `pv` | `1` |
| `tls` | `1` |
| `api` | `/bob/v1` |

地址与端口必须取 SRV/A/AAAA 记录，不在 TXT 写死 IP。客户端必须把 TXT 当作未经认证的发现提示；不得把 mDNS 发布的证书指纹当作可信依据。

## 4. TLS 与 TOFU

### Windows 身份

- 首次运行生成稳定 `serverId` 与自签名证书。
- 证书和 `serverId` 跨程序升级与重启保持稳定。
- 私钥必须以当前 Windows 用户的 DPAPI 保护。
- 生成的证书必须设置 Basic Constraints `CA = false`、Extended Key Usage `serverAuth` 与 Key Usage `digitalSignature`，以满足首次信任算法。
- 最低 TLS 版本为 1.2；平台可用时优先 TLS 1.3。

### Android pin

证书 pin 为：

```text
base64url_without_padding(SHA-256(full_DER_certificate))
```

### 首次信任算法

1. 从 mDNS 选择实例时，Android 先按 TXT `id` 查询本地信任记录：
   - 已有记录：TLS 握手立即使用严格 pin 校验。
   - 没有记录：只为该候选地址创建一次“试连接”。
2. 手动 IP 没有可信 `serverId` 提示，始终先走试连接。
3. 试连接的专用证书校验器只接受同时满足下列条件的叶证书：
   - 对端只提供一张自签名 X.509 证书；
   - 证书可用自己的公钥验证签名；
   - 当前时间处于证书有效期；
   - Basic Constraints 不允许作为 CA；
   - Extended Key Usage 包含 serverAuth，Key Usage 允许 digitalSignature。
4. 试连接建立后，只允许在同一 TLS 连接上请求 `GET /bob/v1/info`；不得建立 WSS 或访问内容端点。
5. mDNS 发起的试连接中，`/info.serverId` 必须等于所选 TXT `id`，否则立即中止。
6. Android 从本次 TLS 的叶证书计算 pin：
   - 如果 `/info.serverId` 已在本地存在，pin 必须与旧记录相同；不同时阻断，绝不覆盖。
   - 如果是未知 ID，原子保存 `serverId + pin + displayName`。
7. 保存或确认记录后关闭试连接，再建立一次严格 pin 连接；只有严格连接可承载 WSS 与文件内容。

### 后续连接与重置

- 严格连接必须同时验证证书有效期、用途和完整 DER pin；自签名链与动态 IP 不再要求主机名匹配，服务端身份完全由 pin 确认。
- 每次严格连接读取的 `/info.serverId` 都必须与选中的 mDNS `id`（若有）及 pin 所属本地记录一致。
- IP 或主机名变化不影响已保存 pin。
- pin 变化时必须中止连接，直到用户显式“重置信任”。
- “重置信任”删除对应 `serverId` 的记录；下一次连接重新执行完整首次信任算法。
- 不启用客户端证书、账号、bearer token 或设备授权。
- 试连接与严格连接必须使用 BOB 专用网络客户端；证书验证不得影响应用其他流量，也不得实现全局 trust-all。

首次 TOFU 无法抵御第一次连接期间的主动中间人，这是可信个人局域网边界内接受的风险。

## 5. 服务信息

`GET /bob/v1/info` 成功返回：

```json
{
  "serverId": "2d8f0aa9-1cd5-4f64-a81e-169eedb115cb",
  "name": "DESKTOP-BOB",
  "appVersion": "1.0.0",
  "protocol": {
    "min": 1,
    "max": 1
  }
}
```

响应：

- `200 OK`
- `Content-Type: application/json; charset=utf-8`
- `Cache-Control: no-store`

## 6. WSS envelope

所有协议消息使用 UTF-8 JSON text frame；v1 不使用 WebSocket binary frame。单个 envelope 最大 1 MiB。

```json
{
  "v": 1,
  "type": "transfer.offer",
  "id": "5f519718-352a-41eb-85df-fd5801688b38",
  "sentAt": "2026-07-11T12:34:56.789Z",
  "replyTo": null,
  "payload": {}
}
```

字段：

| 字段 | 必须 | 规则 |
| --- | --- | --- |
| `v` | 是 | envelope 版本，v1 固定为 1 |
| `type` | 是 | 本文登记的消息类型 |
| `id` | 是 | 消息 UUID |
| `sentAt` | 是 | 发送端 UTC 时间，只供显示/诊断 |
| `replyTo` | 否 | 所响应的消息 ID |
| `payload` | 是 | 对应类型的 JSON object |

- 接收方用本地接收时间排序关键事件，不把 `sentAt` 当作授权或超时依据。
- 未知字段必须忽略，以便同版本向后兼容。
- 未知 `type` 返回 `invalid_message`。
- 业务幂等依赖 `textId` 或 `transferId`，不依赖 envelope `id`。

### 通用错误

```json
{
  "v": 1,
  "type": "error",
  "id": "ec4d7c39-7e68-4391-a458-59af00ae19aa",
  "sentAt": "2026-07-11T12:34:56.900Z",
  "replyTo": "5f519718-352a-41eb-85df-fd5801688b38",
  "payload": {
    "code": "invalid_state",
    "message": "Transfer is not ready for content.",
    "retryable": false,
    "transferId": null
  }
}
```

`message` 面向诊断，不得由客户端解析；逻辑只依赖 `code`。

## 7. 会话

### 建立

WebSocket 建立后，Android 必须在 5 秒内发送 `session.hello`：

```json
{
  "v": 1,
  "type": "session.hello",
  "id": "7a535f70-0a45-4694-ae07-3837dedf2545",
  "sentAt": "2026-07-11T12:00:00.000Z",
  "payload": {
    "protocolMin": 1,
    "protocolMax": 1,
    "client": {
      "installationId": "b00dedf9-839a-49f8-8271-c7a36b638763",
      "name": "Pixel",
      "platform": "android",
      "osVersion": "14",
      "appVersion": "1.0.0"
    }
  }
}
```

`installationId` 只用于显示、日志与重连关联，不是身份或授权凭据。

Windows 响应 `session.welcome`：

```json
{
  "v": 1,
  "type": "session.welcome",
  "id": "d525fba7-9ee9-4c8c-bcd9-dedb6126b465",
  "sentAt": "2026-07-11T12:00:00.050Z",
  "replyTo": "7a535f70-0a45-4694-ae07-3837dedf2545",
  "payload": {
    "protocol": 1,
    "sessionId": "2bf1c89b-b813-4b5f-afb7-2dd2da9a2070",
    "server": {
      "id": "2d8f0aa9-1cd5-4f64-a81e-169eedb115cb",
      "name": "DESKTOP-BOB",
      "platform": "windows",
      "appVersion": "1.0.0"
    },
    "heartbeatSeconds": 20,
    "limits": {
      "maxEnvelopeBytes": 1048576,
      "maxTextBytes": 262144
    }
  }
}
```

- 无版本交集时发送 `unsupported_version`，再以 WebSocket code `4406` 关闭。
- Windows 一期只允许一个活动 Android 会话。第二个连接收到 `peer_busy`，再以 `4429` 关闭。
- Android 同时只维护一个 Windows 会话。

### 心跳

- 双方使用 WebSocket 原生 ping/pong，不创建 JSON 心跳类型。
- 服务端每 20 秒发起一次 ping。
- 60 秒没有 pong 或业务流量即认为连接断开。

### 快照

welcome 后，Windows 发送 `session.snapshot`：

```json
{
  "v": 1,
  "type": "session.snapshot",
  "id": "0b677225-1e83-4146-8e1b-b85b760d4661",
  "sentAt": "2026-07-11T12:00:00.100Z",
  "payload": {
    "snapshotId": "cf555a66-3e89-468d-8328-56440221fd60",
    "page": 0,
    "isLast": true,
    "transfers": [
      {
        "transferId": "3f1ed39a-a0b7-4655-aa33-4c3f713b08c8",
        "direction": "server_to_client",
        "kind": "file",
        "name": "report.pdf",
        "size": 12345,
        "mediaType": "application/pdf",
        "state": "queued",
        "bytesTransferred": 0,
        "createdAt": "2026-07-11T11:59:00.000Z",
        "retryOf": null,
        "terminalAt": null,
        "terminalAckPending": false,
        "sha256": null,
        "storedName": null,
        "error": null
      }
    ]
  }
}
```

snapshot payload 字段：

| 字段 | 类型 | 规则 |
| --- | --- | --- |
| `snapshotId` | UUID | 本轮快照所有分页共用 |
| `page` | int32 | 从 0 开始连续递增 |
| `isLast` | bool | 本轮最后一页为 true |
| `transfers` | array | 当前页的传输记录 |

快照必须在一个一致的持久化视图上生成，只包含：

- 所有非终态传输；
- Windows 发出且 `terminalAckPending = true` 的终态。

快照不得包含普通历史记录或待发文字正文。单页完整 envelope 必须不超过 768 KiB；超出时创建下一页。若连接在最后一页前断开，Android 丢弃本轮分页完成标记，重连后接收新的 `snapshotId`。

`transfers` 的每个对象必须包含下列字段；标记 nullable 的字段也必须出现：

| 字段 | 类型 | 规则 |
| --- | --- | --- |
| `transferId` | UUID | 业务幂等 ID |
| `direction` | enum | `client_to_server` 或 `server_to_client` |
| `kind` | enum | `file` 或 `image` |
| `name` | string | offer 中的原始显示名 |
| `size` | int64/null | offer 声明的大小 |
| `mediaType` | string | offer 声明的 MIME |
| `state` | enum | 本文状态机中的状态 |
| `bytesTransferred` | int64 | Windows 已持久化的进度视图 |
| `createdAt` | timestamp | 创建时间 |
| `retryOf` | UUID/null | 被重试的旧 ID |
| `terminalAt` | timestamp/null | 进入终态的时间 |
| `terminalAckPending` | bool | Windows 发出的终态是否仍待 Android 确认 |
| `sha256` | base64url/null | completed 时的摘要 |
| `storedName` | string/null | completed 时接收端发布的名称 |
| `error` | object/null | failed 时包含 `code`、`message`、`retryable`；message UTF-8 最大 4 KiB |

快照与重放规则：

- 快照是 Windows 的持久化状态视图，Android 必须按业务 ID upsert，不得创建重复记录。
- Windows 在最后一页发出前不发送本轮业务重放；`isLast = true` 后，再把每条待确认文字作为独立 `text.send` 依次重放，避免大文字撑破 snapshot。
- 对 `server_to_client` 的 `queued` 或 `offered` 项，Windows 在快照后用相同 ID 重发 `transfer.offer`；快照不等同于 accepted。
- Android 自己尚未提交的 `client_to_server` 队列不依赖服务端快照，welcome 后用原 ID 发送 offer。
- `terminalAckPending = true` 时，Android 按快照终态字段幂等落库，再发送 `transfer.terminalAck`。
- Android 本地存在“终态待确认”时，无论 Windows 快照是非终态还是相同终态，Android 都重发原终态消息；Windows 幂等落库后重发 `transfer.terminalAck`。
- 两端已持久化不同终态时返回 `invalid_state` 并记录诊断；唯一例外是最终内容已经发布时 `completed` 优先。
- 进程恢复时或确认 HTTP 正文已经中断时，`accepted`/`transferring` 转为 `state = failed`、`error.code = network_interrupted`；v1 不恢复偏移。`verifying` 可在 24 小时窗口内继续 digest/terminal 协调。

## 8. 文字

### 发送

```json
{
  "v": 1,
  "type": "text.send",
  "id": "d8290645-426e-48d6-a99f-794437b0da10",
  "sentAt": "2026-07-11T12:01:00.000Z",
  "payload": {
    "textId": "067522eb-eb29-42f1-9b8d-f627414a8fa6",
    "text": "hello",
    "createdAt": "2026-07-11T12:01:00.000Z"
  }
}
```

### 回执

```json
{
  "v": 1,
  "type": "text.ack",
  "id": "9559056d-5391-4071-80ab-d3f0f52c98a8",
  "sentAt": "2026-07-11T12:01:00.050Z",
  "payload": {
    "textId": "067522eb-eb29-42f1-9b8d-f627414a8fa6",
    "storedAt": "2026-07-11T12:01:00.045Z"
  }
}
```

规则：

- `text` 去除首尾空白后不得为空；内容本身不被改写。
- UTF-8 编码后最大 256 KiB。
- 接收端必须先按 `textId` 幂等持久化，再发送 ack。
- 未收到 ack 时，发送端在重连后用相同 `textId` 重发。
- 重复 `text.send` 只重发 ack，不重复建立历史记录。
- 状态为 `queued → sending → delivered`；v1 无已读回执。
- 文字绕过文件队列。

## 9. 传输元数据

### Offer

```json
{
  "v": 1,
  "type": "transfer.offer",
  "id": "52c11f5e-0827-4f0f-b046-67b860811df7",
  "sentAt": "2026-07-11T12:02:00.000Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "retryOf": null,
    "kind": "file",
    "name": "report.pdf",
    "size": 12345,
    "mediaType": "application/pdf",
    "createdAt": "2026-07-11T12:02:00.000Z"
  }
}
```

字段规则：

- `kind`：`file` 或 `image`。
- `size`：null 或非负 int64。无法从 Android ContentProvider 获得长度时为 null。
- `mediaType`：有效 MIME 字符串；未知时为 `application/octet-stream`。
- `name`：非空 basename，接收端仍必须净化。
- `retryOf`：首次发送为 null，重试时引用旧 transfer ID。

### 自动接受

```json
{
  "v": 1,
  "type": "transfer.accepted",
  "id": "70e8e8af-e1e8-41bb-8aef-fcf4dc821295",
  "sentAt": "2026-07-11T12:02:00.050Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "plannedName": "report (1).pdf"
  }
}
```

- 接收端在创建持久记录和“未发布临时对象”后才发送 accepted：Windows 使用 `.bob-<uuid>.part`；Android 使用 `IS_PENDING = 1` 的 MediaStore 行。
- 空间不足、元数据非法或方向繁忙时发送 `transfer.failed`。
- `plannedName` 是预期名称；若原子发布时出现竞争，最终 `storedName` 可以再次调整。

## 10. Android → Windows 内容流

1. Android 发送 `transfer.offer`。
2. Windows 返回 `transfer.accepted`。
3. Android 执行：

```http
PUT /bob/v1/transfers/67a54b3c-4276-4c08-91ed-366cc5aa3285/content HTTP/1.1
Content-Type: application/pdf
Content-Length: 12345
X-Bob-Transfer-Id: 67a54b3c-4276-4c08-91ed-366cc5aa3285
```

- 已知 `size` 时必须发送一致的 Content-Length。
- `size = null` 时可以省略 Content-Length 并使用 HTTP/1.1 chunked 或 HTTP/2 DATA frames。
- Windows 边读取边写入 `.part` 并增量计算 SHA-256。
- 正文结束且流成功落盘后返回：

```http
HTTP/1.1 202 Accepted
Content-Type: application/json; charset=utf-8
Cache-Control: no-store

{
  "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
  "bytesReceived": 12345,
  "state": "verifying"
}
```

4. Android 发送 `transfer.digest`。
5. Windows 比对摘要和实际字节数，原子发布，然后发送 `transfer.completed`。

## 11. Windows → Android 内容流

1. Windows 发送 `transfer.offer`。
2. Android 返回 `transfer.accepted`。
3. Android 执行：

```http
GET /bob/v1/transfers/67a54b3c-4276-4c08-91ed-366cc5aa3285/content HTTP/1.1
```

Windows 成功响应：

```http
HTTP/1.1 200 OK
Content-Length: 12345
Content-Type: application/pdf
Content-Disposition: attachment; filename*=UTF-8''report.pdf
Cache-Control: no-store
X-Content-Type-Options: nosniff
X-Bob-Transfer-Id: 67a54b3c-4276-4c08-91ed-366cc5aa3285
```

- `size = null` 时服务端可省略 Content-Length。
- Android 不得使用 Content-Disposition 决定最终文件名；名称来自已接受的 offer。
- Android 将正文直接流入 accepted 阶段创建的 pending MediaStore URI；图片使用 Images collection，普通文件使用 Downloads collection。
- 双方在流中增量计算 SHA-256。
- Windows 正常结束响应正文后发送 `transfer.digest`。
- Android 比对成功后将 MediaStore `IS_PENDING` 切换为 0，再发送 `transfer.completed`；失败前不得让该项对其他应用可见。

## 12. 摘要与完成

### Digest

```json
{
  "v": 1,
  "type": "transfer.digest",
  "id": "5b8a1554-b3ca-47fd-9921-e859795cdf08",
  "sentAt": "2026-07-11T12:03:00.000Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "algorithm": "sha-256",
    "value": "base64url-without-padding",
    "bytes": 12345
  }
}
```

正文发送方发送 digest。接收方必须同时验证：

- algorithm 等于 `sha-256`。
- `bytes` 等于实际接收字节数。
- 已知 offer size 时，`bytes` 也等于 offer size。
- 本地增量 SHA-256 等于 `value`。

WSS 与 HTTPS 不共享消息顺序。尤其 Windows→Android 时，digest 可能先于 Android 的 HTTP 读取协程观察到 EOF；接收方必须先幂等保存 digest，等本地正文进入 `verifying` 后再校验，不得把这种到达顺序视为 `invalid_state`。同 ID 的重复 digest 必须相同；值冲突返回 `invalid_metadata`。

### Completed

```json
{
  "v": 1,
  "type": "transfer.completed",
  "id": "1e3c5cd9-5b06-492d-859d-2eb7e3155139",
  "sentAt": "2026-07-11T12:03:00.050Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "bytes": 12345,
    "sha256": "base64url-without-padding",
    "storedName": "report (1).pdf",
    "completedAt": "2026-07-11T12:03:00.045Z"
  }
}
```

- 只有摘要匹配且最终内容发布成功，接收方才发送 completed。Windows 以禁止覆盖的原子移动发布；Android 以 MediaStore `IS_PENDING: 1 → 0` 发布。
- 发送方只有收到 completed 才将自己的记录标为完成。
- `storedName` 不得包含本地完整路径。
- 正文完成而 WSS 暂时断开时，接收方保留未发布临时对象与 `verifying` 状态；重连后双方以相同 transfer ID 重发 digest/完成协调。
- digest 等待清理周期固定为 24 小时，并作为实现常量写入测试；超时后置为 `state = failed`、`error.code = digest_timeout`，删除 Windows 临时文件或 Android pending MediaStore 行。

### Terminal ack

收到 `transfer.completed`、`transfer.failed` 或 `transfer.canceled` 的一方必须先幂等持久化终态，再发送：

```json
{
  "v": 1,
  "type": "transfer.terminalAck",
  "id": "965b2584-a9dc-4fa5-a466-d9e852ebbf3a",
  "sentAt": "2026-07-11T12:03:00.080Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "state": "completed"
  }
}
```

- `state` 必须是 `completed`、`failed` 或 `canceled`，并与已持久化终态一致。
- 终态发出方必须跨重启保留“待确认”标记，直到收到匹配的 terminalAck。
- 重连后终态发出方重放原终态消息；Windows 发出的待确认终态也出现在 snapshot。
- terminalAck 可以安全重复；收到后只清除投递待确认标记，不删除历史记录。
- terminalAck 不属于传输状态机，也不占用方向队列。

## 13. 状态机

```text
queued
  -> offered
  -> accepted
  -> transferring
  -> verifying
  -> completed

任一非终态 -> failed
任一非终态 -> canceled
```

状态约束：

- `completed`、`failed`、`canceled` 是终态。
- 最终内容已经发布时，`completed` 优先于迟到的 cancel。
- 同一 transfer ID 不得从终态回到非终态。
- 重试创建新 ID 并通过 `retryOf` 关联旧记录。
- HTTP 请求只有在对应方向已 accepted 时才合法。
- 每方向最多一个项目处于 `offered`、`accepted`、`transferring` 或 `verifying` 之一；该项目直到终态前持续占用方向队列。两个方向彼此独立。

## 14. 进度、取消与失败

### Progress

接收方最多每秒发送 4 个：

```json
{
  "v": 1,
  "type": "transfer.progress",
  "id": "2b16664b-a016-4e65-a3d6-0a6d265f7487",
  "sentAt": "2026-07-11T12:02:30.000Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "bytes": 5242880,
    "total": 10485760
  }
}
```

`total` 与 offer `size` 一致；大小未知时为 null。速度和 ETA 不上协议，由 UI 从采样计算。

### Cancel

```json
{
  "v": 1,
  "type": "transfer.cancel",
  "id": "c73f7fe4-cf1f-4077-a78a-e492e0de8fee",
  "sentAt": "2026-07-11T12:02:35.000Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "reason": "user"
  }
}
```

发送或收到 cancel 的一方都必须立即停止自己的 HTTP 读写并关闭句柄。文件内容接收方拥有未发布临时对象，无论它是 cancel 的发送方还是接收方，都必须删除自己的 Windows `.part` 或 Android pending MediaStore 行。收到 cancel 的一方随后响应：

```json
{
  "v": 1,
  "type": "transfer.canceled",
  "id": "1019eae4-052b-4563-a1d4-c380c9a12b67",
  "sentAt": "2026-07-11T12:02:35.020Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "canceledAt": "2026-07-11T12:02:35.018Z"
  }
}
```

### Failed

```json
{
  "v": 1,
  "type": "transfer.failed",
  "id": "2293af4a-7f22-418e-a05c-1272438af1c7",
  "sentAt": "2026-07-11T12:03:10.000Z",
  "payload": {
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285",
    "code": "checksum_mismatch",
    "message": "SHA-256 values differ.",
    "retryable": true
  }
}
```

`message` 只作显示；重试判断以 code 与 `retryable` 为准。

## 15. 队列与离线语义

- 同方向按创建顺序 FIFO；只有队首可从 `queued` 进入 `offered`，且在其进入终态前不得 offer 下一项。
- 方向已经被另一个非终态项目占用时，乱序 offer 或内容请求返回 `direction_busy`。
- 多选创建多个独立 transfer。
- 文字不受文件队列阻塞。
- Windows 持久化 Windows→Android 的待发文字、文件路径与元数据，进程重启后仍在。
- Android 重连后通过 snapshot 取得 Windows 待发文件项目；待确认文字在最后一页后以独立 `text.send` 重放。
- Windows 发送正文前重新打开源路径；源不存在或内容无法读取时发送 `transfer.failed`，其中 `code = source_missing`。
- Android 系统分享的 `content://` URI 只保证当前前台会话；一期不承诺 Android 进程退出或长时间离线后恢复该待发项。
- 当前内容流因网络中断失败时不得从偏移续传；用户重试从 0 开始。
- 队列和历史可持久化，但已完成文件内容不复制进数据库。

### 清空历史与协议 tombstone

- “清空历史”只删除用户可见的已终结记录、已送达文字正文和缩略图缓存；不得删除活动/离线队列。
- 待 `text.ack` 的发出文字必须保留 ID 与正文；待 digest/terminal 协调的记录必须保留完成协调所需字段。
- 待 `terminalAck` 的终态必须保留完整终态 payload 和投递标记，跨重启继续重放。
- terminalAck 完成后，传输记录可以缩减为隐藏去重 tombstone，但必须保留 `transferId`、不可变元数据摘要和完整原终态 payload，以便对重复 offer 确定性重放 completed、failed 或 canceled。
- 已确认文字可缩减为只含 `textId` 的隐藏去重 tombstone，不保留正文。
- v1 不自动过期这些最小 tombstone；它们不显示在时间线，也不计入 snapshot。
- 清空历史不得删除已接收文件；文件删除是操作系统中的独立用户动作。

## 16. 幂等与竞态

- `textId` 与 `transferId` 在各自命名空间内全局唯一。
- 同 ID、同元数据的重复 offer 返回先前 accepted、completed 或终态。
- 同 ID 但 name、size、kind 等不可变元数据不同，返回 `invalid_metadata`。
- 重复 PUT/GET 只在原传输尚未开始且状态允许时接受；不做 Range/offset 恢复。
- 接收方完成原子发布与终态持久化后，再发 completed。
- completed、failed、canceled 与 terminalAck 消息都可以安全重发。
- cancel 与 HTTP 完成竞态时：
  - 尚未发布：cancel 生效并删除未发布临时对象。
  - 已发布：completed 生效，重发 completed。
- digest 与断线竞态时，双方保留相同 ID 的摘要/`verifying` 状态，重连后重放，不创建新文件。

## 17. 文件名与路径安全

共同文件名规则：

- 把 `name` 视为 basename，丢弃任何目录语义。
- Unicode 归一化为 NFC。
- 移除 `/`、`\`、NUL、控制字符，以及 Windows 非法字符 `<>:"|?*`。
- 去除 Windows 文件名尾部空格和点。
- 对 `CON`、`PRN`、`AUX`、`NUL`、`COM1..9`、`LPT1..9` 等保留名加下划线前缀。
- 空名称改为 `unnamed`。
- 最多保留 200 UTF-8 bytes，尽量保留扩展名且不得截断 Unicode code point。
- 同名采用 `name (1).ext`、`name (2).ext`；最终创建必须禁止覆盖。
- 不信任 MIME 或扩展名；不得自动执行或自动打开接收内容。

Windows 额外必须：

- 将候选最终路径绝对化后验证仍在配置的接收根目录内。
- 临时文件只按 ID 命名：`.bob-<uuid>.part`。
- 发布时使用禁止覆盖的原子移动；失败、取消、校验不符或超时后删除临时文件。

Android 额外必须：

- `kind = image` 只能写入 MediaStore Images + `Pictures/BOB`；`kind = file` 只能写入 MediaStore Downloads + `Download/BOB`。
- 不构造或校验底层绝对文件路径；只校验目标 collection、`RELATIVE_PATH` 与本次 `insert` 返回的 content URI。
- 创建时设置 `IS_PENDING = 1`，验证完成后才切换为 0；失败、取消、校验不符或超时后删除该 MediaStore 行。

## 18. 错误码

| code | retryable 默认值 | 含义 |
| --- | --- | --- |
| `unsupported_version` | false | 无协议版本交集 |
| `invalid_message` | false | envelope 或 payload 无效 |
| `message_too_large` | false | 超过 WSS 限制 |
| `peer_busy` | true | 已有活动 Android 会话 |
| `invalid_state` | false | 当前状态不允许该操作 |
| `transfer_not_found` | false | 未知 transfer ID |
| `invalid_metadata` | false | 元数据非法或同 ID 冲突 |
| `unsupported_kind` | false | kind 不支持 |
| `wrong_direction` | false | 使用了相反方向端点 |
| `direction_busy` | true | 该方向已有活动项 |
| `source_missing` | true | 源文件不存在或 URI 失效 |
| `permission_denied` | true | 无读取或写入权限 |
| `insufficient_storage` | true | 空间不足 |
| `size_mismatch` | true | 声明与实际字节不符 |
| `checksum_mismatch` | true | SHA-256 不一致 |
| `digest_timeout` | true | 摘要协调超时 |
| `network_interrupted` | true | 网络流中断 |
| `io_error` | true | 其他文件 I/O 错误 |
| `canceled` | true | 用户取消，可重新创建 |
| `internal_error` | true | 未分类内部错误 |

HTTP 建议映射：

| 状态 | code |
| --- | --- |
| 400 | `invalid_metadata` |
| 404 | `transfer_not_found` |
| 409 | `invalid_state`、`wrong_direction`、`direction_busy` |
| 413 | `message_too_large` |
| 422 | `size_mismatch` |
| 507 | `insufficient_storage` |
| 500 | `internal_error` |

不得因缺少 Content-Length 返回 411；`size = null` 是合法输入。

HTTP 错误体：

```json
{
  "error": {
    "code": "invalid_state",
    "message": "Transfer has not been accepted.",
    "retryable": false,
    "transferId": "67a54b3c-4276-4c08-91ed-366cc5aa3285"
  }
}
```

## 19. 实现限制

- 单个 WSS envelope：1 MiB。
- 单个 text UTF-8：256 KiB。
- 文件大小：协议不设人为上限；所有计数使用 int64。
- 每方向活动文件数：1。
- 进度事件：每传输最多 4/s。
- 文件内容不得放入 WSS。
- v1 不支持 Range、offset、断点续传、压缩协商或目录结构。

## 20. 契约验证门槛

实现开始时必须建立 C# 与 Kotlin 共用的 golden fixtures，至少覆盖：

- hello、welcome、snapshot。
- snapshot 多页连续编号、768 KiB 单页上限、末页前断线后更换 snapshotId，以及末页后独立重放待确认文字。
- text.send/ack 与重复 ack。
- 已知/未知 size 的 file 与 image offer。
- accepted、progress、digest、completed、terminalAck 及重复 terminalAck。
- cancel/canceled、failed、通用 error。
- 未知字段兼容。
- null、int64 边界、非 ASCII 和 Emoji 文件名。
- 同 ID 同元数据重放与冲突元数据拒绝。

自动化集成测试至少覆盖：

- 首次试连接、严格重连、手动 IP 命中旧 ID、证书用途错误与错误 pin。
- Android→Windows PUT 以及 Windows→Android GET。
- chunked/未知长度。
- 零字节与 1 GB 流。
- 正文中断、WSS 在 digest 前断开、terminalAck 丢失重放、cancel/complete 竞态。
- 校验失败不发布最终文件或 MediaStore 可见项。
- 路径穿越、保留名、同名竞争和原子不覆盖。
- 两方向同时进行且每方向 FIFO。
