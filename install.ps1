#requires -Version 5.1
<#
============================================================================
 SotF 客户端探测 MOD -- 离线全能安装器  v2.38
----------------------------------------------------------------------------
 设计目标: 无盘工作站 / 网吧环境下, 一次双击完成全部铺设, 全程零联网。

 与 v2.19 的 install.ps1 区别:
   * 不下载任何东西 (不访问 nuget / unity.bepinex.dev / github)
   * BepInEx 本体从本地资源目录取 (zip 或已解压目录均可)
   * Unity 基础库预置到 BepInEx\unity-libs, 杜绝启动期联网下载导致的卡死
   * 优先铺设已编译好的插件 DLL, 跳过编译 (不需要 .NET SDK)
   * 自动改写 BepInEx.cfg: 离线 unity 库源 + 开启磁盘日志

 用法:
   install.ps1 -ResourceRoot "Z:\" [-GameDir "..."] [-WorkDir "..."]
     ResourceRoot : 资源根目录。安装器(EXE/BAT)所在目录, 其下应有两个子目录:
                      BepInEx-BepInExPack_IL2CPP-6.0.755\   (zip 或已解压)
                      UnityEngine\                          (或 2022.2.16.zip)
     GameDir      : 游戏目录。留空则自动探测。
     WorkDir      : 自解压出来的工作目录(含 src\)。留空则用脚本自身目录。
============================================================================
#>
param(
    [string]$GameDir = "",
    [string]$ResourceRoot = "",
    [string]$WorkDir = ""
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- 日志
$script:FailCount = 0
$script:WarnCount = 0
$LogFile = ""

function Start-Log($dir) {
    $script:LogFile = ""
    if ([string]::IsNullOrWhiteSpace($dir)) { return }
    try {
        $p = Join-Path $dir "install_log.txt"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        "# SotF Probe 离线安装日志  v2.38" | Out-File -FilePath $p -Encoding UTF8
        (Get-Date -Format "yyyy-MM-dd HH:mm:ss") | Out-File -FilePath $p -Encoding UTF8 -Append
        $script:LogFile = $p
    } catch {
        try { Write-Host ("  [WARN] 日志无法写入: " + $dir) -ForegroundColor Yellow } catch { }
        $script:LogFile = ""
    }
}
function Log($t)  { if ($script:LogFile) { Add-Content -Path $script:LogFile -Value $t -Encoding UTF8 } }
function Ok($t)   { Write-Host "  [OK]   $t" -ForegroundColor Green;   Log "  [OK]   $t" }
function Info($t) { Write-Host "  [..]   $t" -ForegroundColor Gray;    Log "  [..]   $t" }
function Warn($t) { Write-Host "  [WARN] $t" -ForegroundColor Yellow;  Log "  [WARN] $t"; $script:WarnCount++ }
function Bad($t)  { Write-Host "  [FAIL] $t" -ForegroundColor Red;     Log "  [FAIL] $t"; $script:FailCount++ }
function Sect($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan; Log ""; Log "=== $t ===" }

# ------------------------------------------------- 通用: 解压 / 压缩 zip
function Expand-ZipAny($zipPath, $destDir) {
    # Expand-Archive 只认 .zip 扩展名; 这里用 ZipFile, 不依赖扩展名
    if (Test-Path $destDir) { Remove-Item -Recurse -Force $destDir -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $destDir)
}
function New-ZipFromDir($srcDir, $zipPath) {
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath -ErrorAction SilentlyContinue }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($srcDir, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
}

# ------------------------------------------------- 通用: ini 键设置
function Set-IniValue {
    param([string[]]$Lines, [string]$Section, [string]$Key, [string]$Value)
    $out = New-Object System.Collections.Generic.List[string]
    $inSec = $false
    $found = $false
    $secExists = $false
    $keyRe = '^\s*' + [regex]::Escape($Key) + '\s*='
    foreach ($l in $Lines) {
        $t = $l.Trim()
        if ($t -match '^\[(.+)\]$') {
            if ($inSec -and -not $found) { $out.Add("$Key = $Value"); $found = $true }
            $cur = $Matches[1].Trim()
            if ($cur -eq $Section) { $inSec = $true; $secExists = $true } else { $inSec = $false }
            $out.Add($l)
            continue
        }
        if ($inSec -and $t -match $keyRe) {
            if (-not $found) { $out.Add("$Key = $Value"); $found = $true }
            continue
        }
        $out.Add($l)
    }
    if ($inSec -and -not $found) { $out.Add("$Key = $Value"); $found = $true }
    if (-not $secExists) {
        $out.Add("")
        $out.Add("[$Section]")
        $out.Add("$Key = $Value")
        $found = $true
    }
    return ,$out.ToArray()
}

# ------------------------------------------------- 通用: 在树中找目录
function Find-DirByPattern($roots, $patterns, $maxDepth) {
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        for ($d = 0; $d -le $maxDepth; $d++) {
            $needle = $r
            for ($i = 0; $i -lt $d; $i++) { $needle = Join-Path $needle "*" }
            $needle = Join-Path $needle "*"
            $hits = Get-ChildItem -Path $needle -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $n = $_.Name; ($patterns | Where-Object { $n -like $_ }).Count -gt 0 }
            if ($hits) {
                $first = $hits | Sort-Object FullName | Select-Object -First 1
                return $first.FullName
            }
        }
    }
    return $null
}

