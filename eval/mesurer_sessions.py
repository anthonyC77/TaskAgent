#!/usr/bin/env python3
"""Les six signaux d'apprentissage, par session et par semaine ISO. Stdlib pure, 0 LLM.

    python3 eval/mesurer_sessions.py --projet d--HorusCoachIA
    python3 eval/mesurer_sessions.py --projet <dossier de transcripts> --semaine 2026-W38

La facture brute mesure un volume ; le coût par résultat accepté mesure un progrès. Les
six signaux du texte du 10 septembre 2026, chacun avec son proxy assumé :

  1. coût par résultat accepté        Σ tokens × prix.json ÷ résultats acceptés
  2. temps jusqu'au résultat fiable   dernier résultat accepté − premier message humain (médiane)
  3. corrections répétées             messages humains courts commençant par un terme du lexique,
                                      plus ceux qui suivent un résultat d'outil en erreur
  4. taux d'escalade humaine          (AskUserQuestion + refus de permission) ÷ requêtes API
  5. part couverte par des tests      sessions avec une commande de test ÷ sessions qui ont modifié du code
  6. taux de redécouverte             fichiers lus déjà lus dans ≥ 2 sessions antérieures ÷ fichiers lus

« Résultat accepté » : un `git commit` dans les commandes, ou la ligne `résultat accepté : oui`
dans la passation de la session — la passation prime, seul l'humain sait ce qui a été accepté.

Sorties : <out-dir>/semaine-YYYY-Www.json et .md, une par semaine mesurée, avec le delta sur
la semaine précédente. Codes : 0 mesuré ; 1 au moins un signal s'est dégradé au-delà de
seuils.json — pas une panne, « si les métriques ne bougent pas, le système n'apprend rien »
rendu exécutable ; 2 transcripts introuvables ou grille de prix absente.

La même passe déprécie les candidats promus dont la date de validité est passée ou dont la
cible a disparu : c'est l'étape six de la boucle, celle qu'on oublie.

L'étalon (prix.json, seuils.json, corrections.txt, commandes-test.txt) appartient au projet :
il se lit dans --etalon-dir (défaut eval/apprentissage), et dans modele/eval/apprentissage
quand on tourne dans le harnais lui-même.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import statistics
import sys
from datetime import date, datetime

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lire_transcripts as lt  # noqa: E402

PROJETS_DIR = os.path.join(os.path.expanduser("~"), ".claude", "projects")
ETALON_DIR = os.path.join(HERE, "apprentissage")
ETALON_MODELE = os.path.join(os.path.dirname(HERE), "modele", "eval", "apprentissage")
APPRENTISSAGE_DIR = os.path.join(".claude", "apprentissage")

# Un signal est « meilleur » quand il baisse, sauf la part couverte par des tests.
SIGNAUX = ("cout_par_resultat_accepte", "temps_resultat_fiable_min", "corrections_repetees",
           "taux_escalade_humaine", "part_couverte_par_tests", "taux_redecouverte")
PLUS_HAUT_EST_MIEUX = {"part_couverte_par_tests"}
LIBELLES = {"cout_par_resultat_accepte": "1. coût par résultat accepté",
            "temps_resultat_fiable_min": "2. temps jusqu'au résultat fiable (min, médiane)",
            "corrections_repetees": "3. corrections répétées",
            "taux_escalade_humaine": "4. taux d'escalade humaine",
            "part_couverte_par_tests": "5. part couverte par des tests",
            "taux_redecouverte": "6. taux de redécouverte"}
USAGE_VERS_PRIX = {"input_tokens": "input", "cache_creation_input_tokens": "cache_creation",
                   "cache_read_input_tokens": "cache_read", "output_tokens": "output"}
LONGUEUR_CORRECTION = 200
RE_ACCEPTE = re.compile(r"r[ée]sultat\s+accept[ée]\s*:\s*(oui|non)", re.IGNORECASE)


# --- étalon ------------------------------------------------------------------ #
def charger_etalon(dossier: str) -> dict:
    """prix.json est obligatoire ; le reste a un défaut raisonnable et se dit dans le rapport."""
    if not os.path.isdir(dossier) or not os.path.isfile(os.path.join(dossier, "prix.json")):
        if dossier == ETALON_DIR and os.path.isfile(os.path.join(ETALON_MODELE, "prix.json")):
            dossier = ETALON_MODELE
        else:
            raise FileNotFoundError(f"grille de prix absente : {os.path.join(dossier, 'prix.json')}")
    with open(os.path.join(dossier, "prix.json"), encoding="utf-8") as f:
        prix = json.load(f)
    seuils = {"tolerance": 0.1, "par_signal": {}}
    p = os.path.join(dossier, "seuils.json")
    if os.path.isfile(p):
        with open(p, encoding="utf-8") as f:
            seuils.update(json.load(f))
    return {"dossier": dossier, "prix": prix, "seuils": seuils,
            "corrections": _lexique(os.path.join(dossier, "corrections.txt"), ["non", "pas ça", "je t'ai dit", "encore"]),
            "commandes_test": _lexique(os.path.join(dossier, "commandes-test.txt"), ["pytest", "vitest", "test_hooks.py", "dotnet test", "npm test"])}


def _lexique(chemin: str, defaut: list) -> list:
    if not os.path.isfile(chemin):
        return defaut
    with open(chemin, encoding="utf-8") as f:
        termes = [l.strip().lower() for l in f if l.strip() and not l.startswith("#")]
    return termes or defaut


# --- une session ------------------------------------------------------------- #
def cout(session: lt.Session, prix: dict) -> tuple:
    """(coût, modèles sans prix). Un modèle absent de la grille coûte 0 et se dit : jamais tu."""
    grille = prix.get("par_million") or {}
    total, sans_prix = 0.0, []
    for modele, usage in session.usage_par_modele().items():
        tarif = grille.get(modele) or prix.get("defaut")
        if not isinstance(tarif, dict):
            if any(usage.values()):
                sans_prix.append(modele)
            continue
        for cle, poste in USAGE_VERS_PRIX.items():
            total += usage.get(cle, 0) * float(tarif.get(poste, 0)) / 1_000_000
    return round(total, 6), sorted(sans_prix)


def corrections(session: lt.Session, lexique: list) -> int:
    n, precedent_en_erreur = 0, False
    for t in session.tours:
        if t.role == "humain":
            texte = t.texte.strip().lower().lstrip("«\"'( ")
            if precedent_en_erreur or (len(texte) <= LONGUEUR_CORRECTION and any(texte.startswith(m) for m in lexique)):
                n += 1
            precedent_en_erreur = False
        elif t.role == "resultat":
            precedent_en_erreur = t.erreur
        else:
            precedent_en_erreur = False
    return n


def a_teste(session: lt.Session, commandes_test: list) -> bool:
    return any(m in c.lower() for c in session.commandes() for m in commandes_test)


def passation(session: lt.Session, apprentissage_dir: str):
    """Ce que dit la passation, si elle existe : True / False, ou None sans ligne « résultat accepté »."""
    p = os.path.join(apprentissage_dir, "passations", session.session_id + ".md")
    if not os.path.isfile(p):
        return None
    with open(p, encoding="utf-8", errors="replace") as f:
        m = RE_ACCEPTE.search(f.read())
    return None if m is None else m.group(1).lower() == "oui"


def mesurer_session(session: lt.Session, etalon: dict, apprentissage_dir: str, vus_avant: dict) -> dict:
    """Une ligne du rapport. `vus_avant` : fichier → nombre de sessions antérieures qui l'ont lu."""
    c, sans_prix = cout(session, etalon["prix"])
    commits = session.commits()
    dit_passation = passation(session, apprentissage_dir)
    if dit_passation is not None:
        acceptes, source = int(dit_passation), "passation"
    else:
        acceptes, source = (1 if commits else 0), ("commit" if commits else "aucun")
    debut, fin = session.debut, session.fin
    temps = None
    if acceptes and debut is not None:
        horo_commit = next((t.horodatage for t in reversed(session.appels_outils())
                            if t.entree and any(m in str(t.entree.get("command", "")) for m in lt.MARQUES_COMMIT)), None)
        borne = horo_commit if (commits and horo_commit) else fin
        if borne is not None:
            temps = round((borne - debut).total_seconds() / 60, 2)
    lus = session.fichiers_lus()
    redecouverts = sorted(f for f in lus if vus_avant.get(f, 0) >= 2)
    return {"session": session.session_id, "debut": debut.isoformat() if debut else None, "fin": fin.isoformat() if fin else None,
            "duree_min": round((fin - debut).total_seconds() / 60, 2) if debut and fin else None,
            "modeles": sorted(session.modeles()), "modeles_sans_prix": sans_prix,
            "requetes": session.requetes, "sous_agents": dict(session.sous_agents), "messages_humains": len(session.humains()), "appels_outils": len(session.appels_outils()),
            "tokens": session.usage_total(), "cout": c,
            "resultats_acceptes": acceptes, "accepte_source": source, "temps_resultat_fiable_min": temps,
            "corrections": corrections(session, etalon["corrections"]),
            "escalades": session.escalades(), "refus_permission": session.refus_permission(),
            "a_modifie": bool(session.fichiers_modifies()), "a_teste": a_teste(session, etalon["commandes_test"]),
            "fichiers_lus": len(lus), "redecouverts": redecouverts}


