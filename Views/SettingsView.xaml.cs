using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using UdemyKicker;

namespace UdemyKickerWPF.Views
{
    public partial class SettingsView : Page
    {
        private bool _isLoaded = false;
        private UdemyUser _user;

        public SettingsView(UdemyUser user = null)
        {
            InitializeComponent();
            _user = user;
        }

        public void UpdateUserData(UdemyUser user)
        {
            if (user == null) return;
            _user = user;

            lblGreeting.Text = _user.title ?? _user.display_name;
            lblEmail.Text = string.IsNullOrEmpty(_user.email) ? "Hidden" : _user.email;
            lblCourses.Text = _user.num_subscribed_courses.ToString();
            lblCompleted.Text = _user.num_completed_video_lectures.ToString();

            // Load profile photo asynchronously
            string photoUrl = _user.image_100x100 ?? _user.image_50x50;
            if (!string.IsNullOrWhiteSpace(photoUrl))
                _ = LoadProfileImageAsync(photoUrl);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;

            if (_user != null)
            {
                UpdateUserData(_user);
            }

            int qualityIndex = 0;
            switch (SettingsManager.Current.VideoQuality)
            {
                case "Highest": qualityIndex = 0; break;
                case "1080p": qualityIndex = 1; break;
                case "720p": qualityIndex = 2; break;
                case "480p": qualityIndex = 3; break;
                case "360p": qualityIndex = 4; break;
                case "Lowest": qualityIndex = 5; break;
            }
            cmbQuality.SelectedIndex = qualityIndex;

            int modeIndex = 2; // Default to "All"
            switch (SettingsManager.Current.DownloadMode)
            {
                case "Normal Only": modeIndex = 0; break;
                case "Encrypted Only": modeIndex = 1; break;
                case "All":
                case "Both": modeIndex = 2; break;
            }
            cmbMode.SelectedIndex = modeIndex;

            chkSubs.IsChecked = SettingsManager.Current.DownloadSubtitles;
            chkAtts.IsChecked = SettingsManager.Current.DownloadAttachments;
            chkAttsOnly.IsChecked = SettingsManager.Current.DownloadAttachmentsOnly;

            chkTranslate.IsChecked = SettingsManager.Current.TranslateToArabic;
            int methodIndex = 0;
            switch (SettingsManager.Current.TranslationMethod)
            {
                case "Local Model": methodIndex = 0; break;
                case "API": methodIndex = 1; break;
            }
            cmbTranslateMethod.SelectedIndex = methodIndex;
            pnlTranslateMethod.Visibility = (SettingsManager.Current.TranslateToArabic) ? Visibility.Visible : Visibility.Collapsed;

            UpdateToggleStates();
        }

        private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || cmbQuality.SelectedItem == null) return;
            SettingsManager.Current.VideoQuality = ((ComboBoxItem)cmbQuality.SelectedItem).Content.ToString();
        }

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || cmbMode.SelectedItem == null) return;
            string selectedText = ((ComboBoxItem)cmbMode.SelectedItem).Content.ToString();

            if (selectedText.Contains("DRM-Free"))
                SettingsManager.Current.DownloadMode = "Normal Only";
            else if (selectedText.Contains("DRM Protected"))
                SettingsManager.Current.DownloadMode = "Encrypted Only";
            else
                SettingsManager.Current.DownloadMode = "All";
        }

        private void ChkSubs_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Current.DownloadSubtitles = chkSubs.IsChecked ?? false;
            UpdateToggleStates();
        }

        private void ChkAtts_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Current.DownloadAttachments = chkAtts.IsChecked ?? false;
        }

        private void ChkAttsOnly_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Current.DownloadAttachmentsOnly = chkAttsOnly.IsChecked ?? false;
            UpdateToggleStates();
        }

        private void UpdateToggleStates()
        {
            bool attsOnly = SettingsManager.Current.DownloadAttachmentsOnly;
            cmbQuality.IsEnabled = !attsOnly;
            chkSubs.IsEnabled = !attsOnly;
        }

        // ─── Profile Image Loading ───────────────────────────────────────────────────

        private static readonly HttpClient _httpClient = new HttpClient();

        private async Task LoadProfileImageAsync(string url)
        {
            try
            {
                byte[] data = await _httpClient.GetByteArrayAsync(url);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(data);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze(); // Make thread-safe

                        imgAvatar.Source = bitmap;
                        imgAvatar.Visibility = Visibility.Visible;
                        iconAvatar.Visibility = Visibility.Collapsed; // Hide fallback icon
                    }
                    catch { /* keep fallback icon */ }
                });
            }
            catch { /* network error — keep fallback icon */ }
        }

        private void ChkTranslate_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            bool isChecked = chkTranslate.IsChecked ?? false;
            SettingsManager.Current.TranslateToArabic = isChecked;
            pnlTranslateMethod.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbTranslateMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || cmbTranslateMethod.SelectedItem == null) return;
            string selectedText = ((ComboBoxItem)cmbTranslateMethod.SelectedItem).Content.ToString();
            if (selectedText.Contains("Local Model"))
                SettingsManager.Current.TranslationMethod = "Local Model";
            else
                SettingsManager.Current.TranslationMethod = "API";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            System.Windows.MessageBox.Show("Settings saved!", "UdemyKicker", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }
}
