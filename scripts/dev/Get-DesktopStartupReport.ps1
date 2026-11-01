#requires -Version 5.1
[CmdletBinding()]
param([string]$ExePath, [string]$OutputDirectory, [switch]$Launch)
$ErrorActionPreference = 'Stop'
# Uses Windows' built-in PowerShell; does not install, uninstall, delete or repair anything.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('QuantumHarness-startup-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QuantumHarness_is1'
$registration = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
$windowsRelease = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
$report = [ordered]@{
    collectedAt = [DateTimeOffset]::Now.ToString('o')
    os = [Environment]::OSVersion.VersionString
    os64Bit = [Environment]::Is64BitOperatingSystem
    processArchitecture = $env:PROCESSOR_ARCHITECTURE
    windowsRelease = @{ displayVersion=$windowsRelease.DisplayVersion; build=$windowsRelease.CurrentBuild; revision=$windowsRelease.UBR }
    installation = $null
    shortcut = $null
    executable = $null
    package = $null
    launch = $null
    recentApplicationErrors = @()
}
if ($registration) {
    $report.installation = @{ directory=$registration.InstallLocation; version=$registration.DisplayVersion }
    if (-not $ExePath) { $ExePath = Join-Path $registration.InstallLocation ('versions\' + $registration.DisplayVersion + '\QuantumHarness.exe') }
}
$shortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Quantum Harness.lnk'
if (Test-Path -LiteralPath $shortcut) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcut)
        try { $report.shortcut = @{ target=$link.TargetPath; workingDirectory=$link.WorkingDirectory; targetExists=(Test-Path -LiteralPath $link.TargetPath -PathType Leaf) } }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    if (-not $ExePath) { $ExePath = $report.shortcut.target }
}
if ($ExePath) {
    $ExePath = [IO.Path]::GetFullPath($ExePath)
    $report.executable = @{ path=$ExePath; exists=(Test-Path -LiteralPath $ExePath -PathType Leaf) }
    $manifestPath = Join-Path (Split-Path $ExePath -Parent) 'desktop-manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $packageRoot = [IO.Path]::GetFullPath((Split-Path $manifestPath -Parent))
            $problems = @()
            foreach ($entry in $manifest.files) {
                $file = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
                if (-not $file.StartsWith($packageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package manifest path.' }
                if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $problems += @{ file=$entry.path; status='missing' } }
                elseif ((Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { $problems += @{ file=$entry.path; status='checksum-mismatch' } }
            }
            $report.package = @{ version=$manifest.version; runtime=$manifest.runtime; checkedFiles=@($manifest.files).Count; problems=$problems }
        } catch { $report.package = @{ error=$_.Exception.Message } }
    } else { $report.package = @{ error='desktop-manifest.json missing' } }
}
if ($Launch) {
    if (-not $ExePath -or -not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw 'Cannot locate QuantumHarness.exe. Supply -ExePath with the installed EXE path.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $ExePath
    $info.WorkingDirectory = Split-Path $ExePath -Parent
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Child process only: no persistent environment changes.
    $info.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
    $info.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = Join-Path $OutputDirectory 'host-trace.txt'
    $info.EnvironmentVariables['COREHOST_TRACE'] = '1'
    $info.EnvironmentVariables['COREHOST_TRACEFILE'] = Join-Path $OutputDirectory 'host-trace.txt'
    $started = [DateTime]::Now
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($process.WaitForExit(12000)) {
            $report.launch = @{ exited=$true; exitCode=$process.ExitCode; exitCodeHex=('0x{0:X8}' -f $process.ExitCode) }
            $stdout.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.txt') -Encoding UTF8
            $stderr.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.txt') -Encoding UTF8
        } else { $report.launch = @{ exited=$false; processId=$process.Id; note='Still running after 12 seconds; not terminated by diagnostics.' } }
    } catch { $report.launch = @{ error=$_.Exception.Message } }
    finally { $process.Dispose() }
}
try {
    # Only records naming this product; no broad event-log export or credential reads.
    $report.recentApplicationErrors = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=([DateTime]::Now.AddMinutes(-15)); Id=@(1000,1001,1023,1026) } -ErrorAction Stop |
        Where-Object { $_.Message -match 'QuantumHarness|ClawBox.Desktop' } | Select-Object -First 8 TimeCreated,Id,ProviderName,Message)
} catch { $report.recentApplicationErrors = @() }
$reportPath = Join-Path $OutputDirectory 'startup-report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Output "Startup report: $reportPath"
Write-Output 'Report includes local installation paths and product-related crash messages. Review before sharing.'
Write-Output 'No credentials or chats were collected. No installation, uninstall or automatic repair was performed.'
