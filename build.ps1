param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Clean,
    [switch]$Test,
    [switch]$PackagesOnly
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build Windows Native AOT on Windows with the .NET 10 SDK and Visual Studio C++ build tools.' }
. (Join-Path $PSScriptRoot 'scripts/initialize-windows-build.ps1')
$mnxRoot = $PSScriptRoot
$mnxParent = Split-Path -Parent $mnxRoot
$mnxComponent = 'mnx-cli'
$mnxPrimary = 'src/MNXFile.Cli.csproj'
if ($PackagesOnly -and $mnxComponent -ne 'mnxfile') { throw '-PackagesOnly applies to MNXFile only.' }
$mnxSdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($mnxSdk.Split('.')[0]) -lt 10) { throw '.NET SDK 10 or newer is required.' }
if (-not $env:MNXBuildRoot) { $env:MNXBuildRoot = Join-Path $mnxParent "mnx_clutter/build/$mnxComponent" }
if (-not $env:MNXReleaseRoot) { $env:MNXReleaseRoot = Join-Path $mnxParent "MNX_Builds/$mnxComponent" }
New-Item -ItemType Directory -Force -Path $env:MNXBuildRoot, $env:MNXReleaseRoot | Out-Null
$mnxLock = [IO.File]::Open((Join-Path $env:MNXBuildRoot 'release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$mnxOriginalBuildNumber = $env:MNXBuildNumber
try {
    $env:MSBUILDDISABLENODEREUSE = '1'
    if (-not $env:MNXBuildNumber) {
        $mnxReserved = & dotnet msbuild (Join-Path $mnxRoot $mnxPrimary) -nologo -verbosity:quiet -target:ReserveMNXBuildNumber -getProperty:MNXBuildNumber
        if ($LASTEXITCODE -ne 0) { throw 'Build-number reservation failed.' }
        $env:MNXBuildNumber = ($mnxReserved -join "`n").Trim()
    }
    if ($env:MNXBuildNumber -notmatch '^\d+$' -or [int]$env:MNXBuildNumber -lt 1 -or [int]$env:MNXBuildNumber -gt 65534) { throw 'MNXBuildNumber must be 1..65534.' }
    $mnxOutput = Join-Path $env:MNXReleaseRoot $Runtime
    New-Item -ItemType Directory -Force -Path $mnxOutput | Out-Null
    $mnxFeed = $env:MNXPackageFeed
    if (-not $mnxFeed) { $mnxFeed = Join-Path $mnxParent 'MNX_Builds/mnxfile/packages' }
    $mnxUseLocalFeed = $env:MNXPackageFeed -or (Test-Path -LiteralPath $mnxFeed -PathType Container)
    $mnxFeed = [Security.SecurityElement]::Escape($mnxFeed)
    $mnxConfig = Join-Path $env:MNXBuildRoot 'nuget.config'
    $mnxSources = '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    if ($mnxUseLocalFeed) { $mnxSources = '<add key="MNX client feed" value="' + $mnxFeed + '" />' + $mnxSources }
    [IO.File]::WriteAllText($mnxConfig, '<configuration><packageSources><clear />' + $mnxSources + '</packageSources></configuration>')
    $mnxOptions = @('-c', 'Release', '-p:UseSharedCompilation=false', "-p:RestoreConfigFile=$mnxConfig")
    function Invoke-MNXDotnet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    Write-Host "Building $mnxComponent release $env:MNXBuildNumber for $Runtime"
    if ($Clean) {
        # Remove both RID-specific publish intermediates and RID-independent package builds.
        foreach ($mnxGeneratedFolder in @('bin', 'obj')) {
            $mnxGeneratedPath = Join-Path $env:MNXBuildRoot $mnxGeneratedFolder
            if (Test-Path -LiteralPath $mnxGeneratedPath) {
                Remove-Item -LiteralPath $mnxGeneratedPath -Recurse -Force
                }
            }
        foreach ($mnxCleanProject in @('src/MNXFile.Cli.csproj', 'interactive/MNXFile.Interactive.csproj')) {
            Invoke-MNXDotnet restore (Join-Path $mnxRoot $mnxCleanProject) -r $Runtime "-p:RestoreConfigFile=$mnxConfig"
            Invoke-MNXDotnet clean (Join-Path $mnxRoot $mnxCleanProject) -c Release -r $Runtime -nologo
        }
    }

    if ($PackagesOnly) { throw '-PackagesOnly applies to MNXFile only.' }
    foreach ($mnxProject in @('src/MNXFile.Cli.csproj', 'interactive/MNXFile.Interactive.csproj')) {
        Invoke-MNXDotnet publish (Join-Path $mnxRoot $mnxProject) @mnxOptions -r $Runtime -p:PublishAot=true -o $mnxOutput
    }
    if ($Test) {
        Invoke-MNXDotnet run --project (Join-Path $mnxRoot 'tests/MNXFile.Cli.Smoke/MNXFile.Cli.Smoke.csproj') @mnxOptions -- $mnxOutput
        & (Join-Path $mnxOutput 'mnx.exe') --version
        if ($LASTEXITCODE -ne 0) { throw 'mnx version check failed.' }
        & (Join-Path $mnxOutput 'mnxi.exe') --version
        if ($LASTEXITCODE -ne 0) { throw 'mnxi version check failed.' }
    }

    $mnxSymbols = Join-Path $env:MNXBuildRoot "symbols/$Runtime"
    New-Item -ItemType Directory -Force -Path $mnxSymbols | Out-Null
    Get-ChildItem -LiteralPath $mnxOutput -File | Where-Object { $_.Extension -in '.pdb', '.dbg' } | Move-Item -Destination $mnxSymbols -Force
    Write-Host "Release output: $env:MNXReleaseRoot"
} finally {
    $env:MNXBuildNumber = $mnxOriginalBuildNumber
    $mnxLock.Dispose()
}
