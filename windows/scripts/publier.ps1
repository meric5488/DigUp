# Publie Fouineur dans .\publication (exécutable Windows x64, runtime .NET inclus).
param([switch]$SansRuntime)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

dotnet run --project tests\Fouineur.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Des tests échouent." }

$selfContained = if ($SansRuntime) { 'false' } else { 'true' }
dotnet publish src\Fouineur.App -c Release -r win-x64 --self-contained $selfContained -o publication
if ($LASTEXITCODE -ne 0) { throw "Échec de la publication." }
Write-Host "`nFouineur publié dans $(Resolve-Path publication)\Fouineur.exe" -ForegroundColor Green