# ================================================================ 0. 环境
Sect "0. 环境自检"

# --- 路径净化: 防御 cmd 传参把引号/反斜杠带进来 -----------------------
function Clean-Path($p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return "" }
    $x = $p.Trim()
    # 去掉被引号包裹的整串中的引号
    while ($x.Length -ge 2 -and $x.StartsWith('"') -and $x.EndsWith('"')) { $x = $x.Substring(1, $x.Length - 2).Trim() }
    # 去掉残留引号(任何位置) -- 引号在 Windows 路径里是非法字符
    $x = $x -replace '"', ''
    $x = $x.Trim()
    if ($x.Length -eq 0) { return "" }
    # 去掉末尾多余反斜杠(盘符根 Z:\ 除外)
    while ($x.Length -gt 3 -and $x.EndsWith('\')) { $x = $x.Substring(0, $x.Length - 1) }
    return $x
}
$WorkDir      = Clean-Path $WorkDir
$ResourceRoot = Clean-Path $ResourceRoot

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    if ($PSScriptRoot) { $WorkDir = $PSScriptRoot } else { $WorkDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
}
if ([string]::IsNullOrWhiteSpace($ResourceRoot)) { $ResourceRoot = $WorkDir }

# 资源根目录候选: 显式指定优先, 其次各盘符根
$rrCands = @()
if ($ResourceRoot) { $rrCands += $ResourceRoot }
foreach ($d in @('Z', 'Y', 'X', 'W', 'D', 'E', 'C')) { $rrCands += "${d}:\" }

Info "工作目录: $WorkDir"
Info "资源根目录候选: $($rrCands -join ', ')"
Start-Log $ResourceRoot
if (-not $script:LogFile) { Start-Log $WorkDir }
if (-not $script:LogFile) { Start-Log "Z:\" }
if (-not $script:LogFile) { Start-Log $env:TEMP }
Log "工作目录: $WorkDir"

# ================================================================ 1. 游戏目录
Sect "1. 定位游戏目录"
$exeName = "SonsOfTheForest.exe"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $roots = @()
    foreach ($d in @('C', 'D', 'E', 'F', 'Z', 'Y')) {
        $roots += "${d}:\Program Files (x86)\Steam\steamapps\common\Sons of the Forest"
        $roots += "${d}:\Steam\steamapps\common\Sons of the Forest"
        $roots += "${d}:\SteamLibrary\steamapps\common\Sons of the Forest"
        $roots += "${d}:\Games\Steam\steamapps\common\Sons of the Forest"
    }
    $reg = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction SilentlyContinue
    if ($reg -and $reg.InstallPath) {
        $roots += (Join-Path $reg.InstallPath "steamapps\common\Sons of the Forest")
    }
    foreach ($r in $roots) {
        if ($r -and (Test-Path (Join-Path $r $exeName))) { $GameDir = $r; break }
    }
}

if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path (Join-Path $GameDir $exeName))) {
    Bad "未找到游戏目录。请显式指定:"
    Write-Host '    install.ps1 -GameDir "D:\Steam\steamapps\common\Sons of the Forest"' -ForegroundColor Yellow
    exit 2
}
Ok "游戏目录: $GameDir"
if (-not (Test-Path (Join-Path $GameDir "GameAssembly.dll"))) {
    Bad "GameAssembly.dll 不存在 -- 不是 IL2CPP 版客户端"
    exit 2
}
Ok "IL2CPP 确认 (GameAssembly.dll 存在)"

# ================================================================ 2. 资源定位
Sect "2. 定位本地资源 (零联网)"

