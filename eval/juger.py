#!/usr/bin/env python3
"""Juge programme de l'examen de l'agent de chat par tâche (stdlib uniquement).

    python eval/juger.py eval/runs/<run>.jsonl                      note + comparaison à la référence
    python eval/juger.py eval/runs/<run>.jsonl --ecrire-reference   (décision humaine) la note devient la référence

Ne note que ce qui se vérifie mécaniquement. La prochaine action et les étapes ont été extraites par le
code C# de l'appli (EvalRunner) : on juge exactement ce que l'interface verrait.

Critères communs à tous les cas : prochaine_action, francais, longueur (≤ 400 mots).
Critères propres au cas (champ "criteres" de eval/cas/cas.jsonl) :
  etapes_min, etapes_max, etapes_sans_doublon, questions_min, questions_max, action_differente.

Note d'un cas = critères réussis / critères évalués. Une référence par fournisseur (ClaudeCode, OpenAICompatible…).
Codes de sortie : 0 = pas de régression, 1 = régression, 2 = panne (fichier absent, cas sans réponse, référence illisible).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from datetime import datetime
from pathlib import Path

ICI = Path(__file__).resolve().parent
CAS = ICI / "cas" / "cas.jsonl"
REFERENCE = ICI / "reference.json"
LONGUEUR_MAX_MOTS = 400

MOTS_FR = set("le la les des du et est pour une que qui dans pas tu ton ta tes ce cette sur avec au aux il elle ou mais son sa ses je vous votre plus faire".split())
MOTS_EN = set("the and is to of you your for this that with it are be on can will what how have if".split())


def lire_jsonl(chemin: Path) -> list[dict]:
    return [json.loads(l) for l in chemin.read_text(encoding="utf-8").splitlines() if l.strip()]


def mots(s: str) -> list[str]:
    s = unicodedata.normalize("NFD", s.lower())
    s = "".join(c for c in s if unicodedata.category(c) != "Mn")
    return re.findall(r"[a-z0-9]+", s)


def mots_pleins(s: str) -> set[str]:
    return {w for w in mots(s) if (len(w) > 2 or w.isdigit()) and w not in MOTS_FR}


def proches(a: str, b: str) -> bool:
    """Même étape reformulée : plus des 2/3 des mots pleins de la plus courte se retrouvent dans l'autre
    (« Acheter/récupérer les cartons » ≈ « Acheter des cartons »), et les mêmes nombres (« partie 1 » ≠ « partie 2 »)."""
    ma, mb = mots_pleins(a), mots_pleins(b)
    if not ma or not mb or {w for w in ma if w.isdigit()} != {w for w in mb if w.isdigit()}:
        return False
    petit, grand = (ma, mb) if len(ma) <= len(mb) else (mb, ma)
    return len(petit & grand) / len(petit) > 2 / 3


def en_francais(texte: str) -> bool:
    m = mots(texte)
    fr = sum(w in MOTS_FR for w in m)
    en = sum(w in MOTS_EN for w in m)
    # Une réponse courte (« Non. Prochaine action : … ») a peu de mots outils : on se contente de 2.
    seuil = 5 if len(m) >= 40 else 2
    return fr >= seuil and fr > 2 * en


def nb_questions(texte: str) -> int:
    return len(re.findall(r"\?+", texte))


def juger_cas(cas: dict, sortie: dict) -> dict:
    texte = sortie.get("reponse") or ""
    etapes = sortie.get("etapes") or []
    c = cas.get("criteres", {})
    res: dict[str, bool] = {
        "prochaine_action": bool(sortie.get("prochaine_action")),
        "francais": en_francais(texte),
        "longueur": len(texte.split()) <= LONGUEUR_MAX_MOTS,
    }
    if "etapes_min" in c:
        res["etapes_min"] = len(etapes) >= c["etapes_min"]
    if "etapes_max" in c:
        res["etapes_max"] = len(etapes) <= c["etapes_max"]
    if c.get("etapes_sans_doublon"):
        existantes = sortie.get("sous_taches_existantes") or []
        res["etapes_sans_doublon"] = not any(proches(e, s) for e in etapes for s in existantes)
    if "questions_min" in c:
        res["questions_min"] = nb_questions(texte) >= c["questions_min"]
    if "questions_max" in c:
        res["questions_max"] = nb_questions(texte) <= c["questions_max"]
    if c.get("action_differente"):
        avant, apres = sortie.get("prochaine_action_precedente") or "", sortie.get("prochaine_action") or ""
        res["action_differente"] = bool(apres) and not proches(avant, apres)
    note = sum(res.values()) / len(res)
    return {"note": round(note, 3), "criteres": res}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("run", type=Path, help="sortie de dotnet run -- --eval … (jsonl)")
    ap.add_argument("--ecrire-reference", action="store_true",
                    help="décision humaine : enregistre cette note comme référence du fournisseur")
    a = ap.parse_args()

    try:
        tous_cas = {c["id"]: c for c in lire_jsonl(CAS)}
        sorties = lire_jsonl(a.run)
    except (OSError, json.JSONDecodeError, KeyError) as e:
        print(f"PANNE : {e}", file=sys.stderr)
        return 2
    if not sorties:
        print("PANNE : sortie vide", file=sys.stderr)
        return 2

    fournisseurs = {s.get("fournisseur") for s in sorties}
    if len(fournisseurs) != 1:
        print(f"PANNE : plusieurs fournisseurs dans la même sortie {fournisseurs}", file=sys.stderr)
        return 2
    fournisseur = fournisseurs.pop()

    pannes = [s["id"] for s in sorties if s.get("erreur") or s["id"] not in tous_cas]
    notes = {s["id"]: juger_cas(tous_cas[s["id"]], s) for s in sorties if s["id"] not in pannes}

    try:
        refs = json.loads(REFERENCE.read_text(encoding="utf-8")) if REFERENCE.exists() else {}
    except json.JSONDecodeError as e:
        print(f"PANNE : reference.json illisible ({e})", file=sys.stderr)
        return 2
    ref = refs.get(fournisseur, {}).get("cas", {})

    print(f"Examen {a.run.name} · fournisseur {fournisseur} · candidat {sorties[0].get('candidat')}")
    print(f"{'cas':<26}{'note':>6}{'réf.':>7}  critères échoués")
    regressions = []
    for id_ in sorted(notes):
        n, r = notes[id_]["note"], ref.get(id_)
        echecs = [k for k, ok in notes[id_]["criteres"].items() if not ok]
        marque = ""
        if r is not None and n < r - 1e-9:
            regressions.append(id_)
            marque = "  ← RÉGRESSION"
        piege = " (piège)" if tous_cas[id_].get("piege") else ""
        print(f"{id_ + piege:<26}{n:>6.2f}{'' if r is None else f'{r:.2f}':>7}  {', '.join(echecs) or '—'}{marque}")
    for id_ in pannes:
        print(f"{id_:<26}{'—':>6}{'':>7}  PANNE : {next(s.get('erreur') for s in sorties if s['id'] == id_) or 'cas inconnu'}")

    total = round(sum(v["note"] for v in notes.values()), 3)
    total_ref = refs.get(fournisseur, {}).get("total")
    print(f"\nTotal : {total:.2f} / {len(tous_cas)}" + ("" if total_ref is None else f"  (référence {total_ref:.2f})"))
    absents = sorted(set(tous_cas) - {s["id"] for s in sorties})
    if absents:
        print(f"Cas non joués : {', '.join(absents)}")

    if a.ecrire_reference:
        if pannes or absents:
            print("Référence NON écrite : tous les cas doivent avoir une réponse.", file=sys.stderr)
            return 2
        refs[fournisseur] = {"date": datetime.now().isoformat(timespec="seconds"), "run": a.run.name,
                             "candidat": sorties[0].get("candidat"), "total": total,
                             "cas": {k: v["note"] for k, v in sorted(notes.items())}}
        REFERENCE.write_text(json.dumps(refs, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"Référence {fournisseur} écrite dans {REFERENCE.name}.")
        return 0

    if pannes:
        return 2
    if not ref:
        print(f"Pas encore de référence pour {fournisseur} : relance avec --ecrire-reference si cette note te convient.")
        return 0
    if regressions or (total_ref is not None and total < total_ref - 1e-9 and not absents):
        print(f"RÉGRESSION : {', '.join(regressions) or 'total en baisse'}")
        return 1
    print("Pas de régression.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
