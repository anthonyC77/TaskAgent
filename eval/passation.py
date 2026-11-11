#!/usr/bin/env python3
"""La passation d'une session, en deux couches — le travail derrière les trois hooks
passation-fin / passation-stop / passation-debut (sh et ps1, qui ne font que l'appeler).

    python3 eval/passation.py fin    < hook.json    SessionEnd   : couche mécanique, 0 LLM, sort 0
    python3 eval/passation.py stop   < hook.json    Stop         : 0, ou 2 + consigne sur stderr
    python3 eval/passation.py debut  < hook.json    SessionStart : la dernière passation en additionalContext

Fichier : <dir>/passations/<session>.md, où <dir> est --dir, sinon HARNAIS_APPRENTISSAGE_DIR,
sinon $CLAUDE_PROJECT_DIR/.claude/apprentissage, sinon <cwd du hook>/.claude/apprentissage.

Deux couches, parce qu'un hook ne peut pas demander au modèle d'écrire — sauf par le code 2 :

- **Mécanique** (SessionEnd, lit transcript_path) : fichiers modifiés, commandes de test et
  leur sort, commits, coût en requêtes et tokens. Existe toujours, même quand la session est
  fermée brutalement. Ne porte jamais la ligne « résultat accepté : » — elle appartient à
  l'humain, via la narrative ; sans elle, mesurer_sessions.py se rabat sur les commits.
- **Narrative** (Stop) : si la session a modifié un fichier et que le transcript ne montre
  aucune écriture de la passation depuis ≥ --seuil-outils appels d'outils, exit 2 et la
  consigne des rubriques. Le modèle l'écrit, s'arrête à nouveau, le hook voit l'écriture dans
  le transcript et sort 0. Pas d'état caché : tout se lit dans le transcript.

Une entrée illisible ou un transcript absent : 0, sans rien écrire. Un hook qui plante ne
vaut pas mieux qu'un hook absent, et il bloque l'humain en plus.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lire_transcripts as lt  # noqa: E402

RUBRIQUES = ("objectif", "état actuel", "décisions prises", "risques restants", "prochaines actions", "résultat accepté")
PLAFOND_LIGNES = 60
PLAFOND_COMMANDES = 30
COMMANDES_TEST = ("pytest", "vitest", "test_hooks.py", "dotnet test", "npm test", "unittest", "check_nodes", "doctor")
MARQUE_MECANIQUE, MARQUE_NARRATIVE = "## Mécanique", "## Narrative"


def dossier_apprentissage(explicite: str, entree: dict) -> str:
    if explicite:
        return explicite
    if os.environ.get("HARNAIS_APPRENTISSAGE_DIR"):
        return os.environ["HARNAIS_APPRENTISSAGE_DIR"]
    racine = os.environ.get("CLAUDE_PROJECT_DIR") or str(entree.get("cwd") or os.getcwd())
    return os.path.join(racine, ".claude", "apprentissage")


def chemin_passation(dossier: str, session_id: str) -> str:
    return os.path.join(dossier, "passations", session_id + ".md")


# --- fin ---------------------------------------------------------------------- #
def couche_mecanique(s: lt.Session) -> str:
    u = s.usage_total()
    tests = []
    resultat_par_outil = {}
    # Le sort d'une commande : le résultat d'outil qui la suit.
    precedent = None
    for t in s.tours:
        if t.outil in ("Bash", "PowerShell") and isinstance(t.entree, dict) and t.entree.get("command"):
            precedent = str(t.entree["command"])
        elif t.role == "resultat" and precedent is not None:
            resultat_par_outil[precedent] = "erreur" if t.erreur else "ok"
            precedent = None
    # Toutes les commandes, les vérifications marquées : « quelles commandes sont utiles » est une rubrique de la passation.
    commandes = s.commandes()
    for c in commandes[-PLAFOND_COMMANDES:]:
        marque = " (vérification)" if any(m in c.lower() for m in COMMANDES_TEST) else ""
        tests.append(f"- `{' '.join(c.split())[:160]}` → {resultat_par_outil.get(c, '?')}{marque}")
    if len(commandes) > PLAFOND_COMMANDES:
        tests.insert(0, f"- … {len(commandes) - PLAFOND_COMMANDES} commande(s) plus anciennes omises")
    commits = s.commits()
    L = [MARQUE_MECANIQUE + " (écrite par le hook SessionEnd, sans modèle)", "",
         f"- début : {s.debut.isoformat() if s.debut else '?'} ; fin : {s.fin.isoformat() if s.fin else '?'}",
         f"- requêtes : {s.requetes} (+ {s.sous_agents['requetes']} en sous-agents) ; tokens : {u['input_tokens'] + u['cache_creation_input_tokens'] + u['cache_read_input_tokens']} en entrée, {u['output_tokens']} en sortie",
         f"- modèles : {', '.join(sorted(s.modeles())) or '?'}",
         f"- messages humains : {len(s.humains())} ; appels d'outils : {len(s.appels_outils())} ; erreurs d'outils : {len(s.resultats_en_erreur())} ; refus : {s.refus_permission()}",
         "", "### Fichiers modifiés", ""]
    L += [f"- {f}" for f in sorted(s.fichiers_modifies())] or ["- (aucun)"]
    L += ["", "### Commandes lancées et leur sort", ""] + (tests or ["- (aucune)"])
    L += ["", "### Commits", ""] + ([f"- `{' '.join(c.split())[:160]}`" for c in commits] or ["- (aucun)"])
    L += [f"- commit détecté : {'oui' if commits else 'non'}", ""]
    return "\n".join(L)


def fusionner(existant: str, mecanique: str, s: lt.Session) -> str:
    """Remplace la couche mécanique, garde la narrative telle quelle."""
    entete = f"# Passation — session {s.session_id}\n\n- projet : {s.cwd or s.projet}\n"
    narrative = ""
    if MARQUE_NARRATIVE in existant:
        narrative = existant[existant.index(MARQUE_NARRATIVE):]
        if MARQUE_MECANIQUE in narrative:
            narrative = narrative[: narrative.index(MARQUE_MECANIQUE)].rstrip() + "\n"
    return entete + "\n" + (narrative + "\n" if narrative else "") + mecanique


def fin(entree: dict, dossier: str, erreur) -> int:
    s = _session(entree)
    if s is None:
        return 0
    p = chemin_passation(dossier, s.session_id)
    existant = ""
    if os.path.isfile(p):
        with open(p, encoding="utf-8", errors="replace") as f:
            existant = f.read()
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(fusionner(existant, couche_mecanique(s), s))
    return 0


# --- stop --------------------------------------------------------------------- #
def stop(entree: dict, dossier: str, seuil: int, erreur) -> int:
    if entree.get("stop_hook_active"):
        return 0
    s = _session(entree)
    if s is None or not s.fichiers_modifies():
        return 0
    p = chemin_passation(dossier, s.session_id)
    suffixe = ("passations/" + s.session_id + ".md").lower()
    appels = s.appels_outils()
    derniere_ecriture = None
    for i, t in enumerate(appels):
        # Écrite par Write/Edit — ou par un heredoc Bash qui nomme le fichier : les deux comptent.
        chemins = [lt._normaliser(c).lower() for c in lt._chemins(t.entree)]
        commande = lt._normaliser(str(t.entree.get("command", ""))).lower() if isinstance(t.entree, dict) else ""
        if (t.outil in lt.OUTILS_ECRITURE and any(c.endswith(suffixe) for c in chemins)) or                 (t.outil in ("Bash", "PowerShell") and suffixe in commande and (">" in commande or "set-content" in commande or "out-file" in commande)):
            derniere_ecriture = i
    if derniere_ecriture is not None and len(appels) - 1 - derniere_ecriture < seuil:
        return 0
    print(consigne(p, derniere_ecriture is not None), file=erreur)
    return 2


def consigne(p: str, mise_a_jour: bool) -> str:
    return "\n".join([
        ("PASSATION À METTRE À JOUR — " if mise_a_jour else "PASSATION À ÉCRIRE — ") + "avant de t'arrêter, écris la section « ## Narrative » dans :",
        f"  {p}",
        "(crée le fichier s'il n'existe pas ; s'il existe, remplace seulement la section Narrative, garde le reste). Une ligne par rubrique :",
        "  - objectif : ce qu'on cherchait à faire",
        "  - état actuel : ce qui est fait, ce qui ne l'est pas",
        "  - décisions prises : et pourquoi",
        "  - risques restants : ce qui peut encore casser",
        "  - prochaines actions : la première chose à faire à la prochaine session",
        "  - résultat accepté : oui ou non — l'humain a-t-il validé le résultat ?",
        "Sobre et factuel : c'est ce que le prochain agent lira à la place de tout redécouvrir. Puis termine ta réponse normalement.",
    ])


# --- debut -------------------------------------------------------------------- #
def debut(entree: dict, dossier: str, sortie) -> int:
    d = os.path.join(dossier, "passations")
    if not os.path.isdir(d):
        return 0
    moi = str(entree.get("session_id") or "")
    candidats = [os.path.join(d, n) for n in os.listdir(d) if n.endswith(".md") and n[:-3] != moi]
    if not candidats:
        return 0
    dernier = max(candidats, key=os.path.getmtime)
    with open(dernier, encoding="utf-8", errors="replace") as f:
        lignes = f.read().splitlines()
    tronquee = len(lignes) > PLAFOND_LIGNES
    corps = "\n".join(lignes[:PLAFOND_LIGNES]) + (f"\n… (passation tronquée à {PLAFOND_LIGNES} lignes ; le fichier complet : {dernier})" if tronquee else "")
    quand = datetime.fromtimestamp(os.path.getmtime(dernier), timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    contexte = "\n".join([
        f"PASSATION DE LA SESSION PRÉCÉDENTE ({os.path.basename(dernier)[:-3]}, écrite le {quand}).",
        "Ne relis pas ce qu'elle établit ; vérifie seulement ce qui a pu changer depuis. Commence par ses « prochaines actions ».",
        "", corps])
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": contexte}}, ensure_ascii=False), file=sortie)
    return 0


# --- commun ------------------------------------------------------------------- #
def _session(entree: dict):
    chemin = entree.get("transcript_path")
    if not isinstance(chemin, str) or not os.path.isfile(chemin):
        return None
    s = lt.lire_session(chemin)
    if s is None:
        return None
    if entree.get("session_id"):
        s.session_id = str(entree["session_id"])
    return s


def main(argv=None, entree=None, sortie=None, erreur=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("evenement", choices=["fin", "stop", "debut"])
    p.add_argument("--dir", default="", help="dossier .claude/apprentissage du projet")
    p.add_argument("--seuil-outils", type=int, default=20, help="stop : redemander la narrative après tant d'appels d'outils")
    a = p.parse_args(argv)
    sortie, erreur = sortie or sys.stdout, erreur or sys.stderr
    if entree is None:
        lt._stdout_utf8()
        try:
            sys.stdin.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
        entree = sys.stdin.read()
    try:
        donnees = json.loads(entree) if entree.strip() else {}
    except ValueError:
        return 0
    if not isinstance(donnees, dict):
        return 0
    dossier = dossier_apprentissage(a.dir, donnees)
    try:
        if a.evenement == "fin":
            return fin(donnees, dossier, erreur)
        if a.evenement == "stop":
            return stop(donnees, dossier, a.seuil_outils, erreur)
        return debut(donnees, dossier, sortie)
    except OSError as e:
        print(f"passation : {e}", file=erreur)
        return 0


if __name__ == "__main__":
    sys.exit(main())
