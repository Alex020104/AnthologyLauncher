param(
    [Parameter(Mandatory = $true)]
    [string] $ModsRoot,
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe',
    [int64] $MaximumArchiveBytes = 95MB
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$temporaryRoot = Join-Path $RepositoryRoot '_packing'
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$included = [Collections.Generic.List[object]]::new()
$excluded = [Collections.Generic.List[object]]::new()
$folders = Get-ChildItem -LiteralPath $ModsRoot -Directory -Force |
    Where-Object { $_.Name -notin @('.git', '.vscode') -and $_.Name -notmatch '_separator$' } |
    Sort-Object Name
$index = 0

foreach ($folder in $folders) {
    $index++
    $category = if ($folder.Name -match '^\[([^\]]+)\]') { $Matches[1].ToUpperInvariant() } else { 'MISC' }
    $category = ($category -replace '[<>:"/\\|?*]', '_').Trim()
    $safeName = ($folder.Name -replace '[<>:"/\\|?*]', '_').Trim().TrimEnd('.')
    if ($safeName.Length -gt 96) {
        $safeName = $safeName.Substring(0, 96).Trim()
    }

    $fileName = 'addon-{0:D3}.7z' -f $index
    $temporaryArchive = Join-Path $temporaryRoot $fileName
    $files = Get-ChildItem -LiteralPath $folder.FullName -Recurse -File -Force -ErrorAction SilentlyContinue
    $sourceBytes = ($files | Measure-Object Length -Sum).Sum
    Write-Output ('PACK {0}/{1} {2} ({3:N1} MiB)' -f $index, $folders.Count, $folder.Name, ($sourceBytes / 1MB))

    & $SevenZip a -t7z $temporaryArchive (Join-Path $folder.FullName '*') -mx=3 -mmt=on -bso0 -bsp0 -y
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip failed for $($folder.FullName): $LASTEXITCODE"
    }

    $archiveInfo = Get-Item -LiteralPath $temporaryArchive
    if ($archiveInfo.Length -ge $MaximumArchiveBytes) {
        $excluded.Add([pscustomobject]@{
            name = $folder.Name
            category = $category
            sourceBytes = [long] $sourceBytes
            archiveBytes = $archiveInfo.Length
            reason = 'Archive exceeds the safe 95 MiB limit for regular GitHub Git'
        })
        Remove-Item -LiteralPath $temporaryArchive -Force
        Write-Output ('SKIP {0} archive={1:N1} MiB' -f $folder.Name, ($archiveInfo.Length / 1MB))
        continue
    }

    $categoryRoot = Join-Path $RepositoryRoot (Join-Path 'library' $category)
    New-Item -ItemType Directory -Path $categoryRoot -Force | Out-Null
    $destination = Join-Path $categoryRoot $fileName
    Move-Item -LiteralPath $temporaryArchive -Destination $destination
    $destinationInfo = Get-Item -LiteralPath $destination
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = (Join-Path (Join-Path 'library' $category) $fileName).Replace('\', '/')
    $encodedPath = [Uri]::EscapeDataString($relative).Replace('%2F', '/')
    $included.Add([pscustomobject]@{
        id = 'addon-{0:D3}' -f $index
        name = $folder.Name
        category = $category
        fileCount = $files.Count
        sourceBytes = [long] $sourceBytes
        archiveBytes = $destinationInfo.Length
        sha256 = $hash
        path = $relative
        url = 'https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/' + $encodedPath
    })
    Write-Output ('KEEP {0} archive={1:N1} MiB' -f $folder.Name, ($destinationInfo.Length / 1MB))
}

Remove-Item -LiteralPath $temporaryRoot -Force -Recurse
$catalog = [ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow
    source = 'Anthology 2.1 MO2/mods'
    branch = 'addons-unified-library'
    count = $included.Count
    totalArchiveBytes = [long] (($included | Measure-Object archiveBytes -Sum).Sum)
    items = $included
}
$excludedDocument = [ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow
    count = $excluded.Count
    items = $excluded
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $RepositoryRoot 'addon-catalog.json'), ($catalog | ConvertTo-Json -Depth 8), $utf8)
[IO.File]::WriteAllText((Join-Path $RepositoryRoot 'excluded-addons.json'), ($excludedDocument | ConvertTo-Json -Depth 8), $utf8)
Write-Output ('DONE included={0} excluded={1} archives={2:N2} GiB' -f $included.Count, $excluded.Count, (($included | Measure-Object archiveBytes -Sum).Sum / 1GB))
