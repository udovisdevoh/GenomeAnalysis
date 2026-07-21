using System;
using System.Windows.Forms;

namespace GenomeAnalysis.App
{
    /// <summary>
    /// Desktop entry point.
    /// </summary>
    /// <remarks>
    /// A desktop application rather than a local web server: the tool is for one
    /// person on one machine, and a process with no listening socket cannot be
    /// exposed on a network by accident. The genome file is opened from disk and
    /// read in place — never uploaded, never copied, never buffered through a
    /// request.
    /// </remarks>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (_, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

            Application.Run(new MainForm());
        }

        /// <summary>
        /// Reports a crash without echoing anything from the genome file: an
        /// exception message is a place genotypes must never reach.
        /// </summary>
        private static void ShowFatal(Exception? exception)
        {
            MessageBox.Show(
                "Une erreur inattendue est survenue.\n\n" +
                (exception?.GetType().Name ?? "Erreur") + " : " + (exception?.Message ?? "inconnue"),
                "GenomeAnalysis",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
