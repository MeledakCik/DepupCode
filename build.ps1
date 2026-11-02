param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Runtime = $Runtime.Trim()
if ($Runtime -ne "win-x64") {
    throw "The supported public release target is win-x64; received '$Runtime'."
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    # Self-contained deployment needs RID-specific runtime packs. Restore them before
    # building so publish can use --no-restore and cannot silently fall back to FDD.
    # NuGet reuses packages already in the global cache; if a package is absent,
    # restore still fails when all configured sources are unavailable.
    dotnet restore .\DepupCode\DepupCode.csproj -r $Runtime -p:SelfContained=true -p:NuGetAudit=false --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "Could not restore self-contained runtime packs for $Runtime. Check NuGet access, then rerun .\build.ps1." }

    # Build the shipping application project directly. The test project runs in the
    # desktop smoke workflow and must not gate creation of the application payload.
    dotnet build .\DepupCode\DepupCode.csproj -c Release -r $Runtime --self-contained --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    $publishDirectory = Join-Path $projectRoot "DepupCode\bin\Release\net8.0-windows\$Runtime\publish"
    $resolvedPublishParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $publishDirectory))
    $resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
    if (-not $resolvedPublishParent.StartsWith($resolvedProjectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a publish directory outside the project: $resolvedPublishParent"
    }
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    dotnet publish .\DepupCode\DepupCode.csproj -c Release -r $Runtime --self-contained --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $requiredFiles = @(
        "DepupCode.exe",
        "coreclr.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll",
        "PresentationFramework.dll",
        "Assets\Terminal\index.html",
        "Assets\Terminal\terminal.js",
        "Assets\Terminal\vendor\addon-image.mjs",
        "runtimes\$Runtime\native\copilot.exe",
        "runtimes\$Runtime\native\runtime.node"
    )
    $missingFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_)) })
    if ($missingFiles.Count -gt 0) {
        throw "Self-contained publish is missing runtime files: $($missingFiles -join ', '). No release package was verified."
    }

    $runtimeConfigPath = Join-Path $publishDirectory "DepupCode.runtimeconfig.json"
    if (-not (Test-Path -LiteralPath $runtimeConfigPath)) {
        throw "Self-contained publish is missing DepupCode.runtimeconfig.json. No release package was verified."
    }
    $runtimeOptions = (Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions
    if ($runtimeOptions.framework -or $runtimeOptions.frameworks) {
        throw "Publish output still requires a shared .NET framework. No release package was verified."
    }

    Write-Host "Verified self-contained Depup Code at $publishDirectory"
}
finally {
    Pop-Location
}
