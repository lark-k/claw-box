#requires -Version 7.4
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($reader.PEHeaders.PEHeader -eq $null) { throw 'Not a native Windows apphost.' }
        $entries = @($reader.ReadDebugDirectory() | Where-Object { [int]$_.Type -eq 20 })
        if ($entries.Count -eq 0) { return $false }
        if ($entries.Count -ne 1 -or $entries[0].DataSize -lt 4) { throw 'Invalid extended DLL characteristics.' }
        $stream.Position = $entries[0].DataPointer
        $flags = [byte[]]::new(4)
        $stream.ReadExactly($flags, 0, 4)
        return ([BitConverter]::ToUInt32($flags, 0) -band 1) -ne 0
    } finally { $reader.Dispose() }
} finally { $stream.Dispose() }