# --- une semaine ------------------------------------------------------------- #
def agreger(semaine: str, lignes: list, compteur_lectures: dict, devise: str = "USD") -> dict:
    cout_total = sum(l["cout"] for l in lignes)
    acceptes = sum(l["resultats_acceptes"] for l in lignes)
    temps = [l["temps_resultat_fiable_min"] for l in lignes if l["temps_resultat_fiable_min"] is not None]
    requetes = sum(l["requetes"] for l in lignes)
    modifiantes = [l for l in lignes if l["a_modifie"]]
    lus = sum(l["fichiers_lus"] for l in lignes)
    redecouverts = sum(len(l["redecouverts"]) for l in lignes)
    signaux = {"cout_par_resultat_accepte": round(cout_total / acceptes, 6) if acceptes else None,
               "temps_resultat_fiable_min": round(statistics.median(temps), 2) if temps else None,
               "corrections_repetees": sum(l["corrections"] for l in lignes),
               "taux_escalade_humaine": round(sum(l["escalades"] + l["refus_permission"] for l in lignes) / requetes, 6) if requetes else None,
               "part_couverte_par_tests": round(sum(1 for l in modifiantes if l["a_teste"]) / len(modifiantes), 6) if modifiantes else None,
               "taux_redecouverte": round(redecouverts / lus, 6) if lus else None}
    top = sorted(((f, n) for f, n in compteur_lectures.items() if n >= 3), key=lambda x: (-x[1], x[0]))[:10]
    return {"semaine": semaine, "sessions_mesurees": len(lignes), "cout": round(cout_total, 6), "resultats_acceptes": acceptes,
            "devise": devise,
            "modeles_sans_prix": sorted({m for l in lignes for m in l["modeles_sans_prix"]}),
            "signaux": signaux, "top_redecouverts": [[f, n] for f, n in top], "sessions": lignes}


