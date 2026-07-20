# GenomeAnalysis

Personal human genome analysis tool. It takes a raw consumer DNA test file (23andMe, AncestryDNA, MyHeritage…), parses the genotypes, enriches them from SNPedia and other public databases, and produces an interpretive report — including clinical associations.

**Status: new project, no code written yet.** The structure below is the target, not the current state.

## Core constraints

These take precedence over any technical consideration.

1. **Single machine, local.** The application runs on the user's own machine (IIS Express / `localhost`). It is never deployed to a public server, and there are no accounts or authentication. Do not add multi-user upload, cloud storage, or telemetry.
2. **Genomic data never leaves the machine.** No genotype, no file, no derived data is sent to an external API. Only variant identifiers go out. See "External call privacy" — the rule is subtler than it looks. No genotypes in logs, error messages, or traces.
3. **The tool surfaces clinical content; it does not diagnose.** This is the point of the project: disease associations, carrier status, pharmacogenomics. The constraint is therefore not to censor that content, but to present it with its provenance, evidence level, and limits. See "Presenting clinical content".

## Tech stack

- .NET Framework 4.8, C#
- ASP.NET MVC 5 + Razor views; Web API 2 in the same project for AJAX calls (parsing progress, lazy loading of variant records)
- SQLite or LocalDB, for caching external sources only
- Visual Studio 2022, MSBuild (no `dotnet build` — this is .NET Framework)

## Target solution structure

```
GenomeAnalysis.sln
├── GenomeAnalysis.Web/          MVC 5 + Web API 2 — controllers, views, reports
├── GenomeAnalysis.Core/         Domain: models, parsers, rules engine. No web or network dependency.
├── GenomeAnalysis.Annotations/  Clients for external sources (SNPedia, Ensembl, ClinVar…) + cache
└── GenomeAnalysis.Tests/        Unit tests
```

`Core` references neither `System.Web` nor `Annotations`. The engine receives annotations through interfaces defined in `Core` and implemented in `Annotations` — that is what makes the analysis testable without network or database.

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

- Endpoint: `https://bots.snpedia.com/api.php` (MediaWiki API). This is the intended access path for automated clients; do not scrape the HTML pages on `snpedia.com`.
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
