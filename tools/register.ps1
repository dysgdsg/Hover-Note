$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$dll   = Join-Path $root 'bin\HoverNote.dll'
$clsid = '{B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60}'
$iid   = '{00021500-0000-0000-C000-000000000046}'

if (-not (Test-Path $dll)) { throw "DLL 不存在: $dll" }

$codeBase = 'file:///' + ($dll -replace '\\', '/')

$base = "HKCU:\Software\Classes\CLSID\$clsid"
New-Item -Path $base -Force | Out-Null
New-Item -Path "$base\InprocServer32" -Force | Out-Null

Set-ItemProperty -Path "$base\InprocServer32" -Name '(default)'      -Value 'mscoree.dll'
Set-ItemProperty -Path "$base\InprocServer32" -Name 'ThreadingModel' -Value 'Both'
Set-ItemProperty -Path "$base\InprocServer32" -Name 'Assembly'       -Value 'HoverNote, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
Set-ItemProperty -Path "$base\InprocServer32" -Name 'Class'          -Value 'HoverNote.InfoTipHandler'
Set-ItemProperty -Path "$base\InprocServer32" -Name 'RuntimeVersion' -Value 'v4.0.30319'
Set-ItemProperty -Path "$base\InprocServer32" -Name 'CodeBase'       -Value $codeBase

foreach ($scope in @('*', 'Directory', 'Drive')) {
    $k = "HKCU:\Software\Classes\$scope\shellex\$iid"
    New-Item -Path $k -Force | Out-Null
    Set-ItemProperty -Path $k -Name '(default)' -Value $clsid
}

New-Item -Path 'HKCU:\Software\HoverNote' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\HoverNote' -Name 'InstallPath' -Value $root

Write-Output "已注册: $clsid"
Write-Output "DLL: $dll"
Get-ItemProperty -Path "$base\InprocServer32" | Select-Object Assembly, Class, CodeBase | Format-List