def delta(courant: dict, precedent: dict, seuils: dict) -> dict:
    """Ce qui s'est amélioré ou dégradé au-delà de la tolérance. Un signal absent d'un côté ne juge rien."""
    tol_defaut = float(seuils.get("tolerance", 0.1))
    par_signal = seuils.get("par_signal") or {}
    ameliores, degrades, details = [], [], {}
    for s in SIGNAUX:
        a, b = precedent["signaux"].get(s), courant["signaux"].get(s)
        if a is None or b is None:
            details[s] = {"avant": a, "apres": b, "variation": None}
            continue
        base = abs(a) if a else 1.0
        variation = (b - a) / base
        sens = -variation if s in PLUS_HAUT_EST_MIEUX else variation   # > 0 = pire
        tol = float(par_signal.get(s, tol_defaut))
        details[s] = {"avant": a, "apres": b, "variation": round(variation, 4)}
        if sens > tol:
            degrades.append(s)
        elif sens < -tol:
            ameliores.append(s)
    return {"semaine_precedente": precedent["semaine"], "tolerance": tol_defaut, "ameliores": ameliores, "degrades": degrades, "details": details}


# --- dépréciation ------------------------------------------------------------ #
def deprecier(apprentissage_dir: str, aujourdhui: date, racine: str) -> list:
    """Passe `deprecie` les candidats promus dont la validité est passée ou dont la cible a disparu.
    Renvoie les ids dépréciés. Le fichier est réécrit ligne à ligne, jamais tronqué en silence."""
    p = os.path.join(apprentissage_dir, "candidats.jsonl")
    if not os.path.isfile(p):
        return []
    with open(p, encoding="utf-8") as f:
        brut = [l for l in f.read().splitlines() if l.strip()]
    lignes, deprecies = [], []
    for l in brut:
        try:
            c = json.loads(l)
        except json.JSONDecodeError:
            lignes.append(l); continue
        if isinstance(c, dict) and c.get("statut") == "promu":
            motif = None
            cible = c.get("promu_vers")
            if cible and not os.path.exists(cible if os.path.isabs(cible) else os.path.join(racine, cible)):
                motif = f"cible absente : {cible}"
            vj = c.get("valide_jusqua")
            if vj and _date(vj) is not None and _date(vj) < aujourdhui:
                motif = f"validité échue le {vj}"
            if motif:
                c["statut"], c["motif"], c["deprecie_le"] = "deprecie", motif, aujourdhui.isoformat()
                deprecies.append(str(c.get("id")))
            lignes.append(json.dumps(c, ensure_ascii=False))
        else:
            lignes.append(l)
    if deprecies:
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lignes) + "\n")
    return deprecies


