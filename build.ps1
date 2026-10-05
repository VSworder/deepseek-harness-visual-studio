<#
Builds the extension and packages it as a .vsix.

    .\build.ps1                 # Release build + package
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Install        # build, then install into the local Visual Studio

Visual Studio is located with vswhere; pass -VsInstallRoot to override.
Requires the "Visual Studio extension development" workload.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $VsInstallRoot,

    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

function Find-VsInstallRoot {
    param([string] $Explicit)

    if ($Explicit) { return $Explicit }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.CoreEditor -property installationPath
        if ($path) { return $path.Trim() }
    }

    throw "Visual Studio not found. Pass -VsInstallRoot explicitly."
}

$vsRoot = Find-VsInstallRoot -Explicit $VsInstallRoot
Write-Host "Visual Studio : $vsRoot"

$msbuild = Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

# The VSIX packaging targets live in the VS install; without the workload the build
# fails with a missing Microsoft.VsSDK.targets, so check early and say why.
$vsSdkTargets = Join-Path $vsRoot 'MSBuild\Microsoft\VisualStudio\v18.0\VSSDK\Microsoft.VsSDK.targets'
if (-not (Test-Path $vsSdkTargets)) {
    Write-Warning "Microsoft.VsSDK.targets not found. Install the 'Visual Studio extension development' workload."
}

# NuGet writes to the user profile by default, which is not always writable in
# sandboxed or CI environments. Keep the caches inside the repo's artifacts folder.
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts\dotnet-cli-home'
$env:NUGET_PACKAGES   = Join-Path $repoRoot 'artifacts\nuget-packages'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

$project = Join-Path $repoRoot 'src\DeepSeekHarness.Vsix\DeepSeekHarness.Vsix.csproj'

Write-Host "`n==> Restoring" -ForegroundColor Cyan
& $msbuild $project /t:Restore /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

Write-Host "`n==> Building and packaging" -ForegroundColor Cyan
& $msbuild $project /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$vsix = Get-ChildItem (Join-Path $repoRoot 'artifacts') -Filter '*.vsix' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $vsix) { throw "Build reported success but no .vsix was produced." }
Write-Host "`nVSIX: $($vsix.FullName)  ($([math]::Round($vsix.Length / 1KB)) KB)" -ForegroundColor Green

if ($Install) {
    Write-Host "`n==> Installing into Visual Studio" -ForegroundColor Cyan
    $vsixInstaller = Join-Path $vsRoot 'Common7\IDE\VSIXInstaller.exe'
    if (-not (Test-Path $vsixInstaller)) { throw "VSIXInstaller.exe not found at $vsixInstaller" }

    # Quiet install; the installer needs elevation to write into the VS install folder.
    & $vsixInstaller /quiet $vsix.FullName
    Write-Host "Install exit code: $LASTEXITCODE" -ForegroundColor Green
    Write-Host "Restart Visual Studio to load the extension."
}
