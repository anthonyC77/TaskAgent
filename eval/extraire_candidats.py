#!/usr/bin/env python3
"""Extraire d'une session ce que le prochain agent ne devrait jamais redécouvrir.

    python3 eval/extraire_candidats.py --projet d--HorusCoachIA --depuis 7j
    python3 eval/extraire_candidats.py --projet d--HorusCoachIA --session 5b479542

Le jugement (« est-ce récurrent, stable, réutilisable ? ») est fait par Qwen sur le Mac,
hors du système évalué — le même serveur que eval/juge_local.py. Jamais Claude : la
boucle qui réduit la facture n'en crée pas une autre.

Ce que Qwen reçoit : un *digest* par session (lire_transcripts.digest) — messages humains,
entrées d'outils sans leurs résultats, la passation si elle existe. Quelques milliers de
tokens, pas le transcript. Un digest par appel : un budget partagé avec la réflexion rend
une réponse vide (finish_reason: length, vu le 13 septembre 2026).

Ce que le script fait de la réponse :
  - valide chaque candidat contre eval/schemas/candidat.schema.json ; une ligne invalide
    va dans candidats.rejetes.jsonl avec sa raison, jamais perdue en silence ;
  - dédoublonne sans LLM : un énoncé proche d'un candidat existant incrémente `recurrence`
    au lieu d'ajouter une ligne — c'est ce compteur qui répond à « si deux fois, examen » ;
  - note la session dans extraits.json : une session n'est extraite qu'une fois, sinon la
    récurrence se gonfle toute seule (--refaire pour repasser).

Il n'écrit que sous --apprentissage-dir (.claude/apprentissage/). La promotion est un
geste humain, dans /capitaliser.

Codes : 0 au moins une session extraite ; 1 une session n'a pas donné de JSON exploitable
(réponse tronquée, JSON absent) ; 2 serveur injoignable, transcripts introuvables.

Configuration : HARNAIS_JUGE_URL (http://192.168.1.50:1234/v1), HARNAIS_JUGE_MODELE (brain),
HARNAIS_JUGE_TIMEOUT (900 s ici : l'extraction est plus longue qu'un verdict).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import unicodedata
import urllib.error
import urllib.request
from datetime import date, datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import jsonschema_lite  # noqa: E402
import lire_transcripts as lt  # noqa: E402

URL = os.environ.get("HARNAIS_JUGE_URL", "http://192.168.1.50:1234/v1")
MODELE = os.environ.get("HARNAIS_JUGE_MODELE", "brain")
TIMEOUT = float(os.environ.get("HARNAIS_JUGE_TIMEOUT", "900"))
PROJETS_DIR = os.path.join(os.path.expanduser("~"), ".claude", "projects")
APPRENTISSAGE_DIR = os.path.join(".claude", "apprentissage")
SCHEMA = os.path.join(HERE, "schemas", "candidat.schema.json")
CONSIGNE = os.path.join(HERE, "consigne-extraction.md")
CHAMPS_MODELE = ("type", "enonce", "preuve", "niveau_propose", "applicable", "verifiable_par", "confiance")
SEUIL_DOUBLON = 0.6
MOTS_VIDES = {"les", "des", "dans", "pas", "une", "est", "et", "non", "avec", "pour", "sur", "que", "qui", "the", "and"}


# --- schéma et consigne -------------------------------------------------------- #
def charger_schema(chemin: str = SCHEMA) -> dict:
    with open(chemin, encoding="utf-8") as f:
        return json.load(f)


def charger_consigne(chemin: str = CONSIGNE) -> str:
    with open(chemin, encoding="utf-8") as f:
        return f.read().strip()


def schema_de_reponse(schema: dict) -> dict:
    """Ce que le modèle doit rendre : les seuls champs qu'il connaît, sous {"candidats": [...]}."""
    props = {k: schema["properties"][k] for k in CHAMPS_MODELE}
    return {"type": "object", "additionalProperties": False, "required": ["candidats"],
            "properties": {"candidats": {"type": "array", "items": {"type": "object", "additionalProperties": False,
                                                                     "required": list(CHAMPS_MODELE), "properties": props}}}}