# --- 2a. BepInEx 本体包
$packRoot = Find-DirByPattern $rrCands @('BepInEx-BepInExPack_IL2CPP-6.0.755*', 'BepInExPack_IL2CPP*', 'BepInExPack*') 2
if (-not $packRoot) {
    # 退而找 zip 文件
    foreach ($r in $rrCands) {
        if (-not (Test-Path $r)) { continue }
        $z = Get-ChildItem -Path $r -Filter "*BepInExPack*IL2CPP*.zip" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($z) { $packRoot = $z.FullName; break }
    }
}
if ($packRoot) { Ok "BepInEx 资源源: $packRoot" } else { Bad "未找到 BepInEx 资源 (目录或 zip)"; exit 3 }

# --- 2b. Unity 基础库
$unityZip = $null
$unityDir = $null
$MIN_UNITY = 500KB          # 真实的 Unity 基础库 zip 约 1.8MB; 小于此值视为残次文件
foreach ($r in $rrCands) {
    if (-not (Test-Path $r)) { continue }
    $hits = Get-ChildItem -Path $r -Filter "2022.2.16.zip" -File -Recurse -Depth 1 -ErrorAction SilentlyContinue
    foreach ($z in $hits) {
        if ($z.Length -lt $MIN_UNITY) {
            Warn "跳过残次 Unity 库: $($z.FullName) ($($z.Length) 字节, 期望 ~1.8MB)"
            continue
        }
        $unityZip = $z.FullName; break
    }
    if ($unityZip) { break }
}
if (-not $unityZip) {
    $unityDir = Find-DirByPattern $rrCands @('UnityEngine') 2
    if ($unityDir) {
        $nDll = @(Get-ChildItem -Path $unityDir -Filter "*.dll" -File -ErrorAction SilentlyContinue).Count
        if ($nDll -lt 40) { Warn "UnityEngine 目录 DLL 数量偏少 ($nDll) -- 可能不完整"; }
    }
}
if ($unityZip) { Ok "Unity 基础库: $unityZip (zip)" }
elseif ($unityDir) { Ok "Unity 基础库: $unityDir (目录, 将现场打包)" }
else { Bad "未找到 Unity 基础库 (2022.2.16.zip 或 UnityEngine 目录)"; exit 3 }

# ================================================================ 3. 铺 BepInEx
Sect "3. 铺设 BepInEx"

