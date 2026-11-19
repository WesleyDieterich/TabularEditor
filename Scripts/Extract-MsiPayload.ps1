[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [Parameter(Mandatory)] [string] $Destination
)

$ErrorActionPreference = 'Stop'
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$root = [IO.Path]::GetFullPath($Destination)
if (!(Test-Path -LiteralPath $root)) { $null = New-Item -ItemType Directory -Path $root }
if (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1) {
    throw "The MSI extraction directory must be empty: $root"
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))

function Open-MsiView([string] $Query) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $view
}

function Get-MsiRows([string] $Query, [int] $ColumnCount) {
    $view = Open-MsiView $Query
    while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
        $fields = [string[]]@(for ($i = 1; $i -le $ColumnCount; $i++) {
            $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @([int]$i))
        })
        Write-Output -NoEnumerate $fields
    }
}

$directories = @{}
foreach ($row in Get-MsiRows 'SELECT Directory, Directory_Parent, DefaultDir FROM Directory' 3) {
    $directories[$row[0]] = $row
}
$components = @{}
foreach ($row in Get-MsiRows 'SELECT Component, Directory_ FROM Component' 2) {
    $components[$row[0]] = $row[1]
}

function Resolve-MsiDirectory([string] $Id) {
    if ($Id -eq 'TARGETDIR') { return $root }
    if (!$directories.ContainsKey($Id)) { throw "Unknown MSI directory: $Id" }
    $row = $directories[$Id]
    $parent = Resolve-MsiDirectory $row[1]
    $name = ($row[2].Split(':')[0]).Split('|')[-1]
    if ($name -eq '.') { return $parent }
    Join-Path $parent $name
}

$scratch = Join-Path $root ('_cabinet_' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
try {
    foreach ($entry in Get-MsiRows 'SELECT Cabinet FROM Media' 1) {
        $name = $entry[0]
        if (!$name.StartsWith('#')) { throw "Expected an embedded MSI cabinet: $name" }
        $name = $name.Substring(1).Replace("'", "''")
        $view = Open-MsiView "SELECT Data FROM _Streams WHERE Name = '$name'"
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (!$record) { throw "Embedded MSI cabinet was not found: $name" }

        $cabinet = Join-Path $scratch 'payload.cab'
        $encoding = [Text.Encoding]::GetEncoding(28591)
        $stream = [IO.File]::Create($cabinet)
        try {
            while ($chunk = $record.GetType().InvokeMember('ReadStream', 'InvokeMethod', $null, $record, @(1, 65536, 1))) {
                $bytes = $encoding.GetBytes([string]$chunk)
                $stream.Write($bytes, 0, $bytes.Length)
            }
        }
        finally { $stream.Dispose() }

        & expand.exe '-F:*' $cabinet $scratch | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to expand embedded MSI cabinet: $name" }
    }

    $rootPrefix = $root.TrimEnd('\') + '\'
    foreach ($row in Get-MsiRows 'SELECT File, Component_, FileName FROM File' 3) {
        $name = $row[2].Split('|')[-1]
        $dir = Resolve-MsiDirectory $components[$row[1]]
        $target = [IO.Path]::GetFullPath((Join-Path $dir $name))
        if (!$target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MSI file would leave the extraction directory: $target"
        }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath (Join-Path $scratch $row[0]) -Destination $target
    }
}
finally {
    if ((Resolve-Path -LiteralPath $scratch).Path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
