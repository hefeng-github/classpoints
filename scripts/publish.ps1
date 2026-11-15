# 发布脚本：把应用打成一个可以整目录拷走的文件夹
#
#   pwsh scripts/publish.ps1                    # 默认 win-x64
#   pwsh scripts/publish.ps1 -Runtime win-arm64 # 国产 ARM 一体机
#   pwsh scripts/publish.ps1 -FrameworkDependent # 瘦身版：目标机自己装 .NET 10 运行时
#
# 默认**内置 .NET 运行时**（SelfContained）：产物体积大 70 MB 左右，
# 换目标机零依赖 —— 教室电脑是离线的，让它先装运行时等于把"双击就能用"变成
# "先找安装包、再找管理员"。
#
# 不用 PublishSingleFile：WinUI 3 的 XBF 资源在单文件模式下要额外解包，
# 教室电脑上杀毒软件还容易拦自解压，整目录拷贝反而更省事、启动更快。

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 只发瘦身版时用：不带 .NET 运行时，产物小 70 MB，但目标机必须装
    # ".NET 10 Desktop Runtime"。维护自己电脑的版本可以用它，发给教室别用。
    [switch]$FrameworkDependent,

    # 只在发正式版时传：覆盖 csproj 里的 <Version>。
    # 装出来的 exe 的版本号、安装包文件名、更新清单里的版本号必须是同一个值，
    # 否则会出现"提示更新到 1.3，装完还是 1.2"这种永远更新不完的循环。
    [string]$Version = '',

    # 编译进程序的公开版本库根地址。留空则运行时只能用设置里填的地址。
    # CI 从仓库变量 UPDATE_FEED 传进来，仓库里不出现真实仓库路径。
    [string]$UpdateFeed = '',

    # 只要文件夹、不要 zip。教室那台机器是整目录拷 U 盘的，用不上压缩包。
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$outDir = Join-Path $root "dist/ClassPoints-$Runtime"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

Write-Host "==> 清理 $outDir" -ForegroundColor Cyan
# 用「挪走」而不是「删掉」：上一版发布目录可能正被运行中的 exe 占用，
# Windows 上强删会半途失败留下残缺目录，比不删更糟。挪走是原子的，
# 而且万一新版本有问题，上一版还在，挪回来就能用。
if (Test-Path $outDir) {
    $aside = Join-Path (Split-Path -Parent $outDir) "_previous-$stamp"
    Move-Item $outDir $aside
    Write-Host "    上一版已挪到 $aside（确认新版没问题后可以删）" -ForegroundColor DarkGray
}

Write-Host "==> 发布（$Configuration / $Platform / $Runtime）" -ForegroundColor Cyan
$selfContained = -not $FrameworkDependent
if ($selfContained) {
    Write-Host "    内置 .NET 运行时：目标机不用装任何东西" -ForegroundColor DarkGray
} else {
    Write-Host "    框架依赖：目标机需要 .NET 10 Desktop Runtime！" -ForegroundColor Yellow
}

$versionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$') {
        throw "版本号必须是三段数字（主.次.修），当前是 '$Version'"
    }
    Write-Host "    版本 $Version（覆盖 csproj 里的值）" -ForegroundColor DarkGray
    $versionArgs = @(
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-p:FileVersion=$Version.0",
        "-p:AssemblyVersion=$Version.0"
    )
}

# 更新地址编译进程序集元数据（AssemblyMetadata）。留空 = 运行时只能用设置里填的，
# 这样本地构建出来的包不会带着某个部署的地址到处跑。
if (-not [string]::IsNullOrWhiteSpace($UpdateFeed)) {
    Write-Host "    更新地址 $UpdateFeed" -ForegroundColor DarkGray
    $versionArgs += "-p:UpdateFeed=$UpdateFeed"
}

# 参数里不能直接写 $selfContained.ToString()：PowerShell 会把整个 "xxx.ToString()"
# 当成字面量传给 dotnet，得到 -p:SelfContained=False.ToString()（.NET 拿它当 false，蒙对但不稳）。
$selfContainedArg = "-p:SelfContained=$($selfContained.ToString().ToLowerInvariant())"

