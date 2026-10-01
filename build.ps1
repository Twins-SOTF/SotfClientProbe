#Requires -Version 5.1
<#
    SotF Client Probe -- 编译并校验 (build)

    1. 定位游戏目录
    2. 检查 BepInEx core 与 IL2CPP interop
    3. 定位 .NET 6 SDK
    4. 编译出 SotfClientProbe.dll
    5. 复制到 BepInEx\plugins
    6. 生成后自动校验 (这是本脚本的重点)

    关于 interop:
    Il2CppInterop 在【游戏首次启动】时从 GameAssembly.dll 生成 interop 程序集。
    没有 interop 时项目也能编译, 但会退回 Harmony 兜底路径 (赌游戏类型名存在)。
    所以必须先启动游戏一次, 再运行本脚本。

    用法:  2_build_deploy.bat
           2_build_deploy.bat "D:\Steam\steamapps\common\Sons of the Forest"
#>

param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ------------------------------------------------------------------ logging
$script:LogFile = Join-Path $PSScriptRoot "2_build.log"
function Start-Log {
    $hdr = @(
        "============================================================",
        "  SotF Client Probe -- 编译与校验日志",
        "  时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "  脚本位置: $PSScriptRoot",
        "============================================================",
        ""
    )
    Set-Content -Path $script:LogFile -Value $hdr -Encoding UTF8
}
function Log($t) { Write-Host $t; Add-Content -Path $script:LogFile -Value $t -Encoding UTF8 }
function Ok($t)   { Write-Host "  [OK]   $t" -ForegroundColor Green;   Add-Content -Path $script:LogFile -Value "  [OK]   $t" -Encoding UTF8 }
function Info($t) { Write-Host "  [..]   $t" -ForegroundColor Gray;    Add-Content -Path $script:LogFile -Value "  [..]   $t" -Encoding UTF8 }
function Warn($t) { Write-Host "  [WARN] $t" -ForegroundColor Yellow;  Add-Content -Path $script:LogFile -Value "  [WARN] $t" -Encoding UTF8 }
function Bad($t)  { Write-Host "  [FAIL] $t" -ForegroundColor Red;     Add-Content -Path $script:LogFile -Value "  [FAIL] $t" -Encoding UTF8 }
function Sect($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan; Add-Content -Path $script:LogFile -Value "" -Encoding UTF8; Add-Content -Path $script:LogFile -Value "=== $t ===" -Encoding UTF8 }

Start-Log

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "   SotF Client Probe -- 编译与校验" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ------------------------------------------------------- 1. locate the game
Sect "1/5  定位游戏目录"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    foreach ($d in @('C','D','E','F','G')) {
        foreach ($sub in @(
            "Program Files (x86)\Steam\steamapps\common\Sons of the Forest",
            "Steam\steamapps\common\Sons of the Forest",
            "SteamLibrary\steamapps\common\Sons of the Forest",
            "Games\Steam\steamapps\common\Sons of the Forest"
        )) {
            $p = "${d}:\$sub"
            if (Test-Path (Join-Path $p "SonsOfTheForest.exe")) { $GameDir = $p; break }
        }
        if ($GameDir) { break }
    }
    if (-not $GameDir) {
        try {
            $reg = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction SilentlyContinue
            if ($reg -and $reg.InstallPath) {
                $p = Join-Path $reg.InstallPath "steamapps\common\Sons of the Forest"
                if (Test-Path (Join-Path $p "SonsOfTheForest.exe")) { $GameDir = $p }
            }
        } catch { }
    }
}

if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path (Join-Path $GameDir "SonsOfTheForest.exe"))) {
    Bad "找不到 Sons of the Forest。"
    Write-Host '  手动指定: 2_build_deploy.bat -GameDir "D:\...\Sons of the Forest"' -ForegroundColor Yellow
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (game not found)" -Encoding UTF8
    exit 1
}
Ok "游戏目录: $GameDir"

# ------------------------------------------------------------ 2. preconditions
Sect "2/5  检查前置条件"

