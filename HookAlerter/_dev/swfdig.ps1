# Extract and dig into an uncompressed (FWS) SWF: header geometry, tag histogram,
# and all ActionScript 1/2 constant-pool strings (the game's real identifiers).
param(
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][int]$Offset,
    [Parameter(Mandatory=$true)][int]$Length,
    [string]$OutSwf,
    [string]$OutStrings,
    [int]$TopTags = 30
)
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $ExePath).Path
$exeBytes = [IO.File]::ReadAllBytes($resolved)
Write-Host "DEBUG path=$resolved fileLen=$((Get-Item -LiteralPath $resolved).Length) readLen=$($exeBytes.Length) offset=$Offset want=$Length"
$Length = [Math]::Min($Length, $exeBytes.Length - $Offset)
$b = New-Object byte[] $Length
[Array]::Copy($exeBytes, $Offset, $b, 0, $Length)
if ($OutSwf) { [IO.File]::WriteAllBytes($OutSwf, $b); Write-Host "extracted -> $OutSwf ($Length bytes)" }

# ---------------- bit reader (MSB first) ----------------
$script:bp = 0
function Bits([int]$n) {
    $v = 0
    for ($i = 0; $i -lt $n; $i++) {
        $byte = $script:b[[int]($script:bp -shr 3)]
        $bit = ($byte -shr (7 - ($script:bp -band 7))) -band 1
        $v = ($v -shl 1) -bor $bit
        $script:bp++
    }
    return $v
}
function SBits([int]$n) {
    if ($n -eq 0) { return 0 }
    $v = Bits $n
    if ($v -band (1 -shl ($n - 1))) { $v = $v - (1 -shl $n) }
    return $v
}

$sig = "$([char]$b[0])$([char]$b[1])$([char]$b[2])"
$ver = $b[3]
$declLen = [BitConverter]::ToUInt32($b, 4)
$script:bp = 64
$nBits = Bits 5
$xmin = SBits $nBits; $xmax = SBits $nBits; $ymin = SBits $nBits; $ymax = SBits $nBits
$byteAfterRect = [int](($script:bp + 7) / 8)
$fps = [BitConverter]::ToUInt16($b, $byteAfterRect) / 256.0
$frames = [BitConverter]::ToUInt16($b, $byteAfterRect + 2)
Write-Host ("`n{0} v{1} declaredLen={2} actualLen={3}" -f $sig, $ver, $declLen, $Length)
Write-Host ("stage = {0} x {1} px   fps={2}  frames={3}" -f (($xmax-$xmin)/20.0), (($ymax-$ymin)/20.0), $fps, $frames)
Write-Host ("rect twips: x {0}..{1}  y {2}..{3}   nBits={4}" -f $xmin,$xmax,$ymin,$ymax,$nBits)

# ---------------- tag walk ----------------
$p = $byteAfterRect + 4
$counts = @{}
$pool = New-Object Collections.Generic.List[string]
$tagNames = @{1='ShowFrame';2='DefineShape';4='PlaceObject';5='RemoveObject';6='DefineBits';7='DefineButton';
 9='SetBackgroundColor';10='DefineFont';11='DefineText';12='DoAction';20='DefineBitsLossless';21='DefineBitsJPEG2';
 22='DefineShape2';24='Protect';26='PlaceObject2';32='DefineShape3';33='DefineText2';34='DefineButton2';
 36='DefineBitsLossless2';37='DefineEditText';39='DefineSprite';43='FrameLabel';48='DefineFont2';59='DoInitAction';
 69='FileAttributes';70='PlaceObject3';76='SymbolClass';77='Metadata';82='DoABC';88='DefineFontName'}

while ($p -lt $b.Length - 1) {
    $cl = [BitConverter]::ToUInt16($b, $p); $p += 2
    $code = $cl -shr 6
    $len = $cl -band 0x3F
    if ($len -eq 0x3F) { $len = [BitConverter]::ToUInt32($b, $p); $p += 4 }
    if ($code -eq 0 -or $len -lt 0 -or ($p + $len) -gt $b.Length) { break }
    if (-not $counts.ContainsKey($code)) { $counts[$code] = 0 }
    $counts[$code]++

    if ($code -eq 12 -or $code -eq 59) {   # DoAction / DoInitAction
        $q = $p
        if ($code -eq 59) { $q += 2 }      # skip sprite id
        $end = $p + $len
        while ($q -lt $end) {
            $ac = $b[$q]; $q++
            if ($ac -eq 0) { break }
            if ($ac -ge 0x80) { $alen = [BitConverter]::ToUInt16($b, $q); $q += 2 } else { $alen = 0 }
            if ($ac -eq 0x88) {            # ActionConstantPool
                $cnt = [BitConverter]::ToUInt16($b, $q); $q += 2
                $sb = New-Object Text.StringBuilder
                $inStr = $false
                for ($k = 0; $k -lt ($alen - 2); $k++) {
                    $ch = $b[$q + $k]
                    if ($ch -eq 0) {
                        if ($sb.Length -gt 0) { $pool.Add($sb.ToString()) }
                        [void]$sb.Clear()
                    } elseif ($ch -ge 32 -and $ch -lt 127) { [void]$sb.Append([char]$ch) }
                    elseif ($ch -ge 0xC0) { [void]$sb.Append('?') }
                }
                if ($sb.Length -gt 0) { $pool.Add($sb.ToString()) }
            }
            $q += $alen
        }
    }
    $p += $len
}

Write-Host "`ntag histogram:"
$counts.GetEnumerator() | Sort-Object { -$_.Value } | Select-Object -First $TopTags | ForEach-Object {
    $nm = if ($tagNames.ContainsKey($_.Key)) { $tagNames[$_.Key] } else { '?' }
    Write-Host ("  {0,3} {1,-22} x{2}" -f $_.Key, $nm, $_.Value)
}

$uniq = $pool | Sort-Object -Unique
Write-Host "`nconstant-pool strings: $($pool.Count) total, $($uniq.Count) unique"
if ($OutStrings) { $uniq | Set-Content $OutStrings -Encoding UTF8; Write-Host "-> $OutStrings" }
