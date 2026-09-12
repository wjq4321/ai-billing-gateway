<#
.SYNOPSIS
    AI 计费网关 · 数据重置工具

.DESCRIPTION
    按级别重置数据库内容，可选自动备份。
    所有破坏性操作都需要显式确认，不会静默执行。

    级别：
      账单   只清用量明细 + 把累计用量归零。Key、定价、账号都保留。
            适合"账目重算"，客户端无需改动。
      key    清 Key + 定价 + 账单，保留管理员账号。
            回到刚部署完的状态，需要重新创建并分发 Key。
      全部   连管理员账号一起清。登录会回退到 appsettings.json 的初始账号。
      库     直接 DROP DATABASE，重启网关时自动重建（等价全新部署）。

.EXAMPLE
    # 查看当前数据量，不做任何修改
    .\reset.ps1 -Level status

    # 清账单（会先备份，再要求输入 YES 确认）
    .\reset.ps1 -Level 账单

    # 完全重置，跳过备份
    .\reset.ps1 -Level 全部 -NoBackup

    # 预演：只打印将要执行的 SQL，不真正执行
    .\reset.ps1 -Level key -WhatIf
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('status', '账单', 'key', '全部', '库')]
    [string]$Level = 'status',

    # 跳过备份（默认会备份到 backups\ 目录）
    [switch]$NoBackup,

    # 只打印将要做的事，不执行
    [switch]$WhatIf,

    # 跳过交互确认（用于自动化脚本，慎用）
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 配置读取

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppSettings = Join-Path $Root 'src\AiBilling.Gateway\appsettings.json'

if (-not (Test-Path $AppSettings)) {
    Write-Error "找不到配置文件：$AppSettings"
    exit 1
}

# 从 appsettings.json 解析连接串，避免在这里重复硬编码密码
$cfg = Get-Content $AppSettings -Raw -Encoding UTF8 | ConvertFrom-Json
$connStr = $cfg.Billing.ConnectionString
if ([string]::IsNullOrWhiteSpace($connStr)) {
    Write-Error "appsettings.json 里没有配置 Billing:ConnectionString"
    exit 1
}

# 解析 MySQL 连接串（key=value; 形式）
function Get-ConnValue([string]$conn, [string]$key) {
    foreach ($part in $conn -split ';') {
        $kv = $part -split '=', 2
        if ($kv.Count -eq 2 -and $kv[0].Trim() -ieq $key) { return $kv[1].Trim() }
    }
    return $null
}

$DbHost = Get-ConnValue $connStr 'Server'
$DbPort = Get-ConnValue $connStr 'Port'
$DbUser = Get-ConnValue $connStr 'User ID'
$DbPass = Get-ConnValue $connStr 'Password'
$DbName = Get-ConnValue $connStr 'Database'

if (-not $DbPort) { $DbPort = '3306' }
if (-not $DbHost) { $DbHost = '127.0.0.1' }

