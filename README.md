# kimi-quota-tray

Windows 托盘小工具，实时显示 Kimi Code 额度（5 小时窗口 / 周额度 / Extra Usage 余额）。单文件 exe，免安装、无第三方依赖。

> [!IMPORTANT]
> **免责声明**
> - 本项目为**非官方第三方工具**，与月之暗面（Moonshot AI）无任何关联，未获其背书或认可
> - 额度数据来自 Kimi Code 官方开源代码（[MoonshotAI/kimi-code](https://github.com/MoonshotAI/kimi-code)，MIT 协议）中使用的内部接口（`/usages`，未写入官方文档），官方随时可能变更或关闭，本项目不保证持续可用
> - 使用者需自行承担风险，并请遵守 Kimi Code 服务条款
> - token 刷新使用的 OAuth client_id 来自 Kimi Code CLI 公开分发的客户端（公共客户端无法保密，社区工具通行做法）

## 功能

- **托盘图标**：直接显示剩余百分比数字，按余量变色（>50% 绿 / 20–50% 黄 / <20% 红）
  - 可切换显示源：5 小时滚动窗口（默认）/ 周额度 / Extra 余额
- **左键图标**：弹出详情面板（无边框圆角卡片式 UI：5 小时窗口、周额度、月总额度、Extra Usage 余额各一张卡片，带进度条与重置倒计时），支持高 DPI，窗口可常驻、每次刷新后自动同步数据
- **用量趋势与烧速估算**：本地记录用量历史（`history.jsonl`，仅存本机），绘制 5 小时窗口趋势曲线；按最近消耗速度估算耗尽时间（Theil-Sen 稳健回归），并给出周额度「重置前够不够用」的判断
- **右键菜单**：额度详情、复制额度摘要、立即刷新、显示源、刷新间隔（1/3/5/10 分钟）、低额度气泡提醒、回满提醒、开机自启、打开控制台
- 悬停默认无提示；异常状态（断网 / 凭证失效）显示灰色 `!` 图标，悬停可查看原因

## 使用

1. 前置条件：Windows 10/11，且本机已安装并登录 [Kimi Code CLI](https://github.com/MoonshotAI/kimi-code)（本工具复用其登录凭证，自身不处理账号密码）
2. 从 Releases 下载 `KimiQuotaTray.exe`，双击运行即可（无需安装，.NET Framework 4.8 为系统自带）
3. 如需开机自启：右键托盘图标 → 勾选「开机自启」

## 工作原理

```
读 %USERPROFILE%\.kimi-code\credentials\kimi-code.json（CLI 的登录凭证）
  → access_token 过期则用 refresh_token 换新（轮换后原子写回，与 CLI 方式一致）
  → GET https://api.kimi.com/coding/v1/usages
  → 解析 → 更新托盘图标
```

- 纯查询接口，**不消耗任何模型额度**
- 令牌仅发往 `api.kimi.com` 和 `auth.kimi.com` 两个官方域名，不请求任何其他地址；exe 不内嵌任何用户凭证
- 本工具使用自己的 User-Agent（`kimi-quota-tray/1.2`），不伪装官方客户端

## 从源码编译

无需安装 Visual Studio，直接双击 `build.bat`（调用 Windows 自带的 .NET Framework 4.8 编译器）：

```bat
build.bat
```

产出单文件 `KimiQuotaTray.exe`。

## License

[MIT](LICENSE)
