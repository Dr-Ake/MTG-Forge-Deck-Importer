[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'dist'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET'
$compiler = Join-Path $frameworkRoot 'Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $frameworkRoot 'Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

$source = Join-Path $repositoryRoot 'src\ForgeDeckImporter.cs'
$icon = Join-Path $repositoryRoot 'assets\Forge Deck Importer.ico'
foreach ($requiredFile in @($source, $icon)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw ('A required build file is missing: ' + $requiredFile)
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$executable = Join-Path $OutputDirectory 'Forge Deck Importer.exe'
& $compiler /nologo /target:winexe /optimize+ "/out:$executable" "/win32icon:$icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source
if ($LASTEXITCODE -ne 0) { throw 'The importer did not compile.' }
Write-Host ('Built ' + $executable)
Write-Output $executable
