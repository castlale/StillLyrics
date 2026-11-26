param([switch]$Install)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$msix = Join-Path $project 'StillLyrics.WeChatEdition.msix'
if ($Install) {
    Add-AppxPackage -Path $msix -AllowUnsigned -DeferRegistrationWhenPackagesAreInUse
    return
}
if (Get-Process -Name StillLyrics -ErrorAction SilentlyContinue) {
    throw '请先在托盘右键退出正在运行的定格词岛，然后重新启动微信通知版。'
}
$manifest = [xml][IO.File]::ReadAllText((Join-Path $project 'WeChatEdition/AppxManifest.xml'))
function Get-IslandPackage {
    Get-AppxPackage -Name $manifest.Package.Identity.Name | Where-Object { $_.Publisher -eq $manifest.Package.Identity.Publisher }
}
$package = Get-IslandPackage
if (!$package -or $package.Version -lt [version]$manifest.Package.Identity.Version) {
    if (!(Test-Path -LiteralPath $msix)) { throw '缺少 StillLyrics.WeChatEdition.msix。' }
    # Windows requires an elevated installer for an unsigned MSIX with executable content.
    $installer = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -PassThru -Wait -ArgumentList ('-NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '" -Install')
    if ($installer.ExitCode -ne 0) { throw 'Windows 未安装通知版。普通版的任务栏闪烁提醒仍可使用。' }
    $package = Get-IslandPackage
}
if (!$package) { throw '当前用户尚未注册通知版。如果使用其他管理员账号安装，请用自己的 Windows 账号检查安装状态。' }
Start-Process explorer.exe -ArgumentList ('shell:AppsFolder\' + $package.PackageFamilyName + '!Island')
