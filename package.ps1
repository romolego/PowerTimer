$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path $src 'dist'
$portable = Join-Path $dist 'PowerTimer'
New-Item -ItemType Directory -Path $portable -Force | Out-Null
foreach ($name in @('PowerTimer.exe', 'timerctl.exe')) { Copy-Item -LiteralPath (Join-Path $src ('bin\' + $name)) -Destination $portable -Force }
foreach ($name in @('README_RU.txt', 'API.txt', 'LICENSE.txt')) { Copy-Item -LiteralPath (Join-Path $src $name) -Destination $portable -Force }
Compress-Archive -LiteralPath $portable -DestinationPath (Join-Path $dist 'PowerTimer-portable.zip') -Force
$archive = Join-Path $dist 'PowerTimer-portable.zip'
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), $hash + '  PowerTimer-portable.zip' + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Output $archive
