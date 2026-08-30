param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$catalogPath = Join-Path $RepositoryRoot 'addon-catalog.json'
$libraryRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'library'))
$catalog = [IO.File]::ReadAllText($catalogPath, $utf8) | ConvertFrom-Json
$archives = Get-ChildItem -LiteralPath $libraryRoot -Recurse -File

foreach ($item in $catalog.items) {
    $categoryRoot = [IO.Path]::GetFullPath((Join-Path $libraryRoot $item.category))
    $destination = [IO.Path]::GetFullPath((Join-Path $categoryRoot ($item.id + '.7z')))
    $numericId = $item.id.Replace('addon-', '')
    $sourceInfo = $archives | Where-Object {
        $_.DirectoryName.Equals($categoryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        ($_.Name.Equals($item.id + '.7z', [StringComparison]::OrdinalIgnoreCase) -or $_.Name.StartsWith($numericId + '-', [StringComparison]::OrdinalIgnoreCase))
    } | Select-Object -First 1
    if ($null -eq $sourceInfo) {
        throw "Archive was not found for $($item.id) in $categoryRoot"
    }
    $source = $sourceInfo.FullName
    foreach ($path in @($source, $destination)) {
        if (-not $path.StartsWith($libraryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Archive path leaves library root: $path"
        }
    }

    if (-not $source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $destination) {
            throw "Normalized archive already exists: $destination"
        }
        Move-Item -LiteralPath $source -Destination $destination
    }

    $relative = ('library/{0}/{1}.7z' -f $item.category, $item.id)
    $item.path = $relative
    $item.url = 'https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/' + $relative
}

[IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 8), $utf8)
Write-Output "Normalized $($catalog.items.Count) archive names."
