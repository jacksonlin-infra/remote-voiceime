param(
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64",
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) { & "$PSScriptRoot/build-windows.ps1" -Runtime $Runtime }
. "$PSScriptRoot/windows-release.ps1"
$product = Get-ProductInfo $projectRoot
$folderName = "$($product.Name)-$($product.Version)-$Runtime"
# Release assets carry no space (GitHub would rename it); the ZIP folder keeps the product name.
$releaseName = "$($product.Name.Replace(' ', ''))-$($product.Version)-$Runtime"
$folder = Join-Path $projectRoot "dist/$folderName"
$exeName = "$($product.Name).exe"
$required = @($exeName, "build-info.json", $WindowsGuideName, "DOTNET-LICENSE.txt",
    "DOTNET-THIRD-PARTY-NOTICES.txt", "WINDOWS-DESKTOP-LICENSE.txt")
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $folder $name) -PathType Leaf)) { throw "Missing $name in $folder" }
}
$exe = Join-Path $folder $exeName
# Refuse to wrap an old build: the record must match current sources, metadata and the EXE bytes.
$record = Get-Content -LiteralPath (Join-Path $folder "build-info.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$exeDigest = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($record.product -ne $product.Name -or $record.version -ne $product.Version -or $record.build -ne $product.Build -or
    $record.platform -ne "windows" -or $record.source_sha256 -ne (Get-WindowsSourceDigest $projectRoot) -or $record.binary_sha256 -ne $exeDigest) {
    throw "build-info.json does not match current sources, version or EXE; rebuild before packaging."
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($info.FileVersion -ne "$($product.Version).$($product.Build)" -or $info.ProductName -ne $product.Name) {
    throw "Executable version or product name does not match $($product.Name) $($product.Version).$($product.Build)"
}
$bytes = [IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
$machine = if ($Runtime -eq "win-x64") { 0x8664 } else { 0xaa64 }
if ($bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a -or
    [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes, $pe + 4) -ne $machine -or
    [BitConverter]::ToUInt16($bytes, $pe + 24 + 68) -ne 2) { throw "Invalid PE architecture or GUI subsystem" }
$bundle = [Text.Encoding]::UTF8.GetString($bytes)
foreach ($member in @("System.Private.CoreLib.dll", "System.Windows.Forms.dll", "$($product.Name).dll", "Bridge.Core.dll")) {
    if (!$bundle.Contains($member)) { throw "Missing self-contained bundle member $member" }
}
if ($bundle -notmatch '"includedFrameworks"\s*:\s*(\[[^\]]+\])') { throw "Missing bundled framework configuration" }
# Windows PowerShell 5.1 emits a JSON array as one object; enumerate it explicitly.
$parsedFrameworks = $Matches[1] | ConvertFrom-Json
$frameworks = @($parsedFrameworks | ForEach-Object { $_ })
[xml]$project = Get-Content -LiteralPath "$projectRoot/windows/Bridge.Windows/Bridge.Windows.csproj"
$runtimeVersion = $project.Project.PropertyGroup.RuntimeFrameworkVersion
if ($frameworks.Count -ne 2 -or
    @($frameworks | Where-Object { $_.version -ne $runtimeVersion }).Count -ne 0 -or
    @($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -ne 1 -or
    @($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).Count -ne 1) {
    throw "Unexpected bundled frameworks; expected .NET / WindowsDesktop $runtimeVersion"
}
# Hash every decompressed entry against the source; also catches damaged ZIP data.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $projectRoot "dist/$releaseName.zip"
$temporary = "$zipPath.tmp"
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $required) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
                (Join-Path $folder $name), "$folderName/$name", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        if ($archive.Entries.Count -ne $required.Count) { throw "Unexpected ZIP entries" }
        foreach ($name in $required) {
            $entry = $archive.GetEntry("$folderName/$name")
            if ($null -eq $entry) { throw "Missing ZIP entry $name" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace("-", "") }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($digest -ne (Get-FileHash -LiteralPath (Join-Path $folder $name) -Algorithm SHA256).Hash) {
                throw "ZIP round-trip mismatch: $name"
            }
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $zipPath -Force
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
# Per-user installer (NSIS 3): same verified files, no administrator rights.
$makensis = if ($env:NSIS_BIN) { $env:NSIS_BIN } else { Join-Path ${env:ProgramFiles(x86)} "NSIS/makensis.exe" }
if (!(Test-Path -LiteralPath $makensis -PathType Leaf)) { throw "Install NSIS 3 or set NSIS_BIN to makensis.exe." }
$setupPath = Join-Path $projectRoot "dist/$releaseName-setup.exe"
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath }
& $makensis /V2 /INPUTCHARSET UTF8 "/DPRODUCT_NAME=$($product.Name)" "/DVERSION=$($product.Version)" "/DBUILD=$($product.Build)" `
    "/DSOURCE_DIR=$folder" "/DOUTFILE=$setupPath" "/DICON=$(Join-Path $projectRoot 'resources/icons/AppIcon.ico')" "$PSScriptRoot/windows-installer.nsi"
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "Installer build failed" }
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
if ($setupInfo.ProductVersion -ne "$($product.Version).$($product.Build)" -or $setupInfo.ProductName -ne $product.Name) {
    throw "Installer version or product name does not match $($product.Name) $($product.Version).$($product.Build)"
}
$sums = @(
    "$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())  $releaseName.zip",
    "$((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant())  $releaseName-setup.exe",
    "$exeDigest  $folderName/$exeName"
)
$sums | Set-Content -LiteralPath "$projectRoot/dist/SHA256SUMS.windows-$Runtime.txt" -Encoding ascii
Write-Host "Packaged and verified $zipPath and $setupPath"
$sums | Write-Host
