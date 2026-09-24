<#
.SYNOPSIS
  Deterministic terminal render workload for in-app A/B timing.

.DESCRIPTION
  Run this INSIDE an Ntilde window launched by launch-app.ps1. It writes a fixed
  amount of output (the same bytes every run) in three phases:
    1. dense-color : full screens of SGR-coloured words, redrawn in place
    2. scroll      : coloured compiler-style lines scrolling
    3. unicode     : CJK / emoji / box-drawing / Nerd icons, redrawn in place
  Maximise the window to the same size for both builds before starting.
#>
param([int] $Screens = 300, [int] $ScrollLines = 6000)

$e = [char]27
$w = [Console]::WindowWidth
$h = [Console]::WindowHeight - 1
$out = [Console]::Out

function Dense([int] $f) {
    $sb = [Text.StringBuilder]::new()
    [void]$sb.Append("$e[H")
    for ($r = 0; $r -lt $h; $r++) {
        $col = 0; $seg = 0
        while ($true) {
            $word = 'w{0:D4}' -f (($f * 31 + $r * 7 + $seg) % 9973)
            if ($col + $word.Length + 1 -gt $w) { break }
            $fg = 31 + (($r + $seg + $f) % 7)
            $bold = if ($seg % 3 -eq 0) { ';1' } else { '' }
            [void]$sb.Append("$e[$fg${bold}m$word$e[0m ")
            $col += $word.Length + 1; $seg++
        }
        [void]$sb.Append("$e[K")
        if ($r -lt $h - 1) { [void]$sb.Append("`r`n") }
    }
    $sb.ToString()
}

$pieces = '日本語の文字', '│├──┤╭─╮', '😀🚀🔥', ([string][char]0xE0B0 + [char]0xF113 + [char]0xE725), '한국어', 'Ωλπ', '✓✗'
function Uni([int] $f) {
    $sb = [Text.StringBuilder]::new()
    [void]$sb.Append("$e[H")
    for ($r = 0; $r -lt $h; $r++) {
        $line = [Text.StringBuilder]::new(); $i = $r + $f
        while ($line.Length -lt ($w / 2) - 4) { [void]$line.Append($pieces[$i++ % $pieces.Length]).Append(' ') }
        [void]$sb.Append("$e[3$((($r + $f) % 7) + 1)m").Append($line).Append("$e[0m$e[K")
        if ($r -lt $h - 1) { [void]$sb.Append("`r`n") }
    }
    $sb.ToString()
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$out.Write("$e[2J$e[?25l")
for ($f = 0; $f -lt $Screens; $f++) { $out.Write((Dense $f)); $out.Flush() }
$t1 = $sw.Elapsed.TotalSeconds

$out.Write("$e[2J$e[H")
$pad = 'x' * [Math]::Max(10, $w - 70)
for ($n = 0; $n -lt $ScrollLines; $n++) {
    $out.Write("$e[3$(($n % 7) + 1)m[{0:D6}]$e[0m build: src/Module$($n % 97)/File$($n % 13).cs(42,17): warning CS$(1000 + $n % 900): $pad`r`n" -f $n)
}
$out.Flush()
$t2 = $sw.Elapsed.TotalSeconds

$out.Write("$e[2J")
for ($f = 0; $f -lt $Screens; $f++) { $out.Write((Uni $f)); $out.Flush() }
$t3 = $sw.Elapsed.TotalSeconds

$out.Write("$e[0m$e[?25h$e[2J$e[H")
"workload done ({0}x{1}): dense {2:F1}s, scroll {3:F1}s, unicode {4:F1}s. Close this window now." -f $w, ($h + 1), $t1, ($t2 - $t1), ($t3 - $t2)