$haveBep = (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.Core.dll"))
if ($haveBep) {
    Ok "BepInEx 已存在 -- 跳过铺设 (如需重装请先删除游戏目录下 BepInEx)"
} else {
    $stageRoot = $packRoot
    if ($packRoot -like "*.zip") {
        $tmp = Join-Path (Join-Path $WorkDir "_work_be")
        Info "解压 BepInEx zip 到临时目录..."
        Expand-ZipAny $packRoot $tmp
        $stageRoot = $tmp
    }
    # 在游戏目录风格层: 含 BepInEx\ 子目录的最浅层
    $src = $null
    $all = Get-ChildItem -Path $stageRoot -Recurse -Directory -ErrorAction SilentlyContinue |
           Where-Object { Test-Path (Join-Path $_.FullName "BepInEx") }
    if ($all) { $src = ($all | Sort-Object { $_.FullName.Length } | Select-Object -First 1).FullName }
    if (-not $src) { $src = $stageRoot }

    Info "来源根: $src"
    foreach ($item in Get-ChildItem -Path $src -Force -ErrorAction SilentlyContinue) {
        $dest = Join-Path $GameDir $item.Name
        try {
            if ($item.PSIsContainer) {
                Copy-Item -Path $item.FullName -Destination $dest -Recurse -Force -ErrorAction Stop
            } else {
                Copy-Item -Path $item.FullName -Destination $dest -Force -ErrorAction Stop
            }
            Ok "铺设: $($item.Name)"
        } catch {
            Bad "铺设失败 $($item.Name): $($_.Exception.Message)"
        }
    }
}

# 门栓检查
$proxy = $null
foreach ($cand in @("winhttp.dll", "version.dll")) {
    if (Test-Path (Join-Path $GameDir $cand)) { $proxy = $cand; break }
}
if ($proxy) { Ok "门栓 (doorstop proxy): $proxy" } else { Warn "未找到门栓 -- BepInEx 不会被加载" }
if (Test-Path (Join-Path $GameDir "doorstop_config.ini")) { Ok "doorstop_config.ini 存在" }
else { Warn "doorstop_config.ini 缺失" }

# ================================================================ 4. Unity 基础库 (关键)
Sect "4. 预置 Unity 基础库 (解决启动期联网卡死)"

$unityLibs = Join-Path $GameDir "BepInEx\unity-libs"
try { New-Item -ItemType Directory -Force -Path $unityLibs | Out-Null } catch { }
$targetZip = Join-Path $unityLibs "2022.2.16.zip"

try {
    if ($unityZip) {
        Copy-Item -Path $unityZip -Destination $targetZip -Force -ErrorAction Stop
    } else {
        New-ZipFromDir $unityDir $targetZip
    }
    $sz = (Get-Item $targetZip).Length
    Ok "已预置: $targetZip  ($sz 字节)"
} catch {
    Bad "Unity 基础库预置失败: $($_.Exception.Message)"
}

# ================================================================ 5. 配置 BepInEx.cfg
Sect "5. 改写 BepInEx.cfg"

$cfgDir  = Join-Path $GameDir "BepInEx\config"
$cfgPath = Join-Path $cfgDir "BepInEx.cfg"
try { New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null } catch { }

$cfgLines = @()
if (Test-Path $cfgPath) {
    $cfgLines = Get-Content -Path $cfgPath -Encoding UTF8
    Copy-Item -Path $cfgPath -Destination "$cfgPath.bak_v220" -Force -ErrorAction SilentlyContinue
    Ok "已备份原配置 -> BepInEx.cfg.bak_v220"
} else {
    Warn "BepInEx.cfg 不存在 -- 将新建 (仅含必要项)"
}

# 5a. 离线 Unity 库源: 只留文件名 => BepInEx 直接用 unity-libs 下的同名文件, 不联网
$cfgLines = Set-IniValue $cfgLines "Il2Cpp" "UnityBaseLibrariesSource" "2022.2.16.zip"
# 5b. 开启磁盘日志: 没有 LogOutput.log 就无法诊断卡死
$cfgLines = Set-IniValue $cfgLines "Logging.Disk" "Enabled" "true"
$cfgLines = Set-IniValue $cfgLines "Logging.Disk" "Append" "false"
# 5c. 关闭控制台日志 (游戏黑窗的另一来源)
$cfgLines = Set-IniValue $cfgLines "Logging.Console" "Enabled" "false"

try {
    $cfgLines | Out-File -FilePath $cfgPath -Encoding UTF8
    Ok "配置已写入: $cfgPath"
    Info "  UnityBaseLibrariesSource = 2022.2.16.zip  (离线)"
    Info "  [Logging.Disk] Enabled   = true           (可诊断)"
} catch {
    Bad "配置写入失败: $($_.Exception.Message)"
}

# ================================================================ 6. 铺设插件 DLL
Sect "6. 铺设已编译插件 DLL"

$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
try { New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null } catch { }

$dllFound = $null
$dllSource = ""

# v2.30: this package's own build output wins outright.
#
# v2.30 installed Z:\back\_v219\bin\SotfClientProbe.dll -- a build two
# versions behind -- because the search reached a backup folder first. Every
# observation afterwards came from the old code, so the new features looked
# "missing" when they had simply never been installed. A copy sitting in this
# package's own bin\ is by definition the one matching this src\ tree.
$ownBin = Join-Path $PSScriptRoot "bin\SotfClientProbe.dll"
if (Test-Path $ownBin) {
    $dllFound  = $ownBin
    $dllSource = "own"
    Ok "使用本包编译产物: bin\SotfClientProbe.dll"
}

if (-not $dllFound) {
    # vA1.06: the drive-wide search for an existing SotfClientProbe.dll
    # has been REMOVED.
    #
    # It used to locate e.g. Z:\client\SotFProbe_v236\bin\SotfClientProbe.dll
    # (v2.36, two versions old), copy it into plugins\ and print
    # "deployed plugin" in green. The game then ran that old build while
    # every newer field (ts_utc, sync_window, downed, link, crosscheck)
    # came back empty. Looking installed and being installed are not the
    # same thing: a MISSING plugin is obvious, a STALE one is not.
    #
    # From now on only this package's own bin\ output is accepted;
    # nothing found elsewhere is ever copied into plugins\.
    Warn "本包 bin\ 下没有编译产物 -- 不会从磁盘其它位置搜寻旧 DLL 顶替。"
    Warn "plugins\ 保持为空: 宁可无数据, 也不出假数据。"
}

if ($dllFound) {
    try {
        Copy-Item -Path $dllFound -Destination (Join-Path $pluginsDir "SotfClientProbe.dll") -Force -ErrorAction Stop
        $sz = (Get-Item $dllFound).Length
        if ($dllSource -eq "own") {
            Ok "已铺设插件: $dllFound  ($sz 字节)  -- 本包编译产物"
        } else {
            Ok "已铺设插件: $dllFound  ($sz 字节)  -- 外部搜索所得"
            Warn "这是外部找到的 DLL, 可能与本包 src\ 不一致 (版本可能落后)。"
            Info "如需本包新代码, 请运行 2_build_deploy.bat 重新编译 -- 它会覆盖此项。"
        }
    } catch {
        Bad "插件 DLL 复制失败: $($_.Exception.Message)"
    }
} else {
    Warn "未找到已编译的 SotfClientProbe.dll -- 本次无法跳过编译"
    $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($sdk) {
        Info "检测到 .NET SDK: $($sdk.Source)"
        Info "请稍后运行本包的 2_build_deploy.bat 编译并部署插件。"
    } else {
        Bad "未检测到 .NET SDK -- 无法编译"
        Info "请安装 Z:\dotnet-sdk-8.0.424-win-x64.exe (226MB, 本机自带),"
        Info "安装完成后再运行 2_build_deploy.bat。"
    }
    Info "说明: 编译一次后, SotfClientProbe.dll 会留在本包 bin\ 目录;"
    Info "      重跑 1_setup.bat 时只认该产物(本包 src\ 编译所得)。"
}

# 纯 MOD 条款: plugins 目录应只有本探针
$extra = Get-ChildItem -Path $pluginsDir -Force -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -ne "SotfClientProbe.dll" }
if ($extra) {
    Warn "plugins 目录存在其它条目 (纯MOD条款):"
    foreach ($e in $extra) { Warn "   - $($e.Name)" }
} else {
    Ok "plugins 目录干净 -- 只有本探针"
}

