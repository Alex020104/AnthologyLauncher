param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$catalogPath = Join-Path $RepositoryRoot 'addon-catalog.json'
$libraryRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'library'))
$document = [IO.File]::ReadAllText($catalogPath, $utf8) | ConvertFrom-Json
$items = @()

foreach ($item in $document.items) {
    $name = $item.name
    $relative = $item.path
    if ($name -eq '.vscode' -or $name -match '_separator$') {
        $archive = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relative.Replace('/', '\')))
        if (-not $archive.StartsWith($libraryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Archive path leaves library root: $archive"
        }
        if (Test-Path -LiteralPath $archive -PathType Leaf) {
            Remove-Item -LiteralPath $archive -Force
        }
        continue
    }

    $items += [pscustomobject]@{
        id = $item.id
        name = $name
        category = $item.category
        fileCount = [int] $item.fileCount
        sourceBytes = [long] $item.sourceBytes
        archiveBytes = [long] $item.archiveBytes
        sha256 = $item.sha256
        path = $relative
        url = $item.url
    }
}

$catalog = [ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow
    source = 'Anthology 2.1 MO2/mods'
    branch = 'addons-unified-library'
    count = $items.Count
    totalArchiveBytes = [long] (($items | Measure-Object archiveBytes -Sum).Sum)
    items = $items
}
[IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 8), $utf8)
[pscustomobject]@{
    Included = $items.Count
    GiB = [math]::Round((($items | Measure-Object archiveBytes -Sum).Sum / 1GB), 3)
    Archives = (Get-ChildItem -LiteralPath $libraryRoot -Recurse -File).Count
}
