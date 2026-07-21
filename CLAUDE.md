# GenomeAnalysis

Personal human genome analysis tool. It takes a raw consumer DNA test file (23andMe, AncestryDNA, MyHeritage…), parses the genotypes, enriches them from SNPedia and other public databases, and produces an interpretive report — including clinical associations.

**Status.** `Core` and `Annotations` exist and are covered by tests; `Web` has not been started. The annotation layer works: SNPedia and MyVariant.info clients, throttled HTTP, and the SQLite cache with an enforced offline mode.

## Core constraints

These take precedence over any technical consideration.

1. **Single machine, local.** The application runs on the user's own machine (IIS Express / `localhost`). It is never deployed to a public server, and there are no accounts or authentication. Do not add multi-user upload, cloud storage, or telemetry.
2. **Genomic data never leaves the machine.** No genotype, no file, no derived data is sent to an external API. Only variant identifiers go out. See "External call privacy" — the rule is subtler than it looks. No genotypes in logs, error messages, or traces.
3. **The tool surfaces clinical content; it does not diagnose.** This is the point of the project: disease associations, carrier status, pharmacogenomics. The constraint is therefore not to censor that content, but to present it with its provenance, evidence level, and limits. See "Presenting clinical content".

## Tech stack

- .NET Framework 4.8, C#
- ASP.NET MVC 5 + Razor views; Web API 2 in the same project for AJAX calls (parsing progress, lazy loading of variant records)
- SQLite for caching external sources only, through **`System.Data.SQLite.Core`**. Not `Microsoft.Data.Sqlite`: on net48 it drags System.Memory facade assemblies whose versions do not reconcile, and the provider fails at type-initialisation unless binding redirects are hand-written. `System.Data.SQLite.Core` is built for .NET Framework and ships its own native binaries.
- Visual Studio 2022, MSBuild (no `dotnet build` — this is .NET Framework)

Projects are SDK-style csproj targeting `net48`, which keeps them hand-editable. The `Web` project will have to be classic-style, since MVC 5 and `System.Web` do not work under the SDK format.

```sh
MSBUILD="D:/programmes/Microsoft Visual Studio/2022/MSBuild/Current/Bin/MSBuild.exe"
VSTEST="D:/programmes/Microsoft Visual Studio/2022/Common7/IDE/Extensions/TestPlatform/vstest.console.exe"

"$MSBUILD" GenomeAnalysis.sln -t:Restore    # restore first; MSBuild does not do it implicitly
"$MSBUILD" GenomeAnalysis.sln -v:minimal
"$VSTEST" "GenomeAnalysis.Tests/bin/Debug/net48/GenomeAnalysis.Tests.dll"
```

## Target solution structure

```
GenomeAnalysis.sln
├── GenomeAnalysis.Web/          MVC 5 + Web API 2 — controllers, views, reports
├── GenomeAnalysis.Core/         Domain: models, parsers, rules engine. No web or network dependency.
├── GenomeAnalysis.Annotations/  Clients for external sources (SNPedia, Ensembl, MyVariant) + cache + local database
├── GenomeAnalysis.Harvester/    Console tool that builds data/variant-database.json. Never sees user data.
└── GenomeAnalysis.Tests/        Unit tests
```

`Core` references neither `System.Web` nor `Annotations`. The engine receives annotations through interfaces defined in `Core` and implemented in `Annotations` — that is what makes the analysis testable without network or database.

Every source is consumed through `CachingAnnotationSource`, never directly. Constructed with `allowNetwork: false`, a cache miss returns nothing instead of reaching out, which turns the privacy rule below into something the code enforces rather than something a reviewer has to notice. That is the mode to use for any lookup driven by the user's file.

## Input file formats

Detect the provider from the header, not the file extension. Files run 600k to 1M lines: parse as a stream (`StreamReader`, line by line), never `File.ReadAllLines`.

**23andMe** — TSV, header lines prefixed with `#`, columns `rsid, chromosome, position, genotype`:

```
# rsid	chromosome	position	genotype
rs4477212	1	82154	AA
rs3094315	1	752566	AG
```

**AncestryDNA** — also TSV, but alleles are in two separate columns (`allele1`, `allele2`) and no-calls are written `0` instead of `--`.

Pitfalls to handle in the parser itself:

- No-calls: `--`, `DD`, `II`, `0`. Exclude them from analysis; do not treat them as genotypes.
- Non-autosomal chromosomes: `X`, `Y`, `MT` may carry a single allele (hemizygous).
- Reference build: GRCh37 vs GRCh38 depending on the file's vintage. A position is only meaningful together with its build — read it from the header and keep it in the model.
- Provider-internal identifiers (`i5000940`) with no counterpart in public databases.
- **Merged or withdrawn rsIDs.** dbSNP merges rsIDs regularly; a file from 2013 contains identifiers that are no longer current. Resolve to the current rsID before any lookup, otherwise real variants come back "unknown".

