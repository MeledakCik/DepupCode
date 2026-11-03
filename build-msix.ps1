param(
	[Parameter(Mandatory = $true)]
	[string]$IdentityName,
	[Parameter(Mandatory = $true)]
	[string]$Publisher,
	[string]$PublisherDisplayName = "Depup",
	[string]$Version = "",
	[string]$Runtime = "win-x64",
	[string]$MakeAppxPath = "",
	[string]$SignToolPath = "",
	[string]$CertificatePath = "",
	[string]$CertificatePassword = "",
	[string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Runtime = $Runtime.Trim()
if ($Runtime -ne "win-x64") {
	throw "The supported Store target is win-x64; received '$Runtime'."
}

if ($IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,49}$') {
	throw "IdentityName '$IdentityName' is not a valid Store identity name. Copy the exact value from Partner Center."
}
if ([string]::IsNullOrWhiteSpace($Publisher)) {
	throw "Publisher is required. Copy the exact Publisher value from Partner Center."
}

function Find-WindowsSdkTool([string]$ToolName) {
	$command = Get-Command $ToolName -ErrorAction SilentlyContinue
	if ($command) { return $command.Source }

	$kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
	if (Test-Path -LiteralPath $kitsRoot) {
		$candidates = Get-ChildItem -LiteralPath $kitsRoot -Directory |
			Sort-Object Name -Descending |
			ForEach-Object { Join-Path $_.FullName "x64\$ToolName" }
		foreach ($candidate in $candidates) {
			if (Test-Path -LiteralPath $candidate) { return $candidate }
		}
	}
	return ""
}

function New-PngAsset([string]$Source, [string]$Destination, [int]$Width, [int]$Height, [bool]$Transparent = $false) {
	Add-Type -AssemblyName System.Drawing
	$image = [System.Drawing.Image]::FromFile($Source)
	try {
		$bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
		try {
			$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
			try {
				$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
				$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
				$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
				if ($Transparent) {
					$graphics.Clear([System.Drawing.Color]::Transparent)
				}
				else {
					$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml("#0C0A17"))
				}
				$scale = [Math]::Min(($Width * 0.72) / $image.Width, ($Height * 0.72) / $image.Height)
				$drawWidth = [int][Math]::Round($image.Width * $scale)
				$drawHeight = [int][Math]::Round($image.Height * $scale)
				$x = [int](($Width - $drawWidth) / 2)
				$y = [int](($Height - $drawHeight) / 2)
				$graphics.DrawImage($image, $x, $y, $drawWidth, $drawHeight)
				$bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
			}
			finally { $graphics.Dispose() }
		}
		finally { $bitmap.Dispose() }
	}
	finally { $image.Dispose() }
}

if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
	$MakeAppxPath = Find-WindowsSdkTool "makeappx.exe"
}
if ([string]::IsNullOrWhiteSpace($MakeAppxPath) -or -not (Test-Path -LiteralPath $MakeAppxPath)) {
	throw "makeappx.exe was not found. Install the Windows SDK or pass -MakeAppxPath."
}

$publishDirectory = Join-Path $projectRoot "DepupCode\bin\Release\net8.0-windows\$Runtime\publish"
$publishExe = Join-Path $publishDirectory "DepupCode.exe"
if (-not (Test-Path -LiteralPath $publishExe)) {
	throw "Release publish output not found at $publishDirectory. Run .\build.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
	[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot "DepupCode\DepupCode.csproj") -Raw
	$Version = "$($project.Project.PropertyGroup.Version | Select-Object -First 1)".Trim()
}
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^\d+(\.\d+){1,3}$') {
	throw "Package version '$Version' is not numeric. MSIX requires a value such as 2.5.0.0."
}
$versionParts = @($Version.Split('.') | ForEach-Object { [int]$_ })
while ($versionParts.Count -lt 4) { $versionParts += 0 }
$packageVersion = ($versionParts[0..3] -join '.')

$manifestTemplate = Join-Path $projectRoot "Packaging\Store\AppxManifest.xml"
if (-not (Test-Path -LiteralPath $manifestTemplate)) {
	throw "Store manifest template is missing: $manifestTemplate"
}

