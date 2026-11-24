param(
    [string] $TFlexHome = $env:TFLEX_HOME,
    [string] $SdkDir = $env:TFLEX_SDK_DIR,
    [string] $MSBuild,
    [string] $ReferenceRoot = $env:TFLEX_REFERENCE_ROOT,
    [switch] $WithFixture,
    [switch] $WithTransportCheck,
    [switch] $WithExperiment,
    [switch] $EnableTrustedScript,
    [string] $TrustedScript,
    [string] $JobDirectory,
    [string] $MacroSource
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $TFlexHome -and $SdkDir) {
    $TFlexHome = Split-Path -Parent (Resolve-Path -LiteralPath $SdkDir).Path
}
if (-not $TFlexHome) {
    $installs = @(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory -Filter 'T-FLEX CAD*18*' |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Program\TFlexAPI.dll') })
    if ($installs.Count -ne 1) { throw 'Set TFLEX_HOME to the T-FLEX CAD 18 installation.' }
    $TFlexHome = $installs[0].FullName
}
$TFlexHome = (Resolve-Path -LiteralPath $TFlexHome).Path
if (-not $SdkDir) { $SdkDir = Join-Path $TFlexHome 'SDK' }
$SdkDir = (Resolve-Path -LiteralPath $SdkDir).Path
$api = Get-Item -LiteralPath (Join-Path $TFlexHome 'Program\TFlexAPI.dll')
Write-Host "T-FLEX $($api.VersionInfo.FileVersion) | SDK $SdkDir"
if (-not $MSBuild) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $MSBuild = & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuild) { throw 'Install Visual Studio Build Tools or specify -MSBuild.' }
$projects = @('plugin\TFlexMcp.csproj')
if ($WithFixture) { $projects += 'tests\fixture\Fixture.csproj' }
if ($WithTransportCheck) { $projects += 'tests\pipe\TransportCheck.csproj' }
if ($WithExperiment) { $projects += 'experiments\TrustedRunner.csproj' }
if ($MacroSource) { $projects += 'education\MacroCompileCheck.csproj' }
$buildProperties = @("/p:TFlexHome=$TFlexHome", "/p:TFlexSdkDir=$SdkDir")
if ($MacroSource) { $buildProperties += "/p:MacroSource=$MacroSource" }
if (-not $ReferenceRoot) {
    $localReferences = Join-Path $projectRoot 'artifacts\net472\build'
    if (Test-Path -LiteralPath (Join-Path $localReferences '.NETFramework\v4.7.2')) {
        $ReferenceRoot = $localReferences
    }
}
if ($ReferenceRoot) {
    $buildProperties += "/p:TargetFrameworkRootPath=$((Resolve-Path -LiteralPath $ReferenceRoot).Path)"
}
foreach ($project in $projects) {
    & $MSBuild (Join-Path $projectRoot $project) /nologo /v:minimal /p:Configuration=Release /p:UseSharedCompilation=false @buildProperties
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed for $project ($LASTEXITCODE)." }
}
if ($EnableTrustedScript) {
    if (-not $TrustedScript -or -not $JobDirectory) { throw 'Specify -TrustedScript and -JobDirectory.' }
    $scriptSource = (Resolve-Path -LiteralPath $TrustedScript).Path
    $jobDir = (Resolve-Path -LiteralPath $JobDirectory).Path
    $jobObj = Join-Path $jobDir 'obj'
    & $MSBuild (Join-Path $projectRoot 'experiments\TrustedJob.csproj') /nologo /v:minimal /t:Build /p:Configuration=Release /p:UseSharedCompilation=false @buildProperties "/p:TrustedScript=$scriptSource" "/p:OutputPath=$jobDir/" "/p:IntermediateOutputPath=$jobObj/"
    if ($LASTEXITCODE -ne 0) { throw "Roslyn failed to compile the job ($LASTEXITCODE)." }
}
