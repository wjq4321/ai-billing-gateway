# AI Billing Gateway

**为本地部署的大模型（Ollama）提供 API Key 认证与 Token 计费网关**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-120%20passed-brightgreen.svg)](#运行测试)
[![AI Generated](https://img.shields.io/badge/code-AI%20generated-orange.svg)](.github/AI-DISCLOSURE.md)

> ### ⚠️ AI 创作声明
>
> **本项目代码与文档主要由 AI 生成**，需求设计与测试验证由人类完成。
> 项目已在真实环境（Windows + MySQL 9.6 + Ollama）经过 120 个单元测试与完整端到端验证，
> 但**未经过专业安全审计**，生产使用前请自行评估。
>
> 详见 **[.github/AI-DISCLOSURE.md](.github/AI-DISCLOSURE.md)**（含已知限制与开发中修复的真实缺陷清单）。

---

架设在本地 **Ollama** 前面的反向代理网关，用 **C# / ASP.NET Core** 实现，为本地大模型补齐两个原厂没有的能力：

1. **强制 API Key 认证** —— 没有 Key 或 Key 无效，一律拒绝（401），不给上游留后门。
2. **Token 计量与计费** —— 按模型单价精确记账，支持配额、限速、账单与用量明细。

数据库用 **MySQL**，管理方式**同时提供 Web 后台和命令行工具**。

> **本项目与 Ollama 官方无关**,「Ollama」是其各自权利人的商标，此处仅为描述性使用。

---

## 目录

- [它解决什么问题](#它解决什么问题)
- [整体架构](#整体架构)
- [快速开始](#快速开始)
- [核心概念](#核心概念)
  - [计量模式：Exact vs Realtime](#计量模式exact-vs-realtime)
  - [定价优先级](#定价优先级)
  - [配额与限速](#配额与限速)
- [客户端接入](#客户端接入)
  - [curl](#curl)
  - [C#（含 HttpClient 与 OpenAI 兼容库）](#c含-httpclient-与-openai-兼容库)
  - [Python](#python)
- [命令行工具](#命令行工具)
- [Web 管理后台](#web-管理后台)
- [计费精度校准](#计费精度校准)
- [数据库结构](#数据库结构)
- [配置项说明](#配置项说明)
- [常见问题](#常见问题)
- [生产部署建议](#生产部署建议)
- [项目结构](#项目结构)

---

## 它解决什么问题

Ollama 默认监听 `localhost:11434`，**本身没有认证，也没有计费**。这意味着：

- 任何能访问该端口的人都能白嫖你的 GPU；
- 无法知道谁用了多少 token、该收多少钱；
- 无法限制某个用户/应用的用量，一个失控的脚本就能把卡跑满。

本网关把 Ollama 藏到身后，所有请求必须经过它：

```
客户端 ──(带 API Key)──► 计费网关 :8080 ──► Ollama :11434
                            │
                            ├─ 校验 Key（无 Key 一律 401）
                            ├─ 检查配额 / 限速
                            ├─ 解析 token 用量
                            └─ 计算费用 → 写入 MySQL
```

---

## 整体架构

| 组件 | 项目 | 职责 |
|---|---|---|
| 网关服务 | `AiBilling.Gateway` | HTTP 服务、认证中间件、反向代理、计费、管理后台 |
| 核心库 | `AiBilling.Core` | 实体、MySQL 仓储、计费引擎、token 估算、配额守卫 |
| 命令行 | `AiBilling.Cli` | 运维与自动化脚本 |
| 测试 | `AiBilling.Tests` | 66 个单元测试，覆盖计费/配额/解析逻辑 |

技术栈：.NET 8 + ASP.NET Core + MySql.Data + 原生 HTML/JS 后台（零前端依赖）。

---

## 快速开始

### 1. 前置条件

- .NET 8 SDK 或更高
- MySQL 5.7+ / 8.x / 9.x（本项目在 **MySQL 9.6** 上验证通过）
- Ollama 已在 `127.0.0.1:11434` 运行

### 2. 创建配置文件

`appsettings.json` 含密码，**不包含在仓库中**（见 `.gitignore`）。请从模板复制：

```bash
cp src/AiBilling.Gateway/appsettings.example.json src/AiBilling.Gateway/appsettings.json
```

然后编辑填入你自己的值：

```jsonc
{
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://0.0.0.0:8080" } }   // 网关监听地址
  },
  "Billing": {
    "OllamaBaseUrl": "http://127.0.0.1:11434",                  // 你的 Ollama 地址
    "ConnectionString": "Server=127.0.0.1;Port=3306;User ID=ai_billing;Password=你的密码;Database=ai_billing;CharSet=utf8mb4;AllowPublicKeyRetrieval=true;SslMode=Disabled;",
    "MeteringMode": "Realtime",                                 // 或 Exact
    "AdminUser": "admin",
    "AdminPassword": "改成你自己的密码"                          // ★ 务必修改
  }
}
```

> **SslMode 说明**：连接本机 MySQL 用 `Disabled` 最省事；跨网络部署请改成 `Required`。
>
> **建议**：不要用 `root` 连数据库，单独建一个只授权 `ai_billing` 库的账号：
> ```sql
> CREATE USER 'ai_billing'@'127.0.0.1' IDENTIFIED BY '强密码';
> GRANT ALL PRIVILEGES ON ai_billing.* TO 'ai_billing'@'127.0.0.1';
> FLUSH PRIVILEGES;
> ```
>
> 如需本地覆盖而不改动仓库文件，可另建 `appsettings.Local.json`（已 gitignore），
> 它的优先级高于 `appsettings.json`。

### 3. 构建并启动

```bat
build.cmd           :: 构建全部项目
start-gateway.cmd   :: 启动网关
```

或者手动：

```bat
dotnet build src\AiBilling.Gateway\AiBilling.Gateway.csproj -c Release
dotnet src\AiBilling.Gateway\bin\Release\net8.0\AiBilling.Gateway.dll
```

首次启动会**自动建库建表**（库名取自连接串里的 `Database=`），无需手动执行 SQL。

看到这样的输出就成功了：

```
============================================================
  AI 计费网关已启动
  Ollama 上游: http://127.0.0.1:11434
  计量模式   : Realtime
  管理后台   : http://localhost:8080/admin  (账号 admin)
============================================================
```

### 4. 配置定价并创建第一个 Key

```bat
:: 设定 gemma3 系列的价格（元 / 千 token）
dotnet src\AiBilling.Cli\bin\Release\net8.0\aibilling.dll prices set --model "gemma3*" --in 0.5 --out 1.5

:: 创建一个 Key：总额 100 万 token、金额上限 100 元、每分钟最多 60 次
dotnet src\AiBilling.Cli\bin\Release\net8.0\aibilling.dll keys create --name "张三" --tokens 1000000 --cost 100 --rpm 60
```

命令会**一次性显示明文 Key**（形如 `sk-xxxxxxxx`）——**请立即保存**，网关只存哈希值，之后无法再次查看。

### 5. 自检

```bat
aibilling.dll test
```

```
  [✓] 网关可达           http://127.0.0.1:8080
  [✓] MySQL 已连接
  [✓] 管理端登录成功
  [✓] Ollama 在线        发现 3 个模型
  [✓] 无 Key 请求被拒绝 (401)
  [✓] 无效 Key 被拒绝 (401)
  自检通过 ✓
```

---

## 核心概念

### 计量模式：Exact vs Realtime

这是本网关最关键的取舍，在 `appsettings.json` 的 `Billing:MeteringMode` 切换：

| | `Exact`（精确） | `Realtime`（实时，默认） |
|---|---|---|
| 转发方式 | 缓冲整个响应，读完再回传 | 逐块透传，边生成边下发 |
| Token 来源 | Ollama 返回的 `prompt_eval_count` / `eval_count` | 优先用流尾的真实计数；拿不到则用文本长度估算 |
| 计费精度 | **100% 准确** | 有真实计数时同样准确；纯估算时有偏差 |
| 首字延迟 | 高（要等全量生成完） | **低（秒出字）** |
| 适用场景 | 对账、结算、按量收费 | 交互式聊天、追求体感 |

**建议**：先用 `Realtime` 保证体验，然后看管理后台的「计费校准」页——如果估算偏差可接受，就一直用；如果偏差大且你要按 token 收真金白银，切到 `Exact`。

> 实测数据（gemma3:270m，Exact 模式）：
> Ollama 返回 `prompt_eval_count: 10, eval_count: 15`，网关记账同为 `10 / 15`，**完全一致**。

### 定价优先级

从高到低：

1. **Key 级覆盖**（`override_*`）—— 给单个客户定制价格
2. **模型精确匹配**（`gemma3:270m`）
3. **模型通配匹配**（`gemma*`、`qwen2.5:*`、`*`），**模式越长越优先**
4. **兜底规则** —— 未匹配到任何规则时按**免费**处理（避免误扣费）

支持两种计价方式：

- **按 Token**：`输入价 / 千 token`、`输出价 / 千 token` 分别定价
- **按次**：每次请求固定价格，忽略 token 数量

### 配额与限速

每个 Key 可独立配置。分两类，含义不同：

#### 一、累计额度（用满就停）

| 维度 | 超限响应 | 说明 |
|---|---|---|
| Token 总量 | `402 quota_tokens` | 输入+输出累计 |
| 金额上限 | `402 quota_cost` | 累计费用 |
| 请求数 | `402 quota_requests` | 累计次数 |
| 过期 / 禁用 | `401 key_unusable` | |

**请求前预检**：网关会先估算本次请求的 token 与费用，若预估就会超额，**直接拒绝，不会真的去跑推理**，避免白白浪费 GPU 时间：

```json
{
  "error": {
    "code": "quota_tokens",
    "message": "本次请求约需 524 token，将超出剩余配额 10"
  }
}
```

#### 二、速率限制（控制"多快"）

| 维度 | 行为 | 说明 |
|---|---|---|
| 每分钟请求数 | `429 rate_limit` | RPM，滑动窗口 |
| **输出速度** | **不拒绝，改为限速** | `maxOutputTokensPerSec`，见下 |
| **每分钟 token** | `429 token_rate_per_minute` | 成本节流 |
| **每天 token** | `429 token_rate_per_day` | 成本节流 |
| 模型白名单 | `403 model_not_allowed` | 支持通配，如 `gemma*` |

##### 输出速度限速（token/秒）

限制模型"吐字"的快慢，用于**保护 GPU、防止单个用户独占算力**。

```bat
:: 限制该 Key 的输出速度为 5 token/秒
aibilling.dll keys update 1 --tok-per-sec 5
```

**实测效果**（Qwen2.5-0.5B，111 token 回复）：

| 配置 | 耗时 | 实际速率 |
|---|---|---|
| 不限速 | 0.76 秒 | 146.8 tok/s |
| 限 5 tok/s | 22.13 秒 | **5.0 tok/s** |

111 ÷ 5 = 22.2 秒，实测 22.13 秒，精度很高。

> ⚠️ **仅对流式响应（`stream: true`）生效**。非流式响应一次性返回，无法在传输层节流；
> 网关会事后校验并在日志中告警（可用 `Billing:EnforceRateOnNonStreaming` 关闭）。
>
> 另外 `MeteringMode: Exact` 会缓冲整个响应再回传，**也无法节流** ——
> 要用输出速度限速请切到 `Realtime` 模式。
>
> 还可用 `Billing:DefaultMaxOutputTokensPerSec` 设全局兜底限速（0=不限），
> Key 级配置优先。

##### token 消耗速率（每分钟 / 每天）

限制单位时间能烧多少 token，用于**成本节流**：

```bat
aibilling.dll keys update 1 --tok-per-min 50000     :: 每分钟最多 5 万 token
aibilling.dll keys update 1 --tok-per-day 2000000   :: 每天最多 200 万 token
```

超出后返回：

```json
{
  "error": {
    "code": "token_rate_per_minute",
    "message": "已达每分钟 token 限额：本分钟已用 441 / 上限 400"
  }
}
```

> **设计说明**：输出长度事先无法预知。若每次请求都按"满额输出"保守预估，
> 会出现"配额还有富余、却每次都被拦"的糟糕体验。因此网关采用
> **实际记账兜底**策略：只有**已经用超**才拒绝，不会因预估误伤正常请求。
> 需要在推理开始前严格拒绝的场景（如请求已明确 `num_predict`），
> 可使用 `TokenRateLimiter.CheckStrict`。

---

## 客户端接入

**唯一改动**：把原来的 `http://localhost:11434` 换成网关地址 `http://localhost:8080`，并带上 API Key。

Key 支持三种携带方式（任选其一）：

```
Authorization: Bearer sk-xxxx      ← 推荐
X-API-Key: sk-xxxx
?api_key=sk-xxxx                   ← 仅调试用
```

### curl

```bash
curl http://localhost:8080/api/chat \
  -H "Authorization: Bearer sk-你的密钥" \
  -H "Content-Type: application/json" \
  -d '{
        "model": "gemma3:270m",
        "messages": [{"role":"user","content":"你好"}],
        "stream": false
      }'
```

响应头会带回本次计费结果：

```
X-Billing-Tokens-Prompt: 10
X-Billing-Tokens-Completion: 12
X-Billing-Cost: 0.023
X-Billing-Currency: CNY
```

> 流式（`stream: true`）时，输出 token 在流结束时才确定，因此响应头里为
> `X-Billing-Tokens-Completion: streaming`。**完整数值一定落库**，
> 用 `aibilling usage` 或管理后台查看即可。

### C#（含 HttpClient 与 OpenAI 兼容库）

**方式一：原生 HttpClient**

```csharp
using System.Net.Http.Json;

var handler = new HttpClientHandler();
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };

// 只需加这一行：把 API Key 放进默认请求头
http.DefaultRequestHeaders.Add("Authorization", "Bearer sk-你的密钥");

var resp = await http.PostAsJsonAsync("/api/chat", new
{
    model = "gemma3:270m",
    messages = new[] { new { role = "user", content = "你好" } },
    stream = false
});

resp.EnsureSuccessStatusCode();

// 读取本次计费结果
if (resp.Headers.TryGetValues("X-Billing-Cost", out var cost))
    Console.WriteLine($"本次费用：{cost.First()}");

var result = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>();
Console.WriteLine(result?.Message?.Content);

record OllamaChatResponse(OllamaMessage? Message, long PromptEvalCount, long EvalCount);
record OllamaMessage(string Role, string Content);
```

**方式二：OpenAI 兼容客户端**（如 `OpenAI` 官方 SDK）

网关同时计量 OpenAI 风格的 `/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`
（Ollama 原生就提供这些路径），直接改 `Endpoint` 即可：

```csharp
using OpenAI;
using OpenAI.Chat;

var client = new ChatClient(
    model: "gemma3:270m",
    credential: new System.ClientModel.ApiKeyCredential("sk-你的密钥"),
    options: new OpenAIClientOptions { Endpoint = new Uri("http://localhost:8080/v1") });

ChatCompletion completion = await client.CompleteChatAsync("你好");
Console.WriteLine(completion.Content[0].Text);
```

> `/v1` 路径同样受配额、限速、白名单约束，并按 token 计费
> （网关会自动识别 OpenAI 的 `usage.prompt_tokens` / `usage.completion_tokens` 字段）。

**方式三：OllamaSharp**

```csharp
using OllamaSharp;

var client = new OllamaApiClient(new Uri("http://localhost:8080"), "gemma3:270m");
client.DefaultRequestHeaders.Add("Authorization", "Bearer sk-你的密钥");

await foreach (var chunk in client.GenerateAsync("你好"))
    Console.Write(chunk?.Response);
```

### Python

```python
import requests

r = requests.post(
    "http://localhost:8080/api/chat",
    headers={"Authorization": "Bearer sk-你的密钥"},
    json={
        "model": "gemma3:270m",
        "messages": [{"role": "user", "content": "你好"}],
        "stream": False,
    },
    timeout=300,
)
print(r.json()["message"]["content"])
print("本次费用：", r.headers.get("X-Billing-Cost"))
```

用 `openai` 库也一样：

```python
from openai import OpenAI

client = OpenAI(base_url="http://localhost:8080/v1", api_key="sk-你的密钥")
resp = client.chat.completions.create(
    model="gemma3:270m",
    messages=[{"role": "user", "content": "你好"}],
)
print(resp.choices[0].message.content)
```

---

## 命令行工具

```bat
dotnet aibilling.dll <命令> [子命令] [选项]
```

### 命令一览

| 命令 | 作用 |
|---|---|
| `test` | **部署自检**，逐项验证网关/数据库/Ollama/强制 Key |
| `health` | 查看运行状态 |
| `stats` | 运行概览 + 近 7 天消耗趋势图 |
| `models` | 列出 Ollama 模型及各自当前定价 |
| `keys list` | 列出所有 Key 及用量 |
| `keys create` | 新建 Key |
| `keys show <id>` | 查看 Key 详情 |
| `keys update <id>` | **修改额度 / 定价 / 名称**（只改传入的字段） |
| `keys enable\|disable <id>` | 启用 / 禁用 |
| `keys reset <id>` | 清零累计用量 |
| `keys delete <id>` | 删除 Key |
| `prices list` | 列出定价规则 |
| `prices set` | 设置定价 |
| `prices delete <名>` | 删除定价 |
| `usage` | 用量明细 |
| `calib` | 计费精度校准报告 |
| `billing estimate` | 发一次真实请求，查看计费结果 |

### 修改额度（`keys update`）

```bat
:: 提高配额
aibilling.dll keys update 1 --tokens 5000000 --cost 500 --rpm 300

:: 限制输出速度（5 token/秒）与消耗速率
aibilling.dll keys update 1 --tok-per-sec 5
aibilling.dll keys update 1 --tok-per-min 50000 --tok-per-day 2000000

:: 取消某项限制
aibilling.dll keys update 1 --clear-cost
aibilling.dll keys update 1 --clear-tok-per-sec

:: 给某个客户改专属价
aibilling.dll keys update 3 --in-price 0.3 --out-price 0.9
aibilling.dll keys update 3 --clear-prices      :: 恢复用模型默认价

:: 改状态 / 改名 / 重设模型白名单
aibilling.dll keys update 1 --status Disabled
aibilling.dll keys update 1 --name "客户A（续费）"
aibilling.dll keys update 1 --models "gemma*,qwen2.5:*"
```

> `keys update` 采用**只改你传入的字段**的策略：先读原值再合并提交，
> 因此改额度时**不会**误清白名单和专属价。
> 直接调 `PUT /api/admin/keys/{id}` 则是整体覆盖语义，必须带全所有字段。

### 常用示例

```bat
:: 建一个带全套限制的 Key，并为它单独定价（覆盖模型默认价）
aibilling.dll keys create --name "客户A" ^
    --tokens 5000000 --cost 500 --requests 50000 --rpm 120 ^
    --models "gemma*,qwen2.5:*" ^
    --in-price 0.4 --out-price 1.2

:: 按 token 计价
aibilling.dll prices set --model "gemma3:270m" --in 0.5 --out 1.5

:: 按次计价（每次 0.02 元）
aibilling.dll prices set --model "embedding-model" --req-price 0.02

:: 兜底价：所有未单独配置的模型
aibilling.dll prices set --model "*" --in 1 --out 2

:: 查看近 20 条用量
aibilling.dll usage --limit 20

:: 只看某个 Key 的用量
aibilling.dll usage --key 3 --limit 100

:: 发一次测试请求，立刻看到计费了多少
aibilling.dll billing estimate --key sk-xxxx --model gemma3:270m --prompt "你好"
```

### 全局选项

```bat
--url <地址>        网关地址（默认 http://127.0.0.1:8080）
--user <用户名>     管理端账号
--password <密码>   管理端密码
```

---

## Web 管理后台

浏览器打开 **http://localhost:8080/admin**，用 `appsettings.json` 里的初始账号登录（默认 `admin` / `admin123`）。

| 标签页 | 功能 |
|---|---|
| **API Keys** | 新建 / **编辑** / 禁用 / 删除 Key，配额与用量进度条，一键清零 |
| **模型定价** | 增删改各模型单价，支持通配 |
| **用量明细** | 逐条请求记录：token、费用、耗时、首字延迟、估算偏差、来源 IP |
| **统计报表** | 近 14 天消耗趋势柱状图 + 各模型费用占比 |
| **计费校准** | 对比估算值与真实值，给出参数修正建议 |
| **账号安全** | **修改管理端用户名和密码** |
| **系统设置** | 当前运行配置与接入示例 |

### 编辑 API Key（额度、定价、状态）

在 **API Keys** 页点击任意一行的 **「编辑」** 按钮，即可弹窗修改：

- 名称、状态（启用 / 禁用）
- Token 总配额、金额上限、请求数上限、限速 RPM —— **留空表示不限制**
- 过期时间、允许的模型（支持 `gemma*` 通配）
- 该 Key 的专属定价（输入价 / 输出价 / 单次价，覆盖模型默认价）

弹窗底部会显示该 Key 的**累计用量**，方便判断该调多少。
保存后**立即生效**，无需重启网关；已累计的用量不会被清零。

### 修改管理端账号（用户名 / 密码）

进入 **账号安全** 页：

1. 填写**当前密码**（验证身份，必填）
2. 填写想改的项：**新用户名**、**新密码 + 确认新密码**（只填想改的那个即可）
3. 点「保存并重新登录」

保存后**当前登录立即失效**，需要在新登录页用新凭据登录 ——
这是有意设计：改密后强制重新认证，避免旧会话继续可用。

**密码如何存储**：首次在网页修改账号时，网关会把账号写入数据库 `admin_users` 表，
密码用 **PBKDF2-HMAC-SHA256（10 万次迭代 + 随机盐）** 哈希存储，**不保存明文**。
此后登录以数据库为准，`appsettings.json` 里的旧密码**随即失效**。

> ⚠️ 改过网页账号后，**CLI 也需要用新凭据**。首次指定一次即可被记住：
> ```powershell
> aibilling.dll keys list --user 新用户名 --password 新密码
> # 之后直接 aibilling.dll keys list 就行
> ```
> 凭据保存在 CLI 程序目录下的 `aibilling-credentials.json`。
> 若只改了密码、用户名不变，也同样需要重新指定一次。

---

## 计费精度校准

`Realtime` 模式下，网关用文本长度估算 token。为了让你知道**估算到底准不准**，网关会同时记录：

- `estimated_completion_tokens` —— 估算值
- `actual_completion_tokens` —— Ollama 实测值（只要上游返回了计数就会记）

管理后台「计费校准」页或 `aibilling.dll calib` 会给出报告：

```
  计费精度校准（近 7 天）
  ─────────────────────────────────────────────
    有效样本数      : 17
    平均估算输出    : 38.18 token/次
    平均实测输出    : 25.00 token/次
    实测 / 估算     : 0.6549   （理想值 1.0）

    估算偏高，建议调大 Billing:CharsPerToken。
    建议 CharsPerToken 调整系数：× 1.5271
```

**怎么读**：

- 比值 **> 1**：实际消耗比记账的多 → **在亏钱**，应调小 `CharsPerToken`
- 比值 **< 1**：记账比实际多 → 在赚钱，但可能引起客户投诉
- 落在 **0.95 ~ 1.05**：可以接受，无需调整

> 注意：`Realtime` 模式在流尾拿到真实计数时会**直接用真实值计费**，
> 所以上面的偏差主要出现在上游未返回计数的场景。切到 `Exact` 模式则不存在偏差。

---

## 数据库结构

库名取自连接串的 `Database=`（默认 `ai_billing`），共 5 张表：

| 表 | 用途 |
|---|---|
| `api_keys` | Key 账户：哈希、配额、累计用量、专属定价 |
| `model_prices` | 模型单价规则 |
| `usage_records` | 逐次请求明细（计费凭据） |
| `admin_users` | 管理端账号（预留） |
| `audit_logs` | 操作审计 |

### api_keys

| 字段 | 说明 |
|---|---|
| `key_hash` | **SHA-256 哈希，明文永不落库** |
| `key_prefix` | 展示用缩略（`sk-abc...xyz`） |
| `status` | 1=启用 2=禁用 3=过期 |
| `allowed_models` | JSON 数组，空=不限制 |
| `quota_total_tokens` / `quota_total_cost` / `quota_total_requests` / `rate_limit_rpm` | 配额与限速，`NULL`=不限 |
| `used_*` | 累计用量（原子累加） |
| `override_*` | Key 级单价覆盖，`NULL`=用模型默认价 |

### usage_records 关键字段

| 字段 | 说明 |
|---|---|
| `prompt_tokens` / `completion_tokens` | **最终采信并计费的值** |
| `actual_*` | Ollama 实测值（用于校准） |
| `estimated_completion_tokens` | 估算值（用于校准） |
| `metering_mode` | 1=Realtime 2=Exact |
| `outcome` | 成功/上游错误/配额不足/超限速/客户端中断 |
| `first_token_ms` | 首字延迟，用于衡量体验 |

**失败请求不收费**（`cost = 0`），但仍会记录，方便排查。

---

## 配置项说明

`appsettings.json` → `Billing` 节：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `OllamaBaseUrl` | `http://127.0.0.1:11434` | 上游 Ollama 地址 |
| `ConnectionString` | — | MySQL 连接串 |
| `MeteringMode` | `Realtime` | `Exact` 或 `Realtime` |
| `CharsPerToken` | `1.7` | 估算参数，中文约 1.5、英文约 4 |
| `LogUsageRecords` | `true` | 是否记录明细 |
| `EnableCalibration` | `true` | 是否采集估算/实测对比数据 |
| `AdminUser` / `AdminPassword` | `admin` / `admin123` | **初始**管理账号，务必修改；在网页改过后即以数据库为准 |
| `AdminSessionSecret` | 空 | 会话签名密钥，生产环境请填随机串 |
| `Currency` | `CNY` | 币种标识 |
| `UpstreamTimeoutSeconds` | `600` | 上游超时 |
| `KeyCacheSeconds` | `15` | Key 缓存秒数（减少查库） |
| `RateLimitWindowSeconds` | `60` | 限速窗口 |
| `DefaultMaxOutputTokensPerSec` | `0` | **全局**输出速度上限（token/秒），0=不限；Key 级配置优先 |
| `EnforceRateOnNonStreaming` | `true` | 非流式响应是否事后校验输出速度（只告警不拒绝） |

---

## 常见问题

**Q：想重置数据（清空账单 / 恢复默认）怎么做？**
用 `reset.ps1`，分四个级别，带自动备份和二次确认：

```powershell
.\reset.ps1                    # 先看现状，不做修改
.\reset.ps1 -Level 账单        # 只清账单，保留 Key 和定价
.\reset.ps1 -Level key         # 清 Key + 定价 + 账单
.\reset.ps1 -Level 全部        # 连管理员账号一起清
.\reset.ps1 -Level 库          # 删库，重启网关自动重建
```

详见 [`docs/数据重置指南.md`](docs/数据重置指南.md)。

**Q：改了价格，要重启网关吗？**
不用。网关每 30 秒自动刷新定价，管理后台保存后立即生效（最迟 30 秒）。

**Q：网页上改了管理员密码，结果 CLI 登录不了？**
这是预期的。网页改密后凭据存在数据库里，`appsettings.json` 的旧密码失效。
在 CLI 里指定一次新凭据即可，之后会被自动记住：

```powershell
aibilling.dll keys list --user 新用户名 --password 新密码
```

**Q：忘记管理员密码了怎么办？**
删除数据库里那一行，就会回退使用 `appsettings.json` 里的初始账号：

```sql
DELETE FROM admin_users;
```

然后重启网关即可用配置文件里的账号登录。

**Q：能开多个管理账号吗？**
目前是单账号模式（`admin_users` 表只保留一行）。需要多账号需扩展该表与鉴权逻辑。

**Q：客户说 Key 泄露了怎么办？**
`aibilling.dll keys disable <id>` 立即禁用。若怀疑已产生损失，可 `keys reset <id>` 清零用量。

**Q：为什么流式响应的 `X-Billing-Tokens-Completion` 是 `streaming`？**
流式响应在返回第一块时就必须提交响应头，那时还不知道最终输出多少 token。完整数值已落库，用 `aibilling usage` 查看。

**Q：支持 Ollama 的 `/api/embed` 吗？**
支持，已列入计量端点。embedding 只有输入 token，按输入价计费即可。

**Q：支持 OpenAI 风格的 `/v1/*` 接口吗？**
支持，`/v1/chat/completions`、`/v1/completions`、`/v1/embeddings` 都会正常计费
（Ollama 原生提供这些路径）。网关能自动识别 OpenAI 格式的 `usage` 字段。

**Q：能不能不认证，直接当普通代理用？**
设计上**不允许**。所有 `/api/*` 路径强制要求 Key（仅 `/health`、`/admin` 除外）。这是本项目的核心目的。

**Q：多个网关实例部署时限速不准？**
是的。限速计数器在进程内存中，多实例各算各的。若要多实例部署，需把 `QuotaGuard` 的滑动窗口换成 Redis 计数。

**Q：token 估算偏差很大怎么办？**
看「计费校准」页的建议系数，调整 `Billing:CharsPerToken`（值越大，估算出的 token 越少）。或直接切 `Exact` 模式。

---

## 生产部署建议

1. **务必修改默认密码**：`AdminPassword` 与 MySQL 账号密码。
2. **改 `AdminSessionSecret`**：填一段随机字符串，避免会话令牌可被伪造。
3. **不要把 8080 直接暴露到公网**。若要跨机访问，前置 Nginx/Caddy 做 TLS，并只放行必要来源 IP。
4. **MySQL 用独立账号**，只授权 `ai_billing` 库：
   ```sql
   CREATE USER 'aibilling'@'127.0.0.1' IDENTIFIED BY '强密码';
   GRANT ALL PRIVILEGES ON ai_billing.* TO 'aibilling'@'127.0.0.1';
   FLUSH PRIVILEGES;
   ```
5. **定期归档 `usage_records`**：明细表增长最快，建议按月归档到历史表。
6. **注册为 Windows 服务**（开机自启）：
   ```bat
   sc create AiBillingGateway binPath= "C:\dotnet\dotnet.exe F:\...\AiBilling.Gateway.dll" start= auto
   ```

---

## 项目结构

```
ai聊天计费工具/
├─ AiBilling.slnx                    解决方案
├─ build.cmd                         一键构建
├─ start-gateway.cmd                 一键启动
├─ NuGet.Config                      本机离线包源配置
├─ src/
│  ├─ AiBilling.Core/                核心业务库
│  │  ├─ Models/Entities.cs          实体（Key、定价、用量记录）
│  │  ├─ Data/
│  │  │  ├─ DatabaseInitializer.cs   建库建表（幂等）
│  │  │  └─ BillingRepository.cs     MySQL 数据访问
│  │  ├─ Billing/
│  │  │  ├─ BillingEngine.cs         计费引擎 + 通配匹配
│  │  │  ├─ TokenEstimator.cs        token 估算（中英文感知）
│  │  │  ├─ QuotaGuard.cs            配额校验 + 滑动窗口限速
│  │  │  └─ OllamaUsageParser.cs     解析 Ollama 用量元数据
│  │  ├─ Security/ApiKeyHasher.cs    Key 生成与 SHA-256 哈希
│  │  └─ Options/BillingOptions.cs   配置模型
│  ├─ AiBilling.Gateway/             网关服务
│  │  ├─ Program.cs                  启动、依赖注入、引导
│  │  ├─ Middleware/
│  │  │  └─ ApiKeyAuthMiddleware.cs  ★ 强制 API Key 认证
│  │  ├─ Services/
│  │  │  ├─ MeteringProxy.cs         ★ 反向代理 + token 计量
│  │  │  ├─ BillingQueue.cs          异步入库队列
│  │  │  ├─ ApiKeyStore.cs           Key 缓存
│  │  │  ├─ AdminSessionService.cs   管理端会话与鉴权
│  │  │  └─ AdminPages.cs            内嵌 Web 后台页面
│  │  └─ Endpoints/AdminEndpoints.cs 管理 API
│  └─ AiBilling.Cli/                 命令行工具
├─ tests/AiBilling.Tests/            单元测试（66 个）
└─ docs/                             补充文档
```

---

## 运行测试

```bat
:: 常规环境
dotnet test tests\AiBilling.Tests\AiBilling.Tests.csproj

:: 受限/沙箱环境（VSTest testhost 不可用时）
dotnet tests\AiBilling.Tests\bin\Debug\net8.0\AiBilling.Tests.dll
```

覆盖范围（72 个用例）：

- **计费引擎**：通配匹配、定价优先级、按 token/按次计算、Key 级覆盖
- **配额守卫**：各类配额拦截、滑动窗口限速、请求前预检
- **Token 估算**：中英文差异、请求体解析、异常输入容错
- **Ollama 解析**：原生字段、OpenAI `usage` 字段、NDJSON 流、思维链计入、模型列表
- **API Key**：哈希、展示缩略、恒定时间比较、模型白名单

### 已实测验证的行为

除单元测试外，以下行为已在真实 MySQL 9.6 + Ollama 上端到端验证：

| 验证项 | 结果 |
|---|---|
| 无 Key 请求 | `401 missing_api_key` ✓ |
| 伪造 Key | `401 invalid_api_key` ✓ |
| Token 配额超限 | `402 quota_tokens`，且**不发请求给上游** ✓ |
| RPM 限速（限 2 次） | 前 2 次 200，第 3、4 次 `429` ✓ |
| **输出速度限速（5 tok/s）** | 111 token 耗时 22.13 秒 = **5.0 tok/s**；不限速时 0.76 秒 = 146.8 tok/s ✓ |
| **每分钟 token 配额** | 累计 441 token 后第 11 次被拦：`已用 441 / 上限 400` ✓ |
| **数据库自动迁移** | 旧库启动时自动补 3 个新列，无需手工 SQL ✓ |
| **Web 编辑 Key** | 改额度/限速/白名单，保存立即生效且不清空其他字段 ✓ |
| **网页改账号** | 旧凭据立即失效，新凭据可登录；PBKDF2 哈希落库 ✓ |
| 模型白名单 `gemma*` | `gemma3:270m` 放行，其他模型 `403` ✓ |
| Exact 模式计费 | 与 Ollama 返回的 `prompt_eval_count`/`eval_count` **完全一致** ✓ |
| Realtime 模式 | 流式响应头正常，尾块真实计数被采用 ✓ |
| OpenAI `/v1/chat/completions` | 正常计费，自动识别 `usage` 字段 ✓ |
| 用量落库 | 成功与被拦截请求均记录，失败请求 `cost=0` ✓ |
