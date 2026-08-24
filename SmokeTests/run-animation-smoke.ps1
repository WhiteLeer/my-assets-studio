param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$AssetMapPath,

    [Parameter(Mandatory = $true)]
    [string]$CabMapPath,

    [Parameter(Mandatory = $true)]
    [string]$AnimationMapPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$CliPath,

    [Parameter(Mandatory = $true)]
    [string]$NamesPath,

    [string]$TypeTreeDumpPath = "",

    [string[]]$RequiredActionFolders = @("Run", "Turn", "Walk")
)

$requiredFiles = @($AssetMapPath, $CabMapPath, $AnimationMapPath, $CliPath, $NamesPath)
if (-not [string]::IsNullOrWhiteSpace($TypeTreeDumpPath)) {
    $requiredFiles += $TypeTreeDumpPath
}
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file does not exist: $requiredFile"
    }
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Source folder does not exist: $SourcePath"
}

$arguments = @(
    $SourcePath,
    $OutputPath,
    "--game", "SR",
    "--map_op", "AllMaps",
    "--map_type", "JSON",
    "--asset_map", $AssetMapPath,
    "--animation_map", $AnimationMapPath,
    "--cab_map", $CabMapPath,
    "--batch_load",
    "--group_assets", "ByModel",
    "--types", "GameObject", "Animator", "AnimationClip",
    "--names", $NamesPath
)

if (-not [string]::IsNullOrWhiteSpace($TypeTreeDumpPath)) {
    $arguments += "--type_tree_dump"
    $arguments += $TypeTreeDumpPath
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& $CliPath @arguments
$stopwatch.Stop()
if ($LASTEXITCODE -ne 0) {
    throw "AnimeStudio.CLI exited with code $LASTEXITCODE"
}

$animationFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.anim" -File)
$fbxFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.fbx" -File)
$jsonFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.json" -File)
$modelsPath = Join-Path $OutputPath "Models"
$animationsPath = Join-Path $OutputPath "Animations"
$validationReportPath = Join-Path $OutputPath "animation_extraction_report.md"
$sharedBodyAnimations = @($animationFiles | Where-Object { $_.Name -like "Avatar_Girl_*.anim" })
$characterAnimationFiles = @($animationFiles | Where-Object { $_.Name -notmatch "_Effect|_Camera" })
$missingActionFolders = @($RequiredActionFolders | Where-Object {
    $action = $_
    -not ($characterAnimationFiles | Where-Object { $_.Directory.Name -ieq $action })
})
$mergedBodyRootCurves = 0
foreach ($animationFile in $characterAnimationFiles) {
    if ($null -ne (Select-String -LiteralPath $animationFile.FullName -Pattern "^    path: Main/Root_M$" | Select-Object -First 1)) {
        $mergedBodyRootCurves++
    }
}
$failedAnimations = if (Test-Path -LiteralPath $validationReportPath) {
    @(Select-String -LiteralPath $validationReportPath -Pattern "\| Failed \|").Count
} else {
    -1
}
$mergedAnimations = if (Test-Path -LiteralPath $validationReportPath) {
    @(Select-String -LiteralPath $validationReportPath -Pattern "\| Merged \|").Count
} else {
    0
}
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
    ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
    AnimationFiles = $animationFiles.Count
    FbxFiles = $fbxFiles.Count
    JsonFiles = $jsonFiles.Count
    SharedBodyAnimations = $sharedBodyAnimations.Count
    MissingActionFolders = $missingActionFolders.Count
    MergedBodyRootCurves = $mergedBodyRootCurves
    FailedAnimations = $failedAnimations
    MergedAnimations = $mergedAnimations
    ResolvedPaths = $resolvedPaths
    UnknownPaths = $unknownPaths
    CharacterUnknownPaths = $characterUnknownPaths
    AuxiliaryUnknownPaths = $auxiliaryUnknownPaths
}

if ($animationFiles.Count -eq 0 -or $fbxFiles.Count -eq 0 -or $jsonFiles.Count -ne 0 -or
    -not (Test-Path -LiteralPath $modelsPath -PathType Container) -or
    -not (Test-Path -LiteralPath $animationsPath -PathType Container) -or
    -not (Test-Path -LiteralPath $validationReportPath -PathType Leaf) -or
    $sharedBodyAnimations.Count -ne 0 -or $missingActionFolders.Count -ne 0 -or
    $mergedBodyRootCurves -eq 0 -or $failedAnimations -ne 0 -or
    $mergedAnimations -eq 0 -or $characterUnknownPaths -ne 0) {
    throw "Animation smoke test failed."
}
