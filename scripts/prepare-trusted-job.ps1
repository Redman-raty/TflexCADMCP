param(
    [switch] $Enable,
    [Parameter(Mandatory)] [string] $Script,
    [Parameter(Mandatory)] [string[]] $Files,
    [string] $TFlexHome = $env:TFLEX_HOME,
    [string] $ReferenceRoot = $env:TFLEX_REFERENCE_ROOT
)
$ErrorActionPreference = 'Stop'
if (-not $Enable) { throw 'Pass -Enable to prepare a manually launched trusted-script job.' }
if ($Files.Count -lt 1 -or $Files.Count -gt 100) { throw 'Specify 1-100 .grs test inputs.' }
$inputs = @($Files | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    if ($item.PSIsContainer -or $item.Extension -ine '.grs') { throw "Expected a .grs file: $_" }
    $item.FullName
})
$root = Split-Path -Parent $PSScriptRoot
$job = Join-Path $root ('artifacts\trusted-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $job
& (Join-Path $PSScriptRoot 'build.ps1') -WithExperiment -EnableTrustedScript -TrustedScript $Script -JobDirectory $job -TFlexHome $TFlexHome -ReferenceRoot $ReferenceRoot
$manifest = for ($i = 0; $i -lt $inputs.Count; $i++) {
    $name = '{0:D3}-{1}' -f ($i + 1), [IO.Path]::GetFileName($inputs[$i])
    Copy-Item -LiteralPath $inputs[$i] -Destination (Join-Path $job $name)
    $name
}
$manifest | Set-Content -LiteralPath (Join-Path $job 'job.txt') -Encoding utf8
Write-Output (Join-Path $job 'job.txt')
