$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $taskCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    "/out:$taskRoot\PhotoChecker.exe" "/win32manifest:$PSScriptRoot\app.manifest" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll `
    "$PSScriptRoot\PhotoChecker.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
Get-Item -LiteralPath (Join-Path $taskRoot 'PhotoChecker.exe') | Select-Object FullName,Length,LastWriteTime
