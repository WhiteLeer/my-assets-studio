$dotnet = 'D:\Unpack_Workspace\Tools\dotnet\8\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Required .NET 8 SDK was not found: $dotnet"
}

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE"
    }
}

# prepare patcher
Invoke-Checked { & $dotnet build AnimeStudio.Patcher -c Release -f net8.0 } 'Patcher build'
$patcher = "AnimeStudio.Patcher\bin\Release\net8.0\AnimeStudio.Patcher.exe"

$tfm = 'net8.0-windows'
$outputDir = ".\dist\$tfm"
$configuration = 'Release'

# prepare paths
$guiOut = "AnimeStudio.GUI/bin/$configuration/$tfm"
$cliOut = "AnimeStudio.CLI/bin/$configuration/$tfm"

$guiExe = "$guiOut/AnimeStudio.GUI.exe"
$cliExe = "$cliOut/AnimeStudio.CLI.exe"

# build cli and gui & patch them
Invoke-Checked { & $dotnet build AnimeStudio.CLI -c $configuration -f $tfm } 'CLI build'
Invoke-Checked { & $patcher $cliExe -d bin } 'CLI patch'
Invoke-Checked { & $dotnet build AnimeStudio.GUI -c $configuration -f $tfm } 'GUI build'
Invoke-Checked { & $patcher $guiExe -d bin } 'GUI patch'

# prepare output dir
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory $outputDir
New-Item -ItemType Directory "$outputDir/bin"

# copy to output
Copy-Item "$cliOut/*" "$outputDir/bin" -Recurse
Copy-Item "$guiOut/*" "$outputDir/bin" -Recurse -Force

# move files out
foreach ($exe in 'AnimeStudio.GUI.exe', 'AnimeStudio.CLI.exe') {
    Move-Item "$outputDir/bin/$exe" $outputDir
}
Move-Item "$outputDir/bin/LICENSE" $outputDir
