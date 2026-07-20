# GenomeAnalysis

Outil d'analyse de génome humain personnel. On lui fournit un fichier brut de test ADN grand public (23andMe, AncestryDNA, MyHeritage…), il parse les génotypes, les enrichit à partir de SNPedia et d'autres bases publiques, et produit un rapport interprétatif — y compris sur des associations cliniques.

**État : projet neuf, aucun code encore écrit.** La structure décrite ci-dessous est la cible, pas l'existant.

## Contraintes fondamentales

Ces points passent avant toute considération technique.

1. **Mono-poste, local.** L'application tourne sur la machine de l'utilisateur (IIS Express / `localhost`). Jamais déployée sur un serveur public, ni comptes ni authentification. Ne pas ajouter d'upload multi-utilisateur, de stockage cloud ou de télémétrie.
2. **Les données génomiques ne sortent jamais de la machine.** Aucun génotype, aucun fichier, aucune donnée dérivée n'est transmise à une API externe. On n'envoie que des identifiants de variants. Voir « Confidentialité des appels externes » — la règle est plus subtile qu'elle n'en a l'air. Aucun génotype dans les logs, messages d'erreur ou traces.
3. **L'outil restitue du contenu clinique, il ne pose pas de diagnostic.** C'est le cœur du projet : associations maladie, statut de porteur, pharmacogénomique. La contrainte n'est donc pas de censurer ce contenu, mais de le présenter avec sa provenance, son niveau de preuve et ses limites. Voir « Restitution du contenu clinique ».

## Pile technique

