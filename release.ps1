<#
.SYNOPSIS
  Publishes the current version as a GitHub Release; installed Voca (2.7.0+) lists it under
  Settings → "Cập nhật phiên bản", where the learner installs it with one click.
.DESCRIPTION
  1. Checks that the code is committed and pushed (the release must match the repository).
  2. Builds dist\v<Version>\Voca.exe with build.ps1 (behaviour checks included).
  3. Writes Voca.exe.sha256 and creates release v<Version> on github.com/trankien27/Voca with both files.
  Needs GitHub CLI, signed in once:  winget install --id GitHub.cli   then   gh auth login
  Before each release raise <Version> in Voca.csproj (a version can be released only once).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\release.ps1 -Notes "Thêm tab Từ sai, sửa nút Câu tiếp"
#>
param(
    [string]$Notes = "",
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "trankien27/Voca"

[xml]$project = Get-Content .\Voca.csproj
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Không đọc được <Version> trong Voca.csproj." }
$tag = "v$version"

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $gh) { throw "Chưa có GitHub CLI. Cài bằng: winget install --id GitHub.cli  rồi chạy: gh auth login" }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI chưa đăng nhập. Chạy: gh auth login" }

if (git status --porcelain) { throw "Còn thay đổi chưa commit. Commit (và push) trước khi phát hành." }
git fetch -q origin
git merge-base --is-ancestor HEAD origin/main
if ($LASTEXITCODE -ne 0) { throw "Commit hiện tại chưa có trên origin/main. Chạy git push trước khi phát hành." }

& $gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Đã có bản $tag trên GitHub. Tăng <Version> trong Voca.csproj rồi chạy lại." }

$folder = Join-Path $PSScriptRoot "dist\$tag"
$exe = Join-Path $folder "Voca.exe"
if (-not $SkipBuild -or -not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "build.ps1")
    if (-not (Test-Path $exe)) { throw "Không thấy $exe sau khi build." }
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = Join-Path $folder "Voca.exe.sha256"
[IO.File]::WriteAllText($hashFile, "$hash  Voca.exe`n")

if (-not $Notes) { $Notes = "Voca $version" }
$commit = (git rev-parse HEAD).Trim()
Write-Host "Đang tạo bản phát hành $tag..." -ForegroundColor Cyan
& $gh release create $tag $exe $hashFile --repo $repo --title "Voca $version" --notes $Notes --target $commit
if ($LASTEXITCODE -ne 0) { throw "Tạo bản phát hành thất bại." }

Write-Host "Đã phát hành Voca $version. Trên các máy: Thư viện → Cài đặt → Cập nhật phiên bản → chọn $version → Cập nhật." -ForegroundColor Green
Write-Host "https://github.com/$repo/releases/tag/$tag"
