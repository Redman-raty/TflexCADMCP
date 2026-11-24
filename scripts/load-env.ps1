param(
    [string] $Path = (Join-Path (Split-Path -Parent $PSScriptRoot) '.env')
)

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Local configuration is missing: $Path. Copy .env.example to .env first."
}

$allowed = @(
    'TFLEX_HOME', 'TFLEX_SDK_DIR', 'TFLEX_API_XML', 'TFLEX_REFERENCE_ROOT',
    'TFLEX_MCP_MODE', 'TFLEX_MCP_TIMEOUT', 'TFLEX_MCP_INDEX', 'TFLEX_MCP_PIPE_NAME'
)
$settings = @{}
$lineNumber = 0
foreach ($raw in (Get-Content -Encoding UTF8 -LiteralPath $Path -ErrorAction Stop)) {
    $lineNumber++
    $line = $raw.Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    if ($line -cnotmatch '^([A-Z_][A-Z0-9_]*)\s*=\s*(.*)$') {
        throw "Invalid NAME=VALUE on line $lineNumber in $Path."
    }
    $name = $Matches[1]
    $value = $Matches[2].Trim()
    if ($name -cnotin $allowed) { throw "Unknown variable $name on line $lineNumber in $Path." }
    if ($settings.ContainsKey($name)) { throw "Duplicate variable $name on line $lineNumber in $Path." }
    if ($value.Length -ge 2 -and
        (($value.StartsWith('"') -and $value.EndsWith('"')) -or
         ($value.StartsWith("'") -and $value.EndsWith("'")))) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    $settings[$name] = $value
}

foreach ($name in $settings.Keys) {
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}
