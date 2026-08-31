# Repro for F-009. Run in Windows PowerShell 5.1 (powershell.exe).
# Defining `function Md` does NOT shadow the built-in `md` alias for mkdir.

function Md([string]$Path, [string]$Method = "GET", $Body) {
  "hello from function; path=$Path method=$Method"
}

Set-Location $env:USERPROFILE
Remove-Item .\Status -ErrorAction SilentlyContinue -Recurse -Force

$out = Md "Status"

Write-Host "returned value : $out"
Write-Host "Status exists  : $(Test-Path .\Status)"
Write-Host "Get-Command Md :"
Get-Command Md | Format-List Name, CommandType, Definition

if (Test-Path .\Status) { Remove-Item .\Status -Recurse -Force }