$releaseDirectory = Join-Path $projectRoot "artifacts\release"
$stagingRoot = Join-Path $projectRoot "artifacts\msix"
$stage = Join-Path $stagingRoot "staging"
$assetsStage = Join-Path $stage "Assets"
$appStage = Join-Path $stage "DepupCode"
$packagePath = Join-Path $releaseDirectory "DepupCode-v$packageVersion-$Runtime.msix"

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
if (Test-Path -LiteralPath $stagingRoot) {
	Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $assetsStage -Force | Out-Null
New-Item -ItemType Directory -Path $appStage -Force | Out-Null

try {
	Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $appStage -Recurse -Force

	$iconSource = Join-Path $projectRoot "DepupCode\Assets\logo.png"
	if (-not (Test-Path -LiteralPath $iconSource)) { $iconSource = Join-Path $projectRoot "DepupCode\Assets\icon-128.png" }
	if (-not (Test-Path -LiteralPath $iconSource)) { throw "No source logo found for Store assets." }

	New-PngAsset $iconSource (Join-Path $assetsStage "StoreLogo.png") 50 50
	New-PngAsset $iconSource (Join-Path $assetsStage "Square44x44Logo.png") 44 44 $true
	New-PngAsset $iconSource (Join-Path $assetsStage "Square150x150Logo.png") 150 150
	New-PngAsset $iconSource (Join-Path $assetsStage "LargeTile.png") 310 310
	New-PngAsset $iconSource (Join-Path $assetsStage "Wide310x150Logo.png") 310 150
	New-PngAsset $iconSource (Join-Path $assetsStage "SplashScreen.png") 620 300

	$manifest = Get-Content -LiteralPath $manifestTemplate -Raw
	$escape = { param($value) [System.Security.SecurityElement]::Escape($value) }
	$manifest = $manifest.Replace("__IDENTITY_NAME__", (& $escape $IdentityName))
	$manifest = $manifest.Replace("__PUBLISHER__", (& $escape $Publisher))
	$manifest = $manifest.Replace("__PUBLISHER_DISPLAY_NAME__", (& $escape $PublisherDisplayName))
	$manifest = $manifest.Replace("__PACKAGE_VERSION__", $packageVersion)
	# Validate the substituted manifest before handing it to makeappx.
	[xml]$manifestXml = $manifest
	Set-Content -LiteralPath (Join-Path $stage "AppxManifest.xml") -Value $manifest -Encoding UTF8

	Write-Host "Packing MSIX $packageVersion ..."
	& $MakeAppxPath pack /d $stage /p $packagePath /o
	if ($LASTEXITCODE -ne 0) {
		throw "makeappx failed with exit code $LASTEXITCODE."
	}
	if (-not (Test-Path -LiteralPath $packagePath) -or (Get-Item -LiteralPath $packagePath).Length -le 0) {
		throw "MSIX package was not created or is empty: $packagePath"
	}

	if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
		if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Package certificate not found at $CertificatePath." }
		if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
			$SignToolPath = Find-WindowsSdkTool "signtool.exe"
		}
		if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath)) {
			throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
		}
		$signArguments = @("sign", "/fd", "SHA256", "/f", $CertificatePath, "/tr", $TimestampUrl, "/td", "SHA256")
		if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) { $signArguments += @("/p", $CertificatePassword) }
		$signArguments += $packagePath
		& $SignToolPath @signArguments
		if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
	}

	$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
	$hashPath = "$packagePath.sha256"
	Set-Content -LiteralPath $hashPath -Value "$hash  $(Split-Path -Leaf $packagePath)" -Encoding ASCII

	Write-Host "MSIX:     $packagePath ($([Math]::Round((Get-Item -LiteralPath $packagePath).Length / 1MB, 2)) MB)"
	Write-Host "SHA-256:  $hash"
	if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
		Write-Warning "The MSIX package is unsigned. Sign it with a trusted certificate for local installation testing; Partner Center can sign Store submissions."
	}
}
finally {
	if (Test-Path -LiteralPath $stagingRoot) {
		Remove-Item -LiteralPath $stagingRoot -Recurse -Force
	}
}
