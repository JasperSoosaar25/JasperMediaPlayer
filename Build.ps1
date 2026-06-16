Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\JasperMediaPlayer.csproj"

dotnet restore $project
dotnet build $project -c Release -r win-x64

Write-Host ""
Write-Host "Built! Try running:" -ForegroundColor Green
Write-Host ".\src\bin\Release\net8.0-windows10.0.19041.0\win-x64\JasperMediaPlayer.exe"
