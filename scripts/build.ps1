# SPDX-License-Identifier: GPL-3.0-only
param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [string]$Destination,
    [ValidateSet('', 'moddb')][string]$PackageSuffix = ''
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$GamePath=(Resolve-Path -LiteralPath $GamePath).Path
if(-not $Destination){$Destination=Join-Path $repoRoot 'artifacts'}
$Destination=[IO.Path]::GetFullPath($Destination)
$dotnetCmd=Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe=if($dotnetCmd){$dotnetCmd.Source}else{'C:\Program Files\dotnet\dotnet.exe'}
Push-Location $repoRoot
try {
    $changes=git status --porcelain
    if($LASTEXITCODE -ne 0 -or $changes){throw 'Commit the source before creating a matching binary/source release.'}
    $commit=git rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Cannot determine source revision'}
    $project=Join-Path $repoRoot 'Vintage_DGlab\CoyoteMod.csproj'
    $info=Get-Content (Join-Path $repoRoot 'Vintage_DGlab\modinfo.json') -Raw | ConvertFrom-Json
    [xml]$projectXml=Get-Content $project -Raw
    $gameVersion=[string]$projectXml.Project.PropertyGroup.SupportedGameVersion
    $gameVersionMax=[string]$projectXml.Project.PropertyGroup.SupportedGameVersionMax
    if(-not $gameVersionMax){$gameVersionMax=$gameVersion}
    if([version]$gameVersionMax -lt [version]$gameVersion){throw 'Supported game range is reversed'}
    $gameRange=if($gameVersionMax -eq $gameVersion){$gameVersion}else{"$gameVersion-$gameVersionMax"}
    if($info.dependencies.game -ne $gameVersion){throw 'Project and modinfo target different game versions'}
    if($info.version -ne [string]$projectXml.Project.PropertyGroup.Version){throw 'Assembly and modinfo versions differ'}
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $stage=Join-Path $Destination ('stage-'+[guid]::NewGuid().ToString('N'))
    $compiled=Join-Path $stage 'compiled'
    $package=Join-Path $stage 'package'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    & $dotnetExe restore $project --locked-mode
    if($LASTEXITCODE -ne 0){throw 'Locked package restore failed'}
    & $dotnetExe run --project (Join-Path $repoRoot 'tests\Tests.csproj')
    if($LASTEXITCODE -ne 0){throw 'Tests failed'}
    & $dotnetExe build $project -c Release --no-restore "-p:GamePath=$GamePath" "-p:OutputPath=$compiled"
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
    $dlls=@(Get-ChildItem -LiteralPath $compiled -Filter '*.dll' -File | Select-Object -ExpandProperty Name | Sort-Object)
    if(($dlls -join ',') -ne 'QRCoder.dll,VintageDLab.dll'){throw "Unexpected DLLs in release: $dlls"}
    foreach($file in @('VintageDLab.dll','QRCoder.dll','modinfo.json')){Copy-Item -LiteralPath (Join-Path $compiled $file) -Destination $package}
    foreach($file in @('LICENSE','README.md','THIRD_PARTY_NOTICES.md','VALIDATION.md')){Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $package}
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES') -Destination $package -Recurse
    $sourceUrl="https://github.com/Ghostknife11/Vintage-Dglab/tree/$commit"
    @("Vintage-DGLab $($info.version), supported game range $gameRange", "Source commit: $commit", "Source: $sourceUrl", "Built against real game API: $gameVersion",'See VALIDATION.md for tested versions and compatibility assumptions.','The matching -source.zip is distributed beside this mod ZIP.','Build instructions, dependency versions and licenses are included in the source archive.') | Set-Content -LiteralPath (Join-Path $package 'SOURCE.txt') -Encoding utf8
    $name="Vintage-DGLab-$($info.version)-vs$gameRange"
    if($PackageSuffix){$name+="-$PackageSuffix"}
    $binary=Join-Path $Destination "$name.zip"
    $source=Join-Path $Destination "$name-source.zip"
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $binary -Force
    $ownSource=Join-Path $stage 'own-source.zip'
    git archive --format=zip --prefix=Vintage-DGLab-source/ -o $ownSource $commit
    if($LASTEXITCODE -ne 0){throw 'Source archive failed'}
    $dependencySource=Join-Path $stage 'dependency-source'
    $dependencyZip=Join-Path $Destination 'QRCoder-bd980577-source.zip'
    $dependencySha='a932985b0aedda8fe999e93733696f3709de80a6a1afde487a581a03d1dbeedd'
    if(-not(Test-Path -LiteralPath $dependencyZip)){
        Invoke-WebRequest -Uri 'https://codeload.github.com/codebude/QRCoder/zip/bd980577640c47f8bb881cf24c8443415a579d36' -OutFile $dependencyZip
    }
    if((Get-FileHash -LiteralPath $dependencyZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dependencySha){throw 'QRCoder source checksum mismatch'}
    $sourceStage=Join-Path $stage 'source'
    Expand-Archive -LiteralPath $ownSource -DestinationPath $sourceStage
    Expand-Archive -LiteralPath $dependencyZip -DestinationPath $dependencySource
    $sourceRoot=Join-Path $sourceStage 'Vintage-DGLab-source'
    $thirdParty=Join-Path $sourceRoot 'third_party'
    New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $dependencySource 'QRCoder-bd980577640c47f8bb881cf24c8443415a579d36') -Destination (Join-Path $thirdParty 'QRCoder') -Recurse
    Compress-Archive -LiteralPath $sourceRoot -DestinationPath $source -Force
    $hashLines=foreach($path in @($binary,$source)){
        $hash=Get-FileHash -LiteralPath $path -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($path))"
    }
    $hashLines | Set-Content -LiteralPath (Join-Path $Destination "$name-SHA256SUMS.txt") -Encoding utf8
    Write-Output "Created $binary"
    Write-Output "Corresponding source: $source"
} finally {Pop-Location}