## Data sources

SNPedia remains the primary source for human-readable text, but it is community-maintained, uneven, and sometimes stale. For anything clinical it is not authoritative on its own — cross-check it.

### SNPedia

- Endpoint: `https://bots.snpedia.com/api.php` (MediaWiki API). SNPedia permits bots to read pages under `/index.php/` and asks that automated clients use the API for volume — so HTML parsing is a legitimate fallback, not something forbidden. Prefer the API regardless: it returns structured properties instead of a DOM that breaks whenever an editor reformats a template.
- **As of July 2026 SNPedia is unreachable to automated clients.** `bots.snpedia.com/api.php` returns 502, and `www.snpedia.com` answers a 212-byte Incapsula JavaScript challenge instead of article content — including for `robots.txt`. Retry periodically; the site may recover.
- Do **not** drive a headless browser to get past that challenge. Reading an open page is one thing; defeating a bot protection the operator deliberately switched on is another, and this project does not do it. If SNPedia stays unreachable, use its published bulk export, or do without — MyVariant.info and Ensembl cover the clinical ground and answer normally.
- Licensed **CC BY-NC-SA 3.0 US**: non-commercial use, attribution required, share-alike. Attribution must appear in generated reports. Share-alike is viral — worth knowing before considering anything commercial.
- Page naming: SNP page `Rs53576` (leading capital, rest lowercase); genotype page `Rs53576(A;A)`, alleles separated by `;` inside parentheses.
- Metadata (`Magnitude`, `Repute`, `Summary`, `Orientation`, `StabilizedOrientation`) are Semantic MediaWiki properties — read them through the semantic API rather than by regex over wikitext.
- `Magnitude` is SNPedia's own subjective interest score (0–10), not a risk measure. Do not present it as one.

### Other public sources

| Source | Use | Access |
|---|---|---|
| **MyVariant.info** | Aggregator (dbSNP, ClinVar, gnomAD, CADD, dbNSFP) in a single call | REST, no key, batch POST |
| **Ensembl REST** | VEP consequences, genes, cross-build mapping | `rest.ensembl.org`, no key |
| **NCBI E-utilities** | dbSNP: merged/withdrawn rsIDs, reference positions | Free key recommended (higher rate limit) |
| **ClinVar** | Clinical significance + review status (stars) — authoritative | Via NCBI or MyVariant; public domain |
| **gnomAD** | Population allele frequencies | GraphQL |
| **GWAS Catalog** (EBI) | Trait/variant associations, effect sizes, p-values | REST |
| **PharmGKB / CPIC** | Pharmacogenomics, dosing guidance | Check licensing before integrating — more restrictive |

Precedence when sources disagree on a clinical question: **ClinVar (high review status) > GWAS Catalog > SNPedia**. A disagreement is surfaced in the report, not silently resolved.

### External call privacy

"We only send identifiers" is not sufficient, and this is the easiest trap to miss:

> **Querying only the variants where the user carries a notable allele reveals their genotype**, even if not a single allele letter is ever transmitted. The query pattern is itself the data.

What follows from this:

- Populate the cache from the **chip manifest** (the set of positions the provider tests, which is public), not from the variants selected for this user.
- Prefer **bulk exports** — SNPedia publishes `snpedia.com/index.php/Bulk`, and ClinVar and gnomAD publish full files. An offline pre-populated cache removes the problem at the root and avoids tens of thousands of single-variant calls.
- No network call may depend on the contents of the user's file. If the implementation makes that impossible, the design is what needs revisiting.
- Rate: ~1 req/s serially against SNPedia, and respect published limits elsewhere. Send a User-Agent identifying the tool. On `429`/`5xx`, exponential backoff — never a tight retry loop.

## The local variant database

`data/variant-database.json` is the annotation source the application actually reads. It is built ahead of time by `GenomeAnalysis.Harvester` from `data/seed-variants.json` — a committed list of public identifiers — and queried offline through `VariantDatabase`.

That ordering is the design, not an optimisation. The harvest happens with no user data anywhere near it; analysis happens with no network at all. There is therefore no request whose shape could reveal a genotype, which is a stronger guarantee than any amount of care about *what* gets sent.

```sh
GenomeAnalysis.Harvester/bin/Debug/net48/GenomeAnalysis.Harvester.exe   # ~50 s for 48 variants
```

`data/pharmacogenomics.json` holds CPIC's tables for the 21 genes it rates level A, and `--cpic-only` rebuilds just that file in seconds instead of re-querying every variant.

