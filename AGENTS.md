# BOB repository guidance

## Canonical context

开始工作前先阅读 `docs/active/current.md`，再按其中链接读取当前规格、协议契约与 ADR。若实现和协议冲突，以契约为准并显式报告，不要静默改变协议。

## Scope

- 保持 Android 客户端、Windows 服务端的单向拓扑。
- v1 只做同一 Wi-Fi 下的文字、图片和文件双向传输。
- 不添加设备绑定、账号授权、文件夹传输、断点续传或自动启动。
- 使用仓库已有结构和平台原生能力；引入新依赖前先取得批准。
- 不提交 `.tools/`、证书私钥、签名密钥或本机配置。

## Changes

- 只修改任务所需文件，避免无关重构和格式化。
- 协议字段、持久化结构或用户可见行为发生变化时，同步对应规范。
- Windows 和 Android 必须共享稳定的协议语义，不得各自猜测错误处理。

## Verification

从仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\check.ps1
```

交付时报告修改文件、实际运行的检查、未运行的检查与剩余风险。
