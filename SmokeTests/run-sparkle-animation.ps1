param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$AssetMapPath,

    [Parameter(Mandatory = $true)]
    [string]$CabMapPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$DotnetPath = "dotnet",
    [string]$CliPath = (Join-Path $PSScriptRoot "..\AnimeStudio.CLI\bin\Release\net8.0-windows\AnimeStudio.CLI.dll"),
    [string]$NamesPath = (Join-Path $PSScriptRoot "sparkle-animation-names.txt")
)

$requiredFiles = @($AssetMapPath, $CabMapPath, $CliPath, $NamesPath)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file does not exist: $requiredFile"
    }
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Source folder does not exist: $SourcePath"
}

$arguments = @(
    $CliPath,
    $SourcePath,
    $OutputPath,
    "--game", "SR",
    "--map_op", "AllMaps",
    "--map_type", "MessagePack",
    "--asset_map", $AssetMapPath,
    "--cab_map", $CabMapPath,
    "--batch_load",
    "--reverse_dependencies",
    "--embed_animations",
    "--group_assets", "ByModel",
    "--types", "GameObject", "Animator", "AnimationClip",
    "--names", $NamesPath
)

$elapsed = Measure-Command { & $DotnetPath @arguments }
if ($LASTEXITCODE -ne 0) {
    throw "AnimeStudio.CLI exited with code $LASTEXITCODE"
}

$animationFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.anim" -File)
$fbxFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.fbx" -File)
$jsonFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.json" -File)
$unknownPaths = 0
$resolvedPaths = 0
$characterUnknownPaths = 0
$auxiliaryUnknownPaths = 0

foreach ($animationFile in $animationFiles) {
    $paths = @(Select-String -LiteralPath $animationFile.FullName -Pattern "^    path: ")
    $unknown = @($paths | Where-Object { $_.Line -match "^    path: path_[0-9]+$" }).Count
    $unknownPaths += $unknown
    $resolvedPaths += $paths.Count - $unknown
    if ($animationFile.Name -match "_Effect|_Camera") {
        $auxiliaryUnknownPaths += $unknown
    }
    else {
        $characterUnknownPaths += $unknown
    }
}

[pscustomobject]@{
    ElapsedSeconds = [Math]::Round($elapsed.TotalSeconds, 2)
    AnimationFiles = $animationFiles.Count
    FbxFiles = $fbxFiles.Count
    JsonFiles = $jsonFiles.Count
    ResolvedPaths = $resolvedPaths
    UnknownPaths = $unknownPaths
    CharacterUnknownPaths = $characterUnknownPaths
    AuxiliaryUnknownPaths = $auxiliaryUnknownPaths
}

if ($animationFiles.Count -eq 0 -or $fbxFiles.Count -eq 0 -or $jsonFiles.Count -ne 0 -or $characterUnknownPaths -ne 0) {
    throw "Sparkle animation smoke test failed."
}
