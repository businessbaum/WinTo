<#
    Development build: shell and all modules.

        .\tools\build.ps1                 build
        .\tools\build.ps1 -Run            build and start
        .\tools\build.ps1 -SelfTest       build and run the self-test
        .\tools\build.ps1 -Configuration Release

    Modules always land in build\modules\<id>, regardless of configuration. The shell
    looks there, so a Debug start finds the same modules as a Release start. Mixing both
    leaves whichever was built last in the folder; keep that in mind when debugging.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Run,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $repo
try {
    Write-Host "Building WinTo ($Configuration)" -ForegroundColor Cyan

    dotnet build MultiTool.slnx -c $Configuration --nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

    $modules = Get-ChildItem (Join-Path $repo 'build\modules') -Directory -ErrorAction SilentlyContinue
    Write-Host ("Modules: {0}" -f ($modules.Count)) -ForegroundColor Green
    foreach ($m in $modules) {
        $size = (Get-ChildItem $m.FullName -Recurse -File | Measure-Object Length -Sum).Sum
        '    {0,-28} {1,8:N0} KB' -f $m.Name, ($size / 1KB)
    }

    $exe = Join-Path $repo "src\MultiTool.Host\bin\$Configuration\net10.0-windows\WinTo.exe"

    if ($SelfTest) {
        Write-Host ''
        # Not via the call operator: WinTo.exe is a WinExe, which PowerShell doesn't
        # wait for, so $LASTEXITCODE would keep dotnet build's value and the check could
        # never fail. Grab the handle before waiting, or PowerShell 5.1 has no ExitCode.
        $probe = Start-Process $exe -ArgumentList '--selftest' -PassThru -NoNewWindow
        $null = $probe.Handle
        $probe.WaitForExit()
        if ($probe.ExitCode -ne 0) { throw "Self-test failed (exit $($probe.ExitCode))" }
    }

    if ($Run) { Start-Process $exe }
}
finally {
    Pop-Location
}
