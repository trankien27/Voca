<#
.SYNOPSIS
  Builds Voca into dist\v<Version>\ (version read from Voca.csproj), after running the behaviour checks.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

[xml]$project = Get-Content .\Voca.csproj
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Không đọc được <Version> trong Voca.csproj." }
$target = Join-Path $PSScriptRoot "dist\v$version"

Write-Host "Chạy kiểm tra..." -ForegroundColor Cyan
dotnet run --project .\tests\Voca.Checks -v q
if ($LASTEXITCODE -ne 0) { throw "Kiểm tra thất bại, dừng build." }

if (Test-Path $target) {
    Write-Warning "dist\v$version đã tồn tại và sẽ bị ghi đè. Nhớ tăng <Version> trong Voca.csproj khi có thay đổi mới."
    Remove-Item $target -Recurse -Force
}
dotnet publish .\Voca.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $target -v q
if ($LASTEXITCODE -ne 0) { throw "Build thất bại." }

Write-Host "Đã build Voca $version vào $target" -ForegroundColor Green