$bepCore = Join-Path $GameDir "BepInEx\core\BepInEx.Core.dll"
if (-not (Test-Path $bepCore)) {
    Bad "BepInEx 未安装。"
    Write-Host "  请先运行 1_setup.bat" -ForegroundColor Yellow
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (BepInEx missing)" -Encoding UTF8
    exit 1
}
Ok "BepInEx core 存在"

$hasInterop = Test-Path (Join-Path $GameDir "BepInEx\interop\UnityEngine.CoreModule.dll")
if ($hasInterop) {
    $ic = @(Get-ChildItem -Path (Join-Path $GameDir "BepInEx\interop") -Filter "*.dll" -File -ErrorAction SilentlyContinue)
    Ok "IL2CPP interop 已生成 ($($ic.Count) 个) -- 将使用 ClassInjector 路径 (推荐)"
} else {
    Warn "IL2CPP interop 尚未生成"
    Write-Host ""
    Write-Host "  这说明游戏还没有成功启动过一次。" -ForegroundColor Yellow
    Write-Host "  interop 由 Il2CppInterop 在游戏首次启动时生成。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  现在仍可编译, 但会退回 Harmony 兜底路径 --" -ForegroundColor Yellow
    Write-Host "  它依赖一个游戏类型按名字存在, 正是我们要避开的做法。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  建议: 先启动游戏一次 (进主菜单即可), 退出, 再运行本脚本。" -ForegroundColor White
    Write-Host ""
}

# ------------------------------------------------------------- 3. dotnet SDK
Sect "3/5  定位 .NET SDK"

$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }

if (-not $dotnet) {
    foreach ($p in @(
        "C:\Program Files\dotnet\dotnet.exe",
        "${env:ProgramFiles}\dotnet\dotnet.exe",
        "${env:LOCALAPPDATA}\Microsoft\dotnet\dotnet.exe"
    )) {
        if (Test-Path $p) { $dotnet = $p; break }
    }
}

if (-not $dotnet) {
    Bad "找不到 dotnet SDK。"
    Write-Host "  请安装 .NET 6 SDK: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    Write-Host "  (你下载过的 dotnet-sdk-6.0.428-win-x64.exe 就是这个)" -ForegroundColor Yellow
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (no SDK)" -Encoding UTF8
    exit 1
}
Ok "dotnet: $dotnet"
try {
    $v = & $dotnet --version 2>$null
    Ok "SDK 版本: $v"
} catch { }

# ---------------------------------------------------------------- 4. compile
Sect "4/5  编译"

Push-Location $PSScriptRoot

$buildLog = Join-Path $PSScriptRoot "build_detail.log"

# Force MSBuild to emit English. Two reasons:
#   1. Chinese MSBuild output gets mangled by the console encoding round-trip
#      (the detail log came out as "*gR}yveN0A"), hiding the real error.
#   2. CSxxxx error codes are easier to read in their original form.
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:MSBUILDDISABLENODEREUSE = "1"

# v2.27: keep the NuGet cache on the package drive.
#
# On a diskless station C: is wiped on every reboot, so the default cache
# under %USERPROFILE% dies with it and every rebuild re-downloads the
# targeting packs. From this network that download is slow enough to look
# like a hang: the log stops at "Determining projects to restore..." and
# stays there. Caching on the package drive means only the FIRST build after
# a reimage downloads anything at all.
$nuCache = Join-Path $PSScriptRoot "nuget_cache"
if (-not (Test-Path $nuCache)) {
    try { New-Item -ItemType Directory -Path $nuCache -Force | Out-Null } catch { }
}
if (Test-Path $nuCache) {
    $env:NUGET_PACKAGES = $nuCache
    Info "NuGet cache: $nuCache  (on the package drive -- survives a reimage)"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

& $dotnet build "SotfClientProbe.csproj" -c Release -p:SotfDir="$GameDir" 2>&1 | Tee-Object -FilePath $buildLog

$buildOk = $LASTEXITCODE -eq 0
Pop-Location

if (-not $buildOk) {
    Write-Host ""
    Bad "编译失败。详细输出见: $buildLog"
    Get-Content -Path $buildLog -Tail 30 | ForEach-Object { Add-Content -Path $script:LogFile -Value $_ -Encoding UTF8 }
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (compile error)" -Encoding UTF8
    exit 1
}
Ok "编译成功"

# --------------------------------------------------------- 5. deploy + verify
Sect "5/5  部署与生成后校验"

$dll = Join-Path $PSScriptRoot "bin\SotfClientProbe.dll"

if (-not (Test-Path $dll)) {
    Bad "编译后找不到 bin\SotfClientProbe.dll"
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (dll missing)" -Encoding UTF8
    exit 1
}

$dllItem = Get-Item $dll
Ok "DLL 已生成: $($dllItem.Length) 字节, $($dllItem.LastWriteTime)"

# 校验 1: 是有效 .NET 程序集
try {
    $an = [System.Reflection.AssemblyName]::GetAssemblyName($dll)
    Ok "程序集名称: $($an.Name)"
    Ok "程序集版本: $($an.Version)"
} catch {
    Bad "文件不是有效的 .NET 程序集: $($_.Exception.Message)"
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (invalid assembly)" -Encoding UTF8
    exit 1
}

if ($dllItem.Length -lt 10000) {
    Warn "DLL 体积偏小 ($($dllItem.Length) 字节) -- 可能没包含预期代码"
}

# 部署
$plugins = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins -Force | Out-Null }

