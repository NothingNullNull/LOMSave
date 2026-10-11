param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\LegendOfMortal',
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (!$OutputDir) { $OutputDir = Join-Path $projectRoot 'dist' }
$pluginProject = Join-Path $projectRoot 'LOMSave2\LOMSave2.csproj'
& dotnet build $pluginProject -c Release -p:Deploy=false "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw 'MOD 构建失败，未生成发布包。' }
$dll = Join-Path $projectRoot 'LOMSave2\bin\Release\LOMSave2.dll'
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'DLL 发布版本无效。' }
$null = New-Item -ItemType Directory -Path $OutputDir -Force
$outputRoot = (Resolve-Path -LiteralPath $OutputDir).Path
$stage = Join-Path $projectRoot ('.work\package-' + [Guid]::NewGuid().ToString('N'))
$pluginDirectory = Join-Path $stage 'BepInEx\plugins\LOMSave2'
$instructions = Join-Path $stage 'LOMSave2说明'
$null = New-Item -ItemType Directory -Path $pluginDirectory, $instructions -Force
Copy-Item -LiteralPath $dll -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'CHANGELOG.md'),
    (Join-Path $projectRoot '游戏更新兼容性检查清单.md') -Destination $instructions
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $instructions -Recurse
$publishedDll = Join-Path $outputRoot 'LOMSave2.dll'
Copy-Item -LiteralPath $dll -Destination $publishedDll -Force
$archive = Join-Path $outputRoot "LOMSave-v$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$hashLines = foreach ($file in @($publishedDll, $archive)) {
    $digest = Get-FileHash -LiteralPath $file -Algorithm SHA256
    '{0}  {1}' -f $digest.Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($file)
}
$hashLines | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding UTF8
Write-Host "发布包：$archive"
Write-Host "独立 DLL：$publishedDll"
Write-Host '构建未修改游戏安装目录。'
