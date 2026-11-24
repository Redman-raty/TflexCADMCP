param(
    [string] $Output = 'artifacts\education\McpEducation.cs',
    [string] $TFlexHome = $env:TFLEX_HOME,
    [string] $ReferenceRoot = $env:TFLEX_REFERENCE_ROOT
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sources = @('education\MacroBridge.cs', 'plugin\CadOperations.cs', 'plugin\DrawingOperations.cs',
    'plugin\PipeServer.cs', 'plugin\VariableExpressions.cs')
$headers = [Collections.Generic.List[string]]::new()
$bodies = foreach ($source in $sources) {
    $code = Get-Content -Raw -LiteralPath (Join-Path $root $source)
    $start = $code.IndexOf('namespace ')
    foreach ($line in ($code.Substring(0, $start) -split '\r?\n')) {
        if ($line.StartsWith('using ') -and -not $headers.Contains($line)) { $headers.Add($line) }
    }
    $code.Substring($start)
}
$target = [IO.Path]::GetFullPath($Output)
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
Set-Content -LiteralPath $target -Value (($headers -join "`n") + "`n`n" + ($bodies -join "`n")) -Encoding utf8
& (Join-Path $PSScriptRoot 'build.ps1') -TFlexHome $TFlexHome -ReferenceRoot $ReferenceRoot -MacroSource $target
Write-Output $target
