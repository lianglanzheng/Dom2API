param([Parameter(Mandatory = $true)][string]$PlanPath, [switch]$Broker, [switch]$Elevate)

$ErrorActionPreference = 'Stop'
$pathsValidated = $false
$sourceExited = $false
$replaced = $false
$cleanup = $false
$sourceProcess = $null
$newProcess = $null
$installDirectory = $null
$launchNumber = 0

# 此进程维持原权限，只有安装进程经 UAC 提权；重启仍使用原来的用户和权限。
if ($Broker) {
    $brokerDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($PlanPath))
    $worker = $null
    try {
        $brokerPlan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $brokerTarget = [System.IO.Path]::GetFullPath([string]$brokerPlan.ExecutablePath)
        $workerStart = New-Object System.Diagnostics.ProcessStartInfo
        $workerStart.FileName = Join-Path $PSHOME 'powershell.exe'
        $invoke = "& '" + $PSCommandPath.Replace("'", "''") + "' -PlanPath '" + $PlanPath.Replace("'", "''") + "'"
        $workerStart.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($invoke))
        $workerStart.WorkingDirectory = [System.IO.Path]::GetDirectoryName($brokerTarget)
        $workerStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $workerStart.UseShellExecute = [bool]$Elevate
        $workerStart.CreateNoWindow = -not $Elevate
        if ($Elevate) { $workerStart.Verb = 'runas' }
        $worker = [System.Diagnostics.Process]::Start($workerStart)
        $lastLaunch = ''
        while (-not $worker.HasExited) {
            $launchPath = Join-Path $brokerDirectory 'launch'
            if ([System.IO.File]::Exists($launchPath)) {
                $requestId = [System.IO.File]::ReadAllText($launchPath)
                if ($requestId -match '^\d+$' -and $requestId -ne $lastLaunch) {
                    $lastLaunch = $requestId
                    $success = $false
                    try {
                        $restart = New-Object System.Diagnostics.ProcessStartInfo
                        $restart.FileName = $brokerTarget
                        $restart.WorkingDirectory = [System.IO.Path]::GetDirectoryName($brokerTarget)
                        $restart.UseShellExecute = $true
                        $restartedProcess = [System.Diagnostics.Process]::Start($restart)
                        try { $success = $null -ne $restartedProcess -and -not $restartedProcess.WaitForExit(3000) }
                        finally { if ($null -ne $restartedProcess) { $restartedProcess.Dispose() } }
                    } catch { }
                    [System.IO.File]::WriteAllText((Join-Path $brokerDirectory 'launch-result'), ($requestId + ':' + $success.ToString()))
                }
            }
            Start-Sleep -Milliseconds 100
        }
    } catch {
        $errorMessage = $_.Exception.Message
        $cause = $_.Exception
        while ($null -ne $cause) {
            if ($cause -is [System.ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) {
                $errorMessage = '已取消管理员授权，当前程序未替换。'
                break
            }
            $cause = $cause.InnerException
        }
        [System.IO.File]::WriteAllText((Join-Path $brokerDirectory 'error.txt'), $errorMessage, [Text.Encoding]::UTF8)
    } finally {
        if ($null -ne $worker) { $worker.Dispose() }
        if ([System.IO.File]::Exists((Join-Path $brokerDirectory 'complete'))) {
            Set-Location -LiteralPath ([System.IO.Path]::GetDirectoryName($brokerDirectory))
            [System.Environment]::CurrentDirectory = [System.IO.Path]::GetDirectoryName($brokerDirectory)
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                foreach ($name in @('update.exe', 'plan.json', 'apply.ps1', 'ready', 'apply', 'cancel', 'error.txt', 'Dom2API.settings.json', 'launch', 'launch-result', 'complete')) {
                    try { [System.IO.File]::Delete((Join-Path $brokerDirectory $name)) } catch { }
                }
                try { [System.IO.Directory]::Delete($brokerDirectory, $false); break } catch { Start-Sleep -Milliseconds 250 }
            }
        }
    }
    exit
}

