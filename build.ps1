# Builds this mod.  .\build.ps1  |  .\build.ps1 -Install  (copies into the game)
param([switch]$Install)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$extra = @()
if ($Install) { $extra += '-p:Installieren=true' }
Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' } | ForEach-Object {
    dotnet build $_.FullName -c Release --nologo -v q @extra
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($_.Name)" }
}
Write-Host 'Done.'
