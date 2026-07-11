using System;
using System.IO;
using System.Windows;
using UdemyKicker;
using UdemyKickerWPF.Views;

namespace UdemyKickerWPF
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            SettingsManager.Load();
            string tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_token.txt");
            if (File.Exists(tokenPath))
            {
                UdemyApiManager.AccessToken = File.ReadAllText(tokenPath);
            }
            var splash = new SplashWindow();
            splash.Show();
        }
    }
}
