param(
	[string]$Runtime = "win-x64",
	[string]$InstallerCompilerPath = "",
	[string]$SignToolPath = "",
	[string]$CertificatePath = "",
	[string]$CertificatePassword = "",
	[string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Runtime = $Runtime.Trim()
if ($Runtime -ne "win-x64") {
	throw "The supported installer target is win-x64; received '$Runtime'."
}

function Find-InnoCompiler {
	$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
	if ($command) { return $command.Source }

	$candidates = @(
		(Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
		(Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
		(Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
		(Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe")
	)
	foreach ($candidate in $candidates) {
		if (Test-Path -LiteralPath $candidate) { return $candidate }
	}
	return ""
}

function Find-SignTool {
	if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
		if (-not (Test-Path -LiteralPath $SignToolPath)) { throw "signtool.exe not found at $SignToolPath." }
		return $SignToolPath
	}

	$command = Get-Command signtool.exe -ErrorAction SilentlyContinue
	if ($command) { return $command.Source }

	$kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
	if (Test-Path -LiteralPath $kitsRoot) {
		$candidates = Get-ChildItem -LiteralPath $kitsRoot -Directory |
			Sort-Object Name -Descending |
			ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" }
		foreach ($candidate in $candidates) {
			if (Test-Path -LiteralPath $candidate) { return $candidate }
		}
	}
	return ""
}

function Invoke-CodeSign([string]$FilePath) {
	$arguments = @("sign", "/fd", "SHA256", "/f", $CertificatePath, "/tr", $TimestampUrl, "/td", "SHA256")
	if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
		$arguments += @("/p", $CertificatePassword)
	}
	$arguments += $FilePath
	& $SignToolPath @arguments
	if ($LASTEXITCODE -ne 0) {
		throw "signtool failed with exit code $LASTEXITCODE for $FilePath."
	}
}

if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath)) {
	$InstallerCompilerPath = Find-InnoCompiler
}
if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath) -or -not (Test-Path -LiteralPath $InstallerCompilerPath)) {
	throw "Inno Setup 6 compiler (ISCC.exe) not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -InstallerCompilerPath."
}

$publishDirectory = Join-Path $projectRoot "DepupCode\bin\Release\net8.0-windows\$Runtime\publish"
$publishExe = Join-Path $publishDirectory "DepupCode.exe"
$privacyFile = Join-Path $publishDirectory "PRIVACY.md"
$installerNotice = Join-Path $projectRoot "Packaging\Installer\PRIVACY-NOTICE.txt"
if (-not (Test-Path -LiteralPath $publishExe)) {
	throw "Release publish output not found at $publishDirectory. Run .\build.ps1 first."
}
if (-not (Test-Path -LiteralPath $privacyFile)) {
	throw "PRIVACY.md is missing from publish output. Rebuild the app before packaging."
}
if (-not (Test-Path -LiteralPath $installerNotice)) {
	throw "Installer privacy notice is missing: $installerNotice"
}

[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot "DepupCode\DepupCode.csproj") -Raw
$appVersion = "$($project.Project.PropertyGroup.Version | Select-Object -First 1)".Trim()
if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -notmatch '^\d+(\.\d+){1,3}$') {
	throw "Could not determine a numeric release version from DepupCode.csproj (found '$appVersion'). Windows version metadata requires a value such as 2.5.0."
}
$versionParts = @($appVersion.Split('.') | ForEach-Object { [int]$_ })
while ($versionParts.Count -lt 4) { $versionParts += 0 }
$versionInfoVersion = $versionParts[0..3] -join '.'

$releaseDirectory = Join-Path $projectRoot "artifacts\release"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$installerScript = Join-Path $projectRoot "build-installer.iss"

$shouldSign = -not [string]::IsNullOrWhiteSpace($CertificatePath)
if ($shouldSign) {
	if (-not (Test-Path -LiteralPath $CertificatePath)) {
		throw "Publisher certificate not found at $CertificatePath."
	}
	$SignToolPath = Find-SignTool
	if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
		throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
	}
	Write-Host "Signing application payload with $CertificatePath"
	Invoke-CodeSign $publishExe
}

& $InstallerCompilerPath "/DAppVersion=$appVersion" "/DVersionInfoVersion=$versionInfoVersion" "/DPublishDirectory=$publishDirectory" "/DOutputDirectory=$releaseDirectory" "/DProjectRoot=$projectRoot" $installerScript
if ($LASTEXITCODE -ne 0) {
	throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $releaseDirectory "DepupCode-Setup-v$($appVersion)-$Runtime.exe"
if (-not (Test-Path -LiteralPath $installerPath) -or (Get-Item -LiteralPath $installerPath).Length -le 0) {
	throw "Installer was not created or is empty: $installerPath"
}

if ($shouldSign) {
	Write-Host "Signing installer with $CertificatePath"
	Invoke-CodeSign $installerPath
}

$installer = Get-Item -LiteralPath $installerPath
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$hashPath = "$installerPath.sha256"
Set-Content -LiteralPath $hashPath -Value "$hash  $($installer.Name)" -Encoding ASCII

Write-Host "Installer: $installerPath ($([Math]::Round($installer.Length / 1MB, 2)) MB)"
Write-Host "SHA-256:  $hash"
Write-Host "Checksum: $hashPath"
if (-not $shouldSign) {
	Write-Warning "The installer is not Authenticode-signed. Pass -CertificatePath to sign the app and setup executable before public distribution or Microsoft Store submission."
}
