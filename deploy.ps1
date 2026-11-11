<#
  Déploie une nouvelle version de TaskAgent sans toucher à celle qui tourne.

  1. Ajoute en haut de CHANGELOG.md une section « ## x.y.z — date » avec ce qui change.
  2. Lance :  powershell -ExecutionPolicy Bypass -File deploy.ps1
  3. L'appli ouverte affiche un bandeau « La version x.y.z est prête » → Redémarrer quand ça t'arrange.

  Chaque version vit dans releases\x.y.z\ ; releases\current.txt dit laquelle est la dernière.
  Le raccourci « TaskAgent » (Bureau + menu Démarrer) pointe vers la dernière version.
  Tes données (tâches, réglages) sont dans %AppData%\TaskAgent : partagées par toutes les versions.
#>
param(
    [int]$Keep = 3  # nombre de versions gardées dans releases\ (les plus récentes ; une version en cours d'utilisation n'est jamais supprimée)
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$releases = Join-Path $root 'releases'

# --- 1. Numéro de version = première section « ## x.y.z » de CHANGELOG.md
$changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'), [Text.Encoding]::UTF8)
$m = [regex]::Match($changelog, '(?m)^##\s+(\d+\.\d+\.\d+)')
if (-not $m.Success) { throw "Aucune section « ## x.y.z » dans CHANGELOG.md." }
$version = $m.Groups[1].Value
$target = Join-Path $releases $version
if (Test-Path $target) {
    throw "La version $version est déjà déployée. Ajoute d'abord une nouvelle section « ## x.y.z — date » en haut de CHANGELOG.md."
}

# --- 2. Publication dans son propre dossier (l'appli ouverte n'est pas touchée)
Write-Host "Publication de TaskAgent $version → $target"
dotnet publish (Join-Path $root 'TaskAgent.csproj') -c Release -r win-x64 --self-contained false -p:Version=$version -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    throw "Échec de la publication : la version en cours reste inchangée."
}

# --- 3. « current » pointe vers la nouvelle version : l'appli ouverte la détecte et propose de redémarrer
Set-Content -Path (Join-Path $releases 'current.txt') -Value $version -Encoding ASCII

# --- 4. Raccourcis vers la dernière version
$exe = Join-Path $target 'TaskAgent.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'TaskAgent.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.Description = "TaskAgent $version"
    $lnk.Save()
}

# Tes propres raccourcis vers TaskAgent (ex. « AIOS » épinglé à la barre des tâches) suivent aussi la dernière version,
# qu'ils pointent vers une ancienne version déployée ou vers la version de travail bin\Debug.
$pinned = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'),
            "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
foreach ($dir in $pinned) {
    Get-ChildItem $dir -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $lnk = $shell.CreateShortcut($_.FullName)
        if ($lnk.TargetPath -like "$root\*\TaskAgent.exe" -and $lnk.TargetPath -ne $exe) {
            $lnk.TargetPath = $exe
            $lnk.WorkingDirectory = $target
            $lnk.Save()
            Write-Host "  raccourci « $($_.BaseName) » → $version"
        }
    }
}

# --- 5. Ménage : on garde les $Keep plus récentes. Une version ouverte ne peut pas être renommée → on la garde.
$old = Get-ChildItem $releases -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -Skip $Keep
foreach ($dir in $old) {
    $name = $dir.Name
    try {
        Rename-Item $dir.FullName "$name.suppr" -ErrorAction Stop
        Remove-Item (Join-Path $releases "$name.suppr") -Recurse -Force
        Write-Host "  ancienne version $name supprimée"
    }
    catch { Write-Host "  version $name gardée (en cours d'utilisation)" }
}

Write-Host ""
Write-Host "✔ TaskAgent $version déployée. Dans l'appli ouverte : bandeau « Redémarrer » (ou relance le raccourci TaskAgent)."
