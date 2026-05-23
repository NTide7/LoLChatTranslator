$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$aliasPath = Join-Path $repoRoot "LoLChatTranslator\champion_aliases.txt"

function Read-JsonUtf8 {
    param([Parameter(Mandatory=$true)][string]$Uri)

    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    try {
        $bytes = $client.GetByteArrayAsync($Uri).GetAwaiter().GetResult()
        return [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    }
    finally {
        $client.Dispose()
    }
}

function Read-ExistingAliases {
    param([string]$Path)

    $aliasesByKey = @{}
    if (-not (Test-Path -LiteralPath $Path)) {
        return $aliasesByKey
    }

    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
            continue
        }

        $parts = $line -split "`t"
        if ($parts.Count -lt 5) {
            continue
        }

        $key = $parts[0].Trim()
        $aliases = $parts[4] -split "[|,，;；]" |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $aliasesByKey[$key] = @($aliases)
        }
    }

    return $aliasesByKey
}

$versions = Read-JsonUtf8 -Uri "https://ddragon.leagueoflegends.com/api/versions.json"
$version = $versions[0]
$zhData = Read-JsonUtf8 -Uri "https://ddragon.leagueoflegends.com/cdn/$version/data/zh_CN/champion.json"
$enData = Read-JsonUtf8 -Uri "https://ddragon.leagueoflegends.com/cdn/$version/data/en_US/champion.json"
$manualAliases = Read-ExistingAliases -Path $aliasPath

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# generated_from=Riot Data Dragon $version")
$lines.Add("# key`ten`tzh_name`tzh_title`taliases")

foreach ($property in $enData.data.PSObject.Properties | Sort-Object Name) {
    $key = $property.Name
    $enChampion = $property.Value
    $zhChampion = $zhData.data.$key
    if ($null -eq $zhChampion) {
        continue
    }

    $aliases = New-Object System.Collections.Generic.List[string]
    if ($manualAliases.ContainsKey($key)) {
        foreach ($alias in $manualAliases[$key]) {
            if (-not $aliases.Contains($alias)) {
                $aliases.Add($alias)
            }
        }
    }

    $line = "{0}`t{1}`t{2}`t{3}`t{4}" -f @(
        $key,
        $enChampion.name,
        $zhChampion.name,
        $zhChampion.title,
        ($aliases -join "|"))
    $lines.Add($line)
}

Set-Content -LiteralPath $aliasPath -Value $lines -Encoding UTF8
Write-Host "Updated champion aliases: $aliasPath"
Write-Host "Data Dragon version: $version"
