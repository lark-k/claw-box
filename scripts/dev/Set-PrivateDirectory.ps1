#requires -Version 7.4
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))
$directory.Create()
# Update only the DACL. Re-applying a new security descriptor can require SACL privileges.
$acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory, [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $false)
foreach ($existing in $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) { $acl.RemoveAccessRuleAll($existing) }
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent().Name, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
[IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl)
# MSIX parents can redirect new AppData files into their package's private cache.
# Explorer-launched Quantum Harness must see the exact same physical directory.
$probe = Join-Path $directory.FullName ('.physical-path-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllText($probe, '')
    $physical = & (Join-Path $PSScriptRoot 'Get-PhysicalPath.ps1') -Path $probe
    if (-not [string]::Equals($physical, $probe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows redirected this private directory. Run configuration from a standalone PowerShell terminal, outside the packaged application. See docs/development/phase-8.md; existing credentials were not changed.'
    }
} finally { if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe } }
