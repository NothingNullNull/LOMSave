param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\LegendOfMortal',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $PSScriptRoot 'LOMSave2.Tests.csproj'
$storyDirectory = Join-Path $projectRoot 'assets\story'
if (!(Test-Path -LiteralPath (Join-Path $storyDirectory 'back_02_501.txt')) -or
    !(Test-Path -LiteralPath (Join-Path $storyDirectory 'ch2_4_5_3.txt'))) {
    & $Python (Join-Path $projectRoot 'tools\Extract-TestStories.py') --game-dir $GameDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& dotnet build $testProject -c Release -p:Deploy=false "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$outputDir = Join-Path $projectRoot ('.work\regression-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputDir
& (Join-Path $PSScriptRoot 'bin\Release\LOMSave2.Tests.exe') $GameDir $projectRoot $outputDir | Tee-Object -FilePath (Join-Path $outputDir 'results.txt')
exit $LASTEXITCODE
