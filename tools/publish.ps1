<#
    Builds the release version and, if Inno Setup is available, the installer.

        .\tools\publish.ps1
        .\tools\publish.ps1 -SelfContained     runs without an installed .NET runtime
        .\tools\publish.ps1 -NoInstaller

    Output:
        dist\app\                     the app including the modules\ tree
        dist\WinTo-<v>-Setup.exe      the installer

    ReadyToRun is on: roughly a third less time to first frame and about 10 MB less
    memory. Trimming stays off, because modules are resolved by type names from a JSON
    file, exactly what a trimmer removes.
#>

[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$NoInstaller,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $repo 'dist'
$appDir = Join-Path $dist 'app'

function Get-RepoVersion {
    $props = Join-Path $repo 'src\Directory.Build.props'
    $m = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $m) { throw "No <Version> found in $props." }
    return $m.Matches[0].Groups[1].Value
}

Push-Location $repo
try {
    $version = Get-RepoVersion
    Write-Host "Publishing WinTo $version" -ForegroundColor Cyan

    # Clearing below also removes modules\. If dist\app\WinTo.exe is running, the
    # module folder disappears under it and the next tile click fails with
    # "Dependency resolution failed".
    $running = Get-Process WinTo, MultiTool -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) }

    if ($running) {
        $ids = ($running | ForEach-Object { $_.Id }) -join ', '
        throw "dist\app\WinTo.exe is still running (PID $ids). Close it first, or the module folder is cleared under the running app."
    }

    # Clear the contents instead of removing the folder: a directory can be locked
    # (someone's working directory) while its files aren't.
    if (Test-Path $appDir) {
        Get-ChildItem $appDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null

    # --- Icons ---
    # Checked in; drawn again only if missing. See tools\IconGen\Program.cs.
    if (-not (Test-Path (Join-Path $repo 'src\MultiTool.Host\Assets\app.ico'))) {
        dotnet run --project (Join-Path $PSScriptRoot 'IconGen') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "IconGen failed (exit $LASTEXITCODE)" }
    }

    # --- Shell ---
    Write-Host '  Shell ...' -ForegroundColor DarkGray
    dotnet publish (Join-Path $repo 'src\MultiTool.Host\MultiTool.Host.csproj') `
        -c Release -r $Runtime --self-contained $($SelfContained.IsPresent) `
        -p:PublishReadyToRun=true -p:DebugType=embedded `
        -o $appDir --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Publishing the shell failed (exit $LASTEXITCODE)" }

    # --- Modules ---
    # Built one by one, not published with the shell: a module isn't a reference of the
    # shell but a file found at runtime.
    Write-Host '  Modules ...' -ForegroundColor DarkGray
    $moduleProjects = Get-ChildItem (Join-Path $repo 'src\modules') -Recurse -Filter *.csproj
    foreach ($proj in $moduleProjects) {
        dotnet build $proj.FullName -c Release --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "$($proj.BaseName) failed (exit $LASTEXITCODE)" }
    }

    $built = Join-Path $repo 'build\modules'
    Copy-Item $built (Join-Path $appDir 'modules') -Recurse -Force

    # --- Shared assemblies in a module folder ---
    # At runtime that reads "Object of type 'Control' cannot be converted to type
    # 'Control'". Same list as the build guard in src\modules\Directory.Build.targets.
    $strays = Get-ChildItem (Join-Path $appDir 'modules') -Recurse -File |
        Where-Object {
            $_.Name -like 'Avalonia*.dll' -or $_.Name -like 'SkiaSharp*.dll' -or $_.Name -like 'HarfBuzzSharp*.dll' -or
            $_.Name -like 'libSkiaSharp*' -or $_.Name -like 'libHarfBuzzSharp*' -or
            $_.Name -eq 'CommunityToolkit.Mvvm.dll' -or
            $_.Name -eq 'MultiTool.Abstractions.dll' -or $_.Name -eq 'MultiTool.UI.dll' -or
            $_.Extension -eq '.so' -or $_.Extension -eq '.dylib'
        }
    if ($strays) {
        $strays | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Red }
        throw 'Shared assemblies in a module folder. See src\modules\Directory.Build.targets.'
    }

    # --- Self-test against exactly what ships ---
    Write-Host '  Self-test ...' -ForegroundColor DarkGray
    # Start-Process instead of the call operator; see build.ps1.
    $probe = Start-Process (Join-Path $appDir 'WinTo.exe') -ArgumentList '--selftest' -PassThru -NoNewWindow
    $null = $probe.Handle    # otherwise PowerShell 5.1 has no ExitCode
    $probe.WaitForExit()
    if ($probe.ExitCode -ne 0) { throw "Self-test failed (exit $($probe.ExitCode))" }

    $appSize = (Get-ChildItem $appDir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  dist\app: {0:N1} MB" -f ($appSize / 1MB)) -ForegroundColor Green

    # --- Installer ---
    if ($NoInstaller) { return }

    # winget installs Inno Setup under %LOCALAPPDATA%\Programs, the vendor installer under
    # Program Files (x86). Neither puts ISCC on the PATH, so look for a path.
    $isccPath = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $isccPath) {
        $guesses = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
        $isccPath = $guesses | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $isccPath) {
        Write-Host ''
        Write-Host '  Inno Setup not found, no installer built.' -ForegroundColor Yellow
        Write-Host '  Once:  winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
        return
    }

    Write-Host '  Installer ...' -ForegroundColor DarkGray
    & $isccPath "/DAppVersion=$version" (Join-Path $repo 'installer\MultiTool.iss') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }

    $setup = Join-Path $dist "WinTo-$version-Setup.exe"
    if (Test-Path $setup) {
        Write-Host ("  {0}  ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
