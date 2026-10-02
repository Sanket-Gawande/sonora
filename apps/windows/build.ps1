param([switch]$Verify)
# Builds Sonora with the C# compiler that ships with Windows (.NET Framework 4.8), so no SDK,
# NuGet or network access is needed. The compiler supports C# 5; keep the source to that.
$ErrorActionPreference = 'Stop'
$appRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 is required to build Sonora.' }
$buildRoot = Join-Path $appRoot 'build'
$package = Join-Path $buildRoot 'Sonora'
New-Item -ItemType Directory -Force -Path $package | Out-Null

# Fonts become WPF pack resources inside the executable, so Mona Sans needs no install.
$fontResources = Join-Path $buildRoot 'Sonora.g.resources'
$writer = New-Object System.Resources.ResourceWriter($fontResources)
try {
    foreach ($font in Get-ChildItem (Join-Path $appRoot 'Assets\Fonts') -Filter '*.otf') {
        $writer.AddResource(('assets/fonts/' + $font.Name.ToLowerInvariant()), [System.IO.File]::OpenRead($font.FullName), $true)
    }
    $writer.Generate()
} finally { $writer.Close() }

$icon = Join-Path $appRoot 'Assets\Sonora.ico'
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4',
    ('/out:' + (Join-Path $package 'Sonora.exe')),
    ('/win32manifest:' + (Join-Path $appRoot 'app.manifest')),
    ('/win32icon:' + $icon),
    ('/resource:' + (Join-Path $appRoot 'Styles.xaml') + ',Sonora.Styles.xaml'),
    ('/resource:' + $icon + ',Sonora.Sonora.ico'),
    ('/resource:' + $fontResources + ',Sonora.g.resources'))
foreach ($assembly in 'System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.CSharp.dll') {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationProvider.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll') {
    $arguments += '/reference:' + (Join-Path $framework ('WPF\' + $assembly))
}
# Windows' own media-session API (what's playing, play/pause/next) comes from the WinRT metadata
# every Windows 10/11 install ships, plus .NET's WinRT bridge; no SDK is needed.
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
foreach ($metadata in 'Windows.Foundation.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd') { $arguments += '/reference:' + (Join-Path $winmd $metadata) }
foreach ($assembly in 'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Threading.Tasks.dll') {
    $path = Join-Path $framework $assembly
    if (Test-Path -LiteralPath $path) { $arguments += '/reference:' + $path }
}
$arguments += Get-ChildItem (Join-Path $appRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

Copy-Item (Join-Path $appRoot 'App.config') (Join-Path $package 'Sonora.exe.config') -Force
Remove-Item (Join-Path $package '*-LICENSE.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $appRoot 'Assets\Fonts\LICENSE.txt') (Join-Path $package 'MonaSans-LICENSE.txt') -Force
Copy-Item (Join-Path $appRoot 'PACKAGE-README.txt') (Join-Path $package 'README.txt') -Force

if ($Verify) {
    $verification = Join-Path $buildRoot 'verification'
    $process = Start-Process -FilePath (Join-Path $package 'Sonora.exe') -ArgumentList @('--verify', ('"' + $verification + '"')) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw ('Verification failed. See ' + (Join-Path $env:LOCALAPPDATA 'Sonora\error.log')) }
    Get-Content (Join-Path $verification 'verification.txt')
}
Compress-Archive -Path $package -DestinationPath (Join-Path $buildRoot 'Sonora-Windows-0.5.zip') -Force
Write-Output ('Built: ' + (Join-Path $package 'Sonora.exe'))
