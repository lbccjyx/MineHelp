# Parse an uncompressed SWF: header geometry, tag summary, and UTF-8 string pool.
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [string]$TagFilter = '82|76|87|69|88',
    [int]$MinLen = 4
)
$ErrorActionPreference = 'Stop'
$b = [IO.File]::ReadAllBytes($Path)
Write-Host "file: $Path  $($b.Length) bytes"

# ---- header -------------------------------------------------------------
$sig = "$([char]$b[0])$([char]$b[1])$([char]$b[2])"
$ver = $b[3]
$fileLen = [BitConverter]::ToUInt32($b, 4)
Write-Host "signature=$sig version=$ver declaredLength=$fileLen"

$bitPos = 8 * 8   # RECT starts right after signature(3)+version(1)+length(4) = byte 8
function ReadBits([int]$n) {
    $val = 0
    for ($i = 0; $i -lt $n; $i++) {
        $byteIdx = [int]($script:bitPos / 8)
        $bitIdx = 7 - ($script:bitPos % 8)
        $bit = ($b[$byteIdx] -shr $bitIdx) -band 1
        $val = ($val -shl 1) -bor $bit
        $script:bitPos++
    }
    return $val
}
function ReadSBits([int]$n) {
    if ($n -eq 0) { return 0 }
    $val = ReadBits $n
    if ($val -band (1 -shl ($n - 1))) { $val = $val - (1 -shl $n) }
    return $val
}
$nBits = ReadBits 5
$xmin = ReadSBits $nBits; $xmax = ReadSBits $nBits
$ymin = ReadSBits $nBits; $ymax = ReadSBits $nBits
$stageW = ($xmax - $xmin) / 20.0
$stageH = ($ymax - $ymin) / 20.0
$bitPos = [math]::Ceiling($bitPos / 8) * 8
$frameRate = [BitConverter]::ToUInt16($b, $bitPos/8) / 256.0
$frameCount = [BitConverter]::ToUInt16($b, $bitPos/8 + 2)
Write-Host ("stage = {0} x {1} px   (nBits={2} rect={3},{4},{5},{6} twips)" -f $stageW, $stageH, $nBits, $xmin, $xmax, $ymin, $ymax)
Write-Host "frameRate = $frameRate fps   frameCount = $frameCount"

# ---- tag walk -----------------------------------------------------------
$p = [int]($bitPos/8) + 4
$counts = @{}
$abc = New-Object Collections.Generic.List[byte[]]
while ($p -lt $b.Length - 1) {
    $codeAndLen = [BitConverter]::ToUInt16($b, $p); $p += 2
    $code = $codeAndLen -shr 6
    $len = $codeAndLen -band 0x3F
    if ($len -eq 0x3F) { $len = [BitConverter]::ToUInt32($b, $p); $p += 4 }
    if ($code -eq 0) { break }
    if (-not $counts.ContainsKey($code)) { $counts[$code] = 0 }
    $counts[$code]++
    if ($code -eq 82) {                      # DoABC
        $blob = New-Object byte[] $len
        [Array]::Copy($b, $p, $blob, 0, [Math]::Min($len, $b.Length - $p))
        $abc.Add($blob)
    }
    $p += $len
    if ($len -lt 0) { break }
}
Write-Host "`ntags with DoABC(82): $($abc.Count)  total ABC bytes: $(($abc | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum)"
Write-Host "tag histogram (code:count):"
$counts.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,3} : {1}" -f $_.Key, $_.Value) }

# ---- UTF-8 string extraction from DoABC pools ---------------------------
$sb = New-Object Text.StringBuilder
$strings = New-Object Collections.Generic.HashSet[string]
foreach ($blob in $abc) {
    for ($i = 0; $i -lt $blob.Length; $i++) {
        $c = $blob[$i]
        if ($c -ge 32 -and $c -lt 127) { [void]$sb.Append([char]$c) }
        elseif ($c -ge 0xC0 -and $c -lt 0xE0 -and $i + 1 -lt $blob.Length) {
            $cp = (($c -band 0x1F) -shl 6) -bor ($blob[$i+1] -band 0x3F); $i++
            if ($cp -ge 32) { [void]$sb.Append([char]$cp) } else { [void]$sb.Clear() }
        }
        elseif ($c -ge 0xE0 -and $c -lt 0xF0 -and $i + 2 -lt $blob.Length) {
            $cp = (($c -band 0x0F) -shl 12) -bor (($blob[$i+1] -band 0x3F) -shl 6) -bor ($blob[$i+2] -band 0x3F); $i += 2
            if ($cp -ge 32) { [void]$sb.Append([char]$cp) } else { [void]$sb.Clear() }
        }
        else {
            if ($sb.Length -ge $MinLen) { [void]$strings.Add($sb.ToString()) }
            [void]$sb.Clear()
        }
    }
    if ($sb.Length -ge $MinLen) { [void]$strings.Add($sb.ToString()) }
    [void]$sb.Clear()
}
Write-Host "`nunique strings: $($strings.Count)"
$strings | Sort-Object | Set-Content ".\HookAlerter\_dev\swf_strings.txt" -Encoding UTF8
Write-Host "all strings -> .\HookAlerter\_dev\swf_strings.txt"
