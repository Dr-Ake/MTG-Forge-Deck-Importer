[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}
$toolDirectory = Join-Path $repositoryRoot 'artifacts\icon-builder'
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$builder = Join-Path $toolDirectory 'BuildImporterIcon.exe'
& $compiler /nologo /target:exe /optimize+ "/out:$builder" /reference:System.Drawing.dll /reference:System.Core.dll (Join-Path $repositoryRoot 'tools\BuildImporterIcon.cs')
if ($LASTEXITCODE -ne 0) { throw 'The icon builder did not compile.' }
& $builder (Join-Path $repositoryRoot 'assets')
if ($LASTEXITCODE -ne 0) { throw 'The icon artwork could not be generated.' }
