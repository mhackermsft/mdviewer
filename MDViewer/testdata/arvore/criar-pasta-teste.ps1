<#
.SYNOPSIS
    Cria a pasta de teste da feature 005 (árvore e recarga), passo 1 do onboarding.

.DESCRIPTION
    A pasta é criada fora do projeto (por padrão em %TEMP%\mdviewer-arvore) porque
    contém uma junção circular, que faria o MSBuild varrer em laço se ficasse
    dentro de MDViewer/.

    Resultado esperado ao abrir a pasta no MDViewer:
      - aparecem: .github, notas (oculta), raiz.md
      - não aparecem: node_modules, Bin, obj, .git (pastas ignoradas por nome)
      - loop (junção para a própria raiz) não aparece e a varredura termina

.PARAMETER Path
    Pasta de destino. É apagada e recriada.
#>
param(
    [string]$Path = (Join-Path $env:TEMP 'mdviewer-arvore')
)

$ErrorActionPreference = 'Stop'

if (Test-Path $Path) {
    # Remove a junção antes, para não apagar através dela.
    $loop = Join-Path $Path 'loop'
    if (Test-Path $loop) { cmd /c rmdir "$loop" | Out-Null }
    Remove-Item -Recurse -Force $Path
}

function New-Md([string]$relative, [string]$text) {
    $full = Join-Path $Path $relative
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    Set-Content -Encoding utf8 -Path $full -Value $text
}

New-Md 'raiz.md'                       "# Raiz`n`nArquivo na raiz."
New-Md '.github\bug.md'                "# Bug`n`nPasta com ponto: deve aparecer."
New-Md 'notas\a.md'                    ("# Notas`n`n" + (1..200 | ForEach-Object { "Linha $_`n" }) -join '')
New-Md 'node_modules\pkg\README.md'    "# pkg`n`nNão deve aparecer."
New-Md 'Bin\x.md'                      "# Bin`n`nNão deve aparecer (nome sem diferenciar maiúsculas)."
New-Md 'obj\y.md'                      "# obj`n`nNão deve aparecer."
New-Md '.git\z.md'                     "# git`n`nNão deve aparecer."

attrib +h (Join-Path $Path 'notas')
cmd /c mklink /J (Join-Path $Path 'loop') $Path | Out-Null

Write-Host "Pasta de teste criada em $Path"
