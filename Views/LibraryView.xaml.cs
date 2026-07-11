using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UdemyKicker;
using UdemyKickerWPF.Controls;

namespace UdemyKickerWPF.Views
{
    public partial class LibraryView : Page
    {
        private List<UdemyCourse> fetchedCourses = new List<UdemyCourse>();
        private string downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "udemyKicker");
        private Dictionary<string, CourseCard> uiCards = new Dictionary<string, CourseCard>();

        public LibraryView()
        {
            InitializeComponent();
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            DownloadService.OnProgressUpdated += DownloadService_OnProgressUpdated;
            if (wpCourses.Children.Count == 0)
            {
                _ = LoadCoursesFromApi();
            }
            else
            {
                RefreshCardsFromService();
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            DownloadService.OnProgressUpdated -= DownloadService_OnProgressUpdated;
        }

        private void DownloadService_OnProgressUpdated(string courseId, CourseProgressData progress)
        {
            Dispatcher.Invoke(() =>
            {
                if (uiCards.ContainsKey(courseId))
                {
                    var card = uiCards[courseId];
                    card.UpdateProgress(progress.Percentage, progress.Status, progress.CurrentVideo, progress.ItemsDownloaded, progress.TotalItems);
                    if (progress.IsStopped)
                    {
                        card.SetStoppedState();
                    }
                    else if (DownloadService.ActiveDownloads.ContainsKey(courseId))
                    {
                        card.SetDownloadingState(true);
                    }
                }
            });
        }

        private void RefreshCardsFromService()
        {
            foreach (var kvp in uiCards)
            {
                string courseId = kvp.Key;
                var card = kvp.Value;
                card.RefreshQuality();
                if (DownloadService.SavedProgressDict.ContainsKey(courseId))
                {
                    var progress = DownloadService.SavedProgressDict[courseId];
                    card.UpdateProgress(progress.Percentage, progress.Status, progress.CurrentVideo, progress.ItemsDownloaded, progress.TotalItems);
                    if (progress.IsStopped)
                    {
                        card.SetStoppedState();
                    }
                    else if (DownloadService.ActiveDownloads.ContainsKey(courseId))
                    {
                        card.SetDownloadingState(true);
                    }
                }
            }
        }

        private async Task LoadCoursesFromApi()
        {
            wpCourses.Children.Clear();
            fetchedCourses.Clear();
            uiCards.Clear();
            UdemyApiManager.ResetPagination();
            
            await LoadMoreCourses();
        }

        private async Task LoadMoreCourses()
        {
            loader.Visibility = Visibility.Visible;
            btnLoadMore.Visibility = Visibility.Collapsed;

            var batch = await UdemyApiManager.GetNextCoursesBatchAsync();

            loader.Visibility = Visibility.Collapsed;

            if (batch == null) return;

            fetchedCourses.AddRange(batch);

            foreach (var c in batch)
            {
                await AddCourseCard(c.id, c.title, c.num_lectures, c.estimated_content_length, c.url, c.image_480x270, c.rating);
            }

            if (!string.IsNullOrEmpty(UdemyApiManager.NextSubscribedUrl) || !string.IsNullOrEmpty(UdemyApiManager.NextEnrolledUrl))
            {
                btnLoadMore.Visibility = Visibility.Visible;
            }
        }

        private bool isSearchingAndLoading = false;

        private async void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = txtSearch.Text.ToLowerInvariant().Trim();
            
            // 1. Hide/Show already loaded cards
            foreach (CourseCard card in wpCourses.Children)
            {
                if (string.IsNullOrWhiteSpace(query) || card.CourseTitle.ToLowerInvariant().Contains(query))
                {
                    card.Visibility = Visibility.Visible;
                }
                else
                {
                    card.Visibility = Visibility.Collapsed;
                }
            }

            // 2. If the query is not empty and we are not already fetching, start background fetching!
            if (!string.IsNullOrEmpty(query) && !isSearchingAndLoading)
            {
                if (!string.IsNullOrEmpty(UdemyApiManager.NextSubscribedUrl) || !string.IsNullOrEmpty(UdemyApiManager.NextEnrolledUrl))
                {
                    isSearchingAndLoading = true;
                    try
                    {
                        while (isSearchingAndLoading && !string.IsNullOrEmpty(txtSearch.Text.Trim()))
                        {
                            string currentQuery = txtSearch.Text.ToLowerInvariant().Trim();
                            if (string.IsNullOrEmpty(currentQuery)) break;

                            if (string.IsNullOrEmpty(UdemyApiManager.NextSubscribedUrl) && string.IsNullOrEmpty(UdemyApiManager.NextEnrolledUrl))
                            {
                                break;
                            }

                            // Show loader while fetching next batch
                            loader.Visibility = Visibility.Visible;
                            btnLoadMore.Visibility = Visibility.Collapsed;

                            var batch = await UdemyApiManager.GetNextCoursesBatchAsync();

                            loader.Visibility = Visibility.Collapsed;

                            if (batch == null || batch.Count == 0) break;

                            fetchedCourses.AddRange(batch);

                            foreach (var c in batch)
                            {
                                await AddCourseCard(c.id, c.title, c.num_lectures, c.estimated_content_length, c.url, c.image_480x270, c.rating);
                                var card = uiCards[c.id.ToString()];
                                
                                // Set visibility based on the current search query
                                string latestQuery = txtSearch.Text.ToLowerInvariant().Trim();
                                if (string.IsNullOrEmpty(latestQuery) || c.title.ToLowerInvariant().Contains(latestQuery))
                                {
                                    card.Visibility = Visibility.Visible;
                                }
                                else
                                {
                                    card.Visibility = Visibility.Collapsed;
                                }
                            }

                            if (!string.IsNullOrEmpty(UdemyApiManager.NextSubscribedUrl) || !string.IsNullOrEmpty(UdemyApiManager.NextEnrolledUrl))
                            {
                                btnLoadMore.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        isSearchingAndLoading = false;
                        loader.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private async Task AddCourseCard(int courseId, string courseName, int totalLectures, int estimatedContentLength, string udemyUrl, string imageUrl, double rating = 0.0)
        {
            string safeName = string.Join("_", courseName.Split(Path.GetInvalidFileNameChars()));
            string safeCourseId = courseId.ToString();

            CourseCard card = new CourseCard();
            uiCards[safeCourseId] = card;

            string hoursStr = estimatedContentLength > 0 ? $"{(estimatedContentLength / 60.0):0.0} total hours" : "12.5 total hours";
            card.SetCourseData(courseName, totalLectures, null, hoursStr, null, rating);
            
            card.OnPlayClicked += () => StartDownload(courseId, courseName, udemyUrl);
            card.OnPauseClicked += () => StopDownload(courseId);
            
            card.OnFolderClicked += () => 
            {
                string path = Path.Combine(downloadsRoot, DownloadManager.SanitizeFileName(courseName));
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", path);
            };

            wpCourses.Children.Add(card);

            // Apply saved progress if exists
            if (DownloadService.SavedProgressDict.ContainsKey(safeCourseId))
            { 
                var progress = DownloadService.SavedProgressDict[safeCourseId];
                
                card.UpdateProgress(progress.Percentage, progress.Status, progress.CurrentVideo, progress.ItemsDownloaded, progress.TotalItems);
                if (progress.IsStopped)
                {
                    card.SetStoppedState();
                }
                else if (DownloadService.ActiveDownloads.ContainsKey(safeCourseId))
                {
                    card.SetDownloadingState(true);
                }
            }

            // Run thumbnail download in background without blocking the UI/Card creation flow
            _ = Task.Run(async () =>
            {
                try
                {
                    string thumbPath = await UdemyApiManager.DownloadAndCacheThumbnailAsync(safeName, imageUrl);
                    if (!string.IsNullOrEmpty(thumbPath))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            card.SetCourseData(courseName, totalLectures, thumbPath, hoursStr, null, rating);
                        });
                    }
                }
                catch { }
            });
        }

        private async void StartDownload(int courseId, string courseName, string courseUrl)
        {
            var course = fetchedCourses.Find(c => c.id == courseId);
            if (course != null)
            {
                try
                {
                    // Fetch the course curriculum to display in the selection window
                    var curriculumResult = await UdemyApiManager.GetCourseCurriculumAsync(courseId);
                    var curriculum = curriculumResult.items;

                    if (curriculum == null || curriculum.Count == 0)
                    {
                        MessageBox.Show("Failed to load course curriculum. Please check your connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Filter curriculum according to user download settings (Normal Only, Encrypted Only, All)
                    string mode = SettingsManager.Current.DownloadMode;
                    var filteredCurriculum = new List<UdemyCurriculumItem>();

                    foreach (var item in curriculum)
                    {
                        if (item._class == "chapter")
                        {
                            filteredCurriculum.Add(item);
                        }
                        else if (item._class == "lecture" && item.asset != null && (item.asset.asset_type.ToLower() == "video" || item.asset.asset_type.ToLower() == "videomashup" || item.asset.asset_type.ToLower() == "article"))
                        {
                            bool isEncrypted = !string.IsNullOrEmpty(item.asset.media_license_token);
                            if (mode == "Normal Only" && isEncrypted) continue;
                            if (mode == "Encrypted Only" && !isEncrypted) continue;
                            filteredCurriculum.Add(item);
                        }
                        else if (item._class == "quiz")
                        {
                            if (mode == "Encrypted Only") continue;
                            filteredCurriculum.Add(item);
                        }
                    }

                    // Open the advanced selection window
                    var selectionWindow = new ContentSelectionWindow(course.title, filteredCurriculum);
                    Window mainWin = null;
                    foreach (Window w in Application.Current.Windows)
                    {
                        if (w.GetType().Name == "MainWindow" && w.IsVisible)
                        {
                            mainWin = w;
                            break;
                        }
                    }
                    if (mainWin != null)
                    {
                        selectionWindow.Owner = mainWin;
                    }

                    if (selectionWindow.ShowDialog() == true)
                    {
                        var selectedIds = selectionWindow.SelectedLectureIds;
                        if (selectedIds != null && selectedIds.Count > 0)
                        {
                            DownloadService.StartDownload(course, downloadsRoot, selectedIds);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void StopDownload(int courseId)
        {
            DownloadService.StopDownload(courseId);
        }

        private async void BtnLoadMore_Click(object sender, RoutedEventArgs e)
        {
            await LoadMoreCourses();
        }
    }
}
