[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$errorsFound = @()
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -Recurse -File | Where-Object FullName -NotMatch '[\\/]test-results[\\/]' | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors)
    $errorsFound += @($parseErrors)
}
if ($errorsFound.Count) { $errorsFound | Format-List; exit 1 }
Write-Host 'PASS: all tools PowerShell scripts parse.'
