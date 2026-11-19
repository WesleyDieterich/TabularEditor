[CmdletBinding()]
param([Parameter(Mandatory)] [string] $MsiPath)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($path, 1))

function Invoke-MsiQuery([string] $Query, $Parameters = $null, [switch] $ReadValue) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    try {
        $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @($Parameters))
        if ($ReadValue) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($record) {
                try { $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1)) }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        }
    }
    finally {
        $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

try {
    $current = Invoke-MsiQuery 'SELECT `Value` FROM `Property` WHERE `Property` = ''SecureCustomProperties''' -ReadValue
    if (($current -split ';') -notcontains 'TARGETDIR') {
        $value = (@($current, 'TARGETDIR') | Where-Object { $_ }) -join ';'
        $parameters = $installer.GetType().InvokeMember('CreateRecord', 'InvokeMethod', $null, $installer, @(1))
        try {
            $parameters.GetType().InvokeMember('StringData', 'SetProperty', $null, $parameters, @(1, $value))
            if ($null -ne $current) {
                Invoke-MsiQuery 'UPDATE `Property` SET `Value` = ? WHERE `Property` = ''SecureCustomProperties''' $parameters
            }
            else {
                Invoke-MsiQuery 'INSERT INTO `Property` (`Property`, `Value`) VALUES (''SecureCustomProperties'', ?)' $parameters
            }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($parameters) }
    }

    # Run the default assignment in either sequence if TARGETDIR is still empty.
    # Type 51 drops the FirstSequence flag emitted by the Visual Studio builder.
    $actionType = Invoke-MsiQuery 'SELECT `Type` FROM `CustomAction` WHERE `Action` = ''DIRCA_TARGETDIR''' -ReadValue
    if ($actionType -notin '307', '51') { throw "Unexpected DIRCA_TARGETDIR action type: $actionType" }
    Invoke-MsiQuery 'UPDATE `CustomAction` SET `Type` = 51 WHERE `Action` = ''DIRCA_TARGETDIR'''
    $null = $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null)
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
