param(
    [Parameter(Mandatory = $true)] [string] $StagingDir,
    [Parameter(Mandatory = $true)] [string] $ModId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CodeMask([string] $source) {
    $chars = $source.ToCharArray()
    $length = $chars.Length
    $i = 0

    while ($i -lt $length) {
        $ch = $source[$i]

        if ($ch -eq '/' -and $i + 1 -lt $length -and
            ($source[$i + 1] -eq '/' -or $source[$i + 1] -eq '*')) {
            $lineComment = $source[$i + 1] -eq '/'
            $j = $i + 2

            while ($j -lt $length) {
                if ($lineComment -and ($source[$j] -eq "`r" -or $source[$j] -eq "`n")) { break }
                if (-not $lineComment -and $j + 1 -lt $length -and
                    $source[$j] -eq '*' -and $source[$j + 1] -eq '/') {
                    $j += 2
                    break
                }
                $j++
            }

            for ($k = $i; $k -lt $j; $k++) {
                if ($chars[$k] -ne "`r" -and $chars[$k] -ne "`n") { $chars[$k] = ' ' }
            }
            $i = $j
            continue
        }

        if ($ch -eq '"' -or $ch -eq "'") {
            $verbatim = $ch -eq '"' -and $i -gt 0 -and (
                $source[$i - 1] -eq '@' -or
                ($i -gt 1 -and $source[$i - 1] -eq '$' -and $source[$i - 2] -eq '@')
            )
            $j = $i + 1

            while ($j -lt $length) {
                if ($verbatim -and $source[$j] -eq '"' -and $j + 1 -lt $length -and
                    $source[$j + 1] -eq '"') {
                    $j += 2
                    continue
                }
                if (-not $verbatim -and $source[$j] -eq '\') {
                    $j += 2
                    continue
                }
                if ($source[$j] -eq $ch) {
                    $j++
                    break
                }
                $j++
            }

            if ($j -gt $length) { $j = $length }
            for ($k = $i; $k -lt $j; $k++) {
                if ($chars[$k] -ne "`r" -and $chars[$k] -ne "`n") { $chars[$k] = ' ' }
            }
            $i = $j
            continue
        }

        $i++
    }

    return -join $chars
}

function Get-Depth([string] $mask, [int] $start, [int] $until) {
    $depth = 0
    for ($p = $start; $p -lt $until; $p++) {
        if ($mask[$p] -eq '{') { $depth++ }
        if ($mask[$p] -eq '}') { $depth-- }
    }
    return $depth
}

if (-not (Test-Path -LiteralPath $StagingDir -PathType Container)) {
    throw "Временная папка DEV-мода не найдена: $StagingDir"
}
if ($ModId -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$') {
    throw "Некорректный ModId: $ModId"
}

$found = @()
foreach ($file in Get-ChildItem -LiteralPath $StagingDir -Filter '*.cs' -Recurse -File) {
    if ($file.Name -eq 'DevAutoLoad.cs') { continue }
    $source = [IO.File]::ReadAllText($file.FullName)
    $mask = Get-CodeMask $source

    foreach ($match in [regex]::Matches($mask, '\bclass\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?:global\s*::\s*)?Mod\b')) {
        $found += [pscustomobject]@{
            File = $file.FullName
            Source = $source
            Mask = $mask
            Name = $match.Groups['name'].Value
            DeclarationEnd = $match.Index + $match.Length
        }
    }
}

if ($found.Count -ne 1) {
    throw "В DEV-архиве найдено $($found.Count) классов, наследующих Mod - нужен ровно один основной класс"
}

$entry = $found[0]
$source = $entry.Source
$mask = $entry.Mask
$classOpen = $mask.IndexOf('{', $entry.DeclarationEnd)
if ($classOpen -lt 0) { throw "Не найдено тело класса $($entry.Name)" }

$classEnd = -1
$depth = 0
for ($p = $classOpen; $p -lt $mask.Length; $p++) {
    if ($mask[$p] -eq '{') { $depth++ }
    if ($mask[$p] -eq '}') { $depth-- }
    if ($depth -eq 0) {
        $classEnd = $p
        break
    }
}
if ($classEnd -lt 0) { throw "Не закрыто тело класса $($entry.Name)" }

$startMatch = $null
$startPattern = '\b(?:void|IEnumerator|System\s*\.\s*Collections\s*\.\s*IEnumerator)\s+Start\s*\(\s*\)\s*\{'
foreach ($m in [regex]::Matches($mask, $startPattern)) {
    if ($m.Index -gt $classOpen -and $m.Index -lt $classEnd -and
        (Get-Depth $mask $classOpen $m.Index) -eq 1) {
        if ($null -ne $startMatch) { throw "Найдено несколько Start() в классе $($entry.Name)" }
        $startMatch = $m
    }
}

$call = 'DevAutoLoad.Start(this, "' + $ModId + '");'
if ($null -ne $startMatch) {
    $insertAt = $startMatch.Index + $startMatch.Length
    $addition = "`r`n        $call"
}
else {
    foreach ($m in [regex]::Matches($mask, '\bStart\s*\(\s*\)')) {
        if ($m.Index -gt $classOpen -and $m.Index -lt $classEnd -and
            (Get-Depth $mask $classOpen $m.Index) -eq 1) {
            throw "Start() в классе $($entry.Name) имеет неподдерживаемую форму - автозагрузка не встроена"
        }
    }
    $insertAt = $classOpen + 1
    $addition = "`r`n    private void Start()`r`n    {`r`n        $call`r`n    }`r`n"
}

$patched = $source.Insert($insertAt, $addition)
[IO.File]::WriteAllText($entry.File, $patched, [Text.UTF8Encoding]::new($false))

if (-not $patched.Contains($call)) { throw 'Вызов автозагрузки не добавлен' }
Write-Host "[DevAutoLoad] Временный DEV-исходник изменён: $($entry.Name), оригинал не затронут"