# 查找 mysql.exe / mysqldump.exe
#
# 查找顺序：
#   1) 环境变量 AIBILLING_MYSQL_BIN 指定的目录（自定义安装位置用这个）
#   2) PATH 中的可执行文件
#   3) Windows 常见安装路径
function Find-MySqlTool([string]$exe) {
    # 1) 环境变量优先，便于把 MySQL 装在非标准目录
    if ($env:AIBILLING_MYSQL_BIN) {
        $custom = Join-Path $env:AIBILLING_MYSQL_BIN $exe
        if (Test-Path $custom) { return $custom }
    }

    # 2) PATH
    $cmd = Get-Command $exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 3) 常见安装位置（自动匹配版本号目录）
    $patterns = @(
        'C:\Program Files\MySQL\MySQL Server *\bin',
        'C:\Program Files (x86)\MySQL\MySQL Server *\bin',
        'D:\MySQL\*\bin',
        'F:\server\MySQL\*\*\bin'      # 自定义安装位置示例，可按需增删
    )
    foreach ($p in $patterns) {
        $dirs = Get-ChildItem $p -Directory -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            $candidate = Join-Path $d.FullName $exe
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

$MySql = Find-MySqlTool 'mysql.exe'
$MyDump = Find-MySqlTool 'mysqldump.exe'

if (-not $MySql) {
    Write-Error "找不到 mysql.exe，请把 MySQL 的 bin 目录加入 PATH 或修改脚本中的候选路径"
    exit 1
}

$env:MYSQL_PWD = $DbPass

function Invoke-Sql([string]$sql, [switch]$Silent) {
    if ($WhatIf) {
        if (-not $Silent) { Write-Host "    [预演] $sql" -ForegroundColor DarkGray }
        return
    }
    $out = & $MySql -h $DbHost -P $DbPort -u $DbUser --default-character-set=utf8mb4 -e $sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SQL 执行失败：$sql`n$out"
    }
    if (-not $Silent) { $out }
}

# ---------------------------------------------------------------- 状态查询

function Show-Status {
    Write-Host ""
    Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "   AI 计费网关 · 当前数据状态" -ForegroundColor Cyan
    Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "   数据库 : $DbName @ ${DbHost}:${DbPort}" -ForegroundColor Gray
    Write-Host "   连接账号: $DbUser" -ForegroundColor Gray
    Write-Host ""

    # 库是否存在
    $dbExists = (& $MySql -h $DbHost -P $DbPort -u $DbUser -N -B -e `
        "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='$DbName';" 2>&1)
    if ($dbExists.Trim() -ne '1') {
        Write-Host "   ⚠ 数据库 $DbName 不存在（网关首次启动时会自动创建）" -ForegroundColor Yellow
        Write-Host ""
        return
    }

    # 用英文列名查询：mysql.exe 的输出经过 PowerShell 管道后中文会变乱码
    $counts = & $MySql -h $DbHost -P $DbPort -u $DbUser -N -B -e @"
SELECT 'api_keys',      COUNT(*) FROM ``$DbName``.api_keys
UNION ALL SELECT 'model_prices',  COUNT(*) FROM ``$DbName``.model_prices
UNION ALL SELECT 'usage_records', COUNT(*) FROM ``$DbName``.usage_records
UNION ALL SELECT 'admin_users',   COUNT(*) FROM ``$DbName``.admin_users
UNION ALL SELECT 'audit_logs',    COUNT(*) FROM ``$DbName``.audit_logs;
"@ 2>&1

    $labels = @{
        'api_keys'      = 'API Key 数量'
        'model_prices'  = '定价规则'
        'usage_records' = '用量明细'
        'admin_users'   = '管理账号'
        'audit_logs'    = '审计日志'
    }
    foreach ($line in $counts) {
        $parts = "$line" -split "`t"
        if ($parts.Count -eq 2) {
            $name = $labels[$parts[0]]
            if (-not $name) { $name = $parts[0] }
            Write-Host ("   {0,-14}: {1}" -f $name, $parts[1])
        }
    }

    # 累计用量汇总
    $sum = & $MySql -h $DbHost -P $DbPort -u $DbUser -N -B -e @"
SELECT COALESCE(SUM(used_prompt_tokens+used_completion_tokens),0),
       COALESCE(SUM(used_cost),0),
       COALESCE(SUM(used_requests),0)
FROM ``$DbName``.api_keys;
"@ 2>&1
    $sp = "$sum" -split "`t"
    if ($sp.Count -ge 3) {
        Write-Host ""
        Write-Host ("   累计 Token: {0}    累计费用: {1}    累计请求: {2}" -f $sp[0], $sp[1], $sp[2]) -ForegroundColor Gray
    }
    Write-Host ""
}

# ---------------------------------------------------------------- 备份

