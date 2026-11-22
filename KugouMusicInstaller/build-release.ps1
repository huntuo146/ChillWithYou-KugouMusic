param(
    [string]$Version = "1.0.0",
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$installerProjDir = $PSScriptRoot
$gameRoot = Split-Path -Parent $installerProjDir
$modProj = Join-Path $gameRoot "KugouMusicMod\ChillWithYou.KugouMusic.csproj"
$installerProj = Join-Path $installerProjDir "ChillWithYou.KugouMusic.Installer.csproj"
$releaseDir = Join-Path $gameRoot "Release"
$stageDir = Join-Path $env:TEMP ("cwy_kugou_pkg_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))

Write-Host "=== 1. 构建插件 ChillWithYou.KugouMusic (v$Version) ===" -ForegroundColor Cyan
$verAssembly = ($Version -replace '[-+].*$', '') + ".0"
$buildArgs = @('build', $modProj, '-c', 'Release', '--no-incremental', "-p:Version=$Version", "-p:AssemblyVersion=$verAssembly", "-p:FileVersion=$verAssembly")
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "插件编译失败" }

Write-Host "=== 2. 打包运行依赖与插件荷载 (Payload.zip) ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    $rootFiles = @('.doorstop_version', 'doorstop_config.ini', 'winhttp.dll', 'changelog.txt', '启动酷狗网关.bat')
    foreach ($f in $rootFiles) {
        $src = Join-Path $gameRoot $f
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $stageDir $f) -Force
        }
    }

    # BepInEx\core + 插件 DLL（绝不包含用户 config / cache / log）
    $coreSrc = Join-Path $gameRoot "BepInEx\core"
    $coreDst = Join-Path $stageDir "BepInEx\core"
    Copy-Item -LiteralPath $coreSrc -Destination $coreDst -Recurse -Force

    $pluginsDst = Join-Path $stageDir "BepInEx\plugins"
    New-Item -ItemType Directory -Path $pluginsDst -Force | Out-Null
    $builtDll = Join-Path $gameRoot "KugouMusicMod\bin\Release\ChillWithYou.KugouMusic.dll"
    Copy-Item -LiteralPath $builtDll -Destination (Join-Path $pluginsDst "ChillWithYou.KugouMusic.dll") -Force
    Copy-Item -LiteralPath $builtDll -Destination (Join-Path $releaseDir "ChillWithYou.KugouMusic.dll") -Force

    # KuGouMusicApi 精简运行时（排除 .git / .github / docs / 本地 .env 隐私凭据）
    $apiSrc = Join-Path $gameRoot "KuGouMusicApi"
    $apiDst = Join-Path $stageDir "KuGouMusicApi"
    New-Item -ItemType Directory -Path $apiDst -Force | Out-Null

    foreach ($dirName in @('module', 'util', 'public', 'node_modules')) {
        $sd = Join-Path $apiSrc $dirName
        if (Test-Path -LiteralPath $sd) {
            Copy-Item -LiteralPath $sd -Destination (Join-Path $apiDst $dirName) -Recurse -Force
        }
    }

    foreach ($fileName in @('app.js', 'main.js', 'server.js', 'index.js', 'package.json', 'start-gateway.ps1', '.env.example', 'LICENSE')) {
        $sf = Join-Path $apiSrc $fileName
        if (Test-Path -LiteralPath $sf) {
            Copy-Item -LiteralPath $sf -Destination (Join-Path $apiDst $fileName) -Force
        }
    }

    $zipOut = Join-Path $releaseDir ("ChillWithYou-KugouMusic-v$Version.zip")
    if (Test-Path -LiteralPath $zipOut) { Remove-Item -LiteralPath $zipOut -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipOut, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $assetsDir = Join-Path $installerProjDir "Assets"
    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
    $payloadZip = Join-Path $assetsDir "Payload.zip"
    Copy-Item -LiteralPath $zipOut -Destination $payloadZip -Force
    Write-Host ("已生成完整发布包: {0} ({1:N2} MB)" -f $zipOut, ((Get-Item $zipOut).Length / 1MB)) -ForegroundColor Green

    Write-Host "=== 3. 发布单文件 EXE 安装器 ===" -ForegroundColor Cyan
    $pubTemp = Join-Path $stageDir "pub"
    $pubArgs = @(
        'publish', $installerProj,
        '-c', 'Release',
        '-r', 'win-x64',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$verAssembly",
        "-p:FileVersion=$verAssembly",
        '-o', $pubTemp
    )

    if ($SelfContained) {
        $pubArgs += @('--self-contained=true', '-p:EnableCompressionInSingleFile=true')
    }
    else {
        $pubArgs += @('--self-contained=false', '-p:_RequiresILLinkPack=false')
    }

    & dotnet @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "安装器发布失败" }

    $exeBuilt = Join-Path $pubTemp "ChillWithYou-KugouMusic-Installer.exe"
    $exeOut = Join-Path $releaseDir "ChillWithYou-KugouMusic-Installer.exe"
    Copy-Item -LiteralPath $exeBuilt -Destination $exeOut -Force
    Write-Host ("已生成安装器: {0} ({1:N2} MB)" -f $exeOut, ((Get-Item $exeOut).Length / 1MB)) -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
}
