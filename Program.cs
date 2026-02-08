using System;
using System.Windows.Forms;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Main program entry point for Unified DDR SPD Flasher v2.0
    /// </summary>
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Enable visual styles for modern appearance
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Set up exception handling
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try
            {
                // Run the main form
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ShowErrorDialog("Application Startup Error", ex);
            }
        }

        /// <summary>
        /// Handle UI thread exceptions
        /// </summary>
        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            ShowErrorDialog("Unhandled Thread Exception", e.Exception);
        }

        /// <summary>
        /// Handle non-UI thread exceptions
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                ShowErrorDialog("Unhandled Exception", ex);
            }
        }

        /// <summary>
        /// Display error dialog with exception details
        /// </summary>
        private static void ShowErrorDialog(string title, Exception ex)
        {
            string message = $"An error occurred:\n\n{ex.Message}\n\n";
            message += $"Type: {ex.GetType().Name}\n";

            if (ex.InnerException != null)
            {
                message += $"\nInner Exception: {ex.InnerException.Message}\n";
            }

            message += $"\nStack Trace:\n{ex.StackTrace}";

            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}