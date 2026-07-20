# GenomeAnalysis

Outil d'analyse de génome humain personnel. On lui fournit un fichier brut de test ADN grand public (23andMe, AncestryDNA, MyHeritage…), il parse les génotypes et les enrichit avec les données de SNPedia pour produire un rapport lisible.

**État : projet neuf, aucun code encore écrit.** La structure décrite ci-dessous est la cible, pas l'existant.

## Contraintes fondamentales

Ces trois points passent avant toute considération technique.

1. **Mono-poste, local.** L'application tourne sur la machine de l'utilisateur (IIS Express / `localhost`). Elle n'est jamais déployée sur un serveur public, il n'y a ni comptes ni authentification. Ne pas ajouter d'upload multi-utilisateur, de stockage cloud ou de télémétrie.
2. **Les données génomiques ne sortent jamais de la machine.** On envoie à SNPedia des identifiants de SNP (`rs53576`) — jamais un génotype, jamais un fichier, jamais un lot d'identifiants qui reconstituerait un profil identifiable. Aucun génotype dans les logs, les messages d'erreur ou les traces.
3. **Ce n'est pas un outil médical.** Toute sortie destinée à l'utilisateur porte une réserve explicite : information éducative, non un diagnostic, pas un substitut à un conseil génétique professionnel. Ne jamais formuler une sortie en termes prescriptifs (« vous devriez prendre… », « vous avez une maladie X »).

## Pile technique

- .NET Framework 4.8, C#
- ASP.NET MVC 5 + vues Razor ; Web API 2 dans le même projet pour les appels AJAX (progression du parsing, chargement paresseux des fiches SNP)
- SQLite ou LocalDB pour le cache SNPedia uniquement
- Visual Studio 2022, MSBuild (pas de `dotnet build` — c'est du .NET Framework)

## Structure cible de la solution

```
GenomeAnalysis.sln
├── GenomeAnalysis.Web/            MVC 5 + Web API 2 — contrôleurs, vues, rapports
├── GenomeAnalysis.Core/           Domaine : modèles, parseurs, moteur d'analyse. Aucune dépendance web.
├── GenomeAnalysis.SnpediaClient/  Client API MediaWiki + cache
└── GenomeAnalysis.Tests/          Tests unitaires
```

`Core` ne référence ni `System.Web` ni `SnpediaClient`. Le moteur d'analyse reçoit ses données SNPedia via une interface définie dans `Core` et implémentée dans `SnpediaClient` — c'est ce qui rend l'analyse testable sans réseau.

## Formats de fichiers d'entrée

Détecter le fournisseur à partir de l'en-tête plutôt que de l'extension. Les fichiers font 600 k à 1 M de lignes : parser en flux (`StreamReader` ligne à ligne), jamais `File.ReadAllLines`.

**23andMe** — TSV, lignes d'en-tête préfixées `#`, colonnes `rsid, chromosome, position, genotype` :

```
# rsid	chromosome	position	genotype
rs4477212	1	82154	AA
rs3094315	1	752566	AG
```

**AncestryDNA** — TSV également, mais les allèles sont dans deux colonnes séparées (`allele1`, `allele2`) et les appels manquants s'écrivent `0` au lieu de `--`.

Pièges à gérer dès le parseur :
- Appels manquants : `--`, `DD`, `II`, `0`. Les exclure de l'analyse, ne pas les traiter comme des génotypes.
- Chromosomes non autosomiques : `X`, `Y`, `MT` peuvent porter un génotype à un seul allèle (hémizygote).
- Build de référence : GRCh37 vs GRCh38 selon le millésime du fichier. La position n'a de sens qu'avec son build — le lire dans l'en-tête et le conserver dans le modèle.
- Certains identifiants sont internes au fournisseur (`i5000940`) et n'ont pas de correspondance SNPedia.

## Client SNPedia

- Endpoint : `https://bots.snpedia.com/api.php` (API MediaWiki standard). C'est l'accès prévu pour les clients automatisés ; ne pas scraper les pages HTML de `snpedia.com`.
- Contenu sous **CC BY-NC-SA 3.0 US**. Usage non commercial, attribution obligatoire, partage à l'identique. Afficher l'attribution dans les rapports générés.
- **Le cache est obligatoire, pas une optimisation.** Un génome contient des centaines de milliers de SNP ; taper l'API en direct pour chacun est à la fois irréalisable et abusif. Cacher les réponses en local avec une date d'expiration, et n'interroger le réseau que pour les fiches absentes ou périmées.
- Limiter le débit à ~1 requête/seconde, en série. Envoyer un User-Agent identifiant l'outil. En cas de `429` ou `5xx`, backoff exponentiel — ne jamais boucler en retry serré.
- SNPedia publie aussi des exports en masse (`snpedia.com/index.php/Bulk`) : à privilégier pour préremplir le cache plutôt que des dizaines de milliers d'appels unitaires.

Conventions de nommage des pages, à vérifier contre l'API avant de coder en dur :
- Fiche SNP : `Rs53576` (première lettre capitale, le reste minuscule).
- Fiche génotype : `Rs53576(A;A)` — allèles séparés par `;`, entre parenthèses.
- Les métadonnées utiles (`Magnitude`, `Repute`, `Summary`, orientation) sont des propriétés Semantic MediaWiki ; les lire via l'API sémantique plutôt que par regex sur le wikitext.

**Piège d'orientation.** SNPedia et les fournisseurs de tests ne rapportent pas toujours les allèles sur le même brin d'ADN. Un génotype rapporté `AA` par 23andMe peut correspondre à `(T;T)` sur la fiche SNPedia si les orientations diffèrent. Vérifier l'orientation de chaque fiche et complémenter le génotype quand nécessaire — sans cette étape, une partie des associations sera silencieusement fausse, ce qui est bien pire qu'une absence de résultat. Couvrir ce cas par des tests dédiés.

## Tests

- Le parsing et le moteur d'analyse se testent sans réseau ni base : injecter l'interface SNPedia par un double de test.
- Utiliser des fixtures synthétiques (quelques dizaines de lignes par format), **jamais un vrai fichier de génome** dans le dépôt.
- Cas à couvrir en priorité : appels manquants, hémizygotie X/Y/MT, complémentation de brin, rsid inconnu de SNPedia, fichier tronqué ou en-tête inattendu.

## Conventions

- Le domaine parle anglais dans le code (`Genotype`, `RiskAllele`, `Chromosome`) ; l'interface utilisateur est en français.
- Un fichier de génome est une donnée de santé : le traiter comme un secret, pas comme un fichier d'entrée ordinaire.
