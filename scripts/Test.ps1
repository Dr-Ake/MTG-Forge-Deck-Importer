[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $repositoryRoot ('artifacts\test-' + [Guid]::NewGuid().ToString('N'))
$buildDirectory = Join-Path $testDirectory 'build'
$executable = & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $buildDirectory
$report = Join-Path $testDirectory 'self-test-report.txt'
$testProcess = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
    throw 'The importer did not produce a test report.'
}
Get-Content -LiteralPath $report
if ($testProcess.ExitCode -ne 0) { throw 'The importer self-tests failed.' }
Write-Host ('Test evidence: ' + $testDirectory)