function Start-UpdatedApplication([string]$ExecutablePath) {
    if ($plan.UseBroker) {
        $script:launchNumber++
        $requestId = $script:launchNumber.ToString()
        [System.IO.File]::WriteAllText((Join-Path $updateDirectory 'launch'), $requestId)
        $restartDeadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $restartDeadline) {
            $resultPath = Join-Path $updateDirectory 'launch-result'
            if ([System.IO.File]::Exists($resultPath)) {
                $result = [System.IO.File]::ReadAllText($resultPath)
                if ($result -eq ($requestId + ':True')) { return $null }
                if ($result -eq ($requestId + ':False')) { throw '程序启动失败。' }
            }
            Start-Sleep -Milliseconds 100
        }
        throw '等待原权限进程重启程序超时。'
    }
    $launch = New-Object System.Diagnostics.ProcessStartInfo
    $launch.FileName = $ExecutablePath
    $launch.WorkingDirectory = [System.IO.Path]::GetDirectoryName($ExecutablePath)
    $launch.UseShellExecute = $true
    return [System.Diagnostics.Process]::Start($launch)
}

function Get-Sha256([string]$FilePath) {
    $stream = [System.IO.File]::OpenRead($FilePath)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

try {
    $planFullPath = [System.IO.Path]::GetFullPath($PlanPath)
    $updateDirectory = [System.IO.Path]::GetDirectoryName($planFullPath)
    if ([System.IO.Path]::GetFileName($updateDirectory) -notmatch '^\.Dom2API-update-[0-9a-f]{32}$' -or
        [System.IO.Path]::GetFileName($planFullPath) -ne 'plan.json' -or
        -not [string]::Equals($PSScriptRoot, $updateDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '更新目录无效。'
    }
    $plan = Get-Content -LiteralPath $planFullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $targetPath = [System.IO.Path]::GetFullPath([string]$plan.ExecutablePath)
    $applicationDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
    if ([System.IO.Path]::GetExtension($targetPath) -ne '.exe' -or -not [System.IO.File]::Exists($targetPath)) {
        throw '更新目标必须是现有程序。'
    }
    # 所有移动和删除路径均限定为已验证目录内的固定文件名，不递归删除目录。
    $payloadPath = Join-Path $updateDirectory 'update.exe'
    $backupPath = Join-Path $updateDirectory 'previous.exe'
    $failedPath = Join-Path $updateDirectory 'failed.exe'
    $readyPath = Join-Path $updateDirectory 'ready'
    $applyPath = Join-Path $updateDirectory 'apply'
    $cancelPath = Join-Path $updateDirectory 'cancel'
    $errorPath = Join-Path $updateDirectory 'error.txt'
    $pathsValidated = $true
    $sourceProcess = [System.Diagnostics.Process]::GetProcessById([int]$plan.ProcessId)
    if ($sourceProcess.StartTime.ToUniversalTime().Ticks -ne [long]$plan.ProcessStartTimeUtcTicks -or
        -not [string]::Equals($sourceProcess.MainModule.FileName, $targetPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '待退出的进程与更新目标不匹配。'
    }
    if ($plan.UseBroker) {
        # 提权后才在程序目录准备同卷文件，支持临时目录与程序位于不同磁盘。
        $installDirectory = Join-Path $applicationDirectory ('.Dom2API-update-' + [Guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::CreateDirectory($installDirectory) | Out-Null
        $installedPayload = Join-Path $installDirectory 'update.exe'
        [System.IO.File]::Copy($payloadPath, $installedPayload, $false)
        $payloadPath = $installedPayload
        $backupPath = Join-Path $installDirectory 'previous.exe'
        $failedPath = Join-Path $installDirectory 'failed.exe'
        $settingsSource = Join-Path $updateDirectory 'Dom2API.settings.json'
        if ([System.IO.File]::Exists($settingsSource)) {
            [System.IO.File]::Copy($settingsSource, (Join-Path $installDirectory 'Dom2API.settings.json'), $false)
        }
        if (([System.IO.FileInfo]$payloadPath).Length -ne [long]$plan.Size -or (Get-Sha256 $payloadPath) -ne [string]$plan.Sha256) {
            throw '安装准备校验失败，当前程序未替换。'
        }
    }
    [System.IO.File]::WriteAllText($readyPath, 'ready')
    $commitDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not [System.IO.File]::Exists($applyPath)) {
        if ([System.IO.File]::Exists($cancelPath) -or $sourceProcess.HasExited) { $cleanup = $true; return }
        if ([DateTime]::UtcNow -gt $commitDeadline) { throw '等待更新确认超时，当前程序未替换。' }
        Start-Sleep -Milliseconds 100
    }
    if ([System.IO.File]::Exists($cancelPath)) { $cleanup = $true; return }
    if (-not $sourceProcess.WaitForExit(120000)) { throw '等待程序退出超时，当前程序未替换。' }
    $sourceExited = $true
    if (([System.IO.FileInfo]$payloadPath).Length -ne [long]$plan.Size -or
        (Get-Sha256 $payloadPath) -ne [string]$plan.Sha256) {
        throw '安装前校验失败，已保留原程序。'
    }
    if ((Get-Sha256 $targetPath) -ne [string]$plan.OriginalSha256) {
        throw '下载期间当前程序发生变化，已取消替换。'
    }
    if ($plan.UseBroker -and [System.IO.File]::Exists((Join-Path $installDirectory 'Dom2API.settings.json'))) {
        $savedSettings = Join-Path $applicationDirectory 'Dom2API.settings.json'
        $stagedSettings = Join-Path $installDirectory 'Dom2API.settings.json'
        if ([System.IO.File]::Exists($savedSettings)) { [System.IO.File]::Replace($stagedSettings, $savedSettings, $null) }
        else { [System.IO.File]::Move($stagedSettings, $savedSettings) }
    }
    # 同卷原子替换并创建备份；短暂的杀毒软件文件锁会自动重试。
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            [System.IO.File]::Replace($payloadPath, $targetPath, $backupPath)
            $replaced = $true
            break
        } catch {
            if ($attempt -eq 39) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    $newProcess = Start-UpdatedApplication $targetPath
    if (-not $plan.UseBroker -and ($null -eq $newProcess -or $newProcess.WaitForExit(3000))) { throw '新版程序启动失败，正在恢复原程序。' }
    $cleanup = $true
} catch {
    $failure = $_.Exception.Message
    if ($pathsValidated) {
        $restored = -not $replaced
        if ($replaced) {
            try {
                [System.IO.File]::Replace($backupPath, $targetPath, $failedPath)
                $restored = $true
            } catch { $failure += "`r`n恢复原程序失败：$($_.Exception.Message)`r`n备份位置：$backupPath" }
        }
        [System.IO.File]::WriteAllText($errorPath, $failure, [System.Text.Encoding]::UTF8)
        if (-not $sourceExited -and $null -ne $installDirectory) {
            foreach ($name in @('update.exe', 'Dom2API.settings.json')) {
                try { [System.IO.File]::Delete((Join-Path $installDirectory $name)) } catch { }
            }
            try { [System.IO.Directory]::Delete($installDirectory, $false) } catch { }
        }
        if ($sourceExited) {
            if ($restored) {
                try {
                    $restoredProcess = Start-UpdatedApplication $targetPath
                    if ($null -ne $restoredProcess) { $restoredProcess.Dispose() }
                    $cleanup = $true
                } catch { $failure += "`r`n请手动启动原程序：$targetPath" }
            }
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show($failure, 'Dom2API 更新失败', 'OK', 'Error') | Out-Null
        }
    }
    exit 1
} finally {
    if ($null -ne $sourceProcess) { $sourceProcess.Dispose() }
    if ($null -ne $newProcess) { $newProcess.Dispose() }
    if ($cleanup -and $pathsValidated) {
        # 同时切换 PowerShell 位置和进程工作目录，兼容由旧启动器传入的更新目录。
        Set-Location -LiteralPath $applicationDirectory
        [System.Environment]::CurrentDirectory = $applicationDirectory
        if ($plan.UseBroker) {
            if ($null -ne $installDirectory) {
                for ($cleanupAttempt = 0; $cleanupAttempt -lt 20; $cleanupAttempt++) {
                    foreach ($name in @('update.exe', 'previous.exe', 'failed.exe', 'Dom2API.settings.json')) {
                        try { [System.IO.File]::Delete((Join-Path $installDirectory $name)) } catch { }
                    }
                    try { [System.IO.Directory]::Delete($installDirectory, $false); break } catch { Start-Sleep -Milliseconds 250 }
                }
            }
            [System.IO.File]::WriteAllText((Join-Path $updateDirectory 'complete'), 'complete')
        } else {
            for ($cleanupAttempt = 0; $cleanupAttempt -lt 20; $cleanupAttempt++) {
                foreach ($name in @('update.exe', 'previous.exe', 'failed.exe', 'ready', 'apply', 'cancel', 'error.txt', 'plan.json', 'apply.ps1')) {
                    try { [System.IO.File]::Delete((Join-Path $updateDirectory $name)) } catch { }
                }
                try { [System.IO.Directory]::Delete($updateDirectory, $false); break }
                catch { Start-Sleep -Milliseconds 250 }
            }
        }
    }
}