- .NET Framework 4.8, C#
- ASP.NET MVC 5 + vues Razor ; Web API 2 dans le même projet pour les appels AJAX (progression du parsing, chargement paresseux des fiches)
- SQLite ou LocalDB pour le cache des sources externes uniquement
- Visual Studio 2022, MSBuild (pas de `dotnet build` — c'est du .NET Framework)

## Structure cible de la solution

```
GenomeAnalysis.sln
├── GenomeAnalysis.Web/          MVC 5 + Web API 2 — contrôleurs, vues, rapports
├── GenomeAnalysis.Core/         Domaine : modèles, parseurs, moteur de règles. Aucune dépendance web ni réseau.
├── GenomeAnalysis.Annotations/  Clients des sources externes (SNPedia, Ensembl, ClinVar…) + cache
└── GenomeAnalysis.Tests/        Tests unitaires
```

`Core` ne référence ni `System.Web` ni `Annotations`. Le moteur reçoit ses annotations via des interfaces définies dans `Core` et implémentées dans `Annotations` — c'est ce qui rend l'analyse testable sans réseau ni base.

## Formats de fichiers d'entrée

Détecter le fournisseur à partir de l'en-tête, pas de l'extension. Les fichiers font 600 k à 1 M de lignes : parser en flux (`StreamReader` ligne à ligne), jamais `File.ReadAllLines`.

**23andMe** — TSV, en-tête préfixé `#`, colonnes `rsid, chromosome, position, genotype` :

```
# rsid	chromosome	position	genotype
rs4477212	1	82154	AA
rs3094315	1	752566	AG
```

**AncestryDNA** — TSV aussi, mais allèles dans deux colonnes séparées (`allele1`, `allele2`) et appels manquants notés `0` au lieu de `--`.

Pièges à gérer dès le parseur :

- Appels manquants : `--`, `DD`, `II`, `0`. Les exclure de l'analyse, ne pas les traiter comme des génotypes.
- Chromosomes non autosomiques : `X`, `Y`, `MT` peuvent porter un seul allèle (hémizygote).
- Build de référence : GRCh37 vs GRCh38 selon le millésime. Une position n'a de sens qu'avec son build — le lire dans l'en-tête et le conserver dans le modèle.
- Identifiants internes au fournisseur (`i5000940`) sans correspondance dans les bases publiques.
- **rsID fusionnés ou retirés.** dbSNP fusionne régulièrement des rsID ; un fichier de 2013 contient des identifiants qui ne sont plus courants. Résoudre vers le rsID courant avant toute recherche, sinon des variants réels ressortent « inconnus ».

## Sources de données

SNPedia reste la source principale pour le texte lisible, mais elle est communautaire, inégale et parfois périmée. Pour tout ce qui est clinique, elle ne fait pas autorité seule — la croiser.

### SNPedia

- Endpoint : `https://bots.snpedia.com/api.php` (API MediaWiki). C'est l'accès prévu pour les clients automatisés ; ne pas scraper les pages HTML de `snpedia.com`.
- Licence **CC BY-NC-SA 3.0 US** : usage non commercial, attribution obligatoire, partage à l'identique. L'attribution doit apparaître dans les rapports générés. Le *ShareAlike* se propage — en tenir compte avant d'envisager quoi que ce soit de commercial.
- Nommage des pages : fiche SNP `Rs53576` (initiale capitale, reste minuscule) ; fiche génotype `Rs53576(A;A)`, allèles séparés par `;` entre parenthèses.
- Métadonnées (`Magnitude`, `Repute`, `Summary`, `Orientation`, `StabilizedOrientation`) : propriétés Semantic MediaWiki, à lire via l'API sémantique plutôt que par regex sur le wikitext.
- `Magnitude` est un indice d'intérêt subjectif (0–10) propre à SNPedia, pas une mesure de risque. Ne pas le présenter comme tel.

### Autres sources publiques

| Source | Usage | Accès |
|---|---|---|
| **MyVariant.info** | Agrégateur (dbSNP, ClinVar, gnomAD, CADD, dbNSFP) en un seul appel | REST, sans clé, POST par lot |
| **Ensembl REST** | Consequence VEP, gènes, correspondance entre builds | `rest.ensembl.org`, sans clé |
| **NCBI E-utilities** | dbSNP : rsID fusionnés/retirés, positions de référence | Clé gratuite recommandée (débit plus élevé) |
| **ClinVar** | Signification clinique + niveau de revue (étoiles) — fait autorité | Via NCBI ou MyVariant ; domaine public |
| **gnomAD** | Fréquences alléliques par population | GraphQL |
| **GWAS Catalog** (EBI) | Associations traits/variants, tailles d'effet, p-values | REST |
| **PharmGKB / CPIC** | Pharmacogénomique, recommandations posologiques | Vérifier la licence avant intégration — plus restrictive |

Priorité en cas de désaccord entre sources sur une question clinique : **ClinVar (revue élevée) > GWAS Catalog > SNPedia**. Un désaccord se signale dans le rapport, il ne se tranche pas en silence.

### Confidentialité des appels externes

La règle « on n'envoie que des identifiants » ne suffit pas, et c'est le piège le plus facile à manquer :

> **Interroger uniquement les variants où l'utilisateur porte un allèle notable révèle son génotype**, même sans jamais transmettre une seule lettre d'allèle. Le motif des requêtes est lui-même la donnée.

Conséquences à respecter :

- Peupler le cache à partir du **manifeste de la puce** (l'ensemble des positions testées par le fournisseur, qui est public), pas à partir des variants retenus pour l'utilisateur.
- Privilégier les **exports en masse** — SNPedia publie `snpedia.com/index.php/Bulk`, ClinVar et gnomAD des fichiers complets. Un cache prérempli hors ligne supprime le problème à la racine et évite des dizaines de milliers d'appels unitaires.
- Aucun appel réseau ne doit dépendre du contenu du fichier utilisateur. Si l'implémentation rend cela impossible, c'est la conception qu'il faut revoir.
- Débit : ~1 req/s en série vers SNPedia, respecter les limites publiées des autres. User-Agent identifiant l'outil. Sur `429`/`5xx`, backoff exponentiel — jamais de retry serré.

## Orientation de brin

Le piège technique majeur du projet. Sans cette étape, une partie des associations sera **silencieusement fausse** — bien pire qu'une absence de résultat.

L'ADN est double brin : un `A` sur le brin plus est un `T` sur le brin moins, un `C` est un `G`. Les fournisseurs de tests et SNPedia ne rapportent pas toujours sur le même brin. Un génotype rapporté `AA` par 23andMe peut correspondre à la fiche `Rs…(T;T)`.

SNPedia expose deux propriétés distinctes :

- **`Orientation`** — orientation du variant dans le build courant (GRCh38).
- **`StabilizedOrientation`** — la même notion, mais cohérente avec les pages génotype associées : elle n'est basculée que si les pages génotype liées le sont aussi.

**C'est `StabilizedOrientation` qui doit servir à faire correspondre un génotype à une page génotype.** Utiliser `Orientation` à sa place produit des correspondances fausses. Quand `StabilizedOrientation` vaut `minus`, complémenter les allèles avant la recherche.

### Flips ambigus — le cas qu'on ne peut pas résoudre

Pour un SNP **A/T ou C/G**, la complémentation ne lève aucune ambiguïté : `A;A` complémenté donne `T;T`, qui est *aussi* un génotype valide du même SNP. Aucune logique de brin ne permet de trancher à partir des allèles seuls. SNPedia nomme ces cas *ambiguous flips*.

Ces variants doivent être **détectés et signalés comme indéterminés**, jamais devinés. Une correspondance arbitraire sur un SNP palindromique peut inverser complètement le sens d'une association (allèle protecteur présenté comme allèle à risque). Interdire tout choix par défaut sur ce chemin de code.

À couvrir par des tests dédiés : orientation `plus`, orientation `minus` nécessitant complémentation, SNP palindromique homozygote (indéterminé attendu), et SNP palindromique hétérozygote (`A;T` — inchangé par complémentation, donc exploitable).

## Moteur d'analyse et rapport

Le rapport ne doit pas être une liste plate d'un SNP par ligne. La plupart des interprétations utiles reposent sur une **combinaison** de marqueurs.

### Règles multi-marqueurs

Le moteur travaille sur des règles déclaratives (données, pas code compilé), chacune déclarant les marqueurs dont elle dépend et la façon de les combiner. Cas réels à supporter :

- **Haplotypes composés** — APOE est l'exemple canonique : les allèles ε2/ε3/ε4 se déduisent de la combinaison de `rs429358` et `rs7412`. Aucun des deux SNP pris isolément ne donne l'information.
- **Hétérozygotie composite** — deux variants pathogènes différents dans le même gène, par exemple HFE `rs1800562` (C282Y) et `rs1799945` (H63D) pour l'hémochromatose.
- **Étoiles pharmacogénomiques** — les allèles `*1`, `*2`… de CYP2D6 ou CYP2C19 sont définis par des combinaisons de positions ; le diplotype détermine le phénotype métaboliseur.
- **Scores polygéniques** — agrégation pondérée de nombreux variants à faible effet.

### Limites structurelles à encoder dans le moteur

Ce sont des propriétés des données de puce, pas des défauts d'implémentation. Les traiter comme des cas de premier ordre :

- **Les données de puce ne sont pas phasées.** On ne sait pas si deux variants sont sur la même copie du chromosome ou sur les deux copies opposées. L'hétérozygotie composite vraie (en *trans*, les deux copies atteintes) est donc **indiscernable** de deux variants en *cis* (une copie saine intacte). Le rapport doit dire « compatible avec », jamais « confirmé ».
- **Marqueur requis absent.** Si une règle a besoin de cinq positions et que la puce n'en couvre que trois, le résultat est **indéterminé** — jamais une supposition sur les positions manquantes.
- **Ne pas multiplier naïvement les odds ratios.** Les OR publiés supposent l'indépendance et proviennent de populations différentes ; les composer mécaniquement surestime le résultat.

Un résultat d'évaluation doit donc distinguer au minimum : `Determinate`, `Indeterminate` (données insuffisantes ou flip ambigu), `NotApplicable`. Un `Indeterminate` s'affiche, avec sa raison — c'est une information utile, pas un échec à masquer.

### Ce que « rapport intelligent » veut dire ici

Priorisation et mise en contexte, **pas** génération de texte médical. Concrètement : classer par force de preuve et pertinence, regrouper les marqueurs d'un même mécanisme, contextualiser par la fréquence dans la population (un variant présent chez 40 % des Européens ne se présente pas comme une découverte), et permettre le drill-down vers la source.

Toute affirmation affichée doit être traçable jusqu'à une source citée. Si un jour un LLM est employé pour rédiger les synthèses, il doit être contraint à reformuler des sources fournies avec citations — jamais à produire une affirmation clinique de lui-même.

## Restitution du contenu clinique

L'outil affiche des associations de maladies. Ces règles encadrent *comment*, et ne sont pas négociables.

- **Les données brutes de puce ne sont pas de qualité clinique.** Une étude de référence (Tandy-Connor et al., *Genetics in Medicine*, 2018) a trouvé qu'environ 40 % des variants signalés dans des données brutes grand public étaient des faux positifs à la vérification en laboratoire accrédité. Toute découverte à fort impact doit être présentée comme **nécessitant une confirmation** en laboratoire clinique.
- **L'absence de résultat n'est pas une absence de risque.** Une puce interroge des positions fixes et prédéfinies. Le test BRCA de 23andMe, par exemple, couvre trois variants fondateurs ashkénazes sur plusieurs milliers connus. Ne jamais formuler « aucun risque détecté » ; formuler « aucune des N positions testées ne porte de variant connu ».
- **Graduer selon l'impact.** Un SNP de métabolisme de la caféine et une variante Huntington ou BRCA ne se présentent pas de la même façon. Les résultats à fort impact ou actionnables doivent recommander explicitement un conseil génétique professionnel.
- **Afficher le niveau de preuve.** Le niveau de revue ClinVar (étoiles) et le statut de la classification importent autant que la classification : un VUS à une étoile n'est pas une variante pathogène à quatre étoiles. Ne pas aplatir cette distinction.
- **Jamais prescriptif.** Pas de « vous devriez prendre… », pas de « vous avez la maladie X ». Formuler en termes d'association et de probabilité, au conditionnel.
- Réserve explicite sur toute sortie utilisateur : information éducative, non un diagnostic, ne remplace pas un professionnel de santé.

## Tests

- Parsing et moteur de règles se testent sans réseau ni base : injecter les interfaces d'annotation par des doubles de test.
- Fixtures synthétiques (quelques dizaines de lignes par format), **jamais un vrai fichier de génome** dans le dépôt — y compris celui du développeur.
- Priorités : appels manquants, hémizygotie X/Y/MT, complémentation de brin, flip ambigu, rsID fusionné, marqueur requis absent d'une règle multi-SNP, dérivation d'un haplotype (APOE fait un bon cas de référence), fichier tronqué ou en-tête inattendu.

## Conventions

- Le domaine parle anglais dans le code (`Genotype`, `RiskAllele`, `Haplotype`, `Chromosome`) ; l'interface utilisateur est en français.
- Un fichier de génome est une donnée de santé : le traiter comme un secret, pas comme un fichier d'entrée ordinaire.
