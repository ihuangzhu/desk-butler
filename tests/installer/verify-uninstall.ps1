param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$fixtureId = [Guid]::NewGuid().ToString('N')
$installDirectory = Join-Path $env:TEMP "DeskButler-Uninstall-$fixtureId"
$dataDirectory = Join-Path $env:LOCALAPPDATA 'DeskButler'
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\DeskButler'
$shortcutDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskButler'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskButler_is1'
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$unrelatedName = "DeskButlerUninstallFixture_$fixtureId"
$unrelatedValue = "unrelated-$fixtureId"
$dataMarker = Join-Path $dataDirectory "installer-fixture-$fixtureId.marker"
$registryBase = $null
$runKey = $null
$installed = $false

# 启动安装或卸载进程，并把任何非零退出码视为失败。
function Invoke-CheckedProcess {
    param([string]$FilePath, [string[]]$Arguments)
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode): $FilePath"
    }
}

# 在唯一临时程序目录安装本轮夹具。
function Install-Fixture {
    Invoke-CheckedProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installDirectory")
    $script:installed = $true
}

try {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    if ((Test-Path -LiteralPath $defaultInstallDirectory) -or
        (Test-Path -LiteralPath $dataDirectory) -or
        (Test-Path -LiteralPath $shortcutDirectory)) {
        throw 'Safety gate: an existing DeskButler installation or user-data directory was detected.'
    }
    $uninstallKey = $registryBase.OpenSubKey($uninstallKeyPath)
    if ($null -ne $uninstallKey) {
        $uninstallKey.Dispose()
        throw 'Safety gate: an existing DeskButler uninstall registration was detected.'
    }

    $runKey = $registryBase.CreateSubKey($runKeyPath, $true)
    $runKey.SetValue($unrelatedName, $unrelatedValue, [Microsoft.Win32.RegistryValueKind]::String)

    Install-Fixture
    New-Item -ItemType Directory -Path $dataDirectory | Out-Null
    Set-Content -LiteralPath $dataMarker -Value 'preserve-delete-fixture' -NoNewline
    Invoke-CheckedProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $installed = $false
    if (-not (Test-Path -LiteralPath $dataMarker)) {
        throw 'Silent uninstall did not preserve user data by default.'
    }

    Install-Fixture
    if (-not (Test-Path -LiteralPath $dataMarker)) {
        throw 'Reinstall could not see preserved user data.'
    }
    Invoke-CheckedProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DELETEUSERDATA=1')
    $installed = $false

    if ((Test-Path -LiteralPath $installDirectory) -or
        (Test-Path -LiteralPath $shortcutDirectory) -or
        (Test-Path -LiteralPath $dataDirectory)) {
        throw 'Program files, shortcut, or DeskButler user data remained after delete-data uninstall.'
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
    if ($null -ne $runKey) {
        $runKey.DeleteValue($unrelatedName, $false)
        $runKey.Dispose()
    }
    if ($null -ne $registryBase) {
        $registryBase.Dispose()
    }
}
