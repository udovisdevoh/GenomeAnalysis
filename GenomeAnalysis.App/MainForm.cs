using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Parsing;

namespace GenomeAnalysis.App
{
    /// <summary>
    /// The application window: pick a file, read it, show what is known.
    /// </summary>
    /// <remarks>
    /// Layout is built in code rather than in a designer file so that it stays
    /// reviewable in a diff. Interface text is French, per the project convention
    /// that the domain speaks English in code and the interface speaks French.
    /// </remarks>
    public sealed class MainForm : Form
    {
        private static readonly Color HighImpactColour = Color.FromArgb(0xB1, 0x1F, 0x24);
        private static readonly Color MutedColour = Color.FromArgb(0x66, 0x66, 0x66);

        private readonly Button _openButton;
        private readonly Label _fileLabel;
        private readonly Label _databaseLabel;
        private readonly TabControl _tabs;
        private readonly ListView _findingsList;
        private readonly ListView _indeterminateList;
        private readonly ListView _referenceList;
        private readonly TextBox _detailBox;
        private readonly TextBox _summaryBox;
        private readonly ToolStripStatusLabel _status;
        private readonly ProgressBar _progress;

        private VariantDatabase? _database;
        private IReadOnlyList<Finding> _findings = new List<Finding>();

        public MainForm()
        {
            Text = "GenomeAnalysis — analyse de génome personnel";
            Width = 1180;
            Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            MinimumSize = new Size(900, 600);

            var top = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(12) };

            _openButton = new Button
            {
                Text = "Ouvrir un fichier ADN…",
                Width = 190,
                Height = 32,
                Location = new Point(12, 12)
            };
            _openButton.Click += OnOpenClicked;

            _fileLabel = new Label
            {
                Text = "Aucun fichier chargé.",
                Location = new Point(214, 19),
                AutoSize = true,
                ForeColor = MutedColour
            };

            _databaseLabel = new Label
            {
                Location = new Point(12, 54),
                AutoSize = true,
                ForeColor = MutedColour
            };

            _progress = new ProgressBar
            {
                Location = new Point(214, 50),
                Width = 260,
                Height = 12,
                Style = ProgressBarStyle.Marquee,
                Visible = false
            };

            top.Controls.Add(_openButton);
            top.Controls.Add(_fileLabel);
            top.Controls.Add(_databaseLabel);
            top.Controls.Add(_progress);

            _findingsList = CreateListView(
                ("Marqueur", 110), ("Gène", 110), ("Génotype", 80),
                ("Classification", 210), ("Preuve", 70), ("Fréquence", 90), ("Associations", 260));
            _findingsList.SelectedIndexChanged += (_, __) => ShowDetail(SelectedFinding(_findingsList));

            _indeterminateList = CreateListView(("Marqueur", 110), ("Gène", 110), ("Raison", 780));
            _indeterminateList.SelectedIndexChanged += (_, __) => ShowDetail(SelectedFinding(_indeterminateList));

            _referenceList = CreateListView(
                ("Marqueur", 110), ("Gène", 110), ("Génotype", 90), ("Classification du variant", 260));
            _referenceList.SelectedIndexChanged += (_, __) => ShowDetail(SelectedFinding(_referenceList));

            _detailBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None
            };