def _date(v):
    try:
        return date.fromisoformat(str(v)[:10])
    except ValueError:
        return None


# --- rendu ------------------------------------------------------------------- #
def en_markdown(r: dict) -> str:
    L = [f"# Semaine {r['semaine']} — {r['sessions_mesurees']} session(s), {r['cout']:.2f} {r['devise']}, {r['resultats_acceptes']} résultat(s) accepté(s)", ""]
    if r["modeles_sans_prix"]:
        L += [f"**Modèles sans prix dans la grille** (coût compté 0) : {', '.join(r['modeles_sans_prix'])}", ""]
    L += ["| Signal | Valeur | Semaine précédente | Variation |", "|---|---|---|---|"]
    d = r.get("delta") or {}
    for s in SIGNAUX:
        v = r["signaux"].get(s)
        det = (d.get("details") or {}).get(s) or {}
        avant, var = det.get("avant"), det.get("variation")
        marque = " ⚠" if s in d.get("degrades", []) else (" ✓" if s in d.get("ameliores", []) else "")
        L.append(f"| {LIBELLES[s]} | {_fmt(v)} | {_fmt(avant)} | {_pct(var)}{marque} |")
    L.append("")
    if d:
        L += [f"Delta sur {d['semaine_precedente']}, tolérance {d['tolerance']:.0%} : "
              f"{len(d['ameliores'])} amélioré(s), {len(d['degrades'])} dégradé(s)" + (" — **le système n'apprend pas cette semaine**" if d["degrades"] else ""), ""]
    if r["top_redecouverts"]:
        L += ["## Fichiers redécouverts (lus dans ≥ 3 sessions) — candidats à `docs/INDEX.md`", ""]
        L += [f"- `{f}` — {n} sessions" for f, n in r["top_redecouverts"]] + [""]
    if r.get("deprecies"):
        L += ["## Candidats dépréciés à cette passe", ""] + [f"- {i}" for i in r["deprecies"]] + [""]
    L += ["## Sessions", "", "| Session | Début | Coût | Accepté | Corrections | Escalades | Tests | Redécouverts |", "|---|---|---|---|---|---|---|---|"]
    for s in r["sessions"]:
        L.append(f"| {s['session'][:8]} | {(s['debut'] or '?')[:16]} | {s['cout']:.2f} | {s['resultats_acceptes']} ({s['accepte_source']}) | {s['corrections']} | "
                 f"{s['escalades'] + s['refus_permission']} | {'oui' if s['a_teste'] else ('non' if s['a_modifie'] else '—')} | {len(s['redecouverts'])}/{s['fichiers_lus']} |")
    return "\n".join(L) + "\n"


