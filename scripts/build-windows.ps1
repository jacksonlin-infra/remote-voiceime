param([ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $projectRoot ".build/dotnet-home" }
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $projectRoot ".build/nuget" }
$dotnet = if ($env:DOTNET_BIN) { $env:DOTNET_BIN } else { "dotnet" }
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw "Install .NET 10 SDK or set DOTNET_BIN to its executable (selected: $sdkVersion)."
}
. "$PSScriptRoot/windows-release.ps1"
$product = Get-ProductInfo $projectRoot
$productName = $product.Name
$version = $product.Version
$outputPath = Join-Path $projectRoot "dist/$productName-$version-$Runtime"
& $dotnet run --project "$projectRoot/windows/Bridge.Core.Tests/Bridge.Core.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Windows core checks failed" }
& $dotnet publish "$projectRoot/windows/Bridge.Windows/Bridge.Windows.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
Copy-Item -LiteralPath (Join-Path $projectRoot "docs/$WindowsGuideName") -Destination (Join-Path $outputPath $WindowsGuideName)
$sourceDigest = Get-WindowsSourceDigest $projectRoot
@{
    product = $productName; version = $version; build = $product.Build
    platform = "windows"; source_sha256 = $sourceDigest
    binary_sha256 = (Get-FileHash "$outputPath/$productName.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content "$outputPath/build-info.json" -Encoding UTF8
Write-Host "Built $outputPath/$productName.exe"
