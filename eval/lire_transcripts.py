#!/usr/bin/env python3
"""Lire les transcripts Claude Code (~/.claude/projects/<projet>/*.jsonl) pour la boucle
d'apprentissage. Stdlib pure. Adapté de oneiros/sessions/reader.py, même discipline :

Le format appartient au harness et change sans préavis. Chaque champ est lu sur la
défensive : une clé déplacée, une ligne tronquée par une session encore en cours, une
forme d'entrée jamais vue — rien ne doit lever. Un lecteur qui jette sur une ligne
emporte toute la mesure avec lui.

Ce qu'on garde : les messages humains, le texte des réponses, les *entrées* d'outils,
l'usage par requête API, les horodatages. Ce qu'on laisse : les résultats d'outils (le
gros du volume, jamais ce qu'on mesure), la réflexion, les sidechains, le reste.

Deux faits observés le 12 septembre 2026 (version 2.1.269) et qui comptent :
- une réponse API à plusieurs blocs donne plusieurs entrées `assistant` portant le même
  `requestId` et le même `usage`. L'usage se compte une fois par requête, jamais par entrée ;
- les sous-agents écrivent dans `<session>/subagents/agent-*.jsonl`, tout en `isSidechain`.
  Leur usage est facturé à la session mère : il est agrégé dans `usage_total`, et compté à
  part dans `sous_agents`. Leurs tours ne sont pas lus — ce n'est pas la conversation.

    python3 eval/lire_transcripts.py <fichier.jsonl | dossier>   # digest sur stdout
"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime, timezone
from typing import Iterator, Optional

OUTILS_LECTURE = ("Read", "Grep", "Glob")
OUTILS_ECRITURE = ("Edit", "Write", "MultiEdit", "NotebookEdit")
OUTILS_ESCALADE = ("AskUserQuestion",)
MARQUES_REFUS = ("doesn't want to proceed", "user denied", "permission denied", "was rejected", "refusé par l'utilisateur")
MARQUES_COMMIT = ("git commit",)
USAGE_CLES = ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens")


class Tour:
    """Une entrée de conversation : un message humain, un bloc assistant ou un résultat d'outil."""
    __slots__ = ("role", "horodatage", "texte", "outil", "entree", "resultat_de", "erreur", "refus", "request_id", "usage", "modele")

    def __init__(self, role, horodatage):
        self.role, self.horodatage = role, horodatage
        self.texte = ""
        self.outil, self.entree = None, None            # tool_use : nom, input
        self.resultat_de, self.erreur, self.refus = None, False, False   # tool_result
        self.request_id, self.usage, self.modele = None, None, None


class Session:
    def __init__(self, session_id, projet, chemin):
        self.session_id, self.projet, self.chemin = session_id, projet, chemin
        self.tours: list[Tour] = []
        self.requetes = 0
        self._usage = {k: 0 for k in USAGE_CLES}
        self._usage_par_modele: dict[str, dict] = {}
        self._modeles: set[str] = set()
        self.cwd = ""
        self.sous_agents = {"transcripts": 0, "requetes": 0}

    # --- bornes ---------------------------------------------------------- #
    @property
    def debut(self) -> Optional[datetime]:
        return next((t.horodatage for t in self.tours if t.horodatage), None)

    @property
    def fin(self) -> Optional[datetime]:
        return next((t.horodatage for t in reversed(self.tours) if t.horodatage), None)

    def semaine(self) -> Optional[str]:
        d = self.debut
        if d is None:
            return None
        an, sem, _ = d.isocalendar()
        return f"{an}-W{sem:02d}"

    # --- projections ------------------------------------------------------ #
    def humains(self) -> list[Tour]:
        return [t for t in self.tours if t.role == "humain"]

    def appels_outils(self) -> list[Tour]:
        return [t for t in self.tours if t.outil]

    def usage_total(self) -> dict:
        return dict(self._usage)

    def modeles(self) -> set[str]:
        return set(self._modeles)

    def usage_par_modele(self) -> dict:
        """L'usage ventilé par modèle : une grille de prix se lit par modèle, pas par session."""
        return {m: dict(u) for m, u in self._usage_par_modele.items()}

    def fichiers_lus(self) -> set[str]:
        return {c for t in self.appels_outils() if t.outil in OUTILS_LECTURE for c in _chemins(t.entree)}

    def fichiers_modifies(self) -> set[str]:
        return {c for t in self.appels_outils() if t.outil in OUTILS_ECRITURE for c in _chemins(t.entree)}

    def commandes(self) -> list[str]:
        return [str(t.entree.get("command")) for t in self.appels_outils()
                if t.outil in ("Bash", "PowerShell") and isinstance(t.entree, dict) and t.entree.get("command")]

    def commits(self) -> list[str]:
        return [c for c in self.commandes() if any(m in c for m in MARQUES_COMMIT)]

    def resultats_en_erreur(self) -> list[Tour]:
        return [t for t in self.tours if t.resultat_de and t.erreur]

    def refus_permission(self) -> int:
        return sum(1 for t in self.tours if t.refus)

    def escalades(self) -> int:
        return sum(1 for t in self.appels_outils() if t.outil in OUTILS_ESCALADE)


