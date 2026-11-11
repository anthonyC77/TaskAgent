# Passation — Stop (settings.json). couche narrative : sort 2 (consigne sur stderr) tant que la session a modifie un fichier sans ecrire sa passation. stop_hook_active = 0.
# Tout le travail est dans eval/passation.py (noyau, stdlib) : ce fichier ne fait que le trouver.
# Sans Python : sortie 0. Un hook qui plante ne vaut pas mieux qu un hook absent.

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$env:PYTHONIOENCODING = 'utf-8'
$raw = [Console]::In.ReadToEnd()

$py = $null
foreach ($c in 'python', 'python3') {
    $cmd = Get-Command $c -ErrorAction SilentlyContinue
    if ($cmd) { $py = $cmd.Source; break }
}
if (-not $py) { exit 0 }

$racine = $env:CLAUDE_PROJECT_DIR
if (-not $racine) { $racine = (Get-Location).Path }
$script = Join-Path $racine 'eval/passation.py'
if (-not (Test-Path $script)) { exit 0 }

$sortie = $raw | & $py $script stop
if ($sortie) { [Console]::Out.Write(($sortie -join "`n")) }
exit $LASTEXITCODE
