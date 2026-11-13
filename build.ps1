param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$target = Join-Path $src 'bin'
New-Item -ItemType Directory -Force -Path $target | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x is required.' }
$wpf = Join-Path $framework 'WPF'
$shared = @('/nologo', '/optimize+', '/warn:4', '/warnaserror+', '/codepage:65001', '/r:System.dll', '/r:System.Core.dll', ('/r:' + (Join-Path $framework 'System.Web.Extensions.dll')))
$ui = @('/target:winexe', '/platform:anycpu', '/main:PowerTimer.Program',
    ('/out:' + (Join-Path $target 'PowerTimer.exe')),
    ('/win32manifest:' + (Join-Path $src 'app.manifest')),
    ('/win32icon:' + (Join-Path $src 'icon.ico')),
    '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Xaml.dll')
foreach ($assembly in @('PresentationFramework', 'PresentationCore', 'WindowsBase')) { $ui += '/r:' + (Join-Path $wpf ($assembly + '.dll')) }
foreach ($resource in @('Main.xaml', 'Widget.xaml', 'Custom.xaml', 'Settings.xaml', 'Styles.xaml', 'icon.ico')) { $ui += '/resource:' + (Join-Path $src $resource) + ',PowerTimer.' + $resource }
$sources = @('Core.cs', 'Api.cs', 'App.cs', 'Program.cs', 'Storage.cs', 'Client.cs', 'DurationInput.cs', 'ScheduleTime.cs', 'Preferences.cs', 'Theme.cs', 'SettingsUi.cs', 'TestHarness.cs') | ForEach-Object { Join-Path $src $_ }
& $compiler @shared @ui @sources
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed' }
& $compiler @shared '/target:exe' '/main:PowerTimer.ControlCli' ('/out:' + (Join-Path $target 'timerctl.exe')) (Join-Path $src 'Client.cs')
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
$testSources = @('Core.cs', 'Api.cs', 'Client.cs', 'DurationInput.cs', 'ScheduleTime.cs', 'Preferences.cs', 'Storage.cs', 'CoreTests.cs', 'ReliabilityTests.cs') | ForEach-Object { Join-Path $src $_ }
& $compiler @shared '/target:exe' ('/out:' + (Join-Path $target 'CoreTests.exe')) @testSources '/main:PowerTimer.CoreTests'
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
if (-not $SkipTests) {
    & (Join-Path $target 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
}
Write-Output ('Built: ' + $target)