# --- digest ------------------------------------------------------------------ #
def composer(session: lt.Session, apprentissage_dir: str) -> str:
    texte = lt.digest(session)
    p = os.path.join(apprentissage_dir, "passations", session.session_id + ".md")
    if os.path.isfile(p):
        with open(p, encoding="utf-8", errors="replace") as f:
            texte += "\n\nPASSATION ÉCRITE EN FIN DE SESSION :\n" + f.read().strip()
    return texte


# --- appel -------------------------------------------------------------------- #
def appeler(url: str, modele: str, consigne: str, digest: str, schema_reponse: dict, max_tokens: int, timeout: float) -> dict:
    corps = {"model": modele, "temperature": 0, "stream": False, "max_tokens": max_tokens,
             "messages": [{"role": "system", "content": consigne + " /no_think"}, {"role": "user", "content": digest}],
             "response_format": {"type": "json_schema", "json_schema": {"name": "candidats", "strict": True, "schema": schema_reponse}}}
    req = urllib.request.Request(f"{url.rstrip('/')}/chat/completions", data=json.dumps(corps).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def extraire_json(texte: str):
    """Le dernier objet JSON portant `candidats` — un modèle à réflexion parle avant de répondre."""
    dec = json.JSONDecoder()
    for i in range(len(texte) - 1, -1, -1):
        if texte[i] != "{":
            continue
        try:
            obj, _ = dec.raw_decode(texte[i:])
        except ValueError:
            continue
        if isinstance(obj, dict) and isinstance(obj.get("candidats"), list):
            return obj["candidats"]
    return None


def lire_reponse(reponse: dict):
    """(candidats, motif d'échec). Une réponse tronquée est un échec, pas une liste vide."""
    try:
        choix = reponse["choices"][0]
        message = choix.get("message") or {}
        texte = message.get("content") or choix.get("content") or ""
        # LM Studio + response_format + /no_think (Qwen3, 14 sept. 2026) : le JSON arrive dans
        # `reasoning_content`, `content` reste vide. On lit les deux, le contenu d'abord.
        reflexion = message.get("reasoning_content") or message.get("reasoning") or ""
    except (KeyError, IndexError, TypeError, AttributeError):
        return None, f"réponse inattendue : {json.dumps(reponse)[:200]}"
    cands = extraire_json(texte)
    if cands is None and reflexion:
        cands = extraire_json(reflexion)
    if cands is None:
        if choix.get("finish_reason") == "length":
            return None, "max_tokens atteint avant le JSON (la réflexion a mangé le budget)"
        return None, f"aucun JSON dans la réponse : {texte[-200:]!r}"
    return cands, None


# --- dédoublonnage ----------------------------------------------------------- #
def _mots(texte: str) -> set:
    t = unicodedata.normalize("NFKD", texte.lower())
    t = "".join(c for c in t if not unicodedata.combining(c))
    return {m for m in re.findall(r"[a-z0-9_./\-]{3,}", t) if m not in MOTS_VIDES}


def similarite(a: str, b: str) -> float:
    """Jaccard sur les mots de plus de deux lettres, accents et mots vides retirés."""
    ma, mb = _mots(a), _mots(b)
    if not ma or not mb:
        return 0.0
    return len(ma & mb) / len(ma | mb)


def doublon_de(candidat: dict, existants: list):
    for e in existants:
        if e.get("statut") in ("propose", "promu") and e.get("type") == candidat.get("type") \
                and similarite(e.get("enonce", ""), candidat.get("enonce", "")) >= SEUIL_DOUBLON:
            return e
    return None


# --- fichiers ------------------------------------------------------------------ #
def lire_jsonl(p: str) -> list:
    if not os.path.isfile(p):
        return []
    with open(p, encoding="utf-8") as f:
        out = []
        for l in f:
            l = l.strip()
            if l:
                try:
                    out.append(json.loads(l))
                except json.JSONDecodeError:
                    pass
        return out


def ecrire_jsonl(p: str, lignes: list) -> None:
    os.makedirs(os.path.dirname(p) or ".", exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        for l in lignes:
            f.write(json.dumps(l, ensure_ascii=False) + "\n")


def prochain_id(semaine: str, existants: list) -> str:
    n = max((int(c["id"].rsplit("-", 1)[1]) for c in existants
             if isinstance(c.get("id"), str) and c["id"].startswith(f"c-{semaine}-")), default=0) + 1
    return f"c-{semaine}-{n:03d}"


# --- une session --------------------------------------------------------------- #
def integrer(session: lt.Session, bruts: list, existants: list, rejets: list, schema: dict, aujourdhui: str, refaire: bool) -> dict:
    """Valide, dédoublonne, ajoute. Modifie `existants` et `rejets` en place. Renvoie un bilan."""
    semaine = session.semaine() or aujourdhui[:4] + "-W00"
    bilan = {"session": session.session_id, "ajoutes": 0, "doublons": 0, "rejetes": 0}
    for brut in bruts:
        if not isinstance(brut, dict):
            rejets.append({"session": session.session_id, "date": aujourdhui, "motif": "candidat non objet", "candidat": brut}); bilan["rejetes"] += 1
            continue
        c = {k: brut.get(k) for k in CHAMPS_MODELE}
        c.update({"id": prochain_id(semaine, existants), "session": session.session_id, "date": aujourdhui,
                  "recurrence": 1, "statut": "episode" if c.get("type") == "episode" else "propose",
                  "sessions_vues": [session.session_id], "derniere_vue": (session.debut or datetime.now(timezone.utc)).date().isoformat()})
        if c.get("verifiable_par") is None:
            c["verifiable_par"] = ""
        erreurs = jsonschema_lite.validate(c, schema)
        if erreurs:
            rejets.append({"session": session.session_id, "date": aujourdhui, "motif": "; ".join(map(str, erreurs))[:400], "candidat": brut}); bilan["rejetes"] += 1
            continue
        d = doublon_de(c, existants)
        if d is not None:
            if session.session_id not in d.get("sessions_vues", []):
                d["recurrence"] = int(d.get("recurrence", 1)) + 1
                d.setdefault("sessions_vues", []).append(session.session_id)
                d["derniere_vue"] = max(str(d.get("derniere_vue", "")), c["derniere_vue"])
                if float(c.get("confiance") or 0) > float(d.get("confiance") or 0):
                    d["confiance"] = c["confiance"]
            bilan["doublons"] += 1
            continue
        existants.append(c); bilan["ajoutes"] += 1
    return bilan


def _duree(v: str) -> timedelta:
    m = re.fullmatch(r"(\d+)([jhd]?)", v.strip())
    if not m:
        raise ValueError(f"durée illisible : {v!r} (ex. 7j, 48h)")
    n, u = int(m.group(1)), m.group(2)
    return timedelta(hours=n) if u == "h" else timedelta(days=n)


# --- entrée ------------------------------------------------------------------ #
def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--projet", required=True, help="nom sous ~/.claude/projects ou dossier de transcripts")
    p.add_argument("--projets-dir", default=PROJETS_DIR)
    p.add_argument("--apprentissage-dir", default=APPRENTISSAGE_DIR)
    p.add_argument("--depuis", default="", help="ne traiter que les sessions commencées depuis (ex. 7j, 48h)")
    p.add_argument("--session", default="", help="préfixe d'un identifiant de session")
    p.add_argument("--refaire", action="store_true", help="repasser sur des sessions déjà extraites")
    p.add_argument("--reponses-fichier", default="", help="réponses enregistrées par session (test hors ligne)")
    p.add_argument("--url", default=URL); p.add_argument("--modele", default=MODELE)
    p.add_argument("--timeout", type=float, default=TIMEOUT); p.add_argument("--max-tokens", type=int, default=4096)
    p.add_argument("--schema", default=SCHEMA); p.add_argument("--consigne", default=CONSIGNE)
    p.add_argument("--aujourdhui", default="")
    p.add_argument("--quiet", action="store_true")
    a = p.parse_args(argv)
    lt._stdout_utf8()

    dossier = a.projet if os.path.isdir(a.projet) else os.path.join(a.projets_dir, a.projet)
    if not os.path.isdir(dossier):
        print(f"transcripts introuvables : {dossier}", file=sys.stderr); return 2
    schema, consigne = charger_schema(a.schema), charger_consigne(a.consigne)
    schema_rep = schema_de_reponse(schema)
    aujourdhui = a.aujourdhui or date.today().isoformat()
    reponses = None
    if a.reponses_fichier:
        with open(a.reponses_fichier, encoding="utf-8") as f:
            reponses = json.load(f)

    chemin_c = os.path.join(a.apprentissage_dir, "candidats.jsonl")
    chemin_r = os.path.join(a.apprentissage_dir, "candidats.rejetes.jsonl")
    chemin_e = os.path.join(a.apprentissage_dir, "extraits.json")
    existants, rejets = lire_jsonl(chemin_c), lire_jsonl(chemin_r)
    extraits = {}
    if os.path.isfile(chemin_e):
        with open(chemin_e, encoding="utf-8") as f:
            extraits = json.load(f)

    limite = None
    if a.depuis:
        limite = datetime.now(timezone.utc) - _duree(a.depuis)
    traitees, echecs = 0, 0
    for s in lt.iter_sessions(dossier):
        if a.session and not s.session_id.startswith(a.session):
            continue
        if limite and (s.debut is None or s.debut < limite):
            continue
        if not s.humains():
            continue   # rien d'humain : rien à apprendre
        if s.session_id in extraits and not a.refaire:
            continue
        if reponses is not None:
            rep = reponses.get(s.session_id)
            if rep is None:
                continue
            reponse = {"choices": [{"message": {"content": rep.get("content", ""), "reasoning_content": rep.get("reasoning_content", "")}, "finish_reason": rep.get("finish_reason", "stop")}]}
        else:
            try:
                reponse = appeler(a.url, a.modele, consigne, composer(s, a.apprentissage_dir), schema_rep, a.max_tokens, a.timeout)
            except urllib.error.HTTPError as e:
                print(f"extraction : HTTP {e.code} sur {a.url} : {e.reason}", file=sys.stderr); return 2
            except (urllib.error.URLError, OSError, ValueError) as e:
                print(f"modèle injoignable ({a.url}) : {e}", file=sys.stderr); return 2
        bruts, motif = lire_reponse(reponse)
        if bruts is None:
            echecs += 1
            if not a.quiet:
                print(f"{s.session_id[:8]} : échec — {motif}", file=sys.stderr)
            continue
        if a.refaire:
            # Repasser sur une session : ses propres lignes d'abord retirées, sa marque dans les doublons aussi.
            existants = [c for c in existants if c.get("session") != s.session_id or len(c.get("sessions_vues", [])) > 1]
            for c in existants:
                if s.session_id in c.get("sessions_vues", []) and c.get("session") != s.session_id:
                    c["sessions_vues"].remove(s.session_id); c["recurrence"] = max(1, int(c.get("recurrence", 1)) - 1)
        bilan = integrer(s, bruts, existants, rejets, schema, aujourdhui, a.refaire)
        extraits[s.session_id] = aujourdhui
        traitees += 1
        if not a.quiet:
            print(f"{s.session_id[:8]} : {bilan['ajoutes']} ajouté(s), {bilan['doublons']} doublon(s), {bilan['rejetes']} rejeté(s)")
    if traitees:
        ecrire_jsonl(chemin_c, existants)
        if rejets:
            ecrire_jsonl(chemin_r, rejets)
        os.makedirs(a.apprentissage_dir, exist_ok=True)
        with open(chemin_e, "w", encoding="utf-8", newline="\n") as f:
            json.dump(extraits, f, ensure_ascii=False, indent=1, sort_keys=True)
    if not a.quiet:
        print(f"{traitees} session(s) extraite(s), {echecs} échec(s) → {chemin_c}")
    return 1 if echecs else 0


if __name__ == "__main__":
    sys.exit(main())
