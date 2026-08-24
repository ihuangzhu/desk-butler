param(
    [string]$ExpectedTestAccount = ''
)

$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'DeskButler'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\DeskButler'
$shortcutDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskButler'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskButler_is1'
$sandboxExecutable = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
$blockers = [System.Collections.Generic.List[string]]::new()
$registryBase = $null
$uninstallPresent = $false

try {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    $uninstallKey = $registryBase.OpenSubKey($uninstallKeyPath)
    if ($null -ne $uninstallKey) {
        $uninstallPresent = $true
        $uninstallKey.Dispose()
    }

    if (Test-Path -LiteralPath $dataDirectory) {
        $blockers.Add('Existing real %LOCALAPPDATA%\DeskButler data was detected.')
    }
    if (Test-Path -LiteralPath $installDirectory) {
        $blockers.Add('Existing default DeskButler install directory was detected.')
    }
    if (Test-Path -LiteralPath $shortcutDirectory) {
        $blockers.Add('Existing DeskButler Start Menu directory was detected.')
    }
    if ($uninstallPresent) {
        $blockers.Add('Existing DeskButler uninstall registration was detected.')
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedTestAccount)) {
        $blockers.Add('No dedicated test account was explicitly supplied.')
    } elseif (-not [string]::Equals($ExpectedTestAccount, [Environment]::UserName, [StringComparison]::OrdinalIgnoreCase)) {
        $blockers.Add('The current account is not the explicitly supplied test account.')
    }

    $result = [ordered]@{
        status = if ($blockers.Count -eq 0) { 'READY' } else { 'BLOCK' }
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        userName = [Environment]::UserName
        osVersion = [Environment]::OSVersion.VersionString
        dataDirectoryExists = Test-Path -LiteralPath $dataDirectory
        installDirectoryExists = Test-Path -LiteralPath $installDirectory
        shortcutDirectoryExists = Test-Path -LiteralPath $shortcutDirectory
        uninstallRegistrationExists = $uninstallPresent
        windowsSandboxExecutablePresent = Test-Path -LiteralPath $sandboxExecutable
        windowsSandboxFeatureState = 'NOT_QUERIED_NO_ELEVATION'
        blockers = $blockers.ToArray()
        safety = 'READ_ONLY_NO_ACCOUNT_CREATE_NO_FEATURE_ENABLE_NO_SANDBOX_START_NO_DELETE'
    }
    $result | ConvertTo-Json -Depth 4
    if ($blockers.Count -ne 0) { exit 2 }
} finally {
    if ($null -ne $registryBase) {
        $registryBase.Dispose()
    }
}
