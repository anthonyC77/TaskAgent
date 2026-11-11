# Nouveautés de TaskAgent

<!--
Une section par version, la plus récente en haut. Le numéro de la première section « ## x.y.z — date » est celui
que deploy.ps1 publie : ajouter la section AVANT de lancer le script.
x = gros changement · y = nouvelle fonction · z = correction.
-->

## 1.6.0 — 2026-11-05

### Nouveau
- **🌍 Traduire un EPUB…** (📚) : traduit en français un .epub sans protection (DRM), sans passer par un PDF. Le livre traduit
  (« livre.fr.epub », ou « .md » selon le format choisi) est créé à côté de l'original, avec sa couverture et sa table des matières.
  Le glossaire, les consignes et la reprise après une coupure marchent comme pour un PDF.

### À savoir
- Dans le livre traduit, les italiques, le gras et les liens (renvois vers les notes compris) ne sont pas conservés, et les tableaux sont mis à plat.
- Si le texte semble déjà en français, rien n'est traduit, sauf avec « Toujours traduire en français ». Un livre presque sans texte
  (que des images, par exemple) ne permet pas de reconnaître la langue : c'est alors celle indiquée dans le fichier qui compte.

### Amélioré
- **📝 EPUB → Markdown…** : la langue indiquée dans le fichier .md est maintenant vérifiée sur le texte du livre, et la couverture de l'EPUB est reconnue.

## 1.5.1 — 2026-11-02

### Corrigé
- **Parties en chiffres romains** (« I.pdf », « II.pdf », « III. Titre.pdf ») : elles s'intercalent maintenant entre les chapitres
  (I, 1, 2, 3, II, 4, 5…) au lieu d'être toutes regroupées au début du livre. Quand la page de partie n'a pas de texte lisible,
  sa place est déduite de l'ordre dans lequel les PDF ont été enregistrés.

## 1.5.0 — 2026-11-02

### Corrigé
- **Texte des schémas en double** : l'OCR recopiait les mots écrits DANS les images (schémas, captures) en texte
  à côté de l'image, souvent en charabia (« [ \ Senior i | Ne »). Ce texte est maintenant ignoré : l'image suffit.
- Schéma dessiné (sans image dans le PDF) : il est découpé dans la page et gardé en image, au lieu de devenir du charabia.
- **🩹 Corriger un livre…** et **📝 EPUB → Markdown…** retirent aussi ce texte des livres déjà faits (entre une image et
  sa légende « Figure x-y », plus le charabia isolé), y compris de la table des matières.

### À savoir
- La mémoire des PDF déjà lus repart à zéro une fois (les nouvelles règles demandent de relire les pages).

## 1.4.1 — 2026-11-02

### Corrigé
- La version de travail (lancée avec `dotnet run`, dossier `bin\Debug`) affiche aussi le bandeau : « Passer à la x.y.z »
  bascule sur la version déployée. Avant, aucun bouton n'apparaissait et on pouvait rester sur une vieille version sans le savoir.

## 1.4.0 — 2026-11-02

### Nouveau
- **📝 EPUB → Markdown…** (📚) : transforme un ou plusieurs .epub déjà faits en .md, sans relire les PDF ni refaire l'OCR.
  Titres, listes à puces, images (dossier « nom_images »), ordre de lecture du livre ; les puces mal lues par l'OCR sont
  corrigées au passage. Marche aussi avec un EPUB sans protection (DRM) venu d'ailleurs.

## 1.3.2 — 2026-11-02

### Corrigé
- Dernier « e » parasite : un élément de liste coupé par un changement de page (suite qui commence par une minuscule)
  est recollé à son élément, puis coupé à la puce suivante.
- Corriger un « livre v2.epub » donne « livre v3.epub » (et non « livre v2 v2.epub »).

## 1.3.1 — 2026-11-02

### Corrigé
- Un livre corrigé (« livre v2.epub ») a maintenant sa propre identité : nouveau numéro interne et titre « livre (v2) ».
  Avant, les liseuses (Kindle, Calibre…) le prenaient pour l'ancien et continuaient d'afficher la version avec les « e ».
- Message plus clair quand il n'y a rien à corriger (livre déjà corrigé).

## 1.3.0 — 2026-11-02

### Nouveau
- **🩹 Corriger un livre…** (📚) : corrige un .epub ou .md déjà produit, sans relire les PDF ni refaire l'OCR.
  Le résultat est enregistré à côté (« livre v2.epub ») ; l'original n'est pas touché.

### Corrigé
- Les « e » (ou « © », « ® », « ° »…) parasites devant les éléments de liste : c'étaient des puces « • » mal lues par l'OCR,
  et les éléments d'une liste étaient collés en un seul paragraphe. Ils redeviennent une vraie liste à puces, un élément par ligne.

## 1.2.1 — 2026-11-02

### Corrigé
- Le numéro de version ne chevauche plus le titre « Mes tâches » : il est dans le bouton **?** en haut à droite
  (survol = version, clic = nouveautés).

## 1.2.0 — 2026-11-02

### Nouveau
- **Relance sans tout refaire** (📚) : le texte lu dans chaque PDF (OCR compris) est gardé en mémoire. Si la conversion échoue
  à la fin, « Convertir » à nouveau reprend en quelques secondes au lieu de refaire l'OCR. Case « Repartir de zéro » pour tout relire.
  (Mémoire dans `%LocalAppData%\TaskAgent\cache-pdf`, nettoyée au bout de 30 jours.)

### Corrigé
- Plus d'écrasement ni d'échec si le livre existe déjà ou est ouvert dans une liseuse : le nouveau fichier s'appelle
  « livre v2.epub », puis « v3 »… (idem pour les .md et les versions traduites).

## 1.1.0 — 2026-11-02

### Nouveau
- **Versions et mises à jour** : chaque version est déployée dans son propre dossier (`releases\1.1.0`…), le numéro s'affiche
  dans le titre, et un bandeau propose de redémarrer sur la nouvelle version quand elle est prête (bouton « Nouveautés » pour lire ce fichier).
- **Programmes au démarrage** (fenêtre Mémoire → 🚀 Démarrage) : voir ce qui se lance avec Windows, la RAM occupée, et désactiver d'un clic.
- **Imprimer des pages web** (🖨) : la liste de pages de ton choix en PDF, dans l'ordre, + PDF unique et sommaire.
  - **🔗 Liens d'une page** : récupère tous les liens d'un sommaire, à cocher, à imprimer ou à enregistrer en .txt (accepte aussi le code HTML copié depuis F12).
  - **🖐 Impression assistée** pour les sites qui refusent les robots (403) : les pages s'ouvrent dans ton navigateur, tu fais Ctrl+P, TaskAgent range et numérote.
- **PDF → Markdown** (📚, Format « Markdown ») : un `.md` par PDF avec titres, listes et images.
- **Ordre des chapitres** visible et modifiable (▲ ▼) avant de fusionner un dossier de PDF en un livre.

### Corrigé
- PDF « imprimés depuis un navigateur » (texte dessiné) : l'OCR se lance maintenant tout seul, au lieu de produire un livre vide.
- Chapitres dans le désordre quand les noms de fichiers n'ont pas le même format (« 7. … » avant « Chapter 1… »).
- L'impression automatique s'arrête après 3 refus d'accès d'affilée (évite de faire bloquer ton adresse).

## 1.0.0 — 2026-10-31

- Première version : tâches et sous-tâches, agent IA (Claude Code, API Anthropic ou modèle local), relances,
  jauge et fenêtre Mémoire, PDF → Kindle (EPUB, OCR, traduction).