# ================================================================ 7. doorstop / 黑窗
Sect "7. doorstop 配置 (黑窗排查)"

$dscPath = Join-Path $GameDir "doorstop_config.ini"
if (Test-Path $dscPath) {
    $dsc = Get-Content -Path $dscPath -Encoding UTF8
    Log "---- doorstop_config.ini 全文 ----"
    foreach ($l in $dsc) { Log $l }
    Log "----------------------------------"
    $consoleLines = $dsc | Where-Object { $_ -match '(?i)console' }
    if ($consoleLines) {
        Info "发现 console 相关项 (已记入日志, 未自动修改):"
        foreach ($l in $consoleLines) { Info "   $l" }
        Warn "黑窗由 doorstop 层创建 -- 本次不自动改, 需确认键名后再处理"
    } else {
        Info "doorstop_config.ini 中无 console 项 -- 黑窗来源待查 (已记日志)"
    }
} else {
    Warn "doorstop_config.ini 不存在"
}

# ================================================================ 8. 结果
Sect "结果"

# 复查
$checks = @(
    @{ N = "BepInEx.Core.dll"; P = "BepInEx\core\BepInEx.Core.dll" },
    @{ N = "unity-libs\2022.2.16.zip"; P = "BepInEx\unity-libs\2022.2.16.zip" },
    @{ N = "plugins\SotfClientProbe.dll"; P = "BepInEx\plugins\SotfClientProbe.dll" },
    @{ N = "doorstop_config.ini"; P = "doorstop_config.ini" },
    @{ N = "BepInEx.cfg"; P = "BepInEx\config\BepInEx.cfg" }
)
foreach ($c in $checks) {
    if (Test-Path (Join-Path $GameDir $c.P)) { Ok $c.N } else { Bad "缺失: $($c.N)" }
}

Write-Host ""
if ($script:FailCount -eq 0) {
    Write-Host "  RESULT: INSTALL OK" -ForegroundColor Green
    Log "RESULT: INSTALL OK"
} else {
    Write-Host "  RESULT: INSTALL FAILED (失败 $script:FailCount 项)" -ForegroundColor Red
    Log "RESULT: INSTALL FAILED"
}
Write-Host "  日志: $script:LogFile" -ForegroundColor Gray
Write-Host ""
Write-Host "  下一步:" -ForegroundColor Cyan
if (-not $dllFound) {
    Write-Host "     0) 先运行 2_build_deploy.bat 编译并部署插件 (需 .NET SDK)" -ForegroundColor Cyan
}
Write-Host "     1) 启动游戏 -> 进主菜单 (首次会生成 interop, 需数分钟)" -ForegroundColor Cyan
Write-Host "     2) 进服 -> 站立 30 秒 -> 查看 BepInEx\probe_out\probe_diag.txt" -ForegroundColor Cyan
Write-Host ""

exit $(if ($script:FailCount -eq 0) { 0 } else { 1 })