function Backup-Database {
    if ($NoBackup) {
        Write-Host "   ⚠ 已指定 -NoBackup，跳过备份" -ForegroundColor Yellow
        return $true
    }

    if (-not $MyDump) {
        Write-Host "   ✗ 找不到 mysqldump.exe，无法备份" -ForegroundColor Red
        Write-Host "     如确认无需备份，请加 -NoBackup 参数重新运行" -ForegroundColor Yellow
        return $false
    }

    if ($WhatIf) {
        Write-Host "   [预演] 将备份 $DbName 到 backups\ 目录" -ForegroundColor DarkGray
        return $true
    }

    $backupDir = Join-Path $Root 'backups'
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $file = Join-Path $backupDir "ai_billing_$stamp.sql"

    Write-Host "   正在备份到 $file ..." -ForegroundColor Gray
    & $MyDump -h $DbHost -P $DbPort -u $DbUser --databases $DbName --result-file=$file 2>&1 | Out-Null

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $file)) {
        Write-Host "   ✗ 备份失败，已中止重置（数据未改动）" -ForegroundColor Red
        return $false
    }

    $size = [Math]::Round((Get-Item $file).Length / 1KB, 1)
    Write-Host "   ✓ 备份完成：$file  ($size KB)" -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------- 各等级动作

$Scripts = @{
    '账单' = @(
        "TRUNCATE TABLE ``$DbName``.usage_records;"
        "UPDATE ``$DbName``.api_keys SET used_prompt_tokens=0, used_completion_tokens=0, used_requests=0, used_cost=0, last_used_at=NULL;"
        "TRUNCATE TABLE ``$DbName``.audit_logs;"
    )
    'key' = @(
        "TRUNCATE TABLE ``$DbName``.usage_records;"
        "TRUNCATE TABLE ``$DbName``.api_keys;"
        "TRUNCATE TABLE ``$DbName``.model_prices;"
        "TRUNCATE TABLE ``$DbName``.audit_logs;"
    )
    '全部' = @(
        "TRUNCATE TABLE ``$DbName``.usage_records;"
        "TRUNCATE TABLE ``$DbName``.api_keys;"
        "TRUNCATE TABLE ``$DbName``.model_prices;"
        "TRUNCATE TABLE ``$DbName``.admin_users;"
        "TRUNCATE TABLE ``$DbName``.audit_logs;"
    )
}

$Descriptions = @{
    '账单' = @(
        "清空用量明细（usage_records）"
        "所有 Key 的累计用量归零"
        "清空审计日志"
        "【保留】API Key、定价规则、管理员账号"
    )
    'key' = @(
        "清空用量明细（usage_records）"
        "删除全部 API Key  ← 客户端将立即失效"
        "删除全部定价规则  ← 未定价模型按兜底免费处理"
        "清空审计日志"
        "【保留】管理员账号"
    )
    '全部' = @(
        "清空用量明细（usage_records）"
        "删除全部 API Key"
        "删除全部定价规则"
        "删除管理员账号  ← 登录回退到 appsettings.json 的初始账号"
        "清空审计日志"
    )
    '库' = @(
        "DROP DATABASE $DbName  ← 表结构与数据全部删除"
        "重启网关时会自动重建所有表"
        "登录回退到 appsettings.json 的初始账号"
    )
}

# ---------------------------------------------------------------- 主流程

Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║        AI 计费网关 · 数据重置工具                    ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan

Show-Status

if ($Level -eq 'status') {
    Write-Host "  以上仅为状态查询，未做任何修改。" -ForegroundColor Green
    Write-Host ""
    Write-Host "  可选重置级别：" -ForegroundColor White
    Write-Host "    账单  只清用量与账单，保留 Key 和定价" -ForegroundColor Gray
    Write-Host "    key   清 Key + 定价 + 账单，保留管理员账号" -ForegroundColor Gray
    Write-Host "    全部  连管理员账号一起清" -ForegroundColor Gray
    Write-Host "    库    直接删库，重启网关后自动重建" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  用法： .\reset.ps1 -Level 账单      （会先备份并二次确认）" -ForegroundColor White
    Write-Host ""
    exit 0
}

# --- 即将执行的操作预览 ---
Write-Host "  ──────────────────────────────────────────────────────" -ForegroundColor Yellow
Write-Host "  即将执行重置，级别：$Level" -ForegroundColor Yellow
Write-Host "  ──────────────────────────────────────────────────────" -ForegroundColor Yellow
foreach ($line in $Descriptions[$Level]) {
    $isWarning = $line -match '删除|DROP|失效|回退'
    $color = if ($isWarning) { 'Red' } else { 'Gray' }
    Write-Host "    · $line" -ForegroundColor $color
}
Write-Host ""