try {
    Copy-Item -Path $dll -Destination (Join-Path $plugins "SotfClientProbe.dll") -Force -ErrorAction Stop
    Ok "已部署到 BepInEx\plugins\SotfClientProbe.dll"
} catch {
    Bad "复制失败 (游戏是否正在运行?): $($_.Exception.Message)"
    Add-Content -Path $script:LogFile -Value "RESULT: BUILD FAILED (copy failed)" -Encoding UTF8
    exit 1
}

# 校验 2: plugins 目录干净度 (反作弊规则: 有其他插件则自毁)
$others = @(Get-ChildItem -Path $plugins -Filter "*.dll" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne "SotfClientProbe.dll" })
if ($others.Count -gt 0) {
    Warn "plugins 目录存在 $($others.Count) 个其他插件:"
    foreach ($o in $others) { Write-Host "       - $($o.Name)" -ForegroundColor Yellow }
    Write-Host "  规则: 只要存在其他插件, 本探针会自动失效 (不采集任何数据)。" -ForegroundColor Yellow
} else {
    Ok "plugins 目录干净 -- 只有本探针"
}

# 校验 3: 运行时路径判定
if ($hasInterop) { Ok "每帧回调路径: ClassInjector (不依赖游戏类型名)" }
else             { Warn "每帧回调路径: Harmony 兜底 (依赖游戏类型名, 不推荐)" }

# 校验 4: 输出目录
$outDir = Join-Path $GameDir "BepInEx\probe_out"
if (Test-Path $outDir) { Ok "输出目录已就绪: $outDir" }
else                   { Info "输出目录将在首次运行时创建: $outDir" }

# ------------------------------------------------------------------ summary
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "   RESULT: BUILD OK" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  下一步:" -ForegroundColor White
Write-Host "    1. 启动游戏 (Steam 需已登录)" -ForegroundColor White
Write-Host "    2. 进入服务器, 故意挨几下打  <-- 关键, 否则会误判" -ForegroundColor White
Write-Host "    3. 打开 $outDir" -ForegroundColor White
Write-Host ""
Write-Host "  判读 (先看 vitals_probe.txt):" -ForegroundColor White
Write-Host "    hp_reads > 0 且 hp_changes > 0  -> 真实血量到手, 架构成立" -ForegroundColor Cyan
Write-Host "    hp_reads > 0 但 hp_changes = 0  -> 又一个冻结对象 (像那个 176)" -ForegroundColor Yellow
Write-Host "    hp_reads = 0                    -> 看 vitals_probe.txt 的探测细节" -ForegroundColor Yellow
Write-Host ""
Write-Host "  日志: $($script:LogFile)" -ForegroundColor Gray
Write-Host "  编译明细: $buildLog" -ForegroundColor Gray
Write-Host ""

Add-Content -Path $script:LogFile -Value "RESULT: BUILD OK" -Encoding UTF8
exit 0
