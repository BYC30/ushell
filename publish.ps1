param(
    [string]$Destination = "E:\MLBB_ECS\Packages\com.ushell"
)

$ErrorActionPreference = "Stop"

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$excludedDirectories = @(
    ".git",
    ".vs",
    "bin",
    "obj",
    "Library",
    "Temp"
)
$sourceFiles = @{}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    $baseUri = New-Object System.Uri(($BasePath.TrimEnd('\') + '\'))
    $fullUri = New-Object System.Uri($FullPath)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace('/', '\')
}

function ShouldSkipPath {
    param(
        [string]$RelativePath
    )

    foreach ($name in $excludedDirectories) {
        if ($RelativePath -eq $name -or $RelativePath.StartsWith($name + "\")) {
            return $true
        }
    }

    return $false
}

if (!(Test-Path -LiteralPath $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$files = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File
foreach ($file in $files) {
    $relativePath = Get-RelativePath -BasePath $sourceRoot -FullPath $file.FullName
    if (ShouldSkipPath -RelativePath $relativePath) {
        continue
    }

    $sourceFiles[$relativePath] = $true
    $destinationPath = Join-Path $Destination $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    if (!(Test-Path -LiteralPath $destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
}

$destinationFiles = Get-ChildItem -LiteralPath $Destination -Recurse -File -ErrorAction SilentlyContinue
foreach ($file in $destinationFiles) {
    $relativePath = Get-RelativePath -BasePath $Destination -FullPath $file.FullName
    if (ShouldSkipPath -RelativePath $relativePath) {
        continue
    }

    if ($file.Extension -eq ".meta") {
        continue
    }

    if (-not $sourceFiles.ContainsKey($relativePath)) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
}

$destinationDirectories = Get-ChildItem -LiteralPath $Destination -Recurse -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending
foreach ($directory in $destinationDirectories) {
    $relativePath = Get-RelativePath -BasePath $Destination -FullPath $directory.FullName
    if (ShouldSkipPath -RelativePath $relativePath) {
        continue
    }

    $remainingItems = Get-ChildItem -LiteralPath $directory.FullName -Force
    if ($remainingItems.Count -eq 0) {
        Remove-Item -LiteralPath $directory.FullName -Force
    }
}

Write-Host "Published package from $sourceRoot to $Destination"
