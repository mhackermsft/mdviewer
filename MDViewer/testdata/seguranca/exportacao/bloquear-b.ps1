# Mantém b.md aberto com bloqueio exclusivo até Enter (teste do R-12, feature 001).
$f = [IO.File]::Open((Join-Path $PSScriptRoot 'b.md'), 'Open', 'Read', 'None')
Read-Host 'b.md bloqueado. Exporte index.md no MDViewer e depois tecle Enter para liberar'
$f.Close()