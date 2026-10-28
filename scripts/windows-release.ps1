# Shared Windows release identity; dot-sourced by build-windows.ps1 and package-windows.ps1.
# The source record matches scripts/product.py (same file set, ordinal order, NUL separators).
# Built from code points so Windows PowerShell 5.1 parses this BOM-less file the same in any ANSI code page.
$WindowsGuideName = "Windows" + (-join ([char[]](0x8bd5, 0x7528, 0x6307, 0x5357))) + ".md"

function Get-ProductInfo([string]$ProjectRoot) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $ProjectRoot "resources/Product.props")
    $group = $props.Project.PropertyGroup
    [pscustomobject]@{ Name = $group.ProductName; Version = $group.Version; Build = $group.ReleaseBuild }
}

function Get-WindowsSourceDigest([string]$ProjectRoot) {
    $sourceFiles = @("resources/Product.props", "resources/icons/AppIcon.ico", "scripts/product.py", "windows/Directory.Build.props",
        "scripts/build-windows.sh", "scripts/build-windows.ps1", "scripts/windows-release.ps1")
    foreach ($folder in @("Bridge.Core", "Bridge.Windows")) {
        $sourceFiles += Get-ChildItem -LiteralPath "$ProjectRoot/windows/$folder" -File | ForEach-Object { "windows/$folder/$($_.Name)" }
    }
    $sourceFiles += Get-ChildItem -LiteralPath "$ProjectRoot/resources/windows" -Filter "*.txt" -File | ForEach-Object { "resources/windows/$($_.Name)" }
    [string[]]$sourceFiles = $sourceFiles | Select-Object -Unique
    [Array]::Sort($sourceFiles, [StringComparer]::Ordinal)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($relativePath in $sourceFiles) {
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relativePath))
            $hash.AppendData([byte[]]@(0))
            $hash.AppendData([IO.File]::ReadAllBytes((Join-Path $ProjectRoot $relativePath)))
            $hash.AppendData([byte[]]@(0))
        }
        [BitConverter]::ToString($hash.GetHashAndReset()).Replace("-", "").ToLowerInvariant()
    } finally { $hash.Dispose() }
}
