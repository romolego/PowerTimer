param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$env:PYTHONIOENCODING = 'utf-8'
$src = $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA 'PowerTimer-Test'
$apiPath = Join-Path $data 'api.json'
if (Test-Path -LiteralPath $apiPath) {
    $old = Get-Content -LiteralPath $apiPath -Raw | ConvertFrom-Json
    if (Get-Process -Id $old.pid -ErrorAction SilentlyContinue) { throw 'A test instance is already running. Close it before running the suite.' }
}
if (-not $SkipBuild) { & (Join-Path $src 'build.ps1') }
$exe = Join-Path $src 'bin\PowerTimer.exe'
$ctl = Join-Path $src 'bin\timerctl.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--test','--tray' -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $apiPath) {
            $info = Get-Content -LiteralPath $apiPath -Raw | ConvertFrom-Json
            if ($info.testMode -and $info.pid -eq $process.Id) { $ready = $true; break }
        }
        if ($process.HasExited) { throw 'Test app exited during startup' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Test app did not start' }
    foreach ($test in @('integration_tests.py', 'ui_flow_tests.py', 'settings_integration_tests.py', 'instance_tests.py', 'stress_tests.py', 'lifecycle_tests.py')) {
        & python (Join-Path $src $test)
        if ($LASTEXITCODE -ne 0) { throw ('Test failed: ' + $test) }
    }
    Write-Output 'PASS: complete test suite. No real power actions were invoked.'
} finally {
    if (Test-Path -LiteralPath $apiPath) {
        $info = Get-Content -LiteralPath $apiPath -Raw | ConvertFrom-Json
        if ($info.testMode -and $info.executable -eq $exe) {
            & $ctl --test exit | Out-Null
            $remaining = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
            if ($remaining -and -not $remaining.WaitForExit(5000)) {
                if ($remaining.Path -eq $exe) { $remaining.Kill() }
            }
        }
    }
}