The harvester merges several sources, because none is sufficient alone:

- **Ensembl** supplies `allele_string` and strand. The allele set is what lets strand resolution detect a palindromic variant; without it `StrandResolver` refuses every lookup. It also supplies `synonyms`, from which merged rsIDs are extracted — that is what makes a 2013 file resolve.
- **MyVariant.info** supplies ClinVar significance with review status, and gnomAD frequency.
- **GWAS Catalog** supplies curated trait associations with effect sizes and p-values, which is what makes ranking by evidence possible rather than by whichever finding sounds most alarming. `TraitAssociation` exposes `IsGenomeWideSignificant` (the conventional 5×10⁻⁸ threshold) and `IsNegligibleEffect` (odds ratio between 0.91 and 1.10 — routine GWAS noise at the individual level).
- **CPIC** supplies the star-allele data no per-variant source has: which positions define each allele, and which phenotype each function pair implies. It also expands the seed list — those defining positions are exactly what a chip must cover for a diplotype call to be possible.

### Store the rule, not its expansion

CPIC publishes every diplotype explicitly, which is the Cartesian product of a gene's alleles: RYR1 alone is 60 378 rows, and the full dump came to 46 MB. But those rows encode only two things — each allele's function, and the phenotype a pair of functions produces. Keeping those two tables reproduces everything from 6 rules instead of 60 378 rows, and brings the file to 104 KB.

That is also the form the engine can reason with, and it matches the "declarative rules, data not compiled code" requirement below. Watch for the same shape elsewhere: a combinatorial table is nearly always a derived artefact.

Re-run it after editing the seed list. The output is committed: it is public reference data, it makes the tool work offline out of the box, and its provenance block records every source licence.

### Traps found by running it against real data

Both of these passed fixture tests and were only exposed by live responses.

- **Do not rank `ClinicalSignificance` by its enum order.** `Other`, `Association` and `RiskFactor` are declared above `Pathogenic`, so `Max()` returned `Other` for HFE C282Y — a well-established pathogenic variant came out unclassified. Rank on the pathogenicity axis explicitly.
- **A minor allele frequency above 0.5 is the major allele.** Ensembl's `MAF` returns 0.98274 for rs6025 (factor V Leiden), whose risk allele is near 2%. Taken literally it marks a rare pathogenic variant as common, and the report plays it down. `AnnotationMerge` reads any value above 0.5 as its complement.
- Aggregating ClinVar review status by taking the **weakest** submission sinks any well-studied variant: one submission out of forty without criteria drags an expert-panel classification to zero stars. Follow the best-reviewed submission and read the classification from that level.

### Known gap

GWAS trait associations carry no PubMed identifier. The `associationBySnp` projection embeds no study object, only a link, so a citation would cost one extra request per association across thousands. Each association still links to its own curated record, which is a real drill-down, but the proper fix is the GWAS Catalog's downloadable association TSV — it carries `PUBMEDID` for every row in a single file, and that is the bulk-export path this project prefers anyway.

## Strand orientation

The major technical pitfall in this project. Without this step, some associations will be **silently wrong** — far worse than returning nothing.

DNA is double-stranded: an `A` on the plus strand is a `T` on the minus strand, a `C` is a `G`. Test providers and SNPedia do not always report on the same strand. A genotype reported `AA` by 23andMe may correspond to the `Rs…(T;T)` page.

SNPedia exposes two distinct properties:

- **`Orientation`** — the variant's orientation in the current build (GRCh38).
- **`StabilizedOrientation`** — the same notion, but consistent with the associated genotype pages: it is only flipped when the linked genotype pages are flipped too.

**`StabilizedOrientation` is the one to use when matching a genotype to a genotype page.** Using `Orientation` instead produces incorrect matches. When `StabilizedOrientation` is `minus`, complement the alleles before lookup.

### Ambiguous flips — the case that cannot be resolved

For an **A/T or C/G** SNP, complementing resolves nothing: `A;A` complemented is `T;T`, which is *also* a valid genotype for that same SNP. No strand logic can decide between them from the alleles alone. SNPedia calls these *ambiguous flips*.

These variants must be **detected and reported as indeterminate**, never guessed. An arbitrary match on a palindromic SNP can invert the meaning of an association entirely — a protective allele presented as a risk allele. Forbid any default choice on that code path.

Dedicated tests to cover: `plus` orientation, `minus` orientation requiring complementation, homozygous palindromic SNP (indeterminate expected), and heterozygous palindromic SNP (`A;T` — unchanged by complementation, therefore usable).

## Analysis engine and report

The report must not be a flat one-SNP-per-row list. Most useful interpretations rest on a **combination** of markers.

