[CmdletBinding()]
param(
	[string]$Configuration = "Release",
	[string]$OutputDir = "dist",
	[string]$BundleName = "dnspy-mcp-win-x64",
	[switch]$SkipZip
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
	$extProj = "Extensions\\Examples\\Example1.Extension\\Example1.Extension.csproj"
	$ilspyDecompilerProj = "Extensions\\ILSpy.Decompiler\\dnSpy.Decompiler.ILSpy\\dnSpy.Decompiler.ILSpy.csproj"
	$debuggerProj = "Extensions\\dnSpy.Debugger\\dnSpy.Debugger\\dnSpy.Debugger.csproj"
	$debuggerDotNetProj = "Extensions\\dnSpy.Debugger\\dnSpy.Debugger.DotNet\\dnSpy.Debugger.DotNet.csproj"
	$debuggerCorDebugProj = "Extensions\\dnSpy.Debugger\\dnSpy.Debugger.DotNet.CorDebug\\dnSpy.Debugger.DotNet.CorDebug.csproj"
	$debuggerMonoProj = "Extensions\\dnSpy.Debugger\\dnSpy.Debugger.DotNet.Mono\\dnSpy.Debugger.DotNet.Mono.csproj"
	$dnspyProj = "dnSpy\\dnSpy\\dnSpy.csproj"

	Write-Host "Stopping running dnSpy processes (to unlock build outputs)..."
	Get-Process -Name dnSpy -ErrorAction SilentlyContinue | Stop-Process -Force

	Write-Host "Building extension ($Configuration)..."
	& dotnet msbuild $extProj /t:Build /p:Configuration=$Configuration /m
	if ($LASTEXITCODE -ne 0) {
		throw "Extension build failed."
	}

	Write-Host "Building ILSpy decompiler extension ($Configuration)..."
	& dotnet msbuild $ilspyDecompilerProj /t:Build /p:Configuration=$Configuration /p:TargetFramework=net48 /m
	if ($LASTEXITCODE -ne 0) {
		throw "ILSpy decompiler extension build failed."
	}

	Write-Host "Building debugger extensions ($Configuration)..."
	$debuggerProjects = @($debuggerProj, $debuggerDotNetProj, $debuggerCorDebugProj, $debuggerMonoProj)
	foreach ($proj in $debuggerProjects) {
		& dotnet msbuild $proj /t:Build /p:Configuration=$Configuration /p:TargetFramework=net48 /m
		if ($LASTEXITCODE -ne 0) {
			throw "Debugger extension build failed: $proj"
		}
	}

	Write-Host "Building dnSpy ($Configuration)..."
	& dotnet msbuild $dnspyProj /t:Build /p:Configuration=$Configuration /p:TargetFramework=net48 /m
	if ($LASTEXITCODE -ne 0) {
		throw "dnSpy build failed."
	}

	$dnspyOut = Join-Path $repoRoot ("dnSpy\\dnSpy\\bin\\{0}\\net48" -f $Configuration)
	$extOut = Join-Path $repoRoot ("Extensions\\Examples\\Example1.Extension\\bin\\{0}\\net48" -f $Configuration)
	if (-not (Test-Path (Join-Path $dnspyOut "dnSpy.exe"))) {
		throw "dnSpy output not found: $dnspyOut"
	}
	if (-not (Test-Path (Join-Path $dnspyOut "dnSpy.Decompiler.ILSpy.x.dll"))) {
		throw "Decompiler extension output not found: $(Join-Path $dnspyOut 'dnSpy.Decompiler.ILSpy.x.dll')"
	}
	if (-not (Test-Path (Join-Path $dnspyOut "dnSpy.Debugger.x.dll"))) {
		throw "Debugger extension output not found: $(Join-Path $dnspyOut 'dnSpy.Debugger.x.dll')"
	}
	if (-not (Test-Path (Join-Path $dnspyOut "dnSpy.Debugger.DotNet.x.dll"))) {
		throw "Debugger .NET extension output not found: $(Join-Path $dnspyOut 'dnSpy.Debugger.DotNet.x.dll')"
	}
	if (-not (Test-Path (Join-Path $dnspyOut "dnSpy.Debugger.DotNet.CorDebug.x.dll"))) {
		throw "Debugger CorDebug extension output not found: $(Join-Path $dnspyOut 'dnSpy.Debugger.DotNet.CorDebug.x.dll')"
	}
	if (-not (Test-Path (Join-Path $extOut "Example1.Extension.x.dll"))) {
		throw "Extension output not found: $extOut"
	}

	$bundleRoot = Join-Path $repoRoot (Join-Path $OutputDir $BundleName)
	$bundleDnspy = Join-Path $bundleRoot "dnspy"
	$bundleExt = Join-Path $bundleRoot "extension"
	$bundleTools = Join-Path $bundleRoot "tools"

	Write-Host "Staging bundle: $bundleRoot"
	Remove-Item -Path $bundleRoot -Recurse -Force -ErrorAction SilentlyContinue
	New-Item -ItemType Directory -Path $bundleDnspy, $bundleExt, $bundleTools -Force | Out-Null

	Copy-Item -Path (Join-Path $dnspyOut "*") -Destination $bundleDnspy -Recurse -Force
	# Avoid loading the same MCP extension twice (root + --extension-directory).
	Remove-Item -Path (Join-Path $bundleDnspy "Example1.Extension.x.dll") -Force -ErrorAction SilentlyContinue
	Remove-Item -Path (Join-Path $bundleDnspy "Example1.Extension.x.pdb") -Force -ErrorAction SilentlyContinue
	Copy-Item -Path (Join-Path $extOut "*") -Destination $bundleExt -Recurse -Force
	Copy-Item -Path "tools\\mcp_client.py", "tools\\mcp_smoke_test.py" -Destination $bundleTools -Force

	$startPs1 = @'
param(
	[string]$BindHost = $env:DNSPY_MCP_HOST,
	[string]$Port = $env:DNSPY_MCP_PORT
)

if ([string]::IsNullOrWhiteSpace($BindHost)) { $BindHost = "0.0.0.0" }
if ([string]::IsNullOrWhiteSpace($Port)) { $Port = "3003" }

$env:DNSPY_MCP_HOST = $BindHost
$env:DNSPY_MCP_PORT = $Port

$dnspyExe = Join-Path $PSScriptRoot "dnspy\\dnSpy.exe"
$extDir = Join-Path $PSScriptRoot "extension"

if (-not (Test-Path $dnspyExe)) { throw "dnSpy.exe not found: $dnspyExe" }
if (-not (Test-Path $extDir)) { throw "extension folder not found: $extDir" }

Write-Host "Starting dnSpy MCP on host=$BindHost port=$Port"
Write-Host "Server defaults to 0.0.0.0 and automatically falls back to 127.0.0.1 if bind fails."
Start-Process -FilePath $dnspyExe -ArgumentList "--extension-directory", $extDir
'@
	Set-Content -Path (Join-Path $bundleRoot "start-dnspy-mcp.ps1") -Value $startPs1 -Encoding UTF8

	$startCmd = @'
@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0start-dnspy-mcp.ps1" %*
'@
	Set-Content -Path (Join-Path $bundleRoot "start-dnspy-mcp.cmd") -Value $startCmd -Encoding ASCII

	$readme = @'
dnSpy MCP Bundle

Contents
- dnspy\            dnSpy binaries
- extension\        MCP extension binaries
- start-dnspy-mcp.ps1 / .cmd
- tools\mcp_client.py
- tools\mcp_smoke_test.py

Run
1) start-dnspy-mcp.cmd
2) MCP endpoint: http://<host>:<port>/

Defaults
- Host: 0.0.0.0
- Port: 3003
- If bind to 0.0.0.0 fails, server falls back to 127.0.0.1 automatically.
'@
	Set-Content -Path (Join-Path $bundleRoot "README.txt") -Value $readme -Encoding ASCII

	$zipPath = Join-Path $repoRoot (Join-Path $OutputDir ($BundleName + ".zip"))
	if (-not $SkipZip) {
		Write-Host "Creating zip: $zipPath"
		Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
		Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $zipPath -Force
	}

	Write-Host "Bundle ready: $bundleRoot"
	if (-not $SkipZip) {
		Write-Host "Zip ready: $zipPath"
	}
}
finally {
	Pop-Location
}
