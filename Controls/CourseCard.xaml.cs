using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using UdemyKicker;

namespace UdemyKickerWPF.Controls
{
    public partial class CourseCard : UserControl
    {
        public event Action OnPlayClicked;
        public event Action OnPauseClicked;
        public event Action OnFolderClicked;

        public string CourseTitle { get; private set; }

        public CourseCard()
        {
            InitializeComponent();
        }

        public void SetCourseData(string title, int lectures, string thumbnailPath, string hours, string? quality = null, double rating = 0.0)
        {
            CourseTitle = title;
            Dispatcher.Invoke(() =>
            {
                lblTitle.Text = title;
                lblInfo.Text = $"{lectures} lectures";
                lblHours.Text = hours;
                lblQuality.Text = quality ?? SettingsManager.Current?.VideoQuality ?? "1080p";

                double displayRating = rating;
                if (displayRating <= 0.0)
                {
                    int hash = 0;
                    if (!string.IsNullOrEmpty(title))
                    {
                        foreach (char c in title)
                        {
                            hash = (hash * 31) + c;
                        }
                    }
                    var random = new Random(Math.Abs(hash));
                    displayRating = 4.4 + random.NextDouble() * 0.5;
                }
                lblRating.Text = displayRating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

                if (!string.IsNullOrEmpty(thumbnailPath) && System.IO.File.Exists(thumbnailPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(thumbnailPath);
                    ImageBrush brush = new ImageBrush();
                    brush.ImageSource = bmp;
                    brush.Stretch = Stretch.UniformToFill;
                    bmp.EndInit();
                    myborder.Background = brush;
                }
            });
        }

        public void UpdateProgress(double percentage, string text, string currentVideo = "", int itemsDownloaded = 0, int totalItems = 0)
        {
            Dispatcher.Invoke(() =>
            {
                cname.Text = currentVideo;

                bool isFetching = (text != null && (text.Contains("Starting") || text.Contains("fetching") || text.Contains("Fetching") || text.Contains("Loading"))) || percentage < 0;

                if (isFetching)
                {
                    barVideoProgress.IsIndeterminate = true;
                    barTotalProgress.IsIndeterminate = true;
                }
                else
                {
                    barVideoProgress.IsIndeterminate = false;
                    barTotalProgress.IsIndeterminate = false;

                    if (percentage >= 0)
                    {
                        if (percentage > 100) percentage = 100;
                        barVideoProgress.Value = percentage;
                    }

                    if (totalItems > 0 && itemsDownloaded >= 0)
                    {
                        double overallPct = ((double)itemsDownloaded / totalItems) * 100;
                        if (overallPct < 0) overallPct = 0;
                        if (overallPct > 100) overallPct = 100;
                        barTotalProgress.Value = overallPct;
                    }
                }

                // 3. Status Badge and Text
                if (!string.IsNullOrEmpty(text))
                {
                    lblSpeedBadge.Text = text;
                }
                else
                {
                    lblSpeedBadge.Text = percentage >= 100 && itemsDownloaded >= totalItems && totalItems > 0 ? "Done" : (percentage > 0 ? "Downloading" : "Queued");
                    lblSpeedBadge.Foreground = greenBrush;
                }

                if (!string.IsNullOrEmpty(text) && (text.Contains("Error") || text.Contains("Failed")))
                {
                    lblSpeedBadge.Text = "Error";
                    lblSpeedBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100)); // Red for errors
                }

                lblItemsText.Text = $"Downloaded {itemsDownloaded} out of {totalItems} items";
            });
        }

        private static readonly System.Windows.Media.Brush greenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)); // #4edea3
        private static readonly System.Windows.Media.Brush yellowBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 196, 15)); // #f1c40f

        public void SetDownloadingState(bool isDownloading)
        {
            Dispatcher.Invoke(() =>
            {
                btnPlay.IsEnabled = !isDownloading;
                btnPause.IsEnabled = isDownloading;
                if (isDownloading)
                {
                    barVideoProgress.Foreground = greenBrush;
                    barTotalProgress.Foreground = greenBrush;
                    lblSpeedBadge.Foreground = greenBrush;
                }
            });
        }

        public void SetStoppedState()
        {
            Dispatcher.Invoke(() =>
            {
                barVideoProgress.Foreground = yellowBrush;
                barTotalProgress.Foreground = yellowBrush;
                btnPlay.IsEnabled = true;
                btnPause.IsEnabled = false;
            });
        }

        public void RefreshQuality()
        {
            Dispatcher.Invoke(() =>
            {
                lblQuality.Text = SettingsManager.Current?.VideoQuality ?? "1080p";
            });
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            SetDownloadingState(true);
            OnPlayClicked?.Invoke();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            SetDownloadingState(false);
            OnPauseClicked?.Invoke();
        }

        private void BtnFolder_Click(object sender, RoutedEventArgs e)
        {
            OnFolderClicked?.Invoke();
        }
    }
}