def _fmt(v):
    if v is None:
        return "—"
    return f"{v:.3f}" if isinstance(v, float) else str(v)


def _pct(v):
    return "—" if v is None else f"{v:+.0%}"


# --- entrée ------------------------------------------------------------------ #
def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--projet", required=True, help="nom sous ~/.claude/projects (ex. d--HorusCoachIA) ou dossier de transcripts")
    p.add_argument("--projets-dir", default=PROJETS_DIR)
    p.add_argument("--etalon-dir", default=ETALON_DIR, help="prix.json, seuils.json, corrections.txt, commandes-test.txt")
    p.add_argument("--apprentissage-dir", default=APPRENTISSAGE_DIR, help="passations/ et candidats.jsonl du projet")
    p.add_argument("--out-dir", default=ETALON_DIR)
    p.add_argument("--semaine", default="", help="ne mesurer que cette semaine ISO (ex. 2026-W38) ; défaut : toutes")
    p.add_argument("--aujourdhui", default="", help="date de référence pour la dépréciation (test)")
    p.add_argument("--json", action="store_true", help="imprimer le dernier rapport en JSON")
    p.add_argument("--quiet", action="store_true")
    a = p.parse_args(argv)
    lt._stdout_utf8()

    dossier = a.projet if os.path.isdir(a.projet) else os.path.join(a.projets_dir, a.projet)
    if not os.path.isdir(dossier):
        print(f"transcripts introuvables : {dossier}", file=sys.stderr); return 2
    try:
        etalon = charger_etalon(a.etalon_dir)
    except (FileNotFoundError, json.JSONDecodeError) as e:
        print(str(e), file=sys.stderr); return 2

    # Chronologique : la redécouverte se compte contre les sessions *antérieures*.
    par_semaine: dict = {}
    vus: dict = {}
    vus_fin_semaine: dict = {}   # l'état du compteur à la fin de chaque semaine, pour son top 10
    for s in lt.iter_sessions(dossier):
        sem = s.semaine()
        if sem is None:
            continue
        ligne = mesurer_session(s, etalon, a.apprentissage_dir, vus)
        par_semaine.setdefault(sem, []).append(ligne)
        for f in s.fichiers_lus():
            vus[f] = vus.get(f, 0) + 1
        vus_fin_semaine[sem] = dict(vus)
    if not par_semaine:
        print(f"aucune session datée dans {dossier}", file=sys.stderr); return 2

    aujourdhui = _date(a.aujourdhui) or date.today()
    deprecies = deprecier(a.apprentissage_dir, aujourdhui, os.getcwd())
    os.makedirs(a.out_dir, exist_ok=True)
    semaines = sorted(par_semaine)
    rapports, code, dernier = {}, 0, None
    for i, sem in enumerate(semaines):
        r = agreger(sem, par_semaine[sem], vus_fin_semaine[sem], str(etalon["prix"].get("devise") or "USD"))
        r["delta"] = delta(r, rapports[semaines[i - 1]], etalon["seuils"]) if i else None
        r["deprecies"] = deprecies if i == len(semaines) - 1 else []
        r["etalon"] = etalon["dossier"]
        rapports[sem] = r
        if a.semaine and sem != a.semaine:
            continue
        with open(os.path.join(a.out_dir, f"semaine-{sem}.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(r, f, ensure_ascii=False, indent=1)
        with open(os.path.join(a.out_dir, f"semaine-{sem}.md"), "w", encoding="utf-8", newline="\n") as f:
            f.write(en_markdown(r))
        dernier = r
        if r["delta"] and r["delta"]["degrades"]:
            code = 1
    if dernier is None:
        print(f"semaine {a.semaine} absente ; semaines mesurées : {', '.join(semaines)}", file=sys.stderr); return 2
    if a.json:
        print(json.dumps(dernier, ensure_ascii=False, indent=1))
    elif not a.quiet:
        print(en_markdown(dernier))
        print(f"→ {a.out_dir}  (code {code})")
    return code


if __name__ == "__main__":
    sys.exit(main())