# --------------------------------------------------------------------------- #
def lire_session(chemin: str) -> Optional[Session]:
    """La session d'un transcript. None si le fichier n'existe pas ; vide s'il est illisible."""
    if not os.path.isfile(chemin):
        return None
    projet = os.path.basename(os.path.dirname(os.path.abspath(chemin)))
    s = Session(os.path.splitext(os.path.basename(chemin))[0], projet, chemin)
    requetes_vues: set[str] = set()
    outils_en_attente: dict[str, str] = {}   # tool_use_id -> nom, pour qualifier les résultats
    try:
        f = open(chemin, "r", encoding="utf-8", errors="replace")
    except OSError:
        return s
    with f:
        for ligne in f:
            ligne = ligne.strip()
            if not ligne:
                continue
            try:
                e = json.loads(ligne)
            except json.JSONDecodeError:
                continue   # dernière ligne d'une session en cours d'écriture : la prochaine passe la verra
            if not isinstance(e, dict) or e.get("isSidechain") or e.get("isMeta"):
                continue
            if e.get("sessionId") and s.session_id == os.path.splitext(os.path.basename(chemin))[0]:
                s.session_id = str(e["sessionId"])
            if e.get("cwd") and not s.cwd:
                s.cwd = str(e["cwd"])
            m = e.get("message")
            if not isinstance(m, dict):
                continue
            role = str(m.get("role") or e.get("type") or "")
            horo = _horodatage(e.get("timestamp"))
            if role == "assistant":
                rid = str(e.get("requestId") or e.get("uuid") or "")
                if rid and rid not in requetes_vues:
                    requetes_vues.add(rid)
                    s.requetes += 1
                    modele = str(m.get("model") or "inconnu")
                    _ajouter_usage(s._usage, m.get("usage"))
                    _ajouter_usage(s._usage_par_modele.setdefault(modele, {k: 0 for k in USAGE_CLES}), m.get("usage"))
                    if m.get("model"):
                        s._modeles.add(modele)
                for bloc in _blocs(m.get("content")):
                    k = bloc.get("type")
                    if k == "text" and isinstance(bloc.get("text"), str) and bloc["text"].strip():
                        t = Tour("assistant", horo); t.texte = bloc["text"].strip(); t.request_id = rid
                        s.tours.append(t)
                    elif k == "tool_use":
                        t = Tour("assistant", horo); t.outil = str(bloc.get("name") or "outil")
                        t.entree = bloc.get("input") if isinstance(bloc.get("input"), dict) else {"valeur": bloc.get("input")}
                        t.request_id = rid
                        if bloc.get("id"):
                            outils_en_attente[str(bloc["id"])] = t.outil
                        s.tours.append(t)
                    # thinking et le reste : laissés.
            elif role == "user":
                contenu = m.get("content")
                if isinstance(contenu, str):
                    if contenu.strip():
                        t = Tour("humain", horo); t.texte = contenu.strip(); s.tours.append(t)
                    continue
                for bloc in _blocs(contenu):
                    k = bloc.get("type")
                    if k == "tool_result":
                        t = Tour("resultat", horo)
                        t.resultat_de = outils_en_attente.get(str(bloc.get("tool_use_id")), "outil")
                        t.erreur = bool(bloc.get("is_error"))
                        texte = _texte_resultat(bloc.get("content"))
                        t.refus = any(mq in texte.lower() for mq in MARQUES_REFUS)
                        s.tours.append(t)
                    elif k == "text" and isinstance(bloc.get("text"), str) and bloc["text"].strip():
                        t = Tour("humain", horo); t.texte = bloc["text"].strip(); s.tours.append(t)
    _ajouter_sous_agents(s, os.path.join(os.path.splitext(chemin)[0], "subagents"))
    return s


def _ajouter_sous_agents(s: Session, dossier: str) -> None:
    """L'usage des sous-agents, une fois par requête, rien d'autre."""
    if not os.path.isdir(dossier):
        return
    for nom in sorted(os.listdir(dossier)):
        if not nom.endswith(".jsonl"):
            continue
        s.sous_agents["transcripts"] += 1
        vues: set[str] = set()
        try:
            f = open(os.path.join(dossier, nom), "r", encoding="utf-8", errors="replace")
        except OSError:
            continue
        with f:
            for ligne in f:
                try:
                    e = json.loads(ligne)
                except json.JSONDecodeError:
                    continue
                m = e.get("message") if isinstance(e, dict) else None
                if not isinstance(m, dict) or m.get("role") != "assistant":
                    continue
                rid = str(e.get("requestId") or e.get("uuid") or "")
                if not rid or rid in vues:
                    continue
                vues.add(rid)
                s.sous_agents["requetes"] += 1
                modele = str(m.get("model") or "inconnu")
                _ajouter_usage(s._usage, m.get("usage"))
                _ajouter_usage(s._usage_par_modele.setdefault(modele, {k: 0 for k in USAGE_CLES}), m.get("usage"))
                if m.get("model"):
                    s._modeles.add(modele)


