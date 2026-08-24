param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$UpgradeInstallerPath
)

$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$upgradeInstaller = (Resolve-Path -LiteralPath $UpgradeInstallerPath).Path
$fixtureId = [Guid]::NewGuid().ToString('N')
$installDirectory = Join-Path $env:TEMP "DeskButler-Installer-$fixtureId"
$dataDirectory = Join-Path $env:LOCALAPPDATA 'DeskButler'
$readyMarker = Join-Path $dataDirectory 'run.lock'
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\DeskButler'
$shortcutDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskButler'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskButler_is1'
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$unrelatedName = "DeskButlerInstallerFixture_$fixtureId"
$unrelatedValue = "unrelated-$fixtureId"
$installed = $false
$registryBase = $null
$runKey = $null
$runningProcess = $null

# 启动安装或卸载进程，并把任何非零退出码视为失败。
function Invoke-CheckedProcess {
    param([string]$FilePath, [string[]]$Arguments)
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode): $FilePath"
    }
}

# 读取当前用户卸载项中的展示版本。
function Get-InstalledDisplayVersion {
    $key = $registryBase.OpenSubKey($uninstallKeyPath)
    if ($null -eq $key) {
        throw 'DeskButler uninstall registration was not found.'
    }
    try {
        return [string]$key.GetValue('DisplayVersion', '')
    } finally {
        $key.Dispose()
    }
}

# 等待真实桌面进程创建运行标记并精确写入自己的 HKCU Run 命令。
function Wait-ApplicationReady {
    param([System.Diagnostics.Process]$Process, [string]$ExpectedRunCommand)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "DeskButler exited before becoming ready: $($Process.ExitCode)"
        }
        if ((Test-Path -LiteralPath $readyMarker) -and
            ([string]$runKey.GetValue('DeskButler', '') -ceq $ExpectedRunCommand)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'DeskButler did not become ready or write the exact HKCU Run command in time.'
}

try {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    if ((Test-Path -LiteralPath $defaultInstallDirectory) -or
        (Test-Path -LiteralPath $shortcutDirectory)) {
        throw 'BLOCK: an existing DeskButler installation was detected.'
    }
    if (Test-Path -LiteralPath $dataDirectory) {
        throw 'BLOCK: real install/launch verification requires a clean account with no DeskButler user data.'
    }
    $uninstallKey = $registryBase.OpenSubKey($uninstallKeyPath)
    if ($null -ne $uninstallKey) {
        $uninstallKey.Dispose()
        throw 'BLOCK: an existing DeskButler uninstall registration was detected.'
    }

    $runKey = $registryBase.CreateSubKey($runKeyPath, $true)
    $runKey.SetValue($unrelatedName, $unrelatedValue, [Microsoft.Win32.RegistryValueKind]::String)

    Invoke-CheckedProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installDirectory")
    $installed = $true
    $executable = Join-Path $installDirectory 'DeskButler.Desktop.exe'
    $versionMarker = Join-Path $installDirectory 'installed-version.txt'
    $expectedRunCommand = '"' + [IO.Path]::GetFullPath($executable) + '"'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw 'Installed executable was not found.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $shortcutDirectory 'DeskButler.lnk'))) {
        throw 'Start Menu shortcut was not found.'
    }
    if ((Get-InstalledDisplayVersion) -ne '0.1.0') {
        throw 'Initial uninstall registration DisplayVersion is not 0.1.0.'
    }
    if ([IO.File]::ReadAllText($versionMarker).Trim() -ne '0.1.0') {
        throw 'Initial installed-version marker is not 0.1.0.'
    }
    if ([IO.Directory]::EnumerateFiles($installDirectory, '*.pdb',
        [IO.SearchOption]::AllDirectories).GetEnumerator().MoveNext()) {
        throw 'Installed application tree contains a PDB file.'
    }

    $runningProcess = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
    Wait-ApplicationReady $runningProcess $expectedRunCommand
    $initialProcessId = $runningProcess.Id

    Invoke-CheckedProcess $upgradeInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installDirectory")
    if (-not $runningProcess.WaitForExit(10000)) {
        throw 'Running 0.1.0 instance did not exit naturally during upgrade.'
    }
    $runningProcess.Dispose()
    $runningProcess = $null
    if ((Get-InstalledDisplayVersion) -ne '0.1.1') {
        throw 'Upgrade did not change uninstall registration DisplayVersion to 0.1.1.'
    }
    if ([version]'0.1.1' -le [version]'0.1.0') {
        throw 'Upgrade fixture version must be strictly higher than the initial version.'
    }
    if ([IO.File]::ReadAllText($versionMarker).Trim() -ne '0.1.1') {
        throw 'Upgrade did not replace installed-version marker with 0.1.1.'
    }
    if ([string]$runKey.GetValue('DeskButler', '') -cne $expectedRunCommand) {
        throw 'Running upgrade removed or changed the exact DeskButler HKCU Run command.'
    }
    if ($runKey.GetValue($unrelatedName) -ne $unrelatedValue) {
        throw 'Unrelated HKCU Run value was modified during upgrade.'
    }

    $runningProcess = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
    Wait-ApplicationReady $runningProcess $expectedRunCommand
    if ($runningProcess.Id -eq $initialProcessId) {
        throw 'Upgrade validation did not start a new DeskButler process.'
    }

    Invoke-CheckedProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $installed = $false
    if (-not $runningProcess.WaitForExit(10000)) {
        throw 'Running upgraded instance did not exit naturally during uninstall.'
    }
    $runningProcess.Dispose()
    $runningProcess = $null
    if ((Test-Path -LiteralPath $installDirectory) -or (Test-Path -LiteralPath $shortcutDirectory)) {
        throw 'Program files or Start Menu shortcut remained after uninstall.'
    }
    if ($runKey.GetValue('DeskButler')) {
        throw 'DeskButler HKCU Run value remained after uninstall.'
    }
    if ($runKey.GetValue($unrelatedName) -ne $unrelatedValue) {
        throw 'Unrelated HKCU Run value was removed during uninstall.'
    }
} finally {
    if ($installed -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try {
            Invoke-CheckedProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        } catch {
            Write-Warning $_
        }
    }
    if ($null -ne $runningProcess) {
        $runningProcess.Dispose()
    }
    if ($null -ne $runKey) {
        $runKey.DeleteValue($unrelatedName, $false)
        $runKey.Dispose()
    }
    if ($null -ne $registryBase) {
        $registryBase.Dispose()
    }
}
