[CmdletBinding()]
param([string]$Compiler = "$PSScriptRoot\work\inno\ISCC.exe")
& "$PSScriptRoot\build-release.ps1" -Compiler $Compiler
