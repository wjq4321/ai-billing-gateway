# 贡献指南

感谢你有兴趣参与本项目。请先阅读以下内容。

---

## 重要：这是 AI 辅助开发的项目

本项目的代码主要由 AI 生成（详见 [AI-DISCLOSURE.md](AI-DISCLOSURE.md)）。

因此：

- **欢迎 AI 辅助提交的 PR** —— 这符合项目本身的工作方式，不需要刻意隐瞒
- 但请**真正测试过**你的改动，包括在真实环境验证行为
- 请说明你在什么环境下测试、验证了哪些场景
- 如果改动涉及计费逻辑，请附上单元测试

**不接受**：未经运行、仅凭推理生成的代码。

---

## 开发环境

### 前置条件

- .NET 8 SDK
- MySQL 5.7+ / 8.x / 9.x
- Ollama（用于端到端测试）

### 首次设置

```bash
git clone https://github.com/<your-fork>/ai-billing-gateway.git
cd ai-billing-gateway

# 创建配置（appsettings.json 不在仓库中）
cp src/AiBilling.Gateway/appsettings.example.json src/AiBilling.Gateway/appsettings.json
# 编辑填入你的 MySQL 连接串与管理端密码
```

### 构建与测试

```bash
# Windows
build.cmd

# 跨平台
dotnet build src/AiBilling.Core/AiBilling.Core.csproj -c Release
dotnet build src/AiBilling.Gateway/AiBilling.Gateway.csproj -c Release
dotnet build src/AiBilling.Cli/AiBilling.Cli.csproj -c Release
dotnet build tests/AiBilling.Tests/AiBilling.Tests.csproj -c Release

# 运行测试（120 个用例）
dotnet test tests/AiBilling.Tests/AiBilling.Tests.csproj
```

> 若 `dotnet test` 因环境限制无法运行（VSTest testhost 报
> `OpenProcess` 拒绝访问），可用内置的自运行入口：
> ```bash
> dotnet tests/AiBilling.Tests/bin/Release/net8.0/AiBilling.Tests.dll
> ```

### 离线环境

若所在网络无法访问 nuget.org，创建 `NuGet.Config.local`（已 gitignore）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="global-cache" value="%USERPROFILE%\.nuget\packages" />
  </packageSources>
</configuration>
```

`build.cmd` 会自动检测并使用它。

---

## 提交规范

提交信息用中文或英文均可，建议遵循 Conventional Commits：

```
feat: 增加按模型分时段定价
fix: 修复滑动窗口误删当日记录导致日限额失效
docs: 补充多实例部署说明
test: 补充限速器边界用例
refactor: 拆分 MeteringProxy 的流式处理逻辑
```

---

## 代码风格

- 遵循现有风格，保持一致
- **注释用中文**，说明「为什么」而非「做了什么」
- 关键决策处说明取舍原因（例如为什么用某种算法）
- 公共 API 需要 XML 文档注释

### 特别注意

**不要提交含密码的文件。** 以下文件已被 `.gitignore` 忽略：

```
src/AiBilling.Gateway/appsettings.json
src/AiBilling.Gateway/appsettings.*.json   （appsettings.example.json 除外）
NuGet.Config.local
aibilling-credentials.json
backups/
```

提交前请确认 `git status` 中没有这些文件。

---

## 修改计费 / 限速逻辑时

这部分直接关系到**钱**，请格外谨慎：

1. **必须加单元测试** —— 覆盖正常路径与边界（0 值、负值、恰好等于限额）
2. **说明对现有行为的影响** —— 是否会导致已部署实例计费变化
3. **考虑并发** —— 限速器与配额累加是多线程访问的
4. **不要破坏向后兼容** —— 数据库变更请用 `DatabaseInitializer` 的增量迁移，不要直接改 `CREATE TABLE`

---

## 数据库变更

新增字段请**同时**：

1. 修改 `DatabaseInitializer.SchemaSql` 中的 `CREATE TABLE`（供全新部署）
2. 在 `ApplyMigrationsAsync` 中登记增量迁移（供已有部署）

否则已有实例升级时会缺列。迁移逻辑必须**幂等**（可重复执行）。

---

## Pull Request

请确保：

- [ ] 代码能编译（`build.cmd` 通过）
- [ ] 所有测试通过
- [ ] 新增功能附带测试
- [ ] 未提交任何密码或本机私有路径
- [ ] 文档已同步更新（README / docs）
- [ ] PR 描述中说明**测试环境**与**验证方式**

---

## 许可证

提交贡献即表示你同意以 **GNU GPLv3** 授权你的贡献。

---

## 有问题？

开 Issue 即可。提问时请附上：

- 你的环境（操作系统、MySQL 版本、Ollama 版本）
- 完整错误信息
- 复现步骤

如果是安全问题，请走 [SECURITY.md](SECURITY.md) 的私有渠道，不要开公开 Issue。
