# 打 Windows 安装包（Inno Setup）
#
#   pwsh scripts/pack-installer.ps1                      版本号取 csproj 里的 <Version>
#   pwsh scripts/pack-installer.ps1 -Version 1.2.0       显式指定（CI 用这个）
#   pwsh scripts/pack-installer.ps1 -NoPublish           发布目录已经在了，跳过 dotnet publish
#
# 产物：dist/ClassPoints-Setup-<版本>.exe
#
# 为什么版本号必须三段：客户端的自动更新只认 主.次.修（SemanticVersion）。
# 这里如果放过去一个 "1.2" 或 "1.2.0-beta"，安装能成功，但清单校验会把它判成
# 非正式版 —— 装了也永远不会被更新。所以在打包这一层就拦住。

param(
    [string]$Version = '',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    # 已经发布好的目录。留空就是 dist/ClassPoints-<Runtime>
    [string]$SourceDir = '',

    [switch]$NoPublish,

    # 显式指定 ISCC.exe，一般不用
    [string]$Iscc = '',

    # 不联网取编译器。CI 或离线机器上想用自己的 Inno 时用。
    [switch]$NoBootstrap
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------- 1) 版本号 ----------
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Join-Path $root 'src/ClassPoints.App/ClassPoints.App.csproj'
    $match = [regex]::Match((Get-Content $csproj -Raw), '<Version>\s*([^<]+?)\s*</Version>')
    if (-not $match.Success) { throw "csproj 里找不到 <Version>，请用 -Version 指定" }
    $Version = $match.Groups[1].Value
}

if ($Version -notmatch '^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$') {
    throw "版本号必须是三段数字（主.次.修），当前是 '$Version' —— 自动更新只认正式版"
}

# ---------- 2) 发布目录 ----------
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $root "dist/ClassPoints-$Runtime"
}
$resolved = Resolve-Path $SourceDir -ErrorAction SilentlyContinue
$SourceDir = if ($resolved) { $resolved.Path } else { $null }
if (-not $SourceDir -or -not (Test-Path (Join-Path $SourceDir 'ClassPoints.exe'))) {
    if ($NoPublish) { throw "发布目录里没有 ClassPoints.exe：$SourceDir" }
    Write-Host "==> 发布目录不存在，先跑 publish.ps1" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime -Configuration Release
    $SourceDir = (Resolve-Path (Join-Path $root "dist/ClassPoints-$Runtime")).Path
}

# ---------- 3) 找 ISCC ----------
function Find-Iscc {
    if ($Iscc) {
        if (-not (Test-Path $Iscc)) { throw "指定的 ISCC 不存在：$Iscc" }
        return (Resolve-Path $Iscc).Path
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    # 管道过滤后如果只命中一个，$candidates 会被展开成字符串而不是数组，
    # 那时 $candidates[0] 取的是首字符（'C'），Resolve-Path 会去找 "C" 这个路径。
    # GitHub 的 windows runner 正好只命中一个，于是报 "Cannot find path ...\C"。
    # 显式 @() 兜成数组再取，才有稳定的「第一个」。
    if ($candidates) { return (Resolve-Path (@($candidates)[0])).Path }

    # 装在非默认位置时兜底扫一遍
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $base -or -not (Test-Path $base)) { continue }
        $hit = Get-ChildItem $base -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($hit) { return (Resolve-Path $hit).Path }
    }

    # 都没有就自己取一份。为什么走 NuGet 而不是官网：官网的 .exe 安装包要管理员、
    # 要交互、还被不少学校的网络挡住。Tools.InnoSetup 这个包里直接就有 ISCC.exe，
    # 解压即用，拿到 21 MB 就能编译。缓存进 dist/tools，第二次不再下载。
    if ($NoBootstrap) {
        throw @"
找不到 ISCC.exe（Inno Setup 的命令行编译器）。
  装一个：winget install --id JRSoftware.InnoSetup -e
  或去掉 -NoBootstrap 让脚本自己从 NuGet 取
  或用 -Iscc 指定路径
"@
    }
    return (Get-NugetIscc -Root $root)
}

