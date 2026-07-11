using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UdemyKicker
{
    public class CourseProgressData
    {
        public string CourseId { get; set; } = "";
        public string CourseTitle { get; set; } = "";
        public int LecturesCount { get; set; }
        public string EstimatedHours { get; set; } = "";
        public string CourseUrl { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public int Percentage { get; set; }
        public string Status { get; set; } = "";
        public string CurrentVideo { get; set; } = "";
        public int ItemsDownloaded { get; set; }
        public int TotalItems { get; set; }
        public bool IsStopped { get; set; }
    }

    public static class DownloadService
    {
        public static DownloadManager Manager { get; } = new DownloadManager();
        public static Dictionary<string, CancellationTokenSource> ActiveDownloads { get; } = new Dictionary<string, CancellationTokenSource>();
        public static Dictionary<string, Task> ActiveTasks { get; } = new Dictionary<string, Task>();
        public static Dictionary<string, CourseProgressData> SavedProgressDict { get; } = new Dictionary<string, CourseProgressData>();
        public static Dictionary<string, UdemyCourse> CourseMetadata { get; } = new Dictionary<string, UdemyCourse>();
        public static string ProgressFilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_progress.json");

        public static event Action<string, CourseProgressData>? OnProgressUpdated;

        static DownloadService()
        {
            // Clean up any old progress files to avoid loading stale data.
            try
            {
                if (File.Exists(ProgressFilePath))
                {
                    File.Delete(ProgressFilePath);
                }
            }
            catch { }

            Manager.OnCourseProgress += Manager_OnCourseProgress;
        }

        public static void SaveProgress(string courseId, int pct, string status, string currentVideo, int completed, int total, bool isStopped)
        {
            try
            {
                string title = "";
                int lectures = 0;
                string hours = "";
                string url = "";
                string imageUrl = "";

                if (CourseMetadata.TryGetValue(courseId, out var course))
                {
                    title = course.title;
                    lectures = course.num_lectures;
                    hours = course.estimated_content_length > 0 ? $"{(course.estimated_content_length / 60.0):0.0} total hours" : "12.5 total hours";
                    url = course.url;
                    imageUrl = course.image_480x270;
                }

                if (SavedProgressDict.TryGetValue(courseId, out var existing))
                {
                    if (pct < 0) pct = existing.Percentage;
                    if (string.IsNullOrEmpty(status)) status = existing.Status;
                    if (string.IsNullOrEmpty(currentVideo)) currentVideo = existing.CurrentVideo;
                    if (completed < 0) completed = existing.ItemsDownloaded;
                    if (total < 0) total = existing.TotalItems;

                    if (string.IsNullOrEmpty(title)) title = existing.CourseTitle;
                    if (lectures <= 0) lectures = existing.LecturesCount;
                    if (string.IsNullOrEmpty(hours)) hours = existing.EstimatedHours;
                    if (string.IsNullOrEmpty(url)) url = existing.CourseUrl;
                    if (string.IsNullOrEmpty(imageUrl)) imageUrl = existing.ImageUrl;
                }

                var data = new CourseProgressData
                {
                    CourseId = courseId,
                    CourseTitle = title,
                    LecturesCount = lectures,
                    EstimatedHours = hours,
                    CourseUrl = url,
                    ImageUrl = imageUrl,
                    Percentage = pct,
                    Status = status,
                    CurrentVideo = currentVideo,
                    ItemsDownloaded = completed,
                    TotalItems = total,
                    IsStopped = isStopped
                };

                SavedProgressDict[courseId] = data;

                OnProgressUpdated?.Invoke(courseId, data);
            }
            catch { }
        }

        private static void Manager_OnCourseProgress(object? sender, CourseProgressEventArgs e)
        {
            string idStr = e.CourseName;
            
            SaveProgress(idStr, (int)e.Percentage, e.Status, e.CurrentLecture, e.CompletedLectures, e.TotalLectures, false);

            if (e.Status.Contains("Completed") || e.Status.Contains("Failed") || e.Status.Contains("Error") || e.Status.Contains("Cancelled") || e.Status.Contains("Stopped"))
            {
                if (ActiveDownloads.ContainsKey(idStr))
                    ActiveDownloads.Remove(idStr);

                bool finished = e.Status.Contains("Completed");
                SaveProgress(idStr, finished ? 100 : (int)e.Percentage, e.Status, e.CurrentLecture, e.CompletedLectures, e.TotalLectures, !finished);
            }
        }

        public static void StartDownload(UdemyCourse course, string downloadsRoot, HashSet<int> selectedLectureIds = null)
        {
            string safeCourseId = course.id.ToString();
            if (ActiveDownloads.ContainsKey(safeCourseId)) return;

            // Prevent starting a new download if a previous task for the same course is still terminating
            if (ActiveTasks.TryGetValue(safeCourseId, out var existingTask) && !existingTask.IsCompleted)
            {
                AppLogger.LogInfo($"[Service] Cannot start download: previous task for course {course.title} is still terminating.");
                return;
            }

            // Cache metadata
            CourseMetadata[safeCourseId] = course;

            var cts = new CancellationTokenSource();
            ActiveDownloads[safeCourseId] = cts;

            // Retrieve last known progress values to prevent progress bar reset in UI
            int pct = -1;
            int completed = -1;
            int total = -1;

            if (SavedProgressDict.TryGetValue(safeCourseId, out var existing))
            {
                pct = existing.Percentage;
                completed = existing.ItemsDownloaded;
                total = existing.TotalItems;
            }

            SaveProgress(safeCourseId, pct, "Starting download...", "", completed, total, false);

            var task = Task.Run(async () =>
            {
                string engineDir = ToolExtractor.EngineDirectory;
                await Manager.DownloadCourseHybridAsync(course.id, course.title, course.url, engineDir, downloadsRoot, cts.Token, selectedLectureIds);
            });
            ActiveTasks[safeCourseId] = task;
        }

        public static void StopDownload(int courseId)
        {
            string safeCourseId = courseId.ToString();
            if (ActiveDownloads.ContainsKey(safeCourseId))
            {
                ActiveDownloads[safeCourseId].Cancel();
                ActiveDownloads.Remove(safeCourseId);

                SaveProgress(safeCourseId, -1, "Stopped", "", -1, -1, true);
            }
        }
    }
}
