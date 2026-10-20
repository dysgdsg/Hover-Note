$ErrorActionPreference = 'Stop'

$clsid = '{B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60}'
$iid   = '{00021500-0000-0000-C000-000000000046}'

foreach ($scope in @('*', 'Directory', 'Drive')) {
    $k = "HKCU:\Software\Classes\$scope\shellex\$iid"
    if (Test-Path $k) { Remove-Item -Path $k -Force -Recurse }
}

$base = "HKCU:\Software\Classes\CLSID\$clsid"
if (Test-Path $base) { Remove-Item -Path $base -Force -Recurse }

if (Test-Path 'HKCU:\Software\HoverNote') { Remove-Item -Path 'HKCU:\Software\HoverNote' -Force -Recurse }

Write-Output '已取消注册（DLL 文件未删除，由用户自行处理）'
