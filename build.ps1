param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'), [switch]$Check, [switch]$WindowCheck, [switch]$OnlineCheck, [switch]$WeChatEdition, [switch]$FrostCheck, [string]$MakeAppx, [switch]$FrameCheck)
$ErrorActionPreference = 'Stop'
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$outDir = $OutputDirectory
if ($WeChatEdition -and !$PSBoundParameters.ContainsKey('OutputDirectory')) { $outDir = Join-Path $PSScriptRoot 'WeChatEdition' }
[void](New-Item -ItemType Directory -Force -Path $outDir)
$sources = (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
$refs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Net.Http.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\WindowsBase.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fx $_) }
$refs += @('System.Runtime.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fx $_) }
$refs += @('Windows.Foundation.winmd','Windows.Media.winmd','Windows.Storage.winmd','Windows.UI.winmd','Windows.ApplicationModel.winmd') |
    ForEach-Object { '/reference:' + (Join-Path "$env:WINDIR\System32\WinMetadata" $_) }
& (Join-Path $fx 'csc.exe') /nologo /optimize+ /utf8output /codepage:65001 /target:winexe /platform:x64 ("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')) ("/out:" + (Join-Path $outDir 'StillLyrics.exe')) @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($WeChatEdition) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging/AppxManifest.xml') -Destination $outDir -Force
    $assetDir = Join-Path $outDir 'Assets'
    [void](New-Item -ItemType Directory -Force -Path $assetDir)
    Add-Type -AssemblyName System.Drawing
    foreach ($entry in @{ StoreLogo = 50; Square44 = 44; Square150 = 150 }.GetEnumerator()) {
        $size = [int]$entry.Value
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.ScaleTransform($size / 32.0, $size / 32.0)
            $graphics.FillEllipse([Drawing.Brushes]::MediumAquamarine, 1, 6, 20, 20)
            $graphics.FillEllipse([Drawing.Brushes]::MediumAquamarine, 11, 6, 20, 20)
            $graphics.FillRectangle([Drawing.Brushes]::MediumAquamarine, 10, 6, 12, 20)
            foreach ($bar in @(10, 16, 22)) {
                $height = if ($bar -eq 16) { 12 } else { 7 }
                $graphics.FillRectangle([Drawing.Brushes]::Black, $bar - 1, (32 - $height) / 2, 2, $height)
            }
            $bitmap.Save((Join-Path $assetDir ($entry.Key + '.png')), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    if ($MakeAppx) {
        $msix = Join-Path $PSScriptRoot 'StillLyrics.WeChatEdition.msix'
        & $MakeAppx pack /d $outDir /p $msix /o
        if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }
    }
}
if ($Check -or $WindowCheck -or $OnlineCheck -or $FrostCheck -or $FrameCheck) {
    $testExe = Join-Path ([IO.Path]::GetTempPath()) ('StillLyricsCheck-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        & (Join-Path $fx 'csc.exe') /nologo /optimize+ /codepage:65001 /target:exe /platform:x64 /main:StillLyrics.TimingCheck ("/out:" + $testExe) @refs @sources (Join-Path $PSScriptRoot 'tests\TimingCheck.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
        $options = @()
        if ($WindowCheck) { $options += '--window' }
        if ($FrameCheck) { $options += '--frames' }
        if ($FrostCheck) { $options += '--frost' }
        if ($OnlineCheck) { $options += '--online' }
        & $testExe @options
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    } finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
}
