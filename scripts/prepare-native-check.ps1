param([string] $OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\native-check'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$checkDir = [IO.Path]::GetFullPath($OutputDir)
$null = New-Item -ItemType Directory -Path $checkDir -Force
$model = Get-Content -Raw -LiteralPath (Join-Path $root 'tests\fixture\FixtureModel.cs')
$check = Get-Content -Raw -LiteralPath (Join-Path $root 'tests\fixture\NativeChecks.cs')
$variableExpressions = Get-Content -Raw -LiteralPath (Join-Path $root 'plugin\VariableExpressions.cs')
$references = "using System.Collections.Generic;`nusing System.Globalization;`nusing System.Text;`n"
$source = $references + $model + [Environment]::NewLine +
    $check.Replace('__NATIVE_CHECK_DIRECTORY__', $checkDir.Replace('"', '""')) + [Environment]::NewLine +
    $variableExpressions.Substring($variableExpressions.IndexOf('namespace TFlexMcp'))
$target = Join-Path $checkDir 'native-check.cs'
Set-Content -LiteralPath $target -Value $source -Encoding utf8
Write-Output $target