            _summaryBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None
            };

            var findingsSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 340
            };
            findingsSplit.Panel1.Controls.Add(_findingsList);
            findingsSplit.Panel2.Controls.Add(_detailBox);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(CreateTab("Résultats", findingsSplit));
            _tabs.TabPages.Add(CreateTab("Indéterminés", _indeterminateList));
            _tabs.TabPages.Add(CreateTab("Positions sans variant", _referenceList));
            _tabs.TabPages.Add(CreateTab("Bilan", _summaryBox));

            // The disclaimer is a permanent part of the window, not a dialog that
            // gets dismissed once and forgotten.
            var disclaimer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                Padding = new Padding(12, 8, 12, 8),
                BackColor = Color.FromArgb(0xFF, 0xF8, 0xE1),
                ForeColor = Color.FromArgb(0x5D, 0x40, 0x37),
                Text =
                    "Information éducative — ceci n'est pas un diagnostic et ne remplace pas un professionnel de santé.\n" +
                    "Une puce n'interroge qu'un ensemble fixe de positions : l'absence de résultat ne signifie jamais l'absence de risque.\n" +
                    "Les associations sont statistiques et populationnelles, elles ne se multiplient pas entre elles."
            };

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Prêt.");
            statusStrip.Items.Add(_status);

            Controls.Add(_tabs);
            Controls.Add(disclaimer);
            Controls.Add(statusStrip);
            Controls.Add(top);

            LoadDatabase();
        }

        private static TabPage CreateTab(string title, Control content)
        {
            var page = new TabPage(title) { Padding = new Padding(6) };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        private static ListView CreateListView(params (string Header, int Width)[] columns)
        {
            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                MultiSelect = false,
                HideSelection = false,
                Dock = DockStyle.Fill
            };

            foreach (var column in columns)
            {
                list.Columns.Add(column.Header, column.Width);
            }

            return list;
        }

        private void LoadDatabase()
        {
            var path = Path.Combine(FindRepositoryRoot(), "data", "variant-database.json");

            if (!File.Exists(path))
            {
                _databaseLabel.Text = "Base de variants introuvable — lancez GenomeAnalysis.Harvester.exe.";
                _databaseLabel.ForeColor = HighImpactColour;
                _openButton.Enabled = false;
                return;
            }

            _database = VariantDatabase.Load(path);

            if (_database.Count == 0)
            {
                // Almost always a schema bump. Saying so beats "empty or unreadable",
                // which leaves nothing to act on.
                _databaseLabel.Text =
                    "Base illisible (schéma attendu : v" +
                    GenomeAnalysis.Annotations.Cache.AnnotationSerializer.SchemaVersion +
                    "). Régénérez-la avec GenomeAnalysis.Harvester.exe.";
                _databaseLabel.ForeColor = HighImpactColour;
                _openButton.Enabled = false;
                return;
            }

            _databaseLabel.Text =
                "Base locale : " + _database.Count.ToString("N0") + " variants" +
                (_database.GeneratedAt.HasValue
                    ? ", générée le " + _database.GeneratedAt.Value.ToString("yyyy-MM-dd")
                    : string.Empty) +
                "   ·   analyse entièrement hors ligne, aucun appel réseau";
        }

        private async void OnOpenClicked(object? sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Choisir un fichier ADN brut";
                dialog.Filter = "Fichiers ADN (*.txt;*.csv)|*.txt;*.csv|Tous les fichiers (*.*)|*.*";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                await AnalyzeAsync(dialog.FileName).ConfigureAwait(true);
            }
        }

        private async Task AnalyzeAsync(string path)
        {
            _openButton.Enabled = false;
            _progress.Visible = true;
            _status.Text = "Lecture en cours…";
            _fileLabel.Text = Path.GetFileName(path);
            _fileLabel.ForeColor = SystemColors.ControlText;

            try
            {
                // Off the UI thread: these files run to a million lines.
                var result = await Task.Run(() => Analyze(path)).ConfigureAwait(true);

                _findings = result.Findings;
                PopulateFindings();
                PopulateIndeterminate();
                PopulateReference();
                PopulateSummary(result);

                _status.Text =
                    result.Statistics.TotalRows.ToString("N0") + " marqueurs lus · " +
                    result.Summary.Determinate + " interprétables · " +
                    result.Summary.Indeterminate + " indéterminés";
            }
            catch (Exception exception)
            {
                // No genotype ever reaches this message.
                MessageBox.Show(
                    this,
                    "Le fichier n'a pas pu être lu.\n\n" + exception.GetType().Name + " : " + exception.Message,
                    "Lecture impossible",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                _status.Text = "Échec de la lecture.";
            }
            finally
            {
                _progress.Visible = false;
                _openButton.Enabled = true;
            }
        }

        private AnalysisRun Analyze(string path)
        {
            using (var reader = GenomeFileReader.Open(path))
            {
                var analyzer = new GenomeAnalyzer(_database!);
                var findings = GenomeAnalyzer.Prioritise(analyzer.Analyze(reader.ReadCalls()));

                return new AnalysisRun(
                    reader.Header,
                    reader.Statistics,
                    findings,
                    new AnalysisSummary(findings));
            }
        }

        private void PopulateFindings()
        {
            _findingsList.BeginUpdate();
            _findingsList.Items.Clear();

            foreach (var finding in _findings.Where(f =>
                         f.Status == FindingStatus.Determinate && f.PriorityScore > 0))
            {
                var annotation = finding.Annotation!;
                var clinical = finding.Clinical;

                var item = new ListViewItem(finding.Call.MarkerId)
                {
                    Tag = finding
                };

                item.SubItems.Add(annotation.GeneSymbol ?? "—");
                item.SubItems.Add(finding.StrandMatch!.ResolvedGenotype!.Value.ToString());
                item.SubItems.Add(clinical == null ? "—" : SignificanceName(clinical.Significance));
                item.SubItems.Add(clinical == null ? "" : Stars(clinical.ReviewStatus.ToStarRating()));
                item.SubItems.Add(annotation.MinorAlleleFrequency.HasValue
                    ? (annotation.MinorAlleleFrequency.Value * 100).ToString("0.##") + " %"
                    : "—");
                item.SubItems.Add(string.Join(", ", annotation.TraitAssociations
                    .Where(t => t.IsGenomeWideSignificant && !t.IsNegligibleEffect)
                    .Select(t => t.Trait)
                    .Take(2)));

                if (finding.RequiresConfirmatoryTesting)
                {
                    item.ForeColor = HighImpactColour;
                    item.Font = new Font(_findingsList.Font, FontStyle.Bold);
                }

                _findingsList.Items.Add(item);
            }

            _findingsList.EndUpdate();

            if (_findingsList.Items.Count > 0)
            {
                _findingsList.Items[0].Selected = true;
            }
            else
            {
                _detailBox.Text =
                    "Aucune des positions testées et couvertes par la base locale ne porte de variant documenté." +
                    Environment.NewLine + Environment.NewLine +
                    "Cela ne signifie pas une absence de risque : voir l'avertissement en bas de fenêtre.";
            }
        }

        private void PopulateIndeterminate()
        {
            _indeterminateList.BeginUpdate();
            _indeterminateList.Items.Clear();

            foreach (var finding in _findings.Where(f => f.Status == FindingStatus.Indeterminate))
            {
                var item = new ListViewItem(finding.Call.MarkerId) { Tag = finding };
                item.SubItems.Add(finding.Annotation?.GeneSymbol ?? "—");
                item.SubItems.Add(finding.Reason);
                _indeterminateList.Items.Add(item);
            }

            _indeterminateList.EndUpdate();
        }

        private void PopulateReference()
        {
            _referenceList.BeginUpdate();
            _referenceList.Items.Clear();

            foreach (var finding in _findings.Where(f =>
                         f.Status == FindingStatus.Determinate && f.CarriesVariant == false))
            {
                var item = new ListViewItem(finding.Call.MarkerId) { Tag = finding };
                item.SubItems.Add(finding.Annotation?.GeneSymbol ?? "—");
                item.SubItems.Add(finding.StrandMatch!.ResolvedGenotype!.Value.ToString());
                item.SubItems.Add(finding.ClinicalForVariant == null
                    ? "—"
                    : SignificanceName(finding.ClinicalForVariant.Significance));
                _referenceList.Items.Add(item);
            }

            _referenceList.EndUpdate();
        }

        private void PopulateSummary(AnalysisRun run)
        {
            var text = new System.Text.StringBuilder();

            text.AppendLine("FICHIER");
            text.AppendLine("  Fournisseur              : " + ProviderName(run.Header.Provider));
            text.AppendLine("  Build de référence       : " + BuildName(run.Header.Build));
            text.AppendLine();

            foreach (var warning in run.Header.Warnings)
            {
                text.AppendLine("  ⚠ " + warning);
            }

            if (run.Header.Warnings.Count > 0)
            {
                text.AppendLine();
            }

            text.AppendLine("LECTURE");
            text.AppendLine("  Marqueurs lus            : " + run.Statistics.TotalRows.ToString("N0"));
            text.AppendLine("  Génotypes exploitables   : " + run.Statistics.CalledGenotypes.ToString("N0"));
            text.AppendLine("  Absences de lecture      : " + run.Statistics.NoCalls.ToString("N0"));
            text.AppendLine("  Appels hémizygotes       : " + run.Statistics.HemizygousCalls.ToString("N0") + "   (X, Y, MT)");
            text.AppendLine("  Identifiants internes    : " + run.Statistics.ProviderInternalIds.ToString("N0") +
                            "   (sans équivalent public)");

            if (run.Statistics.MalformedRows > 0)
            {
                text.AppendLine("  Lignes illisibles        : " + run.Statistics.MalformedRows.ToString("N0"));
            }

            text.AppendLine();
            text.AppendLine("ANALYSE");
            text.AppendLine("  Interprétables           : " + run.Summary.Determinate);
            text.AppendLine("  Indéterminés             : " + run.Summary.Indeterminate);
            text.AppendLine("  Hors périmètre           : " + run.Summary.NotApplicable);
            text.AppendLine("  Dont flips ambigus       : " + run.Summary.AmbiguousFlips +
                            "   (SNP palindromiques A/T ou C/G)");
            text.AppendLine("  Brins complémentés       : " + run.Summary.Complemented);
            text.AppendLine("  Avec dossier clinique    : " + run.Summary.WithClinicalRecord);
            text.AppendLine("  Exigeant confirmation    : " + run.Summary.RequiringConfirmation);
            text.AppendLine();
            text.AppendLine("PORTÉE");
            text.AppendLine("  Une puce ADN grand public n'interrogeant qu'un ensemble fixe de positions,");
            text.AppendLine("  ce bilan décrit ce qui a été examiné — pas ce qui existe. La grande majorité");
            text.AppendLine("  des variants pathogènes connus ne figurent sur aucune puce de ce type.");

            _summaryBox.Text = text.ToString();
        }

        private static Finding? SelectedFinding(ListView list) =>
            list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as Finding;

        private void ShowDetail(Finding? finding)
        {
            if (finding == null)
            {
                return;
            }

            var annotation = finding.Annotation;
            var text = new System.Text.StringBuilder();

            text.AppendLine(finding.Call.MarkerId +
                            (annotation?.GeneSymbol == null ? "" : "   ·   gène " + annotation.GeneSymbol) +
                            "   ·   chromosome " + finding.Call.Chromosome);

            if (finding.StrandMatch?.ResolvedGenotype != null)
            {
                text.AppendLine("Génotype : " + finding.StrandMatch.ResolvedGenotype.Value +
                                (finding.StrandMatch.WasComplemented
                                    ? "   (allèles complémentés — la source lit le brin opposé)"
                                    : string.Empty));
            }

            text.AppendLine();

            if (finding.Status != FindingStatus.Determinate)
            {
                text.AppendLine(finding.Status == FindingStatus.Indeterminate ? "INDÉTERMINÉ" : "HORS PÉRIMÈTRE");
                text.AppendLine("  " + finding.Reason);
                _detailBox.Text = text.ToString();
                return;
            }

            if (finding.CarriesVariant == false)
            {
                text.AppendLine("CE GÉNOTYPE NE PORTE PAS LE VARIANT");
                text.AppendLine("  La position a été testée et correspond à l'allèle de référence.");
                text.AppendLine("  La classification ci-dessous décrit le variant, pas ce résultat.");
                text.AppendLine();
            }
            else if (finding.CarriesVariant == null)
            {
                text.AppendLine("STATUT DE PORTEUR INDÉTERMINABLE");
                text.AppendLine("  L'allèle de référence est inconnu pour ce marqueur : impossible de dire");
                text.AppendLine("  si ce génotype porte le variant. Les informations ci-dessous décrivent");
                text.AppendLine("  le variant, pas nécessairement ce résultat.");
                text.AppendLine();
            }

            var clinical = finding.ClinicalForVariant;

            if (clinical != null)
            {
                text.AppendLine("CLASSIFICATION CLINIQUE");
                text.AppendLine("  " + SignificanceName(clinical.Significance) +
                                "   " + Stars(clinical.ReviewStatus.ToStarRating()) +
                                "  (" + clinical.ReviewStatus.ToStarRating() + "/4 — niveau de revue ClinVar)");

                foreach (var condition in clinical.Conditions.Take(6))
                {
                    text.AppendLine("    · " + condition);
                }

                if (clinical.Conditions.Count > 6)
                {
                    text.AppendLine("    · … et " + (clinical.Conditions.Count - 6) + " autres");
                }

                text.AppendLine("  Source : " + clinical.Attribution.SourceName +
                                (clinical.Attribution.RecordUrl == null ? "" : "  " + clinical.Attribution.RecordUrl));
                text.AppendLine();
            }

            if (annotation?.MinorAlleleFrequency != null)
            {
                text.AppendLine("FRÉQUENCE EN POPULATION GÉNÉRALE");
                text.AppendLine("  " + (annotation.MinorAlleleFrequency.Value * 100).ToString("0.##") + " %" +
                                (annotation.IsCommon ? "   — variant commun" : string.Empty));
                text.AppendLine();
            }

            var traits = annotation?.TraitAssociations
                .Where(t => t.IsGenomeWideSignificant && !t.IsNegligibleEffect)
                .Take(8)
                .ToList();

            if (traits != null && traits.Count > 0)
            {
                text.AppendLine("ASSOCIATIONS PUBLIÉES");

                foreach (var trait in traits)
                {
                    text.Append("  · " + trait.Trait);

                    if (trait.OddsRatio.HasValue)
                    {
                        text.Append("   OR " + trait.OddsRatio.Value.ToString("0.##"));

                        if (trait.RiskAllele != null)
                        {
                            text.Append(" par copie de " + trait.RiskAllele);
                        }
                    }

                    if (trait.PValue.HasValue && trait.PValue.Value > 0)
                    {
                        text.Append("   p = " + trait.PValue.Value.ToString("0.0e+0"));
                    }

                    text.AppendLine();
                }

                text.AppendLine();
                text.AppendLine("  Ces valeurs décrivent des différences de fréquence entre groupes, pas une");
                text.AppendLine("  probabilité individuelle. Elles ne se multiplient pas entre elles.");
                text.AppendLine();
            }

            if (finding.RequiresConfirmatoryTesting)
            {
                text.AppendLine("⚑ RÉSULTAT À FORT IMPACT");
                text.AppendLine("  Les données de puce brutes ne sont pas de qualité clinique : une part");
                text.AppendLine("  importante des variants ainsi rapportés se révèle être des faux positifs");
                text.AppendLine("  à la vérification en laboratoire accrédité. Ce résultat exige une");
                text.AppendLine("  confirmation par test clinique et un conseil génétique professionnel");
                text.AppendLine("  avant toute interprétation.");
                text.AppendLine();
            }

            if (annotation != null)
            {
                text.AppendLine("PROVENANCE");
                text.AppendLine("  " + annotation.Attribution.SourceName + "  ·  " + annotation.Attribution.Licence);

                if (annotation.Attribution.RecordUrl != null)
                {
                    text.AppendLine("  " + annotation.Attribution.RecordUrl);
                }

                if (annotation.MergedRsIds.Count > 0)
                {
                    text.AppendLine("  Anciens identifiants fusionnés : " + string.Join(", ", annotation.MergedRsIds.Take(6)));
                }
            }

            _detailBox.Text = text.ToString();
        }

        private static string Stars(int rating) => new string('★', rating) + new string('☆', 4 - rating);

        private static string ProviderName(GenomeFileProvider provider)
        {
            switch (provider)
            {
                case GenomeFileProvider.TwentyThreeAndMe: return "23andMe";
                case GenomeFileProvider.AncestryDna: return "AncestryDNA";
                case GenomeFileProvider.MyHeritage: return "MyHeritage";
                case GenomeFileProvider.FamilyTreeDna: return "Family Tree DNA";
                default: return "non reconnu";
            }
        }

        private static string BuildName(GenomeBuild build)
        {
            switch (build)
            {
                case GenomeBuild.GRCh37: return "GRCh37 (build 37)";
                case GenomeBuild.GRCh38: return "GRCh38 (build 38)";
                default: return "non déclaré";
            }
        }

        private static string SignificanceName(ClinicalSignificance significance)
        {
            switch (significance)
            {
                case ClinicalSignificance.Pathogenic: return "pathogène";
                case ClinicalSignificance.LikelyPathogenic: return "probablement pathogène";
                case ClinicalSignificance.UncertainSignificance: return "signification incertaine (VUS)";
                case ClinicalSignificance.LikelyBenign: return "probablement bénin";
                case ClinicalSignificance.Benign: return "bénin";
                case ClinicalSignificance.ConflictingInterpretations: return "interprétations divergentes";
                case ClinicalSignificance.DrugResponse: return "réponse médicamenteuse";
                case ClinicalSignificance.RiskFactor: return "facteur de risque";
                case ClinicalSignificance.Protective: return "protecteur";
                case ClinicalSignificance.Association: return "association";
                default: return "non renseigné";
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GenomeAnalysis.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }

        private sealed class AnalysisRun
        {
            public AnalysisRun(
                GenomeFileHeader header,
                ParseStatistics statistics,
                IReadOnlyList<Finding> findings,
                AnalysisSummary summary)
            {
                Header = header;
                Statistics = statistics;
                Findings = findings;
                Summary = summary;
            }

            public GenomeFileHeader Header { get; }

            public ParseStatistics Statistics { get; }

            public IReadOnlyList<Finding> Findings { get; }

            public AnalysisSummary Summary { get; }
        }
    }
}
