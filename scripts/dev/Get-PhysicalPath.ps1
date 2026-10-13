#requires -Version 7.4
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
if (-not ('ClawBoxPathProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ClawBoxPathProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder b, uint n, uint flags);
}
'@
}
$handle = [IO.File]::OpenHandle([IO.Path]::GetFullPath($Path))
try {
    $buffer = [Text.StringBuilder]::new(32768)
    $length = [ClawBoxPathProbe]::GetFinalPathNameByHandle($handle, $buffer, $buffer.Capacity, 0)
    if ($length -eq 0 -or $length -ge $buffer.Capacity) { throw 'Cannot determine the physical configuration path.' }
    $physical = $buffer.ToString()
    if ($physical.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { return '\\' + $physical.Substring(8) }
    if ($physical.StartsWith('\\?\', [StringComparison]::Ordinal)) { return $physical.Substring(4) }
    return $physical
} finally { $handle.Dispose() }
