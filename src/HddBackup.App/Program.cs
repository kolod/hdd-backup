using System;
using System.Threading;
using System.Windows.Forms;
using HddBackup.App.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;

namespace HddBackup.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);

            var settings = PortableSettings.Load();
            var language = LanguageService.IsSupported(settings.Language) ? settings.Language : LanguageService.DetectOsLanguage();
            LanguageService.SetLanguage(language);

            using (var form = new MainForm(settings))
            {
                Application.Run(form);
            }
        }

        private static void ShowFatal(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    Strings.Format("Error_Unhandled", ex?.ToString() ?? "unknown"),
                    Strings.Get("App_Name"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception)
            {
                // Nothing else can be done here.
            }
        }
    }
}
