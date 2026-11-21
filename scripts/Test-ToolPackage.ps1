# Copyright (c) McpSharp contributors
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Smoke-tests the MCP server .NET tool packages produced by `dotnet pack`.

.DESCRIPTION
    For each package ID, checks that the platform-agnostic package and the package for the
    requested runtime identifier exist in PackageDirectory. It then extracts the
    runtime-specific package, launches the server executable named by its
    DotnetToolSettings.xml, sends an MCP `initialize` request over stdio (NDJSON) and checks
    that the response is a successful result whose server name matches the tool command and
    whose server version matches the package version.

.EXAMPLE
    ./scripts/Test-ToolPackage.ps1 -PackageDirectory artifacts/packages -RuntimeIdentifier win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }),

    [string[]] $PackageId = @(
        'Mikeagun.McpTools.CiDebugMcp',
        'Mikeagun.McpTools.ElicitMcp',
        'Mikeagun.McpTools.HyperVMcp',
        'Mikeagun.McpTools.MsBuildMcp'
    ),

    # When set, the packages must have exactly this version.
    [string] $Version,

    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Find-Package([string] $id) {
    # Package file names are <id>.<version>.nupkg; anchor on a digit so that
    # 'Foo' does not also match 'Foo.win-x64.1.0.0.nupkg'.
    $pattern = '^' + [regex]::Escape($id) + '\.(\d[^\\/]*)\.nupkg$'
    $found = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' |
        Where-Object { $_.Name -match $pattern -and (-not $Version -or $Matches[1] -eq $Version) })
    if ($found.Count -ne 1) {
        throw "Expected exactly one package '$id'$(if ($Version) { " version $Version" }) in '$PackageDirectory', found $($found.Count)."
    }
    [pscustomobject]@{ File = $found[0]; Version = [regex]::Match($found[0].Name, $pattern).Groups[1].Value }
}

function Invoke-Initialize([string] $exePath, [string] $workingDirectory) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
    $psi.WorkingDirectory = $workingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    # Drain stderr concurrently so a chatty server cannot block on a full pipe.
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $request = @{
            jsonrpc = '2.0'
            id      = 1
            method  = 'initialize'
            params  = @{
                protocolVersion = '2025-06-18'
                capabilities    = @{}
                clientInfo      = @{ name = 'Test-ToolPackage'; version = '1.0' }
            }
        } | ConvertTo-Json -Depth 5 -Compress
        $process.StandardInput.WriteLine($request)
        $process.StandardInput.Flush()

        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ($true) {
            $remaining = $deadline - [DateTime]::UtcNow
            $read = $process.StandardOutput.ReadLineAsync()
            if ($remaining -le [TimeSpan]::Zero -or -not $read.Wait($remaining)) {
                throw "Timed out after $TimeoutSeconds s waiting for the initialize response."
            }
            $line = $read.Result
            if ($null -eq $line) { throw 'Server exited before responding.' }
            if ($line.Trim() -eq '') { continue }
            $message = $line | ConvertFrom-Json
            if ($message.PSObject.Properties['id'] -and $message.id -eq 1) { return $message }
        }
    }
    catch {
        if (-not $process.HasExited) { $process.Kill($true) }
        $err = if ($stderr.Wait(5000)) { $stderr.Result } else { '' }
        throw "$($_.Exception.Message)`nServer stderr:`n$err"
    }
    finally {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.WaitForExit(10000)) { $process.Kill($true) }
        $process.Dispose()
    }
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("tool-package-smoke-" + [guid]::NewGuid().ToString('N'))
$failures = 0
try {
    foreach ($id in $PackageId) {
        try {
            $pointer = Find-Package $id
            $ridPackage = Find-Package "$id.$RuntimeIdentifier"

            if ($ridPackage.Version -ne $pointer.Version) { throw "Package versions differ: $($pointer.File.Name) and $($ridPackage.File.Name)." }

            $extractDir = Join-Path $scratch $ridPackage.File.BaseName
            [System.IO.Compression.ZipFile]::ExtractToDirectory($ridPackage.File.FullName, $extractDir)

            $settingsFile = @(Get-ChildItem -LiteralPath (Join-Path $extractDir 'tools') -Recurse -Filter 'DotnetToolSettings.xml')
            if ($settingsFile.Count -ne 1) { throw "Expected one DotnetToolSettings.xml in '$($ridPackage.File.Name)', found $($settingsFile.Count)." }
            $command = ([xml] (Get-Content -LiteralPath $settingsFile[0].FullName -Raw)).DotNetCliTool.Commands.Command
            $exePath = Join-Path $settingsFile[0].DirectoryName $command.EntryPoint
            if (-not (Test-Path -LiteralPath $exePath)) { throw "Entry point '$($command.EntryPoint)' not found in '$($ridPackage.File.Name)'." }

            $response = Invoke-Initialize $exePath $extractDir
            if ($response.PSObject.Properties['error']) { throw "initialize returned an error: $($response.error | ConvertTo-Json -Compress)" }
            $serverName = $response.result.serverInfo.name
            if ($serverName -ne $command.Name) { throw "Expected server name '$($command.Name)', got '$serverName'." }
            $serverVersion = $response.result.serverInfo.version
            if ($serverVersion -ne $ridPackage.Version) { throw "Expected server version '$($ridPackage.Version)', got '$serverVersion'." }

            Write-Host "PASS $($ridPackage.File.Name) (with $($pointer.File.Name)): $serverName $serverVersion, protocol $($response.result.protocolVersion)"
        }
        catch {
            $failures++
            Write-Host "FAIL ${id} ($RuntimeIdentifier): $($_.Exception.Message)"
        }
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Error "$failures of $($PackageId.Count) package smoke tests failed."
    exit 1
}
