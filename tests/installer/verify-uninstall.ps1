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
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    Invoke-CheckedProcess $uninstaller $Arguments
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

# 首先验证无真实安装和数据，再创建唯一的无关 Run 夹具值。
function Initialize-UninstallFixture {
    if ((Test-Path -LiteralPath $defaultInstallDirectory) -or
        (Test-Path -LiteralPath $dataRoot) -or
        (Test-Path -LiteralPath $shortcutDirectory)) {
        throw 'Safety gate: an existing DeskButler installation or user-data directory was detected.'
    }

    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $uninstallKey = $registryBase.OpenSubKey($uninstallKeyPath)
        if ($null -ne $uninstallKey) {
            $uninstallKey.Dispose()
            throw 'Safety gate: an existing DeskButler uninstall registration was detected.'
        }

        $runKey = $registryBase.CreateSubKey($runKeyPath, $true)
        try {
            $runKey.SetValue($unrelatedName, $unrelatedValue, [Microsoft.Win32.RegistryValueKind]::String)
        } finally {
            $runKey.Dispose()
        }
    } finally {
        $registryBase.Dispose()
    }
}

# 失败清理只卸载本轮临时程序并删除本轮唯一 Run 值，绝不删除用户数据根。
function Restore-UninstallFixtureEnvironment {
    if ($script:installed -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try {
            Uninstall-Fixture -Arguments $defaultUninstallArguments
        } catch {
            Write-Warning $_
        }
    }

    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $runKey = $registryBase.OpenSubKey($runKeyPath, $true)
        if ($null -ne $runKey) {
            try {
                $runKey.DeleteValue($unrelatedName, $false)
            } finally {
                $runKey.Dispose()
            }
        }
    } finally {
        $registryBase.Dispose()
    }
}

function Assert-UninstallRegistryContract {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $runKey = $registryBase.OpenSubKey($runKeyPath)
        if ($null -eq $runKey) {
            throw 'HKCU Run key was unavailable after uninstall.'
        }
        try {
            if ($runKey.GetValue('DeskButler')) {
                throw 'DeskButler HKCU Run value remained after uninstall.'
            }
            if ($runKey.GetValue($unrelatedName) -ne $unrelatedValue) {
                throw 'Unrelated HKCU Run value was removed during uninstall.'
            }
        } finally {
            $runKey.Dispose()
        }
    } finally {
        $registryBase.Dispose()
    }
}

function Verify-DefaultUninstallPreservesUserData {
    try {
        Install-Fixture
        New-Item -ItemType Directory -Path $dataRoot | Out-Null
        Set-Content -LiteralPath $dataMarker -Value 'preserve-delete-fixture' -NoNewline
        Set-Content -LiteralPath $residentSettingsMarker -Value "resident-settings-$fixtureId" -NoNewline
        Set-Content -LiteralPath $residentLaunchSessionMarker -Value "resident-session-$fixtureId" -NoNewline
        Uninstall-Fixture -Arguments $defaultUninstallArguments
        Assert-DefaultUserDataPreserved
    } catch {
        Restore-UninstallFixtureEnvironment
        throw
    }
}

function Verify-DeleteDataUninstallRemovesUserData {
    try {
        Install-Fixture
        Assert-DefaultUserDataPreserved
        Uninstall-Fixture -Arguments $deleteDataUninstallArguments
        Assert-DeletedUserDataRoot
        if ((Test-Path -LiteralPath $installDirectory) -or
            (Test-Path -LiteralPath $shortcutDirectory)) {
            throw 'Program files or shortcut remained after delete-data uninstall.'
        }
        Assert-UninstallRegistryContract
    } finally {
        Restore-UninstallFixtureEnvironment
    }
}

# 固定可达直线控制流：先验证默认保留，再验证显式删除精确数据根。
function Invoke-UninstallContractFixture {
    Verify-DefaultUninstallPreservesUserData
    Verify-DeleteDataUninstallRemovesUserData
}

# CANONICAL TOP-LEVEL EXECUTION
Initialize-UninstallFixture
Invoke-UninstallContractFixture
