[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory = "artifacts"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repositoryRoot "src\Bdeyes\Bdeyes.csproj"
$projectVersion = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "Could not read the bdeyes project version."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
}
elseif ($Version -ne $projectVersion) {
    throw "Requested version '$Version' does not match project version '$projectVersion'."
}

if (![IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$runtime = "win-x64"
$archiveBaseName = "bdeyes-$Version-$runtime"
$archivePath = Join-Path $OutputDirectory "$archiveBaseName.zip"
$checksumPath = "$archivePath.sha256"
Remove-Item -Force -ErrorAction SilentlyContinue $archivePath, $checksumPath

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) "bdeyes-package-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $stagingRoot $archiveBaseName

function Get-MetadataValue {
    param(
        [System.Xml.XmlNode]$Metadata,
        [string]$Name
    )

    $node = $Metadata.SelectSingleNode("./*[local-name()='$Name']")
    if ($null -eq $node) {
        return ""
    }

    return $node.InnerText.Trim()
}

function Copy-PackageLicenses {
    param(
        [string]$AssetsPath,
        [string]$Destination
    )

    $assets = Get-Content -Raw -LiteralPath $AssetsPath | ConvertFrom-Json
    $packageFolder = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($packageFolder)) {
        throw "NuGet package folder is missing from project.assets.json."
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $inventory = [Collections.Generic.List[string]]::new()
    $inventory.Add("bdeyes $Version third-party package inventory")
    $inventory.Add("Generated from the resolved NuGet metadata used by this build.")
    $inventory.Add("Copied license and notice files are stored beside this inventory.")
    $inventory.Add("")

    $packages = $assets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -eq "package" } |
        Sort-Object Name
    foreach ($package in $packages) {
        $separator = $package.Name.LastIndexOf("/")
        $id = $package.Name.Substring(0, $separator)
        $packageVersion = $package.Name.Substring($separator + 1)
        $packagePath = Join-Path $packageFolder $package.Value.path
        $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter "*.nuspec" -File |
            Select-Object -First 1

        $license = "not declared in nuspec"
        $authors = ""
        $copyright = ""
        $projectUrl = ""
        if ($null -ne $nuspec) {
            [xml]$nuspecXml = Get-Content -Raw -LiteralPath $nuspec.FullName
            $metadata = $nuspecXml.SelectSingleNode("//*[local-name()='metadata']")
            if ($null -ne $metadata) {
                $licenseNode = $metadata.SelectSingleNode("./*[local-name()='license']")
                if ($null -ne $licenseNode) {
                    $licenseType = $licenseNode.Attributes["type"]
                    $license = if ($null -ne $licenseType) {
                        "$($licenseType.Value): $($licenseNode.InnerText.Trim())"
                    }
                    else {
                        $licenseNode.InnerText.Trim()
                    }
                }
                $authors = Get-MetadataValue $metadata "authors"
                $copyright = Get-MetadataValue $metadata "copyright"
                $projectUrl = Get-MetadataValue $metadata "projectUrl"
            }
        }

        $inventory.Add("$id $packageVersion")
        $inventory.Add("  License: $license")
        if (![string]::IsNullOrWhiteSpace($authors)) {
            $inventory.Add("  Authors: $authors")
        }
        if (![string]::IsNullOrWhiteSpace($copyright)) {
            $inventory.Add("  Copyright: $copyright")
        }
        if (![string]::IsNullOrWhiteSpace($projectUrl)) {
            $inventory.Add("  Project: $projectUrl")
        }
        $inventory.Add("")

        $notices = Get-ChildItem -LiteralPath $packagePath -Recurse -File |
            Where-Object { $_.Name -match "(?i)(license|notice|copying)" }
        foreach ($notice in $notices) {
            $relative = $notice.FullName.Substring($packagePath.Length).TrimStart("\", "/")
            $safeName = "$id-$packageVersion-$relative" -replace '[\\/:*?"<>|]', '_'
            Copy-Item -Force -LiteralPath $notice.FullName -Destination (Join-Path $Destination $safeName)
        }
    }

    $downloadDependencies = foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        $property = $framework.Value.PSObject.Properties["downloadDependencies"]
        if ($null -ne $property) {
            $property.Value
        }
    }
    $runtimeDependency = $downloadDependencies |
        Where-Object { $_.name -eq "Microsoft.NETCore.App.Runtime.win-x64" } |
        Select-Object -First 1
    if ($null -eq $runtimeDependency) {
        throw "Resolved .NET Windows runtime pack is missing from project.assets.json."
    }

    $runtimeVersion = $runtimeDependency.version.Trim([char[]]"[]").Split(",")[0]
    $runtimeId = $runtimeDependency.name
    $runtimePath = Join-Path $packageFolder "$($runtimeId.ToLowerInvariant())\$runtimeVersion"
    if (!(Test-Path -LiteralPath $runtimePath -PathType Container)) {
        throw "Resolved .NET runtime pack '$runtimeId $runtimeVersion' is not installed."
    }

    $inventory.Add("$runtimeId $runtimeVersion")
    $inventory.Add("  License: see copied runtime LICENSE and THIRD-PARTY-NOTICES files")
    $inventory.Add("  Project: https://github.com/dotnet/runtime")
    $inventory.Add("")
    $runtimeNotices = Get-ChildItem -LiteralPath $runtimePath -Recurse -File |
        Where-Object { $_.Name -match "(?i)(license|notice|copying)" }
    foreach ($notice in $runtimeNotices) {
        $relative = $notice.FullName.Substring($runtimePath.Length).TrimStart("\", "/")
        $safeName = "$runtimeId-$runtimeVersion-$relative" -replace '[\\/:*?"<>|]', '_'
        Copy-Item -Force -LiteralPath $notice.FullName -Destination (Join-Path $Destination $safeName)
    }

    $inventoryPath = Join-Path $Destination "PACKAGE-LICENSES.txt"
    [IO.File]::WriteAllLines($inventoryPath, $inventory, [Text.UTF8Encoding]::new($false))
}

function New-DeterministicZip {
    param(
        [string]$SourceDirectory,
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $prefixLength = $SourceDirectory.TrimEnd("\", "/").Length + 1
            $files = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File |
                Sort-Object FullName
            foreach ($file in $files) {
                $entryName = $file.FullName.Substring($prefixLength).Replace("\", "/")
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                $fileStream = [IO.File]::OpenRead($file.FullName)
                try {
                    $fileStream.CopyTo($entryStream)
                }
                finally {
                    $fileStream.Dispose()
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
    $publishArguments = @(
        "publish",
        $project,
        "--configuration", "Release",
        "--runtime", $runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "--output", $publishDirectory
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }

    Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -Recurse -File |
        Remove-Item -Force
    $remainingSymbols = @(Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -Recurse -File)
    if ($remainingSymbols.Count -ne 0) {
        throw "Debug symbols remain in the publish directory."
    }

    $executable = Join-Path $publishDirectory "Bdeyes.exe"
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing."
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        !$productVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
        throw "Published product version '$productVersion' does not match '$Version'."
    }

    Copy-Item -Force -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $publishDirectory
    Copy-Item -Force -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $publishDirectory
    Copy-Item -Force -LiteralPath (Join-Path $repositoryRoot "SECURITY.md") -Destination $publishDirectory
    $assetsPath = Join-Path $repositoryRoot "src\Bdeyes\obj\project.assets.json"
    $licensesPath = Join-Path $publishDirectory "licenses"
    Copy-PackageLicenses $assetsPath $licensesPath

    New-DeterministicZip $publishDirectory $archivePath
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$hash *$([IO.Path]::GetFileName($archivePath))`n",
        [Text.ASCIIEncoding]::new())

    [pscustomobject]@{
        Version = $Version
        Runtime = $runtime
        Archive = $archivePath
        Checksum = $checksumPath
        Sha256 = $hash
        Bytes = (Get-Item -LiteralPath $archivePath).Length
    }
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stagingRoot
}
