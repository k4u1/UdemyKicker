using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using UdemyKicker;
using UdemyKickerWPF.Controls;

namespace UdemyKickerWPF.Views
{
    public partial class HomeView : Page
    {
        private Dictionary<string, CourseCard> activeCards = new Dictionary<string, CourseCard>();

        public HomeView()
        {
            InitializeComponent();
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            DownloadService.OnProgressUpdated += DownloadService_OnProgressUpdated;
            RefreshActiveDownloads();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            DownloadService.OnProgressUpdated -= DownloadService_OnProgressUpdated;
        }

        private void DownloadService_OnProgressUpdated(string courseId, CourseProgressData progress)
        {
            Dispatcher.Invoke(() =>
            {
                if (activeCards.ContainsKey(courseId))
                {
                    var card = activeCards[courseId];
                    card.UpdateProgress(progress.Percentage, progress.Status, progress.CurrentVideo, progress.ItemsDownloaded, progress.TotalItems);
                    if (progress.IsStopped)
                    {
                        card.SetStoppedState();
                    }
                    else
                    {
                        card.SetDownloadingState(true);
                    }
                }
                else
                {
                    bool isCompleted = progress.Status != null && (progress.Status.Contains("Completed") || progress.Status.Contains("Done") || progress.Percentage >= 100);
                    if (!isCompleted)
                    {
                        AddActiveCourseCard(progress);
                    }
                }
                UpdateNoDownloadsMessage();
            });
        }

        private void RefreshActiveDownloads()
        {
            wpActiveDownloads.Children.Clear();
            activeCards.Clear();

            foreach (var kvp in DownloadService.SavedProgressDict)
            {
                var progress = kvp.Value;
                bool isCompleted = progress.Status != null && (progress.Status.Contains("Completed") || progress.Status.Contains("Done") || progress.Percentage >= 100);
                if (!isCompleted)
                {
                    AddActiveCourseCard(progress);
                }
            }

            UpdateNoDownloadsMessage();
        }

        private void AddActiveCourseCard(CourseProgressData progress)
        {
            string safeCourseId = progress.CourseId;
            if (activeCards.ContainsKey(safeCourseId)) return;

            var card = new CourseCard();
            activeCards[safeCourseId] = card;

            card.SetCourseData(progress.CourseTitle, progress.LecturesCount, null, progress.EstimatedHours);

            card.OnPlayClicked += () =>
            {
                if (DownloadService.CourseMetadata.TryGetValue(progress.CourseId, out var course))
                {
                    string downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "udemyKicker");
                    DownloadService.StartDownload(course, downloadsRoot);
                }
            };

            card.OnPauseClicked += () =>
            {
                DownloadService.StopDownload(int.Parse(progress.CourseId));
            };

            card.OnFolderClicked += () =>
            {
                string downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "udemyKicker");
                string path = Path.Combine(downloadsRoot, DownloadManager.SanitizeFileName(progress.CourseTitle));
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", path);
            };

            card.UpdateProgress(progress.Percentage, progress.Status, progress.CurrentVideo, progress.ItemsDownloaded, progress.TotalItems);
            if (progress.IsStopped)
            {
                card.SetStoppedState();
            }
            else if (DownloadService.ActiveDownloads.ContainsKey(safeCourseId))
            {
                card.SetDownloadingState(true);
            }

            wpActiveDownloads.Children.Add(card);

            Task.Run(async () =>
            {
                string safeName = string.Join("_", progress.CourseTitle.Split(Path.GetInvalidFileNameChars()));
                string thumbPath = await UdemyApiManager.DownloadAndCacheThumbnailAsync(safeName, progress.ImageUrl);
                if (!string.IsNullOrEmpty(thumbPath))
                {
                    Dispatcher.Invoke(() =>
                    {
                        card.SetCourseData(progress.CourseTitle, progress.LecturesCount, thumbPath, progress.EstimatedHours);
                    });
                }
            });
        }

        private void UpdateNoDownloadsMessage()
        {
            lblNoDownloads.Visibility = wpActiveDownloads.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnPauseAll_Click(object sender, RoutedEventArgs e)
        {
            var activeIds = new List<string>(DownloadService.ActiveDownloads.Keys);
            foreach (var id in activeIds)
            {
                if (int.TryParse(id, out int courseId))
                {
                    DownloadService.StopDownload(courseId);
                }
            }
        }

        private void BtnResumeAll_Click(object sender, RoutedEventArgs e)
        {
            string downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "udemyKicker");
            
            foreach (var kvp in DownloadService.SavedProgressDict)
            {
                var progress = kvp.Value;
                string courseId = kvp.Key;

                // Check if it is completed
                bool isCompleted = progress.Status != null && (progress.Status.Contains("Completed") || progress.Status.Contains("Done") || progress.Percentage >= 100);
                
                // If not completed and not currently active, resume it
                if (!isCompleted && !DownloadService.ActiveDownloads.ContainsKey(courseId))
                {
                    if (DownloadService.CourseMetadata.TryGetValue(courseId, out var course))
                    {
                        DownloadService.StartDownload(course, downloadsRoot);
                    }
                }
            }
        }
    }
}
