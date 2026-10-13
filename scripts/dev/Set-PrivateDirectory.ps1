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
