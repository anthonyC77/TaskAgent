# TaskAgent

Petit agent de bureau (C# / .NET 8 / Avalonia) qui suit tes tâches au quotidien :

- liste de tâches avec **case à cocher = terminée**
- **échéance** par tâche avec code couleur : 🟥 en retard · 🟧 ≤ 2 jours · 🟩 large · ⬜ aucune / terminée
- **âge** de la tâche (« créée il y a X j ») et temps **sans avancée**
- **sous-tâches** cochables avec barre de progression ; chaque coche est datée dans l'**historique**
- **relances automatiques** quand une tâche n'avance pas (ou que l'échéance approche) : l'appli te demande *pourquoi* (info manquante, dépendance, trop gros/flou, problème technique, pas le temps, pas envie…)
- **chat par tâche** avec une IA pour la débloquer ; l'agent termine toujours par une « Prochaine action » affichée dans la liste, et peut proposer des étapes `- [ ] …` importables en sous-tâches d'un clic

- **outil de gestion dédié** par tâche (ex. tâche « psy » suivie dans le projet Claude « Psy ») : outil, nom du projet, lien ↗ ouvrable ; affiché dans la liste et transmis à l'agent
- **mémoire vive** : jauge « RAM xx % » en haut (vert < 80 % · orange < 90 % · rouge), clic = applis les plus gourmandes regroupées comme dans le Gestionnaire des tâches, boutons **Fermer** (propre) / **Forcer**, conseils ciblés (Brave, Chrome, WSL…), et **💬 Demander à l'agent** qui crée une tâche « Réduire l'usage mémoire » avec l'état des lieux. Alerte automatique si la RAM reste au-delà de 90 %.
- **imprimer des pages web** : bouton **🖨** en haut → les pages que tu choisis, en PDF dans le dossier choisi. Colle les adresses (une par ligne, ou un texte/des favoris : les liens sont repérés), charge-les **depuis un fichier**, ou utilise **🔗 Liens d'une page…** : TaskAgent lit une page (sommaire, menu…), liste tous ses liens avec leur texte, tu coches ceux qui t'intéressent (filtre, « même site seulement »), puis tu les ajoutes à la liste ou tu les **enregistres en .txt**. Les pages sont imprimées dans l'ordre (`001 - Titre.pdf`…) par un Edge invisible piloté par Playwright (sinon Chrome, sinon Chromium téléchargé une fois), après défilement pour charger les images. Options : PDF unique fusionné, paysage, `_sommaire.txt` (fichier ↔ adresse), **Me connecter d'abord** pour les sites avec compte (session gardée dans `%LocalAppData%\TaskAgent\site-printer-profile`), et **Explorer aussi les pages reliées** pour tout un site (liens internes + `sitemap.xml`, nombre max de pages, rester sous le chemin de départ).
- **impression assistée** (sites qui renvoient 403 aux navigateurs automatisés) : bouton **🖐 Impression assistée** → chaque page de la liste s'ouvre dans ton navigateur habituel, tu fais Ctrl+P → Entrée (la 1re fois : « Enregistrer au format PDF » + le dossier choisi), TaskAgent repère le PDF arrivé, le renomme `001 - …`, ouvre la page suivante, puis crée le PDF unique et `_sommaire.txt`. Pour les liens d'une telle page : **🔗 Liens d'une page…** accepte aussi le code HTML copié depuis F12 (ou une page enregistrée en .html).
- **programmes au démarrage** : bouton **🚀 Démarrage** dans la fenêtre Mémoire → liste de ce qui se lance à l'ouverture de session (registre `Run` utilisateur et machine, dossiers Démarrage), RAM occupée en ce moment par chacun, et interrupteur Activé/Désactivé. Même mécanisme que l'onglet Démarrage du Gestionnaire des tâches (drapeau dans `Explorer\StartupApproved`, rien n'est supprimé, réversible). Les entrées « tous les utilisateurs » déclenchent une demande d'autorisation administrateur. Les applis du Microsoft Store sont dans **⚙ Paramètres Windows**.

Image mentale : la liste, c'est ton tableau de bord ; les relances, c'est le collègue qui passe la tête par la porte (« t'en es où sur X ? ») ; le chat, c'est la discussion à la machine à café qui débloque le sujet.

## Compiler et lancer

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download).

```bash
cd TaskAgent
dotnet run                     # lancer en dev

# Un seul .exe autonome pour Windows (pas besoin d'installer .NET sur la machine) :
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → bin/Release/net8.0/win-x64/publish/TaskAgent.exe
#   ⚠ garder avec l'exe les fichiers natifs publiés à côté (pdfium.dll, libSkiaSharp.dll, dossiers x64/ et x86/ de l'OCR)

# Plus tard, pour ton Mac (Apple Silicon) :
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

Les données sont dans `%AppData%\TaskAgent\` (Windows) ou `~/.config/TaskAgent/` (macOS) :
`tasks.json` (tâches, sous-tâches, historique, chats) et `settings.json` (réglages, ouvrable via ⚙).

## Choisir le « cerveau » (settings.json → `Provider`)

| Provider | Ce que ça utilise | Configuration |
|---|---|---|
| `ClaudeCode` (défaut) | **Ton abonnement Claude** (Pro/Max) via la CLI Claude Code | Installer Claude Code, lancer `claude` une fois dans un terminal et se connecter avec ton compte. L'appli appelle ensuite `claude -p` en arrière-plan. `ClaudeCliModel` : `"sonnet"`, `"opus"` ou `null`. |
| `AnthropicApi` | API Anthropic (clé API, **facturée à part** de l'abonnement) | Variable d'environnement `ANTHROPIC_API_KEY`, `AnthropicModel`. |
| `OpenAICompatible` | Un modèle local, ex. **Qwen via Ollama** ou LM Studio | `OpenAIBaseUrl` (Ollama : `http://localhost:11434/v1`), `OpenAIModel` (ex. `qwen3:8b`). |

Pourquoi Claude Code pour l'abonnement ? L'abonnement claude.ai n'a pas de clé API ; la CLI Claude Code, elle, sait s'authentifier avec ton compte Pro/Max. L'appli s'en sert comme d'un « traducteur » local.

Sur Mac, si ton Qwen tourne sur une autre machine du réseau, mets son IP dans `OpenAIBaseUrl` (ex. `http://192.168.1.20:11434/v1`).

## Modèle, recherche web et automatismes (settings.json)

- `ClaudeCliModel` (`"sonnet"`) et `ClaudeCliEffort` (`"medium"` : low, medium, high, xhigh, max) : affichés en haut à gauche ; chaque réponse indique sous « Agent » le modèle réellement utilisé, l'effort et la durée.
- `AllowWebResearch` (true) : l'agent peut chercher sur le web (Claude Code uniquement) et cite ses sources. Bouton **🔎 Rechercher** pour le lancer sur une tâche.
- `AutoResearchNewTasks` (true) : dès qu'une tâche est créée, l'agent fait une première recherche tout seul.
- `LaunchAtStartup` (true) : TaskAgent se lance à l'ouverture de session Windows (clé `HKCU\...\Run`, ignorée pour la version de dev `bin\Debug`). Lance une fois l'exe publié depuis son dossier final pour l'inscrire.
- `ReplyTimeoutMinutes` (8) : délai max d'une réponse ; bouton **Annuler** pendant l'attente.
- Bouton **✨ Trouver** (ligne « Lien ») : l'IA relie la tâche à un de tes dépôts GitHub (`UseGitHub`) ou dépôts git locaux (`LocalProjectRoots`, ex. `["D:\\Dev"]`). Le jeton GitHub (lecture seule) est demandé au premier usage et stocké chiffré : DPAPI sous Windows (`github-token.secret`), Trousseau sous macOS. Il n'est jamais envoyé à l'IA, seuls les noms des dépôts le sont ; ça fonctionne donc aussi avec Qwen.

## Versions et déploiement

L'appli que tu utilises et celle que l'on modifie sont séparées : `dotnet run` compile dans `bin\Debug` (version « dev »),
`deploy.ps1` publie une **version figée** dans `releases\x.y.z\` — jamais bloquée par l'appli ouverte.

1. Ajouter en haut de `CHANGELOG.md` une section `## x.y.z — date` (x = gros changement, y = nouvelle fonction, z = correction).
2. `powershell -ExecutionPolicy Bypass -File deploy.ps1` : publie `releases\x.y.z\`, met à jour `releases\current.txt`
   et le raccourci **TaskAgent** (Bureau + menu Démarrer), garde les 3 dernières versions.
3. L'appli ouverte affiche un bandeau « La version x.y.z est prête » → **Redémarrer** quand ça t'arrange ;
   **Nouveautés** (ou le bouton « ? » en haut à droite) affiche `CHANGELOG.md`.

Le numéro est aussi dans le titre de la fenêtre. Tes données (`%AppData%\TaskAgent`) sont partagées par toutes les versions ;
le lancement au démarrage de Windows suit la version que tu as lancée en dernier.

## Mémoire (settings.json)

- `MemoryAlertPercent` (90) : ouvre la fenêtre Mémoire si la RAM reste au-delà de ce seuil ~15 s (0 = jamais)
- `MemoryAlertCooldownMinutes` (60) : pas plus d'une alerte par heure

## Relances (settings.json)

- `ReminderEveryDays` (7) : rythme des rappels, une fois par semaine. Chaque tâche a sa date « ⏰ Prochain rappel »
  modifiable dans son détail ; à chaque rappel, tu choisis le suivant (demain → 1 mois). Une vraie avancée le repousse d'un cycle.
- `FirstReminderAfterDays` (14) : premier rappel d'une tâche qu'on vient de créer.
- `SnoozeHours` (4) : exception pour les échéances ≤ demain, relancées si rien n'a bougé depuis X h.
- `CheckIntervalMinutes` (15) : fréquence de vérification. Une seule relance à la fois, la plus urgente d'abord.

Ce qui compte comme **avancée** : « J'ai avancé », cocher une sous-tâche, terminer/rouvrir la tâche. Discuter avec l'agent ou déclarer un blocage **ne** remet **pas** le compteur à zéro (sinon on pourrait « procrastiner en chattant »).

## PDF → Kindle / Markdown (bouton 📚)

Ex-outil `pdf2kindle`, désormais intégré : un PDF — ou **tout un dossier de PDF** — devient un EPUB lisible sur Kindle
(texte remis en forme, titres, table des matières, images), avec en option une **version traduite en français**.
Glisser-déposer un PDF ou un dossier dans la fenêtre fonctionne aussi. Envoi : bouton « ↗ Envoyer vers Kindle »
(<https://www.amazon.fr/sendtokindle>) ou par e-mail à ton adresse `@kindle.com`.

**Ordre des chapitres** (dossier → un seul livre) : deviné d'après les noms, quel que soit leur format (« Chapter 7… », « 7. … », « Chapitre 07 ») — couverture/préface/introduction d'abord, chaque « Part N » juste avant son premier chapitre (lu dans son texte ; sinon après la préface), chapitres par numéro, puis conclusion/annexes/index/« Other Books ». La liste s'affiche dans la fenêtre et se corrige avec ▲ ▼ avant de convertir.

**Depuis un livre déjà fait** : **📝 EPUB → Markdown…** transforme un .epub (de TaskAgent ou sans DRM) en .md sans relire les PDF ; **🩹 Corriger un livre…** répare un .epub/.md (puces lues « e » par l'OCR) et l'enregistre en « v2 » avec sa propre identité ; **🌍 Traduire un EPUB…** traduit un .epub sans DRM en français (`livre.fr.epub`, ou `.md` selon le *Format*) avec le même glossaire et le même cache que pour un PDF. Italiques, gras et liens ne sont pas conservés, et les tableaux sont mis à plat.

**Format Markdown** : choisis « Markdown (.md) » dans *Format*. Chaque PDF devient un `.md` rangé à côté de lui (sous-dossiers compris avec « Inclure les sous-dossiers ») : en-tête YAML (titre, langue, source), `#` titre, titres du PDF en `##`/`###`, paragraphes, listes, et images dans un dossier `nom_images/` (liens relatifs). OCR et traduction marchent pareil (`nom.fr.md`). Décocher « Un fichier par PDF » regroupe tout le dossier dans un seul `.md` (1 PDF = 1 chapitre `##`).

- **Dossier** : PDF triés « naturellement » (`chap2` avant `chap10`), 1 PDF = 1 chapitre ; ou « Un EPUB par PDF ».
- **Traduction** : faite par le même cerveau que l'agent (abonnement Claude, API, ou Qwen local — un modèle local traduit une requête à la fois).
  Deux passes : l'IA lit un échantillon et dresse un **glossaire** (ce qui reste en anglais : *borrow checker*, *smart contract*…),
  puis traduit par lots avec ce glossaire et la typographie française. Le glossaire est enregistré à côté du livre
  (`livre.glossaire.json`) : corrige-le et donne-le dans « Glossaire » pour relancer. Format texte accepté
  (voir `Kindle/glossaire-exemple.txt`). Un cache (`livre.cache-traduction.json`) permet de reprendre après une coupure.
- ⚠ Traduire un livre entier via l'abonnement consomme beaucoup de quota (un appel par lot de ~6 000 caractères).
- **OCR (PDF scannés)** : en mode « Automatique », une page presque sans texte mais couverte par une grande image (scan)
  ou remplie de tracés vectoriels (texte « dessiné », fréquent avec « Imprimer en PDF » depuis un navigateur) est
  « photographiée » par PDFium ([PDFtoImage](https://github.com/sungaila/PDFtoImage), MIT — décode aussi les scans JBIG2/JPEG2000)
  à 300 DPI, puis lue par Tesseract 5.5 ([TesseractOCR](https://github.com/Sicos1977/TesseractOCR), Apache 2.0).
  Le texte retrouvé passe par la même mise en forme que le reste (titres, césures, en-têtes) et devient traduisible ;
  l'image du scan est retirée. « Forcer » relit toutes les pages (PDF au calque texte illisible).
  Langues : codes Tesseract reliés par « + » (`fra+eng` par défaut). Les modèles `tessdata_best` sont téléchargés au
  premier scan rencontré dans `%AppData%\TaskAgent	essdata`. Compter quelques secondes par page.
  Prérequis Windows : runtime Visual C++ 2015-2022 x64 (presque toujours déjà installé ; sinon message explicite).
  Sur Mac, l'OCR n'est pas encore disponible (le paquet ne fournit Tesseract que pour Windows) : le reste fonctionne.
- **Limites** : mises en page très complexes (magazines, tableaux) et formules approximatives ; OCR sans détection du gras.

## Architecture

```
Models/TaskItem.cs        tâche, sous-tâches, journal, chat + libellés calculés (échéance, âge, couleurs)
Services/Storage.cs       sauvegarde JSON atomique + réglages
Services/Llm.cs           ILlmProvider + 3 implémentations (Claude Code, API Anthropic, OpenAI-compatible/Qwen)
Services/PromptBuilder.cs prompt système (date du jour, tâche, sous-tâches, historique) + extraction "Prochaine action" et étapes
Services/ReminderService  minuterie de relances
Services/MemoryMonitor.cs RAM globale + mémoire par appli (plage de travail privée, comme le Gestionnaire des tâches)
Services/StartupApps.cs   programmes au démarrage : lecture (Run, dossiers Démarrage) et activation/désactivation via StartupApproved
Services/SitePrinter.cs   pages web en PDF via Playwright : liste saisie, liens d'une page, exploration (liens + sitemap)
Services/LinkFinder.cs    retrouver le dépôt GitHub / dossier local d'une tâche ; Secrets.cs : jetons chiffrés
Kindle/                   PDF → EPUB / Markdown : PdfExtractor (PdfPig), PdfOcr (PDFium + Tesseract), BookBuilder, EpubWriter, MarkdownWriter, ChapterOrder, KindleConverter (point d'entrée sans UI)
Kindle/Translation/       BookTranslator (glossaire + lots + cache), branché sur ILlmProvider
Views/                    fenêtre principale, relance, Mémoire, Démarrage, PDF → Kindle / Markdown, Imprimer un site, petites boîtes de dialogue
```

## Pistes pour la suite

- icône dans la barre des tâches / zone de notification + notifications système, lancement au démarrage
- résumé du matin (« voici tes 3 priorités du jour ») et bilan du soir
- tri/filtre (masquer les terminées, trier par échéance)
- laisser l'agent agir lui-même (cocher une sous-tâche, changer l'échéance) via du *tool use*
- synchro PC ↔ Mac (dossier partagé ou petit back-end)
