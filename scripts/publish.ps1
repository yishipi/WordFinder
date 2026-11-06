param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipTests) {
        dotnet test tests/WordFinder.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    dotnet publish src/WordFinder.Desktop -p:PublishProfile=Portable
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $releaseDirectory = Join-Path $projectRoot 'artifacts/release'
    New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
    $executable = Join-Path $releaseDirectory 'WordFinder.exe'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts/publish/win-x64/WordFinder.exe') -Destination $executable
    $hash = Get-FileHash -LiteralPath $executable -Algorithm SHA256
    Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Value "$($hash.Hash.ToLowerInvariant())  WordFinder.exe" -Encoding ascii
    Write-Output "EXE: $executable"
    Write-Output "Size: $([math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 2)) MiB"
    Write-Output "SHA256: $($hash.Hash)"
}
finally { Pop-Location }