dotnet publish (Join-Path $root 'src/ClassPoints.App/ClassPoints.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    -p:Platform=$platform `
    -p:WindowsAppSDKSelfContained=true `
    $selfContainedArg `
    -p:PublishReadyToRun=false `
    @versionArgs `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit=$LASTEXITCODE）" }

# 发布目录里那九十来个语言文件夹（af-ZA / ar-SA …）是 WindowsAppSDK 自带的
# Microsoft.ui.xaml.dll.mui 资源，每个 40KB 左右，合计约 4MB —— 属于 WinUI 的正常组成，
# 删掉不是优化而是隐患（删漏一个就可能让某个控件加载资源失败）。
# .NET 卫星程序集那部分已经用 SatelliteResourceLanguages 在编译期裁到中英双语。

$exe = Join-Path $outDir 'ClassPoints.exe'
if (-not (Test-Path $exe)) { throw "没有产出 ClassPoints.exe，检查发布参数" }

# 版本号"写的和编的不是一个"是最难查的一类问题：界面显示 1.2.0、安装包叫 1.3.0，
# 客户端就会一直提示有新版。这里用 exe 自己的 ProductVersion 对一次。
$productVersion = (Get-Item $exe).VersionInfo.ProductVersion
if (-not [string]::IsNullOrWhiteSpace($Version) -and $productVersion -notlike "$Version*") {
    throw "发布产物里的版本是 '$productVersion'，和要求的 $Version 不一致 —— 检查 -p:Version 是否被 csproj 里的值覆盖了"
}
Write-Host "    产物版本：$productVersion" -ForegroundColor DarkGray

# 「说好了内置运行时，产物里却没有 coreclr」是这一类改动最容易翻车的地方：
# csproj 里 SelfContained 的条件写错、或者发布参数被覆盖，都不会报任何错，
# 只会在教室电脑上双击时弹「你需要安装 .NET 桌面运行时」。按产物内容硬性对账。
$runtimeFiles = 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll'
$present = @($runtimeFiles | Where-Object { Test-Path (Join-Path $outDir $_) })
if ($selfContained -and $present.Count -lt $runtimeFiles.Count) {
    $missing = $runtimeFiles | Where-Object { -not (Test-Path (Join-Path $outDir $_)) }
    throw "要求内置 .NET，但产物里缺少 $($missing -join '、') —— 检查 csproj 的 SelfContained 条件是否生效"
}
if (-not $selfContained -and $present.Count -gt 0) {
    throw "要求框架依赖，但产物里带着 $($present -join '、') —— SelfContained=false 没传下去"
}

# XAML 页面在编译期被做成 XBF 塞进 ClassPoints.pri，运行时不读 .xaml 源文件。
# 少了这个 pri，exe 双击起来就是 XamlParseException（MainWindow.InitializeComponent），
# 而且 `dotnet build` 的产物是好的、只有 publish 的产物坏，很容易看走眼。
# 根因是 csproj 里 EnableMsixTooling 被关掉时 MSIX 公共 targets 不导入，
# AddProjectPriToResolvedFileToPublish 也就不存在了。这里硬性卡一道。
foreach ($need in 'ClassPoints.pri', 'Microsoft.UI.Xaml.Controls.pri') {
    $p = Join-Path $outDir $need
    if (-not (Test-Path $p)) { throw "发布目录缺少 $need —— XAML 资源没进包，检查 csproj 的 EnableMsixTooling 是否仍为 true" }
}

# 一个启动脚本，双击就能用。
# 形态（大屏/电脑/只读）和预配置包都不走命令行参数了 —— 首次启动向导里问一次，
# 存在本机，之后启动直接进对应界面。理由：老师不会记参数，参数写死在快捷方式里
# 换台机器就静默进错形态。
@'
@echo off
start "" "%~dp0ClassPoints.exe"
'@ | Set-Content (Join-Path $outDir '启动.cmd') -Encoding Default

$size = (Get-ChildItem $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host "==> 完成：$outDir" -ForegroundColor Green
Write-Host ("    文件 {0} 个，共 {1:N1} MB" -f (Get-ChildItem $outDir -Recurse -File).Count, $size)
Write-Host "    整个目录拷到目标机，双击「启动.cmd」；第一次启动会让你选这台电脑做什么"

# 顺手压一个 zip：GitHub Actions 要把它传到 CNB 版本库当附件，
# 教室里也可能有人想发个压缩包给同事（邮件/网盘比整目录好传）。
#
# 用 Compress-Archive 而不是 ZipFile.CreateFromDirectory：后者要先 Add-Type
# 加载 System.IO.Compression.FileSystem，在受限环境里会被拦；Compress-Archive
# 是内置 cmdlet，哪都能跑。代价是慢一些（371 个文件约 45 秒），只在发布时发生。
if (-not $NoZip) {
    $zip = Join-Path $root "dist/ClassPoints-$Runtime.zip"
    if (Test-Path $zip) {
        # 同样是挪走不删：正在被别的进程读着的时候删不掉
        Move-Item $zip (Join-Path $root "dist/_previous-$stamp.zip")
    }

    # 进度条会把 CI 日志刷成几百行，而且没有信息量
    $prevProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Compress-Archive -Path $outDir -DestinationPath $zip -CompressionLevel Optimal
    }
    finally {
        $ProgressPreference = $prevProgress
    }

    $zipSize = (Get-Item $zip).Length / 1MB
    Write-Host ""
    Write-Host ("==> 压缩包：$zip（{0:N1} MB，解压出来是「{1}」文件夹）" -f $zipSize, (Split-Path -Leaf $outDir)) -ForegroundColor Green
}
