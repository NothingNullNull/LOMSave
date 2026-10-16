param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\LegendOfMortal',
    [string]$OutputDir,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (!$OutputDir) { $OutputDir = Join-Path $projectRoot 'dist' }
$pluginProject = Join-Path $projectRoot 'LOMSave2\LOMSave2.csproj'
$packager = Join-Path $PSScriptRoot 'package_bundle.py'
$stage = Join-Path $projectRoot ('.work\package-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $stage 'artifacts'
$null = New-Item -ItemType Directory -Path $artifacts -Force
$version = $null
$names = @()
foreach ($major in @(5, 6)) {
    $loader = Join-Path $stage "loader-bepinex$major"
    & $Python $packager prepare --bepinex $major --output $loader
    if ($LASTEXITCODE -ne 0) { throw "BepInEx $major 官方依赖准备失败。" }
    $core = Join-Path $loader 'BepInEx\core'
    & dotnet build $pluginProject -c Release -p:Deploy=false "-p:GameDir=$GameDir" "-p:BepInExVersion=$major" "-p:BepInExDir=$core"
    if ($LASTEXITCODE -ne 0) { throw "BepInEx $major MOD 构建失败，未发布产物。" }
    $dll = Join-Path $projectRoot "LOMSave2\bin\bepinex$major\Release\LOMSave2.dll"
    $builtVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
    if ($builtVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'DLL 发布版本无效。' }
    if ($version -and $version -ne $builtVersion) { throw '两种构建的版本号不一致。' }
    $version = $builtVersion
    $packageStage = Join-Path $stage "mod-bepinex$major"
    $pluginDirectory = Join-Path $packageStage 'BepInEx\plugins\LOMSave2'
    $instructions = Join-Path $packageStage 'LOMSave2说明'
    $null = New-Item -ItemType Directory -Path $pluginDirectory, $instructions -Force
    Copy-Item -LiteralPath $dll -Destination $pluginDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'CHANGELOG.md'), (Join-Path $projectRoot '游戏更新兼容性检查清单.md') -Destination $instructions
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $instructions -Recurse
    $loaderVersion = if ($major -eq 5) { '5.4.23.5 Windows x64 Mono' } else { '6.0.0-be.692 Unity Mono Windows x64' }
    [IO.File]::WriteAllText((Join-Path $instructions '包版本.md'), "# 本包适用版本`n`nLOMSave $version，适用于 BepInEx $loaderVersion。`n`n只安装一份 LOMSave2.dll；另一种 BepInEx 构建不能同时安装。详细升级与回退步骤见 README.md。`n", (New-Object Text.UTF8Encoding($false)))
    $dllName = "LOMSave2-BepInEx$major.dll"
    $archiveName = "LOMSave-v$version-BepInEx$major.zip"
    $bundleName = "LOMSave-v$version-BepInEx$major-OneClick.zip"
    Copy-Item -LiteralPath $dll -Destination (Join-Path $artifacts $dllName)
    Compress-Archive -Path (Join-Path $packageStage '*') -DestinationPath (Join-Path $artifacts $archiveName)
    & $Python $packager build --bepinex $major --mod-package (Join-Path $artifacts $archiveName) --output (Join-Path $artifacts $bundleName)
    if ($LASTEXITCODE -ne 0) { throw "BepInEx $major 一键包构建失败。" }
    $names += @($dllName, $archiveName, $bundleName)
}
$hashLines = foreach ($name in $names) {
    $digest = Get-FileHash -LiteralPath (Join-Path $artifacts $name) -Algorithm SHA256
    '{0}  {1}' -f $digest.Hash.ToLowerInvariant(), $name
}
[IO.File]::WriteAllText((Join-Path $artifacts 'SHA256SUMS.txt'), (($hashLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
& $Python $packager verify --release-dir $artifacts --version $version
if ($LASTEXITCODE -ne 0) { throw '双版本发布包检查失败，不能发布。' }
$null = New-Item -ItemType Directory -Path $OutputDir -Force
foreach ($name in ($names + @('SHA256SUMS.txt'))) {
    Copy-Item -LiteralPath (Join-Path $artifacts $name) -Destination (Join-Path $OutputDir $name) -Force
}
Write-Host "双版本发布包已生成：$((Resolve-Path -LiteralPath $OutputDir).Path)"
Write-Host '包含两种普通包、两种一键包、两个 DLL 和校验文件；未修改游戏安装目录。'
