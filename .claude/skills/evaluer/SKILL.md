---
name: evaluer
description: Fait repasser l'examen à l'agent de chat par tâche (vrai prompt PromptBuilder + vrai cerveau), le note avec eval/juger.py, compare à eval/reference.json et résume les régressions. À utiliser pour "évalue", "rejoue l'examen", "lance l'éval", "est-ce que j'ai cassé l'agent", ou après toute modification de PromptBuilder.cs, Llm.cs ou des réglages du modèle.
argument-hint: "[OpenAICompatible|AnthropicApi] [--cas 04,12]"
---

L'examen mesure l'agent ; il ne s'ajuste jamais pour lui. **Ne modifie jamais `eval/cas/` ni `eval/reference.json` pour « faire passer »** : un cas qui échoue est une information, pas un bug du cas. Si un cas ou le juge te paraît faux, dis-le et laisse l'humain décider.

1. **Compiler.** `dotnet build -nologo -v q`. Si la compilation échoue parce que `bin\Debug\TaskAgent.exe` est verrouillé, l'appli de dev est ouverte : demande de la fermer.
2. **Faire passer l'examen.** `dotnet run --no-build -- --eval eval/cas/cas.jsonl --sortie eval/runs/<AAAA-MM-JJ_HHmm>.jsonl`, plus `--fournisseur <X>` si $ARGUMENTS en nomme un (Qwen = `OpenAICompatible` : vérifier d'abord `curl -s http://localhost:11434/v1/models`), plus `--cas …` si demandé. Code 2 = panne (cerveau injoignable, délai dépassé) : rapporte le message, ne juge pas.
3. **Juger.** `python eval/juger.py eval/runs/<run>.jsonl` (avec `PYTHONIOENCODING=utf-8` sous Git Bash). Codes : 0 pas de régression · 1 régression · 2 panne.
4. **Résumer**, en commençant par le verdict (régression ou non). Pour chaque cas en baisse : critère échoué, extrait de la réponse fautive (`reponse` dans le run), cause probable. Les réponses de l'IA varient d'un passage à l'autre : avant de conclure à une régression sur un seul cas, rejoue-le une fois (`--cas <id>`).
5. **Référence.** `--ecrire-reference` uniquement si l'humain le demande explicitement, avec ce run précis. Jamais de ta propre initiative.

Ne lance pas `deploy.ps1` : l'éval n'est pas une fonctionnalité visible.