### Multi-marker rules

The engine works from declarative rules (data, not compiled code), each declaring the markers it depends on and how they combine. Real cases to support:

- **Compound haplotypes** — APOE is the canonical example: the ε2/ε3/ε4 alleles are derived from the combination of `rs429358` and `rs7412`. Neither SNP alone carries the information.
- **Compound heterozygosity** — two different pathogenic variants in the same gene, e.g. HFE `rs1800562` (C282Y) and `rs1799945` (H63D) for hemochromatosis.
- **Pharmacogenomic star alleles** — `*1`, `*2`… alleles of CYP2D6 or CYP2C19 are defined by combinations of positions; the diplotype determines the metabolizer phenotype.
- **Polygenic scores** — weighted aggregation of many small-effect variants.

### Structural limits to encode in the engine

These are properties of array data, not implementation shortcomings. Treat them as first-class cases:

- **Array data is unphased.** There is no way to know whether two variants sit on the same chromosome copy or on opposite copies. True compound heterozygosity (in *trans*, both copies affected) is therefore **indistinguishable** from two variants in *cis* (one intact copy). The report must say "consistent with", never "confirmed".
- **Required marker missing.** If a rule needs five positions and the chip covers three, the result is **indeterminate** — never an assumption about the missing positions.
- **Do not naively multiply odds ratios.** Published ORs assume independence and come from different populations; composing them mechanically overstates the result.

An evaluation result must therefore distinguish at minimum: `Determinate`, `Indeterminate` (insufficient data or ambiguous flip), `NotApplicable`. An `Indeterminate` is displayed along with its reason — it is useful information, not a failure to hide.

### What "intelligent report" means here

Prioritization and contextualization, **not** medical text generation. Concretely: rank by evidence strength and relevance, group markers belonging to the same mechanism, contextualize with population frequency (a variant present in 40% of Europeans is not presented as a discovery), and allow drill-down to the source.

Every claim displayed must be traceable to a cited source. If an LLM is ever used to write summaries, it must be constrained to rephrasing supplied sources with citations — never to producing a clinical claim of its own.

## Presenting clinical content

The tool displays disease associations. These rules govern *how*, and are not negotiable.

- **A classification describes the variant, not the person.** ClinVar classifies the alternate allele. Someone homozygous for the reference allele does not carry the finding, and attaching the variant's classification to their result is a false alarm — prothrombin G20210A reported as "pathogenic" for a plain `GG` genotype. Compare the genotype against `VariantAnnotation.ReferenceAllele` before showing anything clinical. Where the reference allele is unknown, say so: `Finding.CarriesVariant` returns `null`, and "cannot tell" must never be rendered as "does not carry it".
- **Report the reference genotypes too.** "This position was examined and carries the ordinary allele" is a different statement from "this position was never looked at", and a reader cannot tell them apart unless both are shown.

- **Raw array data is not clinical grade.** A reference study (Tandy-Connor et al., *Genetics in Medicine*, 2018) found roughly 40% of variants reported in raw consumer data were false positives on confirmation in an accredited laboratory. Any high-impact finding must be presented as **requiring confirmatory clinical testing**.
- **Absence of a finding is not absence of risk.** An array interrogates fixed, predefined positions. 23andMe's BRCA test, for instance, covers three Ashkenazi founder variants out of thousands known. Never phrase as "no risk detected"; phrase as "none of the N tested positions carries a known variant".
- **Scale presentation to impact.** A caffeine metabolism SNP and a Huntington or BRCA variant do not get the same treatment. High-impact or actionable findings must explicitly recommend professional genetic counselling.
- **Show the evidence level.** ClinVar review status (stars) and classification status matter as much as the classification itself: a one-star VUS is not a four-star pathogenic variant. Do not flatten that distinction.
- **Never prescriptive.** No "you should take…", no "you have condition X". Phrase in terms of association and probability, in the conditional.
- Explicit disclaimer on all user-facing output: educational information, not a diagnosis, not a substitute for a healthcare professional.

## Testing

- Parsing and the rules engine are tested without network or database: inject the annotation interfaces with test doubles.
- Synthetic fixtures (a few dozen lines per format), **never a real genome file** in the repository — including the developer's own.
- Priorities: no-calls, X/Y/MT hemizygosity, strand complementation, ambiguous flip, merged rsID, missing required marker in a multi-SNP rule, haplotype derivation (APOE makes a good reference case), truncated file or unexpected header.

## Conventions

- The domain speaks English in code (`Genotype`, `RiskAllele`, `Haplotype`, `Chromosome`); **the user interface is in French**.
- A genome file is health data: treat it as a secret, not as an ordinary input file.