def iter_sessions(dossier: str) -> Iterator[Session]:
    """Toutes les sessions d'un dossier projet, par ordre de début (les vides à la fin)."""
    if not os.path.isdir(dossier):
        return
    sessions = []
    for nom in sorted(os.listdir(dossier)):
        if nom.endswith(".jsonl"):
            s = lire_session(os.path.join(dossier, nom))
            if s is not None:
                sessions.append(s)
    sessions.sort(key=lambda s: (s.debut is None, s.debut or datetime.max.replace(tzinfo=timezone.utc)))
    yield from sessions


def digest(s: Session, limite: int = 600) -> str:
    """Ce qu'on envoie au modèle qui extrait : messages humains intégraux, réponses tronquées,
    entrées d'outils sans leurs résultats. Quelques milliers de tokens, pas le transcript."""
    lignes = [f"session {s.session_id} — projet {s.projet} — {s.debut.isoformat() if s.debut else '?'} → {s.fin.isoformat() if s.fin else '?'}",
              f"requêtes API : {s.requetes} (+ {s.sous_agents['requetes']} en sous-agents) ; usage : {json.dumps(s.usage_total())}", ""]
    for t in s.tours:
        if t.role == "humain":
            lignes.append(f"[humain] {t.texte}")
        elif t.outil:
            lignes.append(f"[{t.outil}] {_resume_entree(t.outil, t.entree, limite)}")
        elif t.role == "resultat":
            if t.refus:
                lignes[-1] += " → refusé par l'utilisateur"
            elif t.erreur:
                lignes[-1] += " → erreur"
        elif t.texte:
            lignes.append("[assistant] " + (t.texte if len(t.texte) <= limite else t.texte[:limite] + "…"))
    return "\n".join(lignes)


# --------------------------------------------------------------------------- #
def _blocs(contenu) -> list[dict]:
    return [b for b in contenu if isinstance(b, dict)] if isinstance(contenu, list) else []


def _horodatage(v) -> Optional[datetime]:
    if not isinstance(v, str) or not v:
        return None
    try:
        d = datetime.fromisoformat(v.replace("Z", "+00:00"))
    except ValueError:
        return None
    return d if d.tzinfo else d.replace(tzinfo=timezone.utc)


def _ajouter_usage(cumul: dict, usage) -> None:
    if not isinstance(usage, dict):
        return
    for k in USAGE_CLES:
        v = usage.get(k)
        if isinstance(v, (int, float)) and v >= 0:
            cumul[k] += int(v)


def _normaliser(chemin: str) -> str:
    return chemin.replace("\\", "/").rstrip("/")


def _chemins(entree) -> list[str]:
    """Les chemins d'une entrée d'outil : file_path, path, notebook_path. Glob/Grep sans path : rien."""
    if not isinstance(entree, dict):
        return []
    return [_normaliser(str(entree[k])) for k in ("file_path", "path", "notebook_path") if isinstance(entree.get(k), str) and entree[k]]


def _texte_resultat(contenu) -> str:
    if isinstance(contenu, str):
        return contenu
    return " ".join(str(b.get("text", "")) for b in _blocs(contenu))


def _resume_entree(outil: str, entree, limite: int) -> str:
    if not isinstance(entree, dict):
        return ""
    if outil in ("Bash", "PowerShell") and entree.get("command"):
        court = str(entree["command"])
    elif _chemins(entree):
        court = " ".join(_chemins(entree)) + (f"  /{entree['pattern']}/" if entree.get("pattern") else "")
    elif entree.get("pattern"):
        court = f"/{entree['pattern']}/"
    else:
        try:
            court = json.dumps({k: v for k, v in entree.items() if k not in ("content", "new_string", "old_string")}, ensure_ascii=False, sort_keys=True)
        except (TypeError, ValueError):
            court = str(entree)
    court = " ".join(court.split())
    return court if len(court) <= limite else court[:limite] + "…"


def _stdout_utf8() -> None:
    """Sous Windows la console est en cp1252 : une flèche suffit à faire planter l'affichage."""
    for flux in (sys.stdout, sys.stderr):
        if hasattr(flux, "reconfigure"):
            try:
                flux.reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    _stdout_utf8()
    if not argv:
        print(__doc__); return 2
    cible = argv[0]
    sessions = list(iter_sessions(cible)) if os.path.isdir(cible) else [lire_session(cible)]
    if not sessions or sessions[0] is None:
        print(f"introuvable : {cible}", file=sys.stderr); return 2
    for s in sessions:
        print(digest(s)); print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
