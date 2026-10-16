param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\LegendOfMortal',
    [ValidateSet(5, 6)][int]$BepInExVersion = 5,
    [string]$BepInExDir,
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
if (!$BepInExDir) { $BepInExDir = Join-Path $GameDir 'BepInEx\core' }
& dotnet build $testProject -c Release -p:Deploy=false "-p:GameDir=$GameDir" "-p:BepInExVersion=$BepInExVersion" "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$outputDir = Join-Path $projectRoot ('.work\regression-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputDir
& (Join-Path $PSScriptRoot "bin\bepinex$BepInExVersion\Release\LOMSave2.Tests.exe") $GameDir $projectRoot $outputDir $BepInExDir | Tee-Object -FilePath (Join-Path $outputDir 'results.txt')
exit $LASTEXITCODE
