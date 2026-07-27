$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseDirectory = Join-Path $repoRoot "release"
$publishDirectory = Join-Path $releaseDirectory "controller"
$version = if ($env:GITHUB_REF_NAME) { $env:GITHUB_REF_NAME } else { "local" }

New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
dotnet publish (Join-Path $repoRoot "src\GarageGames.Controller\GarageGames.Controller.csproj") `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output $publishDirectory

Copy-Item (Join-Path $repoRoot "config") $publishDirectory -Recurse -Force
Copy-Item (Join-Path $repoRoot "docs\OPERATOR_RUNBOOK.md") $publishDirectory -Force

$archive = Join-Path $releaseDirectory "garage-games-controller-$version-win-x64.zip"
if (Test-Path $archive) { Remove-Item -LiteralPath $archive }
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive
Write-Output $archive