function Get-NugetIscc {
    param([string]$Root)

    # 版本钉死：Inno 7 与 6 的消息文件不通用，跟着 CI 一起升才不会出现
    # "本机能编、CI 报 message not found"。升级时改这一行。
    $version = '7.1.0'
    $cache = Join-Path $Root 'dist/tools/inno'
    $exe = Join-Path $cache 'ISCC.exe'
    if (Test-Path $exe) { return (Resolve-Path $exe).Path }

    Write-Host "==> 本机没有 Inno Setup，从 NuGet 取一份编译器（$version，只取一次）" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $cache | Out-Null

    $nupkg = Join-Path $cache 'tools.innosetup.nupkg'
    $zip = Join-Path $cache 'tools.innosetup.zip'
    $url = "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$version/tools.innosetup.$version.nupkg"

    # nupkg 就是个 zip，但 Expand-Archive 认扩展名，所以先复制成 .zip
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
    Copy-Item $nupkg $zip -Force
    Expand-Archive -Path $zip -DestinationPath (Join-Path $cache 'pkg') -Force
    Copy-Item (Join-Path $cache 'pkg/tools/*') $cache -Recurse -Force
    Remove-Item $zip, $nupkg -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $exe)) { throw "下载的包里没有 ISCC.exe，检查 $url" }
    return (Resolve-Path $exe).Path
}

$isccPath = Find-Iscc
$isccDir = Split-Path -Parent $isccPath
Write-Host "==> ISCC：$isccPath" -ForegroundColor DarkGray

# ---------- 4) 中文语言包 ----------
# 优先用 Inno 自带的（和它的版本严格配套），没有就用仓库里带的那份。
# 都没找到才退回英文向导并明确告警 —— 静默变英文，老师装软件时容易点错。
$langVendored = Join-Path $root 'installer/lang/ChineseSimplified.isl'
$langInstalled = Join-Path $isccDir 'Languages/ChineseSimplified.isl'
$langFile = @($langInstalled, $langVendored) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($langFile) {
    $langFile = (Resolve-Path $langFile).Path
    Write-Host "==> 向导语言：简体中文（$langFile）" -ForegroundColor DarkGray
}
else {
    $langFile = 'compiler:Default.isl'
    Write-Warning "找不到 ChineseSimplified.isl，本次安装包会是英文向导"
    Write-Warning "修法：确认 installer/lang/ChineseSimplified.isl 还在仓库里"
}

# ---------- 5) 编译 ----------
$outDir = Join-Path $root 'dist'
$icon = Join-Path $root 'src/ClassPoints.App/Assets/app.ico'
$iss = Join-Path $root 'installer/ClassPoints.iss'

# 上一版同名安装包可能正被某个进程读着（老师刚下载完还没装），删不掉就挪开
$target = Join-Path $outDir "ClassPoints-Setup-$Version.exe"
if (Test-Path $target) {
    $aside = Join-Path $outDir ("_previous-Setup-{0}.exe" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Move-Item $target $aside
    Write-Host "    旧的同名安装包已挪到 $aside" -ForegroundColor DarkGray
}

Write-Host "==> 编译安装包（版本 $Version）" -ForegroundColor Cyan
& $isccPath `
    "/DMyVersion=$Version" `
    "/DMySourceDir=$SourceDir" `
    "/DMyOutputDir=$outDir" `
    "/DMyIconFile=$icon" `
    "/DLangFile=$langFile" `
    $iss

if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（exit=$LASTEXITCODE）" }
if (-not (Test-Path $target)) { throw "没有产出 $target" }

$info = Get-Item $target
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host ("==> 完成：{0}" -f $info.FullName) -ForegroundColor Green
Write-Host ("    版本 {0} · {1:N1} MB · 装到 Program Files，需要管理员权限" -f $Version, ($info.Length / 1MB))
Write-Host ("    sha256 {0}" -f $hash)

# 给发布脚本/CI 用的机器可读结果
$meta = [ordered]@{
    version = $Version
    file    = $info.Name
    size    = $info.Length
    sha256  = $hash
    path    = $info.FullName
}
$metaPath = Join-Path $outDir 'installer.json'
$meta | ConvertTo-Json | Set-Content $metaPath -Encoding utf8
Write-Host "    $metaPath"