# --- 网关运行检查（运行中重置可能导致短暂异常）---
$gwRunning = Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue
if ($gwRunning) {
    Write-Host "  ⚠ 网关正在运行（8080 端口被占用）。" -ForegroundColor Yellow
    Write-Host "     重置过程中 Key 缓存可能有短暂不一致，建议先停止网关。" -ForegroundColor Yellow
    Write-Host "     如需继续，稍等片刻即可；或先执行 start-gateway.cmd 的窗口按 Ctrl+C。" -ForegroundColor Gray
    Write-Host ""
}

# --- 二次确认 ---
if (-not $Force -and -not $WhatIf) {
    Write-Host "  此操作不可撤销。" -ForegroundColor Red
    Write-Host "  请输入大写 YES 以确认执行（其他任何输入都会取消）：" -ForegroundColor White -NoNewline

    $answer = $null
    try {
        $answer = Read-Host " "
    }
    catch {
        # 非交互式环境（CI、管道、自动化脚本）下 Read-Host 不可用。
        # 这种情况必须视为"未确认"，直接取消，绝不能默默执行破坏性操作。
        Write-Host ""
        Write-Host ""
        Write-Host "  ⚠ 当前是非交互式会话，无法读取确认输入。" -ForegroundColor Yellow
        Write-Host "    为避免误删，已自动取消，数据未做任何修改。" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "    如需在脚本中无人值守执行，请显式加 -Force 参数：" -ForegroundColor Gray
        Write-Host "      .\reset.ps1 -Level $Level -Force" -ForegroundColor DarkGray
        Write-Host ""
        exit 0
    }

    if ($answer -cne 'YES') {
        Write-Host ""
        Write-Host "  已取消，数据未做任何修改。" -ForegroundColor Green
        Write-Host ""
        exit 0
    }
    Write-Host ""
}

# --- 备份 ---
Write-Host "  [1/2] 备份" -ForegroundColor White
if (-not (Backup-Database)) { exit 1 }
Write-Host ""

# --- 执行 ---
Write-Host "  [2/2] 执行重置" -ForegroundColor White

if ($Level -eq '库') {
    Invoke-Sql "DROP DATABASE IF EXISTS ``$DbName``;" -Silent
    if (-not $WhatIf) { Write-Host "   ✓ 数据库已删除" -ForegroundColor Green }
} else {
    foreach ($sql in $Scripts[$Level]) {
        $short = $sql.Substring(0, [Math]::Min(70, $sql.Length))
        Invoke-Sql $sql -Silent
        if (-not $WhatIf) { Write-Host "   ✓ $short" -ForegroundColor Green }
    }
}

Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Cyan
if ($WhatIf) {
    Write-Host "  预演结束，未做任何修改。" -ForegroundColor Yellow
    Write-Host "  去掉 -WhatIf 参数即可真正执行。" -ForegroundColor Gray
} else {
    Write-Host "  重置完成 ✓" -ForegroundColor Green
    Write-Host ""
    Write-Host "  后续步骤：" -ForegroundColor White
    if ($Level -in @('key', '全部', '库')) {
        Write-Host "    1) 重新配置定价：" -ForegroundColor Gray
        Write-Host "       aibilling.dll prices set --model `"*`" --in 1 --out 2" -ForegroundColor DarkGray
        Write-Host "    2) 创建 API Key：" -ForegroundColor Gray
        Write-Host "       aibilling.dll keys create --name `"客户A`" --tokens 1000000 --cost 100" -ForegroundColor DarkGray
    }
    if ($Level -in @('全部', '库')) {
        Write-Host "    3) 登录账号已回退到 appsettings.json 里的初始账号" -ForegroundColor Gray
        Write-Host "       若网关正在运行，请重启它以重建数据表：" -ForegroundColor Gray
        Write-Host "       start-gateway.cmd" -ForegroundColor DarkGray
    }
    if ($Level -eq '账单') {
        Write-Host "    Key 与定价保持不变，客户端无需改动。" -ForegroundColor Gray
    }
}
Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if (Test-Path (Join-Path $Root 'backups')) {
    Write-Host "  备份文件目录：$(Join-Path $Root 'backups')" -ForegroundColor Gray
    Write-Host ""
}
