param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
if (!(Test-Path $compiler)) { throw 'The Windows .NET Framework 4.x compiler is required.' }
$common = @('Shared.cs','Config.cs','Interop.cs','SwitchEngine.cs','SoundCues.cs') | ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
$refs = @('/r:System.dll','/r:System.Core.dll')
$uiRefs = @('/r:System.Xaml.dll',"/r:$wpf\WindowsBase.dll","/r:$wpf\PresentationCore.dll","/r:$wpf\PresentationFramework.dll")
$assets = @("/resource:$PSScriptRoot\assets\confirm.wav,AudioSwitcher.Confirm.wav","/resource:$PSScriptRoot\assets\error.wav,AudioSwitcher.Error.wav")
$uiAsset = "/resource:$PSScriptRoot\src\SetupWindow.xaml,AudioSwitcher.SetupWindow.xaml"
$flags = @('/nologo','/optimize+','/platform:x64','/warn:4',"/win32manifest:$PSScriptRoot\src\app.manifest","/win32icon:$PSScriptRoot\assets\app.ico")
& $compiler @flags /target:winexe /main:HeadphoneSwitcher.ToggleProgram "/out:$PSScriptRoot\AudioSwitcher.Toggle.exe" @refs @assets @common "$PSScriptRoot\src\ToggleProgram.cs"
if ($LASTEXITCODE -ne 0) { throw 'Toggle build failed.' }
& $compiler @flags /target:winexe /main:HeadphoneSwitcher.SetupProgram "/out:$PSScriptRoot\AudioSwitcher.Config.exe" @refs @uiRefs @assets $uiAsset @common "$PSScriptRoot\src\SetupWindow.cs" "$PSScriptRoot\src\SetupProgram.cs"
if ($LASTEXITCODE -ne 0) { throw 'Config build failed.' }
if ($Test) {
    New-Item -ItemType Directory -Force -Path "$PSScriptRoot\artifacts" | Out-Null
    & $compiler @flags /target:exe /main:HeadphoneSwitcher.Tests "/out:$PSScriptRoot\artifacts\AudioSwitcher.Tests.exe" @refs @uiRefs @assets $uiAsset @common "$PSScriptRoot\src\SetupWindow.cs" "$PSScriptRoot\tests\Tests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & "$PSScriptRoot\artifacts\AudioSwitcher.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Package) {
    $packageDirectory = Join-Path $PSScriptRoot 'artifacts\package'
    if (Test-Path $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot\AudioSwitcher.Config.exe","$PSScriptRoot\AudioSwitcher.Toggle.exe","$PSScriptRoot\README.md","$PSScriptRoot\LICENSE" -Destination $packageDirectory
    Copy-Item -LiteralPath "$PSScriptRoot\assets\README.md" -Destination "$packageDirectory\SOUND-NOTES.md"
    Compress-Archive -LiteralPath "$packageDirectory\AudioSwitcher.Config.exe","$packageDirectory\AudioSwitcher.Toggle.exe","$packageDirectory\README.md","$packageDirectory\LICENSE","$packageDirectory\SOUND-NOTES.md" -DestinationPath "$PSScriptRoot\AudioSwitcher.zip" -Force
    Write-Host 'Packaged AudioSwitcher.zip.'
}
Write-Host 'Built AudioSwitcher.Config.exe and AudioSwitcher.Toggle.exe.'

