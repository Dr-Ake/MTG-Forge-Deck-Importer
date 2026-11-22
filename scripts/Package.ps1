[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$executable = & (Join-Path $PSScriptRoot 'Build.ps1')
$readme = Join-Path $repositoryRoot 'README.md'
if (-not (Test-Path -LiteralPath $readme -PathType Leaf)) {
    throw 'README.md is missing.'
}

# Only these public distribution files are included. Personal decks,
# machine-specific settings, and test reports are never collected.
$archive = Join-Path $OutputDirectory 'Forge-Deck-Importer-Windows.zip'
Compress-Archive -LiteralPath @($executable, $readme) -DestinationPath $archive -CompressionLevel Optimal -Force
$checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = $checksum + '  ' + [System.IO.Path]::GetFileName($archive) + [Environment]::NewLine
[System.IO.File]::WriteAllText($checksumPath, $checksumLine, [System.Text.UTF8Encoding]::new($false))
Write-Host ('Packaged ' + $archive)
Write-Output $archive
Write-Output $checksumPath
