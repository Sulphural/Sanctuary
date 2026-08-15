#Requires -Version 7.0

param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$AssetRoot = 'E:\UnrealFromForgelight_WorkFull\UnpackedSource'
)

$ErrorActionPreference = 'Stop'

$resourceRoot = Join-Path $SourceRoot 'Resources'
$definitionPath = Join-Path $resourceRoot 'ClientItemDefinitions.json'
$coinStorePath = Join-Path $resourceRoot 'CoinStoreItems.json'
$storeBundlePath = Join-Path $resourceRoot 'StoreBundles.json'
$placementPath = Join-Path $resourceRoot 'HousingPlacementData.txt'
$modelPath = Join-Path $resourceRoot 'Models.txt'
$generatorPath = Join-Path $SourceRoot 'Sanctuary.Game\Resources\HousingItemDefinitionGenerator.cs'

$requiredPaths = @(
    $definitionPath,
    $coinStorePath,
    $storeBundlePath,
    $placementPath,
    $modelPath,
    $generatorPath,
    $AssetRoot
)

foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required housing audit path does not exist: $path"
    }
}

$definitions = [System.Collections.Generic.List[object]]::new()
foreach ($entry in (Get-Content -Raw -LiteralPath $definitionPath | ConvertFrom-Json)) {
    [void]$definitions.Add($entry)
}
$definitionsById = @{}
$definitionsByComment = @{}

foreach ($definition in $definitions) {
    $id = [int]$definition.Id
    if (-not $definitionsById.ContainsKey($id)) {
        $definitionsById[$id] = $definition
    }

    $comment = ([string]$definition.Comment).ToLowerInvariant()
    if ($comment -and -not $definitionsByComment.ContainsKey($comment)) {
        $definitionsByComment[$comment] = $definition
    }
}

$placements = @{}
foreach ($line in Get-Content -LiteralPath $placementPath) {
    if ($line -match '^(\d+)\^\d+\^(.+)$') {
        $placements[[int]$Matches[1]] = $Matches[2].Trim()
    }
}

$modelsById = @{}
foreach ($line in Get-Content -LiteralPath $modelPath) {
    if ($line -match '^(\d+)\^([^\^]+)\^') {
        $modelsById[[int]$Matches[1]] = $Matches[2].Trim()
    }
}

$generatedModels = @{
    10451 = 'hsg_vip_party_pool_01.agr'
    16193 = 'hsg_dance_floor_01.adr'
    76878 = 'hsg_vip_juicebar_01.adr'
}

foreach ($line in Get-Content -LiteralPath $generatorPath) {
    if ($line -match '\[(\d+)\]\s*=\s*new\("([^"]+\.(?:adr|agr))"') {
        $generatedModels[[int]$Matches[1]] = $Matches[2]
    }
}

$availableAssets = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

& rg --files $AssetRoot | ForEach-Object {
    $name = [System.IO.Path]::GetFileName($_)
    if ($name -match '(?i)\.(adr|agr)$') {
        [void]$availableAssets.Add($name)
    }
}

$references = @{}
function Add-AssetReference {
    param(
        [string]$Asset,
        [string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($Asset)) {
        return
    }

    $key = $Asset.ToLowerInvariant()
    if (-not $references.ContainsKey($key)) {
        $references[$key] = [pscustomobject]@{
            Asset = $Asset
            Sources = [System.Collections.Generic.List[string]]::new()
        }
    }

    $references[$key].Sources.Add($Source)
}

$coinItems = [System.Collections.Generic.List[object]]::new()
foreach ($entry in (Get-Content -Raw -LiteralPath $coinStorePath | ConvertFrom-Json)) {
    [void]$coinItems.Add($entry)
}
$seenCoinItems = @{}
$coinFixtureCount = 0

foreach ($coinItem in $coinItems) {
    $id = [int]$coinItem.Id
    if ($seenCoinItems.ContainsKey($id) -or -not $definitionsById.ContainsKey($id)) {
        continue
    }

    $definition = $definitionsById[$id]
    $type = [int]$definition.Type
    $category = [int]$definition.CategoryId
    $parameter = [int]$definition.Param1
    $model = [string]$definition.ModelName
    $isCustomization = $type -eq 17 -and
        $parameter -eq 2 -and
        $category -in 51, 55, 59, 60
    $hasHousingModel = $type -eq 1 -and
        ($model.StartsWith('hsg_', [System.StringComparison]::OrdinalIgnoreCase) -or
            $model.StartsWith('mkt_boombox', [System.StringComparison]::OrdinalIgnoreCase))
    $hasHousingPlacement = $type -eq 1 -and
        $placements.ContainsKey($id) -and
        $category -in 52, 53, 54, 56, 57, 147
    $isFixture = $type -ne 16 -and
        ($isCustomization -or $type -eq 29 -or $hasHousingModel -or $hasHousingPlacement)

    if (-not $isFixture) {
        continue
    }

    $seenCoinItems[$id] = $true
    $coinFixtureCount++

    if ($isCustomization) {
        continue
    }

    $asset = if ($parameter -gt 0 -and $modelsById.ContainsKey($parameter)) {
        $modelsById[$parameter]
    }
    elseif ($placements.ContainsKey($id)) {
        $placements[$id]
    }
    else {
        $model
    }

    Add-AssetReference -Asset $asset -Source "Coin item $id $($definition.Comment)"
}

$stationFixtureCount = 0
$storeBundles = [System.Collections.Generic.List[object]]::new()
foreach ($entry in (Get-Content -Raw -LiteralPath $storeBundlePath | ConvertFrom-Json)) {
    [void]$storeBundles.Add($entry)
}
foreach ($bundle in $storeBundles) {
    if ([int]$bundle.CategoryGroupId -notin 119, 123) {
        continue
    }

    foreach ($entry in $bundle.Entries) {
        $id = [int]$entry.MarketingItemId
        if ($id -eq 15968) {
            continue
        }

        $stationFixtureCount++
        $asset = ''

        if ($placements.ContainsKey($id)) {
            $asset = $placements[$id]
        }
        elseif ($definitionsById.ContainsKey($id)) {
            $asset = [string]$definitionsById[$id].ModelName
        }
        elseif ($generatedModels.ContainsKey($id)) {
            $asset = $generatedModels[$id]
        }
        else {
            $comment = ([string]$bundle.Comment).ToLowerInvariant()
            if ($definitionsByComment.ContainsKey($comment)) {
                $asset = [string]$definitionsByComment[$comment].ModelName
            }
        }

        if ([string]::IsNullOrWhiteSpace($asset)) {
            $asset = "UNRESOLVED_ITEM_DEFINITION_$id"
        }

        Add-AssetReference -Asset $asset -Source "Station bundle $($bundle.Id) $($bundle.Comment), item $id"
    }
}

$missing = @(
    $references.Values |
        Where-Object { -not $availableAssets.Contains($_.Asset) } |
        Sort-Object Asset
)

Write-Output "Station fixture entries checked: $stationFixtureCount"
Write-Output "Unique coin-store fixtures checked: $coinFixtureCount"
Write-Output "Unique housing ADR/AGR references checked: $($references.Count)"
Write-Output "Available unpacked ADR/AGR assets: $($availableAssets.Count)"
Write-Output "Missing housing assets: $($missing.Count)"

foreach ($entry in $missing) {
    Write-Output "$($entry.Asset): $($entry.Sources -join ' | ')"
}

if ($missing.Count -ne 0) {
    throw "Housing client-asset audit failed with $($missing.Count) missing assets."
}
