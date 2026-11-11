---
name: capitaliser
description: La revue hebdomadaire de la boucle d'apprentissage — mesure les six signaux, pose les quatre questions (redécouvert, corrections répétées, à promouvoir, à supprimer) sur la file de candidats, et promeut ce que l'humain valide vers docs / skill / test / hook ; à utiliser pour "capitalise", "revue de la semaine", "qu'est-ce qu'on a appris", "le système apprend-il".
disable-model-invocation: true
argument-hint: "[projet, ex. d--HorusCoachIA] [--depuis 7j]"
---

Revue de $ARGUMENTS, dans la conversation principale : c'est ici que l'humain est, et c'est lui qui promeut. Tu proposes, tu écris ce qu'il a validé, tu n'inventes aucun apprentissage.

Le projet est le nom du dossier sous `~/.claude/projects` (ex. `d--HorusCoachIA`) ; sans argument, déduis-le du répertoire courant (`D:\X\Y` → `d--X-Y`). Les fichiers : `.claude/apprentissage/candidats.jsonl` (la file), `.claude/apprentissage/passations/`, `eval/apprentissage/semaine-*.md` (les mesures).

1. **Mesurer.** `python eval/mesurer_sessions.py --projet <projet>`. Affiche le tableau des six signaux et le delta tel quel. Code 1 = au moins un signal s'est dégradé : dis-le en premier, avant tout le reste — c'est la seule phrase qui compte si le système n'apprend pas. Code 2 = grille de prix ou transcripts absents : arrête-toi là et donne la commande qui corrige.
2. **Extraire.** Si `extraits.json` ne couvre pas les sessions de la période, `python eval/extraire_candidats.py --projet <projet> --depuis 7j`. Qwen doit être résident (`python mac-ia/router/profiles.py status`) ; sinon code 2, tu le rapportes, tu ne remplaces pas Qwen par toi-même.
3. **Les quatre questions**, chacune alimentée par un fichier, jamais par ta mémoire de la semaine :
   - *Qu'avons-nous redécouvert ?* ← `top_redecouverts` du rapport de la semaine + candidats `type: fait` en `propose`. Un fichier lu dans trois sessions est un candidat à `docs/INDEX.md`.
   - *Quelles corrections se sont répétées ?* ← candidats avec `recurrence ≥ 2`. Deux fois = examen pour promotion, c'est la règle.
   - *Que faut-il promouvoir ?* ← les `propose`, groupés par `niveau_propose`, avec `enonce`, `preuve`, `verifiable_par`, `confiance`.
   - *Que faut-il supprimer ?* ← les `deprecie` de cette passe (motif : validité échue ou cible disparue) et les `promu` dont `valide_jusqua` tombe dans le mois.
4. **Porte humaine, par candidat.** Pour chacun, l'humain dit : *promeut vers `<chemin>`* (avec une date de validité), *rejette `<motif>`*, ou *épisode*. Tu ne regroupes pas, tu ne présumes pas, tu ne promeus rien qu'il n'a pas nommé. Un candidat sans `verifiable_par` promu au niveau `test` ou `hook` : demande d'abord ce qui le vérifiera.
5. **Écrire la cible et la trace dans le même geste.** Selon le niveau : `doc` → un fragment court dans `docs/` **et** sa ligne dans `docs/INDEX.md` (description qui permet de décider sans ouvrir) ; `skill` → `.claude/skills/<nom>/SKILL.md` avec prérequis, étapes, état attendu, critère de fin ; `test` → une sonde dans `.claude/hooks/test_hooks.py` ou un cas dans `eval/` ; `hook` → une garde, **une seule modification de hook ou de garde par cycle** et `python .claude/hooks/test_hooks.py` doit sortir 0 ; `code` → un script, avec son test. Puis mets à jour la ligne du candidat : `statut`, `promu_vers`, `promu_le`, `valide_jusqua` (ou `motif`). Le fichier reste du JSONL valide contre `eval/schemas/candidat.schema.json`.
6. **Déprécier proprement.** Pour un `deprecie`, retire la cible ou marque-la caduque (ne laisse pas une règle fausse en place « au cas où »), et dis-le dans la trace.
7. **Arrêt.** Trois lignes : ce qui a été promu (chemins), ce qui reste `propose` (nombre, plus ancien), la date de la prochaine revue. Rien d'autre. Ne commite pas : l'humain relit le diff.
