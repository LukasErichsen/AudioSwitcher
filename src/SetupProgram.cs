using System;
using System.IO;
using System.Windows;

namespace HeadphoneSwitcher
{
    internal static class SetupProgram
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDirectory);
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                return app.Run(new SetupWindow(new WindowsAudioBackend(), AppPaths.ConfigPath, AppPaths.LogPath));
            }
            catch (Exception ex)
            {
                Logger.Write(AppPaths.LogPath, "Setup could not open.", ex);
                MessageBox.Show("AudioSwitcher could not open.\n\n" + ex.Message, "AudioSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1;
            }
        }
    }
}
