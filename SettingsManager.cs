using System;
using System.IO;
using Newtonsoft.Json;

namespace UdemyKicker
{
    public class AppSettings
    {
        public string DownloadMode { get; set; } = "All"; // "Normal Only", "Encrypted Only", "All"
        public string VideoQuality { get; set; } = "720p"; // "Auto", "1080p", "720p", "480p", "360p"
        public bool DownloadSubtitles { get; set; } = true;
        public string SubtitleLanguage { get; set; } = "all";
        public bool DownloadAttachments { get; set; } = true;
        public bool DownloadAttachmentsOnly { get; set; } = false;
        
        // translation configuration
        public bool TranslateToArabic { get; set; } = false;
        public string TranslationMethod { get; set; } = "Local Model"; // "API", "Local Model"
    }

    public static class SettingsManager
    {
        private static string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_settings.json");
        public static AppSettings Current { get; set; } = new AppSettings();

        public static void Load()
        {
            if (File.Exists(settingsFile))
            {
                try
                {
                    string json = File.ReadAllText(settingsFile);
                    Current = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                catch { }
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Current, Formatting.Indented);
                File.WriteAllText(settingsFile, json);
            }
            catch { }
        }
    }
}
