param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$fixtureId = [Guid]::NewGuid().ToString('N')
$installDirectory = Join-Path $env:TEMP "DeskButler-Uninstall-$fixtureId"
$dataRoot = Join-Path $env:LOCALAPPDATA 'DeskButler'
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\DeskButler'
$shortcutDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskButler'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskButler_is1'
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$unrelatedName = "DeskButlerUninstallFixture_$fixtureId"
$unrelatedValue = "unrelated-$fixtureId"
$dataMarker = Join-Path $dataRoot "installer-fixture-$fixtureId.marker"
$residentSettingsMarker = Join-Path $dataRoot 'settings.json'
$residentLaunchSessionMarker = Join-Path $dataRoot 'resident-launch-session.json'
$defaultUninstallArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
$deleteDataUninstallArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DELETEUSERDATA=1')
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

# 使用调用方明确选择的参数卸载夹具；绝不发现或终止第三方进程。
function Uninstall-Fixture {
    param([string[]]$Arguments)
    Invoke-CheckedProcess (Join-Path $installDirectory 'unins000.exe') $Arguments
    $script:installed = $false
}

# 默认卸载后，精确数据根及常驻设置、会话证据必须全部保留。
function Assert-DefaultUserDataPreserved {
    if (-not (Test-Path -LiteralPath $dataRoot)) {
        throw 'Default uninstall did not preserve the DeskButler user-data root.'
    }
    if (-not (Test-Path -LiteralPath $dataMarker)) {
        throw 'Silent uninstall did not preserve user data by default.'
    }
    if (-not (Test-Path -LiteralPath $residentSettingsMarker)) {
        throw 'Default uninstall did not preserve resident settings.'
    }
    if (-not (Test-Path -LiteralPath $residentLaunchSessionMarker)) {
        throw 'Default uninstall did not preserve the resident launch session.'
    }
}

# 显式删除数据后，只接受精确 DeskButler 数据根完全不存在。
function Assert-DeletedUserDataRoot {
    if (Test-Path -LiteralPath $dataRoot) {
        throw 'Delete-data uninstall left the DeskButler user-data root behind.'
    }
}

function Verify-DefaultUninstallPreservesUserData {
    Uninstall-Fixture -Arguments $defaultUninstallArguments
    Assert-DefaultUserDataPreserved
}

function Verify-DeleteDataUninstallRemovesUserData {
    Uninstall-Fixture -Arguments $deleteDataUninstallArguments
    Assert-DeletedUserDataRoot
}

# 固定场景顺序：先验证默认保留，再重新安装并验证显式删除精确数据根。
function Invoke-UninstallContractFixture {
    Install-Fixture
    New-Item -ItemType Directory -Path $dataRoot | Out-Null
    Set-Content -LiteralPath $dataMarker -Value 'preserve-delete-fixture' -NoNewline
    Set-Content -LiteralPath $residentSettingsMarker -Value "resident-settings-$fixtureId" -NoNewline
    Set-Content -LiteralPath $residentLaunchSessionMarker -Value "resident-session-$fixtureId" -NoNewline
    Verify-DefaultUninstallPreservesUserData

    Install-Fixture
    Assert-DefaultUserDataPreserved
    Verify-DeleteDataUninstallRemovesUserData

    if ((Test-Path -LiteralPath $installDirectory) -or
        (Test-Path -LiteralPath $shortcutDirectory)) {
        throw 'Program files or shortcut remained after delete-data uninstall.'
    }
}

try {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    if ((Test-Path -LiteralPath $defaultInstallDirectory) -or
        (Test-Path -LiteralPath $dataRoot) -or
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

    Invoke-UninstallContractFixture
    if ($runKey.GetValue('DeskButler')) {
        throw 'DeskButler HKCU Run value remained after uninstall.'
    }
    if ($runKey.GetValue($unrelatedName) -ne $unrelatedValue) {
        throw 'Unrelated HKCU Run value was removed during uninstall.'
    }
} finally {
    if ($installed -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try {
            Uninstall-Fixture -Arguments $defaultUninstallArguments
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
