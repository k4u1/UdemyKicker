using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UdemyKicker
{
    public class CourseProgressEventArgs : EventArgs
    {
        public string CourseName { get; set; }
        public string CurrentLecture { get; set; }
        public double Percentage { get; set; }
        public int CompletedLectures { get; set; }
        public int TotalLectures { get; set; }
        public string Status { get; set; }
    }

    public class DownloadManager
    {
        public event EventHandler<CourseProgressEventArgs> OnCourseProgress;
        private static readonly HttpClient httpClient = new HttpClient();

        public string FormatSpeed(double bytesPerSecond)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int unitIndex = 0;
            double speed = bytesPerSecond;

            if (speed >= 1024)
            {
                unitIndex = speed >= Math.Pow(1024, 3) ? 3 : speed >= Math.Pow(1024, 2) ? 2 : 1;
                speed /= Math.Pow(1024, unitIndex);
            }

            return $"{speed:0.00} {units[unitIndex]}";
        }

        public static string SanitizeFileName(string name)
        {
            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            foreach (char c in invalidChars)
            {
                name = name.Replace(c.ToString(), "");
            }
            return name.Replace("?", "").Replace(":", " -").Trim();
        }

        public async Task DownloadCourseHybridAsync(int courseId, string courseName, string courseUrl, string engineDir, string downloadRoot, CancellationToken token, HashSet<int> selectedLectureIds = null)
        {
            int totalLectures = 0;

            string mode = SettingsManager.Current.DownloadMode;
            try
            {
                AppLogger.LogInfo($"[Course {courseId}] Starting hybrid download: {courseName}");
                
                var curriculumResult = await UdemyApiManager.GetCourseCurriculumAsync(courseId);
                var curriculum = curriculumResult.items;
                string debugJson = curriculumResult.debugJson;
                
                if (curriculum == null || curriculum.Count == 0)
                {
                    string safeErr = !string.IsNullOrEmpty(debugJson) && debugJson.Length > 100 ? debugJson.Substring(0, 100) + "..." : (debugJson ?? "");
                    AppLogger.LogInfo($"[Course {courseId}] Error: Empty Curriculum. Raw JSON: {debugJson}");
                    OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Error: Empty Curriculum - " + safeErr, CompletedLectures = 0, TotalLectures = 0 });
                    return;
                }

                if (selectedLectureIds != null)
                {
                    if (SettingsManager.Current.DownloadAttachmentsOnly)
                    {
                        totalLectures = curriculum.Where(c => c._class == "lecture" && selectedLectureIds.Contains(c.id) && c.supplementary_assets != null).Sum(c => c.supplementary_assets.Count);
                    }
                    else
                    {
                        totalLectures = curriculum.Count(c =>
                            (c._class == "lecture" && selectedLectureIds.Contains(c.id) && c.asset != null && (c.asset.asset_type.ToLower() == "video" || c.asset.asset_type.ToLower() == "videomashup" || c.asset.asset_type.ToLower() == "article"))
                            || (c._class == "quiz" && selectedLectureIds.Contains(c.id))
                        );
                    }
                }
                else
                {
                    if (SettingsManager.Current.DownloadAttachmentsOnly)
                    {
                        totalLectures = curriculum.Where(c => c.supplementary_assets != null).Sum(c => c.supplementary_assets.Count);
                        if (totalLectures == 0)
                        {
                            AppLogger.LogInfo($"[Course {courseId}] Completed successfully: No attachments found to download.");
                            OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Course Completed (No Attachments)", CompletedLectures = 0, TotalLectures = 0, Percentage = 100 });
                            return;
                        }
                    }
                    else if(mode == "Normal Only")
                    {
                        totalLectures = curriculum.Count(c => c._class == "lecture" && c.asset != null && c.asset.media_license_token == null && (c.asset.asset_type.ToLower() == "video" || c.asset.asset_type.ToLower() == "videomashup" || c.asset.asset_type.ToLower() == "article"));
                        curriculum.RemoveAll(c => c._class == "lecture" && c.asset != null && c.asset.media_license_token != null);
                    }
                    else if (mode == "Encrypted Only")
                    {
                        totalLectures = curriculum.Count(c => c._class == "lecture" && c.asset != null && c.asset.media_license_token != null && (c.asset.asset_type.ToLower() == "video" || c.asset.asset_type.ToLower() == "videomashup"));
                        curriculum.RemoveAll(c => c._class == "lecture" && c.asset != null && c.asset.media_license_token == null);
                    }
                    else
                    {
                        totalLectures = curriculum.Count(c =>
                            (c._class == "lecture" && c.asset != null && (c.asset.asset_type.ToLower() == "video" || c.asset.asset_type.ToLower() == "videomashup" || c.asset.asset_type.ToLower() == "article"))
                            || c._class == "quiz"
                        );
                    }
                }

                // Parse DRM JSON if it exists
                var localJsonItems = LoadLocalJsonCommands(courseName);

                // Collect unique subtitle languages across all curriculum lectures and show selection popup
                List<string>? selectedLocales = null;
                if (SettingsManager.Current.DownloadSubtitles && !SettingsManager.Current.DownloadAttachmentsOnly)
                {
                    var allCaptions = curriculum
                        .Where(c => c._class == "lecture" && c.asset != null && c.asset.captions != null)
                        .SelectMany(c => c.asset.captions)
                        .Where(cap => !string.IsNullOrEmpty(cap.locale_id))
                        .ToList();

                    var uniqueLocales = allCaptions
                        .GroupBy(cap => {
                            string code = cap.locale_id.ToLower();
                            int idx = code.IndexOfAny(new[] { '_', '-' });
                            return idx > 0 ? code.Substring(0, idx) : code;
                        })
                        .Select(g => new UdemyKickerWPF.Views.SubtitleItem { LocaleId = g.Key, DisplayName = g.Key })
                        .ToList();

                    if (uniqueLocales.Count > 0)
                    {
                        bool isCancelled = false;
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var dialog = new UdemyKickerWPF.Views.SubtitleSelectionWindow(courseId.ToString(), courseName, uniqueLocales);
                            System.Windows.Window? mainWin = null;
                            foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
                            {
                                if (w.GetType().Name == "MainWindow" && w.IsVisible)
                                {
                                    mainWin = w;
                                    break;
                                }
                            }
                            if (mainWin != null && mainWin != dialog)
                            {
                                dialog.Owner = mainWin;
                            }
                            if (dialog.ShowDialog() == true)
                            {
                                selectedLocales = dialog.SelectedLocales;
                            }
                            else
                            {
                                isCancelled = true;
                            }
                        });

                        if (isCancelled)
                        {
                            AppLogger.LogInfo($"[Course {courseId}] Download cancelled by user in subtitle selection dialog.");
                            OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Stopped", CompletedLectures = 0, TotalLectures = totalLectures, Percentage = -1 });
                            return;
                        }
                    }
                }

                int completed = 0;
                string currentChapter = "Chapter 1";
                int chapterIndex = 1;
                int lectureIndex = 1;

                // Initial player generation
                try
                {
                    string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                    string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                    UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                }
                catch { }

                foreach (var item in curriculum)
                {
                   
                    if (token.IsCancellationRequested) break;
                   
                    if (item._class == "chapter")
                    {
                        currentChapter = SanitizeFileName($"{chapterIndex}.{item.title}");
                        chapterIndex++;
                        lectureIndex = 1;
                        continue;
                    }

                    if (item._class == "lecture" && item.asset != null)
                    {
                        if (selectedLectureIds != null)
                        {
                            if (SettingsManager.Current.DownloadAttachmentsOnly)
                            {
                                bool hasAnySelectedAttachment = item.supplementary_assets != null && item.supplementary_assets.Any(att => selectedLectureIds.Contains(att.id));
                                if (!hasAnySelectedAttachment)
                                {
                                    lectureIndex++;
                                    continue;
                                }
                            }
                            else if (!selectedLectureIds.Contains(item.id))
                            {
                                lectureIndex++;
                                continue;
                            }
                        }

                        string assetType = item.asset.asset_type.ToLower();
                        if (assetType == "video" || assetType == "videomashup")
                        {
                            if (!SettingsManager.Current.DownloadAttachmentsOnly)
                            {
                                string safeLectureName = SanitizeFileName($"{chapterIndex - 1}.{lectureIndex} {item.title}");
                                string saveDir = Path.Combine(downloadRoot, SanitizeFileName(courseName), currentChapter);
                                Directory.CreateDirectory(saveDir);

                                bool isEncrypted = !string.IsNullOrEmpty(item.asset.media_license_token);
                                
                                if (mode == "Normal Only" && isEncrypted) { AppLogger.LogInfo($"[Course {courseId}] Skipped DRM lecture: {item.title}"); completed++; continue; }
                                if (mode == "Encrypted Only" && !isEncrypted) { AppLogger.LogInfo($"[Course {courseId}] Skipped Normal lecture: {item.title}"); completed++; continue; }

                                string targetFile = isEncrypted ? Path.Combine(saveDir, safeLectureName + ".mkv") : Path.Combine(saveDir, safeLectureName + ".mp4");
                                int retryCount = 0;
                                int maxRetries = 9999;
                                
                                while (!File.Exists(targetFile) && !token.IsCancellationRequested && retryCount < maxRetries)
                                {
                                    if (retryCount > 0)
                                    {
                                        AppLogger.LogInfo($"[Course {courseId}] Retrying download for: {item.title}. Attempt {retryCount}...");
                                        
                                        OnCourseProgress?.Invoke(this, new CourseProgressEventArgs {
                                            CourseName = courseId.ToString(),
                                            Status = $"No Connection. Retrying in 10s... (Attempt {retryCount})",
                                            CompletedLectures = completed,
                                            TotalLectures = totalLectures,
                                            Percentage = (double)completed / totalLectures * 100
                                        });
                                        
                                        try { await Task.Delay(10000, token); } catch { break; }
                                    }
                                    
                                    while (!await CheckInternetAsync() && !token.IsCancellationRequested)
                                    {
                                        AppLogger.LogInfo($"[Course {courseId}] Internet connection lost. Waiting for connection...");
                                        OnCourseProgress?.Invoke(this, new CourseProgressEventArgs {
                                            CourseName = courseId.ToString(),
                                            Status = "Waiting for connection...",
                                            CompletedLectures = completed,
                                            TotalLectures = totalLectures,
                                            Percentage = (double)completed / totalLectures * 100
                                        });
                                        try { await Task.Delay(5000, token); } catch { break; }
                                    }
                                    
                                    if (token.IsCancellationRequested) break;

                                    if (isEncrypted)
                                    {
                                        // DRM Download
                                        string lecId = item.id.ToString();
                                        string baseUrl = (courseUrl ?? "").TrimEnd('/');
                                        string lectureUrl = !string.IsNullOrEmpty(item.url) ? item.url + "?autoplay=1" : $"{baseUrl}/learn/lecture/{lecId}?autoplay=1";
                                        await DownloadEncryptedLectureAsync(courseId.ToString(), courseName, lecId, lectureUrl, item.title, safeLectureName, saveDir, localJsonItems, engineDir, token, completed, totalLectures);
                                    }
                                    else
                                    {
                                        // Normal Download
                                        AppLogger.LogInfo($"[Course {courseId}] Starting Normal Download: {item.title}");
                                        await DownloadNormalLectureAsync(courseId.ToString(), item.asset, safeLectureName, saveDir, token, completed, totalLectures);
                                    }
                                    
                                    retryCount++;
                                }

                                if (!File.Exists(targetFile))
                                {
                                    AppLogger.LogInfo($"[Course {courseId}] Download failed or cancelled for: {item.title}. Stopping course download loop.");
                                    break;
                                }

                                // Download Subtitles
                                await DownloadCaptionsAsync(courseId.ToString(), completed, totalLectures, item.asset.captions, saveDir, safeLectureName, selectedLocales, token);

                                completed++;

                                // Update player executable with download status
                                try
                                {
                                    string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                                    string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                                    UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                                }
                                catch { }
                            }
                            lectureIndex++;
                        }
                        else if (assetType == "article")
                        {
                            if (!SettingsManager.Current.DownloadAttachmentsOnly)
                            {
                                AppLogger.LogInfo($"[Course {courseId}] Processing Article: {item.title}");
                                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                {
                                    CourseName = courseId.ToString(),
                                    CurrentLecture = item.title,
                                    Status = $"Processing Article: {item.title}",
                                    CompletedLectures = completed,
                                    TotalLectures = totalLectures,
                                    Percentage = totalLectures > 0 ? (double)completed / totalLectures * 100 : 0
                                });

                                if (string.IsNullOrEmpty(item.asset.body))
                                {
                                    string body = await UdemyApiManager.GetArticleBodyAsync(item.id);
                                    if (!string.IsNullOrEmpty(body))
                                    {
                                        item.asset.body = body;
                                    }
                                }

                                completed++;

                                // Update player executable with article body
                                try
                                {
                                    string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                                    string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                                    UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                                }
                                catch { }
                            }
                            lectureIndex++;
                        }
                    }

                    bool shouldDownloadAttachments = SettingsManager.Current.DownloadAttachmentsOnly || SettingsManager.Current.DownloadAttachments;
                    if (shouldDownloadAttachments && item.supplementary_assets != null && item.supplementary_assets.Count > 0)
                    {
                        foreach (var att in item.supplementary_assets)
                        {
                            if (token.IsCancellationRequested) break;

                            if (SettingsManager.Current.DownloadAttachmentsOnly && selectedLectureIds != null && !selectedLectureIds.Contains(att.id))
                            {
                                continue;
                            }

                            string saveDir = Path.Combine(downloadRoot, SanitizeFileName(courseName), currentChapter);
                            Directory.CreateDirectory(saveDir);
                            await DownloadAttachmentAsync(courseId.ToString(), att, saveDir, token, completed, totalLectures);
                            
                            // Update player executable with downloaded attachment
                            try
                            {
                                string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                                string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                                UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                            }
                            catch { }

                            if (SettingsManager.Current.DownloadAttachmentsOnly)
                            {
                                completed++;
                                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs {
                                    CourseName = courseId.ToString(),
                                    Status = $"Downloaded attachment {completed}/{totalLectures}",
                                    CompletedLectures = completed,
                                    TotalLectures = totalLectures,
                                    Percentage = (double)completed / totalLectures * 100
                                });
                            }
                        }
                    }

                    // ââ QUIZ: fetch assessments and embed in curriculum item ââ
                    if (item._class == "quiz" && !SettingsManager.Current.DownloadAttachmentsOnly)
                    {
                        if (selectedLectureIds == null || selectedLectureIds.Contains(item.id))
                        {
                            AppLogger.LogInfo($"[Course {courseId}] Fetching quiz assessments: {item.title} (id={item.id})");

                            OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                            {
                                CourseName = courseId.ToString(),
                                CurrentLecture = item.title,
                                Status = $"Downloading quiz: {item.title}",
                                CompletedLectures = completed,
                                TotalLectures = totalLectures,
                                Percentage = totalLectures > 0 ? (double)completed / totalLectures * 100 : 0
                            });

                            var assessments = await UdemyApiManager.GetQuizAssessmentsAsync(item.id);
                            item.quiz_assessments = assessments;

                            completed++;

                            // Sync player JSON with quiz questions embedded
                            try
                            {
                                string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                                string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                                UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                            }
                            catch { }

                            OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                            {
                                CourseName = courseId.ToString(),
                                CurrentLecture = item.title,
                                Status = $"Quiz saved ({assessments.Count} questions): {item.title}",
                                CompletedLectures = completed,
                                TotalLectures = totalLectures,
                                Percentage = totalLectures > 0 ? (double)completed / totalLectures * 100 : 0
                            });
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    // Final player sync on course completion
                    try
                    {
                        string courseDir = Path.Combine(downloadRoot, SanitizeFileName(courseName));
                        string jsonCurr = UdemyKickerWPF.PlayerGenerator.GenerateCourseCurriculumJson(courseName, curriculum, downloadRoot);
                        UdemyKickerWPF.PlayerGenerator.UpdatePlayerExecutable(courseDir, jsonCurr);
                    }
                    catch { }

                    AppLogger.LogInfo($"[Course {courseId}] Completed successfully.");
                    OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Course Completed", CompletedLectures = totalLectures, TotalLectures = totalLectures, Percentage = 100 });
                }
                else
                {
                    AppLogger.LogInfo($"[Course {courseId}] Download stopped by user.");
                    OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Stopped", CompletedLectures = completed, TotalLectures = totalLectures, Percentage = -1 });
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[Course {courseId}] Exception: {ex.Message}\n{ex.StackTrace}");
                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs { CourseName = courseId.ToString(), Status = "Error: " + ex.Message, CompletedLectures = 0, TotalLectures = 0 });
            }
        }


        private string LoadCachedDrmCommand(string courseName, string lectureId)
        {
            string cacheFile = Path.Combine(@"C:\Program Files\udemyKicker", SanitizeFileName(courseName) + "_keys.udm");
            if (!File.Exists(cacheFile)) return null;

            try
            {
                string encryptedStr = File.ReadAllText(cacheFile);
                var items = CryptoManager.DecryptCommandFile(encryptedStr, CryptoManager.KeyForLocalFiles());
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.lecture == lectureId && item.command != null)
                        {
                            return item.command;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void SaveCachedDrmCommand(string courseName, string lectureId, string cmd)
        {
            string cacheFile = Path.Combine(@"C:\Program Files\udemyKicker", SanitizeFileName(courseName) + "_keys.udm");
            List<CourseItem> items = new List<CourseItem>();

            try
            {
                if (File.Exists(cacheFile))
                {
                    string encryptedStr = File.ReadAllText(cacheFile);
                    items = CryptoManager.DecryptCommandFile(encryptedStr, CryptoManager.KeyForLocalFiles()) ?? new List<CourseItem>();
                }
            }
            catch { }

            items.RemoveAll(x => x.lecture == lectureId);
            items.Add(new CourseItem { course = courseName, lecture = lectureId, command = cmd });

            try
            {
                string newEncrypted = CryptoManager.EncryptCommandFile(items, CryptoManager.KeyForLocalFiles());
                File.WriteAllText(cacheFile, newEncrypted);
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"Failed to cache DRM command: {ex.Message}");
            }
        }

        private List<CourseItem> LoadLocalJsonCommands(string courseName)
        {
            string commandsDir = @"C:\Program Files\udemyKicker";
            if (!Directory.Exists(commandsDir)) return new List<CourseItem>();

            var files = Directory.GetFiles(commandsDir, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string content = File.ReadAllText(file);
                    var data = CryptoManager.DecryptCommandFile(content, CryptoManager.KeyForLocalFiles());
                    
                    if (data != null && data.Count > 0 && data[0].course.Equals(courseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return data;
                    }
                }
                catch { }
            }
            return new List<CourseItem>();
        }

        private async Task DownloadNormalLectureAsync(string courseIdStr, UdemyAsset asset, string safeName, string saveDir, CancellationToken token, int completed, int total)
        {
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            string targetFile = Path.Combine(saveDir, safeName + ".mp4");
            if (File.Exists(targetFile)) return;

            string dlUrl = "";
            var targetQualities = new List<string> { SettingsManager.Current.VideoQuality, "Auto", "720", "1080", "480", "360" };
            if (SettingsManager.Current.VideoQuality == "Auto") targetQualities = new List<string> { "Auto", "720", "1080", "480", "360" };
            else targetQualities = new List<string> { SettingsManager.Current.VideoQuality.Replace("p", ""), "Auto", "720", "1080", "480", "360" };

            if (asset.download_urls != null && asset.download_urls.ContainsKey("Video"))
            {
                foreach (var q in targetQualities) { var m = asset.download_urls["Video"].FirstOrDefault(x => x.label == q && x.type == "video/mp4"); if (m != null) { dlUrl = m.file ?? m.src; break; } }
                if (string.IsNullOrEmpty(dlUrl)) dlUrl = asset.download_urls["Video"].FirstOrDefault()?.file;
            }
            if (string.IsNullOrEmpty(dlUrl) && asset.stream_urls != null && asset.stream_urls.ContainsKey("Video"))
            {
                foreach (var q in targetQualities) { var m = asset.stream_urls["Video"].FirstOrDefault(x => x.label == q && x.type == "video/mp4"); if (m != null) { dlUrl = m.file ?? m.src; break; } }
                if (string.IsNullOrEmpty(dlUrl)) dlUrl = asset.stream_urls["Video"].FirstOrDefault()?.file;
            }
            if (string.IsNullOrEmpty(dlUrl) && asset.media_sources != null)
            {
                foreach (var q in targetQualities) { var m = asset.media_sources.FirstOrDefault(x => x.label == q && x.type == "video/mp4"); if (m != null) { dlUrl = m.file ?? m.src; break; } }
                if (string.IsNullOrEmpty(dlUrl)) dlUrl = asset.media_sources.FirstOrDefault()?.file ?? asset.media_sources.FirstOrDefault()?.src;
            }

            if (string.IsNullOrEmpty(dlUrl)) 
            {
                AppLogger.LogInfo($"[Course {courseIdStr}] Error: Could not find download URL for normal video: {safeName}");
                return;
            }

            try
            {
                var fi = new FileInfo(targetFile + ".khaled");
                long existingLength = fi.Exists ? fi.Length : 0;

                var request = new HttpRequestMessage(HttpMethod.Get, dlUrl);
                if (existingLength > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                }

                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && totalBytes.HasValue)
                        totalBytes += existingLength;
                    else if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        existingLength = 0; // Server didn't support resume

                    using (var fs = new FileStream(targetFile + ".khaled", existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[8192];
                        long totalRead = existingLength;
                        long intervalRead = 0;
                        int bytesRead;
                        var sw = Stopwatch.StartNew();
                        
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytesRead, token);
                            totalRead += bytesRead;
                            intervalRead += bytesRead;

                            if (sw.ElapsedMilliseconds >= 1000)
                            {
                                double pct = totalBytes.HasValue ? (double)totalRead / totalBytes.Value * 100 : 0;
                                double bytesPerSec = intervalRead / (sw.ElapsedMilliseconds / 1000.0);
                                
                                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                {
                                    CourseName = courseIdStr,
                                    CurrentLecture = safeName,
                                    Percentage = pct,
                                    CompletedLectures = completed,
                                    TotalLectures = total,
                                    Status = FormatSpeed(bytesPerSec)
                                });
                                
                                intervalRead = 0;
                                sw.Restart();
                            }
                        }
                    }
                }

                if (File.Exists(targetFile + ".khaled"))
                {
                    File.Move(targetFile + ".khaled", targetFile);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.LogInfo($"[Course {courseIdStr}] Normal DL Error: {ex.Message}"); }
        }

        private async Task DownloadEncryptedLectureAsync(string courseIdStr, string courseName, string lectureIdStr, string lectureUrl, string originalLectureName, string safeName, string saveDir, List<CourseItem> jsonItems, string engineDir, CancellationToken token, int completed, int total)
        {
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            string targetMkv = Path.Combine(saveDir, safeName + ".mkv");
            if (File.Exists(targetMkv)) return;

            string cachedCmd = LoadCachedDrmCommand(courseName, lectureIdStr);
            string finalCmd = "";

            if (!string.IsNullOrEmpty(cachedCmd))
            {
                finalCmd = cachedCmd;
                AppLogger.LogInfo($"[DRM] Found cached command for {originalLectureName}");
            }
            else
            {
                // Fallback to legacy jsonItems
                var match = jsonItems.FirstOrDefault(j => j.lecture.Contains(originalLectureName) || originalLectureName.Contains(j.lecture));
                if (match != null)
                {
                    finalCmd = match.command;
                }
                else if (!string.IsNullOrEmpty(lectureUrl))
                {
                    string fullUrl = lectureUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? lectureUrl : $"https://www.udemy.com{(lectureUrl.StartsWith("/") ? "" : "/")}{lectureUrl}";
                    
                    string rawJsonData = "";
                    int attempt = 1;
                    while (string.IsNullOrEmpty(rawJsonData))
                    {
                        if (token.IsCancellationRequested)
                        {
                            AppLogger.LogInfo($"[DRM] Metadata extraction cancelled for {originalLectureName}.");
                            return;
                        }

                        AppLogger.LogInfo($"[DRM] Navigating internal browser to extract metadata for {originalLectureName} (Attempt {attempt})...");
                        rawJsonData = await BrowserHost.FetchDrmCommandAsync(fullUrl);
                        
                        if (string.IsNullOrEmpty(rawJsonData))
                        {
                            AppLogger.LogInfo($"[DRM] Timeout/Failed to extract metadata for {originalLectureName}. Retrying page load in 5 seconds...");
                            try { await Task.Delay(5000, token); } catch { break; }
                            attempt++;
                        }
                    }

                    if (string.IsNullOrEmpty(rawJsonData)) return;
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        var metadata = JObject.Parse(rawJsonData);
                        string pssh = metadata["pssh_data"]?.ToString();
                        string license_url = metadata["license_url"]?.ToString();
                        
                        var license_headers = metadata["license_headers"] as JObject;
                        // Write args to a temporary JSON file to avoid command-line length limits
                        string tempJsonArgs = Path.GetTempFileName();
                        var argsObj = new JObject
                        {
                            ["pssh"] = pssh,
                            ["license_url"] = license_url,
                            ["headers"] = license_headers ?? new JObject()
                        };
                        File.WriteAllText(tempJsonArgs, argsObj.ToString());

                        // Call cdm_worker.py in-memory via python -c using the base64 encoded script contents
                        string base64Script = "aW1wb3J0IHN5cw0KaW1wb3J0IGpzb24NCmltcG9ydCBiYXNlNjQNCmltcG9ydCBvcw0KaW1wb3J0IHJlDQppbXBvcnQgcmVxdWVzdHMNCmltcG9ydCB0aW1lDQpmcm9tIHB5d2lkZXZpbmUuY2RtIGltcG9ydCBDZG0NCmZyb20gcHl3aWRldmluZS5kZXZpY2UgaW1wb3J0IERldmljZQ0KZnJvbSBweXdpZGV2aW5lLnBzc2ggaW1wb3J0IFBTU0gNCg0KZGVmIF9sb2FkX3d2ZF9iNjQoKToNCiAgICBlbmMgPSBiYXNlNjQuYjY0ZGVjb2RlKCJBM29pZDNGVmZ4RUtPV0ZkZjM4Y0RoZFZlWEIzRkFBN2MyRjNkejBJTW5WSmUxZ0Rld0YwZlZaY0J5NFhVVVJLQXo4TVFVcGhCSGdST2psRkczTUhaaWtLWUgxWER6WWxUZ3RJQlZzY01VOXhhRjUxUG5zYlJubCtmaEV6Q0Z4ZlJGVVpPQzVRUlFGSFB6OFVaMzFSVkEwWU5INUVmaGtGY3l0K2FXSmZZQ1l6Wldwalp4UUJHbnRFYTF3Z0pCTnFSWGhsRHlFS1VBRndUak0vTUVjQUFuVWVJeGxsUVg1alBoc1hBV2NaVGdNeUwySUVBM2tVR3hCOEcxaENCeTVKVTF4cWRRd3RIbmxVVTM1bFpDMWpCM1lBSHlvVEJuWlVEeEFmVHdaYVlHUVBFVEVEZWtKSFl3NE5jRVYzVEIwTlNsQlRWMzhqRFI5NUJYcHNiQVF1WW1CNlh3UWJUMFovVUZnMmVCdGJBRlp3T0RNMVFWZHJleklTTVdOYlgxVitPaGRkWUVaa0hTRTJlVlFaQUJva1BGZ0VWZ1lrRDA1MWFWVjZGaG96R1ZaalV5VUNHMU1IUTNvd2NrbG1SVmdDUHc4TUJWTmVkQVI0SUVkNkFISURPVTFXQmtNUEdCOUxBbkVDV1NZYUduUmtSWDhSQ2lsemNuTlpIQWs1Y0ZsUlhqRUlEVmtJU2xjbEF6Sm1HM3hlSmd4TmMzUUZVZ1YrRW53R1lsOHdjazUxUVZSU0hYbExYa1ZkZWpFR1RFcDhYRjhrZlJOZmZBUmhlaEZUY0ZaYWNCRUFEbGdCVUE5bkhnaGdCMEZVUFNjOFVFcHpZd0p6SFdSSkJtTVJKVDFIWEdkRU5ESU1lbUpxV1E4ZkZVSUVZSE1FRGpKV2ZFc0ZOQjBpV0FVQkdUQUdObW9ESFZRSE9qSlhSbnArWjMwaFN3ZHFaUUFQS0ZkVWNXRVlQUlY0UUVWT093d3VhMTE2Unh3RlRVdEtkMXdHSFROSGZrRk1OenBJQkFkWVR3UXNQMHB6Q21JdEl3cGVlVVo0WVQ4VGNIUUNaam96TkhBZkMyNGVjMHdHY245aUVBMUJmR1pXWkRzL0hnTUJIWE1oQVI1QmRrWmhNV0F5VkI5a1RqYzhQbnhLUm1ZZUQwaENRa2g1WUM1TUFGeGRHUXdnRlh0UlVWVitNa3hvY1ZFQlB4NDhZMmxyVGg4bUZFTm5kMDlrQlRZZFFVUkZNUnRYQmw0R2ZCa09NZ0ZvUjF0akJSRUthRkJDSWhFK1MzRkFYZ1lnTzFWcGQzY2dDa3QwZGdWT1ppVVBCM2xrVHc4ZEVYRm9SRjRCTGpsNUJIMWdKeVUxY0VBRFZDSXhER1ZrYWdFL2VrNWZZMUZnYlNWVFEwVnhkelluS2twVWNRVWdjaXhSQ0FZQlpHUU5SSHNGR1FFeklWTkRYaGtVS1NCb1kzeDNJQ2t1ZjM5QVVXUVpBUjFwQkZjNkFqWmxad0JUSlN3N0ExRkdZd0E0Q1ZSOVJsZ1FlelZIVkZkR096RW9DMElBWEMwdUxsaFRhM0ZrQWs1clgzVWRGQjhiY0FORlJob0NQbkJVVVVjOEowaHhWMnR6RkQ5QVdGdGhBeFltTVFKRFFuVWFDQlVHWkZNQkp5VlhSQjlJY1Jrb1MyRmhVRUVaUFFKQUJWWlFIVDFPQ3dGS1lXRVpEV1FJWEZBY0hodFpZV3RRQVRsSVFVWUFHUUFBQUFZRmVYNG5MU2xpQ1dObEpISUJRWE5jUkFka09YcGVjSGRoRFI1blVuOENMeDBhVmtCaVVoOEpJZ29GVkh3MkNUMWpRaGwzRmcwTGVRQUVUajRzRlhkUlIyNFRmaEJxZTBGT0Uzb2JaRko2QVQ0Sk1BVlNBWEVpS2p0QWZBRmVJd2hOUVhOVmJ4Y2pTbjFYU0E0dmV6a0tZZ0ZmQlQxTFJFTjdlQlk3RWtSVGFGMG5memx4WDFBREFqNCtHVmwvUlJKNVUyaDNVQVVjQUJCY1VWaDBPQzgwWDF4bGIyY2dOSDlhR1hvbmVrcEdVbnRST0J4T2ZYSkhVREU0TDNSZWExd2JNd0JGZGtGWlBENFhBRkpyZVRnekhGRjFHV1lZRHcwSGV3QURNRGtYY3dZS1VSby9LMzFrQTJVSEhGY0xZR2gvTWlNVmN3WmJEeE01QUdCVEMzVmpKRHA1Q1dzWkdpSStCZ0Y5VnlFaVYyTjdjRkVXRGhsTFVVdGJZUUVpZXdaQ1JnRXpLa2RSWW1BZkxCcElBbEJkTW1STUMxa0xiMk16Tmw1YVlsSTZLaHBHQWdac0lTUktVMWtDWUJzOE4xdDdYRVUySHpGZlptUnZPeDBVQUFCRVhRMGlLMU4zZmc4bkd5cHdYMUI4WVNVSlpWTVpleDQ4R2dBRVJnNDRMejVqQWtCekRTaEtTM05SVXhJSVRHVkZVVUVHQkN0UVlBZG5IM2tvWDFvQUdSOWtQMlZJQVg4QklBQldlVjlrQVFFZGRRUjRXR0VrTEFvQ1VYZ2pNd0p6WDNWM0FYa3JZWGg0Um14eUttRUpZMk1RSGswQlJHSUNCbmtTYWxGd2NSMTlQVXBvQjBFRE16dDJVZ1orQkhrdVMxbGplUk00VG1oakIyTThEakZYVlZrSEZpTk9ha0ZXYmlaOUYwTktDbU1rY2hvS0gxTlFiU0U5WEg1SVJSSW5PbVJnQmtVOUNDOS9DR1ZHRTM4S0JXQnpld1VPSVhoaGEzZzZDVTFoWG1aQU9RVWhTZ0J3SFF3YUNBVlJWVjBYSmkxZ1VtUnZPaklBVzJkN1Jqb2pDWGg3VkdBbUxrdERhZ0lHQjM5TWRRbHpVUmNPQ0YxOGNVSmxEenR6ZVdGekV3Y2JSM1pJVENjUk8xZEhaVUlrSER0eFhWOTZIQkpKUUZGMVJoUVNFVmhYZTBFeUxEMTVjVjEvRndvcGNYVjdYVGNDRVZab1puOEFNMHA4ZGdSZUxIZ3dYMzFRQlJNUEZ3Si9YWElSZnp4cVZFZENFRE5QQTBsVVJHWUREV3RhWWtVZkxpMTVaR0JQWlN3TFVXQjFXekFpUVVWRlNtQWdmQjFEZndCZ0JETUFSbFI3UlNCN0Rnb2ZTbjhoTEZOK1hXdEdEeWM2YzNGZVJUeDZFWDBHVzJRd2Z4UjRYWDFDSXg0d2RCc0NVMklmS1h0QmRFWTBKQXhoWVJsY01TbFBkR2g0YjNvaExRWnhHWE5tZlRjRGRnVmdKQjBiZUZnTERpVjZGMVJrQzJRUk9DaGRYRU5pTWgxWEFtSmlRRHdGUG5SY1dWd0FKaFZBWDJFQ0VuOHNmMU5xYkNFelRGc0laWGNuSUJ4Y0FBZGpiWE1kZUhFZFEyVitEVlpLVms4WkdVaGxVbmRQTEFrZlMzSlZYaFlZSWtnR1ExOHlZRWwzVlg4R0pYaFBYUVpWZVFValMyRmZZbGtQSWpsQmVuTkVZSEk2QkZ0YVRpYytGMFlIQVdBYWZTaGJmWDBGSVRJdGRIUldVMkVsSVh4blJGcG1PZ29DU0dKUUJBOHJXMGh3UlFNYk9WVjljSGNVRGhjS2MzaC9GQjB6UTNGalVSY09PWE5SVldjUURrbGZXMTVqSXdGS0NrQmxRUXdoRm5GWFZGNE1LVDE5YUY5dWZoRk9TR3BHUlJzQkVuNUNlaDBVZXc1Y0JYUk1QUUVQWjFkOFJBMTZMR05XR1hrY0l4MUFaMTFuQnlJN1FXTklUQmdSR2xoaEExVUdDakJmVldKVU1uc1FVRWRjVUJ3QVBBVmNZVlJnUGdKNFcxQm5JQUVTY0hwYVJXWUZMbE55Q2wwUU9DSmRZMnQ5RTNnVWZHbGxRencrTjNSNFlBVTNmVXRIWEVCdmVnRTJCMWtHZUNVdENBUlZCM3d0QWtGV0FGdC9HVG9BUzFaY0JtMHRDM1p5VjM0OFBEOVJVblZ1UG54QkFHRlpZbVI5TmxCeWZIUTNCUWdHWUFOZ0V3NFhjM044ZHhRbEdWOWRRMjg2SUExckNHSlBZQ2hMWmx4a1pBSUVOV1ZJZlc0dk94OUZmVlZqTXpzTkFuRnJWR2MvUVVOSVVGY0hPQ3NIZDNkVEkzTTdlQVZRZmhJc1RtUm1ZMU5oTDFNQkNRZG1OMlFTZEFWNkFqa3NBR0pKWTJjRkRoRlpZUU5aSDJROGRYVmtjRFljU0doa1hHUWlNUUlIWGxFUFlIMDdlVnA2UkNFUE9YcG1RM2RsTWpaYllRcHVaQThzWDFoK0R6MGdEVkZqVlZvMkxSOXJlMkIvSEMwdFlWd1pZQ0VoTW1kVVdVSmlQaWxHWDJKL0lDSTVHV2hYUkJCeU1BVWJkRVF2UGh0S2UxQURIdzFLUkJzQVdEMHVRRUJrYTNBK0JDaERaZ0JWQWpFd2ZIaGZkV0VHTlVaV1lGUnRmQkY5UTFOOEl3NFBSbkZpWUQxOEUzdEhZVk5qSUNoUUJWQlpKd0F0QkJ0aEJ5RTdLbjFBWFY4aktoQm5aa0Y2RFFBZFVGdFdReWNTRVFVR0hYTmdFeklDV254N09qb3RjVlpMYkJvc1BVcERTbklORFJkSVNHSlRZZ0VLZUFoQWRTTXNNa3BCQW5RRU9nMXpWMVYwRUNNNlFnUVpYd3crQVVVZlIzMGFNVE53QW5jZEhEd3RkWGtGWUNNN1BYUjVYUUlXQmpGN2NuRlJIZ2c1WTNWekJBVXBDRVpwWEVFQUdEeENRSFlCQkhrdEJGZ2RVVGdkT1dVQ0MzNDhQemxDQ1Voa0xSTlhkbkprZDIweEFIOGZYRVk4UFFKWkIxOFpZUjhiWTFsald5UWJWd1JrR1dWNkJ5MW1ablpPQkgxT0JVZFZZaDRFU0dnZlhWb1NaQWhWWUF0Rk9YOFJWMTlBQURzUEhtTkNSRzhnS2hKZWZsRlpIZ0ZYWjF4Zll6Y0hGMHBpWTFBZmN5NVRBMTk1Qm5OTFEwUjhRQnR6VFF0aFhYZzNCeUIwRzM1aVBYdFRCVUptRGkwWkRnSm9WVTRBQnh4MlJrcGZZQ2hKVVhGV2ZqRXBNZ3NBVzF3UlBqNUxCQVpoRFJGTGVRZGdVQXgrSVFScGZRNE1NVHQ0YWdOZkF3QUxZd1o5Qm1VQlBRcENZV3c1QVJ4NGVIWjVaMzVMWTJNQlJ5SURLMkJpWDFwbUJBbDNYWDErUHlnT1MzSlZBemNpRDFsMVZrODRHVDErZlVRRFBIZ1VWbWw3Y3hvRVBIQlJHUWN2SVNoREExUkRCWEpCVjN0RUJRMGdQd1pJQkZsa0l4cGVVUjEvUEF3Ulh3WkVYVEEvU0YxaGUzSVVHamx3ZTJKM1BBNFhjM1I3V1NFaE5YMURIVjBaQlM4RkJ3cGtQamtoWEVaUVZ3MENNM1pFUUZFWkxpOVFCd3QzTW5wUEJFaG9WUjk3RlFSZ1ExMHdjaTFRUkZ0OEFoMFVaMTFJVXdONVBWdHhDbjRZQ2h0S0JBQm1IakVaQ3dNR2VoTjhMVnRTVVVNYlBpQnJTbWQ2Qnk0ZVcxUmRBQ0lDSUFzQWZFWWdNenQ5WjBOL0Foa0pZd2xhZUIwQVYxeG1heGswR3pWV2ZGdGpNU0V1V1Zad1lEUU5MWGRjQUZjQ0pSeElZQmx6R0R3clNGSlRabUlvVjE1elFWczJEQTBDWG1JRlBuc3dmRVpmYmhSN0RBQmlka0pnR1JOUWNoMTVJQWdXSFFOd2RDY3lMV05oVTFnZUdDNUxYa3BBUEFjWlduWkdaeFFQVGxCcEJWa2NCazFmWW44R05ENGJaMGhjSFNRQVYxOEJCRTg2UEFsalpVUmtJUjRTUVVKRlhBY3ZJbjFUR1VZSFBUdEFkZ1p2RUFZckdWZHpUaThBTkVKVFVBY1dBaGRSV2dCNUFRQWhRbDVrQldRREtuZC9ZRUViSXpOWWFWaGdBUmc4UjN0WUR4c0VUQU5GVmxVeUF4TUdaRU1PSUFnY1JrTlFaaEFUSW5GeFFtSTlMRDlBV1V0OE1RRXJSWDk2QXdFR1NVUmNhZ1lmTGh0bGZXWVBGd1FhVUY5b0R4d3REbjEvY3dFZkhTaDFZbGNIR3hNdWZGdDVWeXdLTTJvREEwRjZLREZ6Y1hOR0d6NFRhM2wvUUIwOUhVRWZlMEFhUFZkZmQzOWJBQUV3WW1saGZnUXNPbmRSQm5RU0NoQjFSMUZGTHlFWkFXVmpBUnNmTXdKR0FYOHhFUWwwVlZObE4yQU1SV054UkdVSkFFQkpCM0U5RWpOMmQzeEFOeE02V2xKY1dqTXBGWFJFYUdRY0RDb0FDVVJzWnpNVWRWbDNmUlltU1VScWRXQW1FMG9IV0ZCaEFCZzlBblppWTJRS0gxQUNCbEVHSGoxVlltVUhaQ2svZEFCUUJSd3FQM05mWUc4TkFSSlRkMTRHRHh3MkFsUnFmRGtUU2dkWVVHRUFHRGtCVndaNFBTUWRjVmRHWFE4VElrSnBBR0F6S1JWMFJHaGtIQnNpQUdaSGJBMEJDR3NCQ3dJYUR5SlVhV3A4SVF3UmUzdDJmaGN5R2dCaUEyOW1HUjVRWFhSQ0R4a3hZVk1BWkNjVFNsWkhVM0ZzUGlKa0NRWjVFUkVlYTJoNFFoSW1LWGx6WDN4a0tpOUtXMm9FT1Q0aVh3aGhZRGd2RGxBQ1ZrVVBHRUZJYW5WQ014RkxjRjlRQkdBbklBRlhCbmc1Y2hCUlhRSkFEM2t1UjJwcWZDVVNTUXNFZlhJUExTRnFla1o1UHlBT1ozWjhaUmdZVEVwL2RuZG1CaEp6Ulg5aUhESTBTR2tCZXk4U1MzeDBld0F4RXpaZVUxOWtPUklXWkY1K0JBY25IRnNCUUd3Tkp3SjFXQVo5RXdNY1FtcDFZR2NxTHdkY2FnUWJJQnBrQ1FCc0RRRUNVMmNMUXhBc0lVcCtjUUlpQnhKelVYaDNPaTBhQUdaR2JtY0ZBVmRvY0FZM2VrRklhbVY0WkNnVlhnQlhZR3c4SVdwaVdGY1RjZ3RvYUdoYU53a3hjSDEyZnhvT09YZFhjM1V5QlRWemNuTjNFQ3c1WjNGekN3PT0iKQ0KICAgIHhvcl9rZXkgPSBiIlVLeDIwMjYiDQogICAgcmV0dXJuIGJ5dGVzKFtiIF4geG9yX2tleVtpICUgbGVuKHhvcl9rZXkpXSBmb3IgaSwgYiBpbiBlbnVtZXJhdGUoZW5jKV0pLmRlY29kZSgidXRmLTgiKQ0KDQpkZWYgbWFpbigpOg0KICAgIGlmIGxlbihzeXMuYXJndikgPCAyOg0KICAgICAgICBwcmludChqc29uLmR1bXBzKHsic3RhdHVzIjogImVycm9yIiwgIm1lc3NhZ2UiOiAiTWlzc2luZyBhcmd1bWVudHMifSkpDQogICAgICAgIHJldHVybg0KDQogICAganNvbl9wYXRoID0gc3lzLmFyZ3ZbMV0NCiAgICB3aXRoIG9wZW4oanNvbl9wYXRoLCAncicsIGVuY29kaW5nPSd1dGYtOCcpIGFzIGY6DQogICAgICAgIGRhdGEgPSBqc29uLmxvYWQoZikNCiAgICAgICAgDQogICAgcHNzaF9zdHIgPSBkYXRhLmdldCgicHNzaCIsICIiKQ0KICAgIGxpY2Vuc2VfdXJsID0gZGF0YS5nZXQoImxpY2Vuc2VfdXJsIiwgIiIpDQogICAgaGVhZGVycyA9IGRhdGEuZ2V0KCJoZWFkZXJzIiwge30pDQoNCiAgICB0cnk6DQogICAgICAgIGRlZiBsb2dfdHJhY2UobXNnKToNCiAgICAgICAgICAgIHdpdGggb3Blbigid29ya2VyX3RyYWNlLmxvZyIsICJhIiwgZW5jb2Rpbmc9InV0Zi04IikgYXMgbGY6DQogICAgICAgICAgICAgICAgbGYud3JpdGUobXNnICsgIlxuIikNCiAgICAgICAgICAgICAgICANCiAgICAgICAgbG9nX3RyYWNlKCJTdGFydGluZyBDRE0gd29ya2VyIikNCiAgICAgICAgDQogICAgICAgIHd2ZF9yYXcgPSBfbG9hZF93dmRfYjY0KCkNCiAgICAgICAgaWYgbm90IHd2ZF9yYXc6DQogICAgICAgICAgICBwcmludChqc29uLmR1bXBzKHsic3RhdHVzIjogImVycm9yIiwgIm1lc3NhZ2UiOiAid3ZkLmRhdCBub3QgZm91bmQifSkpDQogICAgICAgICAgICByZXR1cm4NCiAgICAgICAgICAgIA0KICAgICAgICBiNjRfY2xlYW4gPSByZS5zdWIocidbXmEtekEtWjAtOSsvPV0nLCAnJywgd3ZkX3JhdykNCiAgICAgICAgYjY0X2NsZWFuICs9ICI9IiAqICgoNCAtIGxlbihiNjRfY2xlYW4pICUgNCkgJSA0KQ0KICAgICAgICANCiAgICAgICAgaW1wb3J0IHRlbXBmaWxlDQogICAgICAgIHdpdGggdGVtcGZpbGUuTmFtZWRUZW1wb3JhcnlGaWxlKGRlbGV0ZT1GYWxzZSkgYXMgdGY6DQogICAgICAgICAgICB0Zi53cml0ZShiYXNlNjQuYjY0ZGVjb2RlKGI2NF9jbGVhbikpDQogICAgICAgICAgICB0bXBfcGF0aCA9IHRmLm5hbWUNCiAgICAgICAgDQogICAgICAgIGxvZ190cmFjZSgiTG9hZGluZyBkZXZpY2UuLi4iKQ0KICAgICAgICB0cnk6DQogICAgICAgICAgICBkZXZpY2UgPSBEZXZpY2UubG9hZCh0bXBfcGF0aCkNCiAgICAgICAgZmluYWxseToNCiAgICAgICAgICAgIGlmIG9zLnBhdGguZXhpc3RzKHRtcF9wYXRoKTogb3MucmVtb3ZlKHRtcF9wYXRoKQ0KICAgICAgICAgICAgDQogICAgICAgIGxvZ190cmFjZSgiRGV2aWNlIGxvYWRlZC4gT3BlbmluZyBDRE0uLi4iKQ0KICAgICAgICBjZG0gPSBDZG0uZnJvbV9kZXZpY2UoZGV2aWNlKQ0KICAgICAgICBwc3NoID0gUFNTSChwc3NoX3N0cikNCiAgICAgICAgc2Vzc2lvbl9pZCA9IGNkbS5vcGVuKCkNCiAgICAgICAgDQogICAgICAgIGxvZ190cmFjZSgiR2V0dGluZyBjaGFsbGVuZ2UuLi4iKQ0KICAgICAgICBjaGFsbGVuZ2UgPSBjZG0uZ2V0X2xpY2Vuc2VfY2hhbGxlbmdlKHNlc3Npb25faWQsIHBzc2gpDQogICAgICAgIA0KICAgICAgICBmb3IgaCBpbiBbIkNvbnRlbnQtTGVuZ3RoIiwgIkhvc3QiLCAiQ29udGVudC1UeXBlIiwgIkFjY2VwdC1FbmNvZGluZyIsICJDb25uZWN0aW9uIl06DQogICAgICAgICAgICBoZWFkZXJzLnBvcChoLCBOb25lKQ0KICAgICAgICAgICAgaGVhZGVycy5wb3AoaC5sb3dlcigpLCBOb25lKQ0KICAgICAgICAgICAgDQogICAgICAgIGhlYWRlcnNbJ0NvbnRlbnQtVHlwZSddID0gJ2FwcGxpY2F0aW9uL29jdGV0LXN0cmVhbScNCiAgICAgICAgDQogICAgICAgIGxvZ190cmFjZShmIlNlbmRpbmcgcmVxdWVzdCB0byB7bGljZW5zZV91cmx9Li4uIikNCiAgICAgICAgcmVzcCA9IE5vbmUNCiAgICAgICAgZm9yIGF0dGVtcHQgaW4gcmFuZ2UoNCk6DQogICAgICAgICAgICBsb2dfdHJhY2UoZiJBdHRlbXB0IHthdHRlbXB0KzF9Li4uIikNCiAgICAgICAgICAgIHJlc3AgPSByZXF1ZXN0cy5wb3N0KGxpY2Vuc2VfdXJsLCBkYXRhPWNoYWxsZW5nZSwgaGVhZGVycz1oZWFkZXJzLCB0aW1lb3V0PTIwLCB2ZXJpZnk9VHJ1ZSkNCiAgICAgICAgICAgIGlmIHJlc3Auc3RhdHVzX2NvZGUgPT0gMjAwOg0KICAgICAgICAgICAgICAgIGJyZWFrDQogICAgICAgICAgICB0aW1lLnNsZWVwKDEuNSkNCiAgICAgICAgICAgIA0KICAgICAgICBsb2dfdHJhY2UoZiJGaW5hbCBSZWNlaXZlZCByZXNwb25zZToge3Jlc3Auc3RhdHVzX2NvZGV9IikNCiAgICAgICAgDQogICAgICAgIGlmIHJlc3Auc3RhdHVzX2NvZGUgIT0gMjAwOg0KICAgICAgICAgICAgcHJpbnQoanNvbi5kdW1wcyh7InN0YXR1cyI6ICJlcnJvciIsICJtZXNzYWdlIjogZiJBUEkgRXJyb3Ige3Jlc3Auc3RhdHVzX2NvZGV9OiB7cmVzcC50ZXh0WzoxMDBdfSJ9KSkNCiAgICAgICAgICAgIHN5cy5zdGRvdXQuZmx1c2goKQ0KICAgICAgICAgICAgb3MuX2V4aXQoMSkNCiAgICAgICAgICAgIA0KICAgICAgICBsb2dfdHJhY2UoIlBhcnNpbmcgbGljZW5zZS4uLiIpDQogICAgICAgIGNkbS5wYXJzZV9saWNlbnNlKHNlc3Npb25faWQsIHJlc3AuY29udGVudCkNCiAgICAgICAga2V5cyA9IGNkbS5nZXRfa2V5cyhzZXNzaW9uX2lkKQ0KICAgICAgICANCiAgICAgICAga2V5c19saXN0ID0gW10NCiAgICAgICAgZm9yIGsgaW4ga2V5czoNCiAgICAgICAgICAgIGlmIGsudHlwZSA9PSAnQ09OVEVOVCc6DQogICAgICAgICAgICAgICAga2lkX29iaiA9IGdldGF0dHIoaywgJ2tpZCcsIGdldGF0dHIoaywgJ2lkJywgTm9uZSkpDQogICAgICAgICAgICAgICAga2V5X29iaiA9IGdldGF0dHIoaywgJ2tleScsIGdldGF0dHIoaywgJ3ZhbHVlJywgTm9uZSkpDQogICAgICAgICAgICAgICAgZGVmIHRvX2hleChvYmopOg0KICAgICAgICAgICAgICAgICAgICBpZiBoYXNhdHRyKG9iaiwgJ2hleCcpIGFuZCBpc2luc3RhbmNlKG9iai5oZXgsIHN0cik6IHJldHVybiBvYmouaGV4DQogICAgICAgICAgICAgICAgICAgIGlmIGhhc2F0dHIob2JqLCAnaGV4JykgYW5kIGNhbGxhYmxlKG9iai5oZXgpOiByZXR1cm4gb2JqLmhleCgpDQogICAgICAgICAgICAgICAgICAgIGltcG9ydCBiaW5hc2NpaQ0KICAgICAgICAgICAgICAgICAgICByZXR1cm4gYmluYXNjaWkuaGV4bGlmeShvYmopLmRlY29kZSgpDQogICAgICAgICAgICAgICAga2V5c19saXN0LmFwcGVuZChmInt0b19oZXgoa2lkX29iail9Ont0b19oZXgoa2V5X29iail9IikNCiAgICAgICAgICAgICAgICANCiAgICAgICAgY2RtLmNsb3NlKHNlc3Npb25faWQpDQogICAgICAgIA0KICAgICAgICBsb2dfdHJhY2UoZiJTdWNjZXNzZnVsbHkgZXh0cmFjdGVkIHtsZW4oa2V5c19saXN0KX0ga2V5cy4iKQ0KICAgICAgICBwcmludChqc29uLmR1bXBzKHsic3RhdHVzIjogIm9rIiwgImtleXMiOiBrZXlzX2xpc3R9KSkNCiAgICAgICAgc3lzLnN0ZG91dC5mbHVzaCgpDQogICAgICAgIG9zLl9leGl0KDApDQogICAgICAgIA0KICAgIGV4Y2VwdCBFeGNlcHRpb24gYXMgZToNCiAgICAgICAgbG9nX3RyYWNlKGYiRXhjZXB0aW9uIG9jY3VycmVkOiB7c3RyKGUpfSIpDQogICAgICAgIHByaW50KGpzb24uZHVtcHMoeyJzdGF0dXMiOiAiZXJyb3IiLCAibWVzc2FnZSI6IHN0cihlKX0pKQ0KICAgICAgICBzeXMuc3Rkb3V0LmZsdXNoKCkNCiAgICAgICAgb3MuX2V4aXQoMSkNCg0KaWYgX19uYW1lX18gPT0gIl9fbWFpbl9fIjoNCiAgICBtYWluKCkNCg==";
                        var pythonStartInfo = new ProcessStartInfo
                        {
                            FileName = "python",
                            Arguments = $"-c \"import base64, sys; exec(base64.b64decode('{base64Script}').decode('utf-8'))\" \"{tempJsonArgs}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        AppLogger.LogInfo($"[DRM] Requesting keys for {originalLectureName}...");
                        string keysArgs = "";
                        string output = "";
                        string errorOutput = "";
                        using (var process = new Process { StartInfo = pythonStartInfo })
                        {
                            process.OutputDataReceived += (s, e) => { if (e.Data != null) output += e.Data + "\n"; };
                            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput += e.Data + "\n"; };
                            
                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            
                            bool exited = await Task.Run(() => process.WaitForExit(60000)); // 60 seconds timeout
                            if (!exited)
                            {
                                try { process.Kill(); } catch { }
                                AppLogger.LogInfo($"[DRM] Python CDM script timed out after 60 seconds.");
                            }
                            else
                            {
                                process.WaitForExit(); // Ensures async event handlers finish
                            }
                            
                            if (!string.IsNullOrWhiteSpace(errorOutput) && string.IsNullOrWhiteSpace(output))
                            {
                                AppLogger.LogInfo($"[DRM] Python Error: {errorOutput}");
                            }

                            try { if (File.Exists(tempJsonArgs)) File.Delete(tempJsonArgs); } catch { }

                            if (string.IsNullOrWhiteSpace(output))
                            {
                                AppLogger.LogInfo($"[DRM] Python CDM script returned empty.");
                                return;
                            }

                            try
                            {
                                var result = JObject.Parse(output);
                                if (result["status"]?.ToString() == "ok")
                                {
                                    var keys = result["keys"] as JArray;
                                    if (keys != null)
                                    {
                                        foreach (var k in keys)
                                        {
                                            keysArgs += $"--key \"{k}\" ";
                                        }
                                    }
                                }
                                else
                                {
                                    AppLogger.LogInfo($"[DRM] CDM Error: {result["message"]?.ToString()}");
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLogger.LogInfo($"[DRM] Failed to parse CDM output: {output}. Error: {ex.Message}");
                                return;
                            }
                        }
                        
                        if (token.IsCancellationRequested) return;

                        AppLogger.LogInfo($"[DRM] Keys successfully extracted for {originalLectureName}. Preparing download...");

                        if (string.IsNullOrEmpty(keysArgs))
                        {
                            AppLogger.LogInfo($"[DRM] No keys were found for {originalLectureName}.");
                            return;
                        }

                        // Build N_m3u8DL-RE command
                        var manifest = metadata["manifests"]?.First;
                        string mUrl = manifest?["url"]?.ToString();
                        if (string.IsNullOrEmpty(mUrl))
                        {
                            AppLogger.LogInfo($"[DRM] No manifest URL found for {originalLectureName}.");
                            return;
                        }

                        var mHeaders = manifest?["headers"] as JObject;
                        string headerArgs = "";
                        if (mHeaders != null)
                        {
                            foreach (var prop in mHeaders.Properties())
                            {
                                headerArgs += $"-H \"{prop.Name}: {prop.Value.ToString().Replace("\"", "'")}\" ";
                            }
                        }

                        finalCmd = $"N_m3u8DL-RE.exe \"{mUrl}\" {headerArgs} {keysArgs} --save-dir \"{saveDir}\" --save-name \"{safeName}\" -M format=mkv --check-segments-count false";
                        
                        SaveCachedDrmCommand(courseName, lectureIdStr, finalCmd);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogInfo($"[DRM] Error processing DRM data for {originalLectureName}: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    AppLogger.LogInfo($"[DRM] Skipping {originalLectureName} - Missing URL and not found in legacy cache.");
                    return;
                }
            }

            if (token.IsCancellationRequested) return;

            // Strip any existing select-video and select-audio flags from finalCmd
            finalCmd = Regex.Replace(finalCmd, @"--select-video\s+(\S+|""[^""]+"")", "");
            finalCmd = Regex.Replace(finalCmd, @"--select-audio\s+(\S+|""[^""]+"")", "");

            string qualityArg = "--select-audio best --select-video best"; // Default Auto
            string q = SettingsManager.Current.VideoQuality;
            if (q == "1080p") qualityArg = "--select-audio best --select-video \"res=.*x(10|11|12|13|14|15|16)[0-9].*\"";
            else if (q == "720p") qualityArg = "--select-audio best --select-video \"res=.*x(7|8|9)[0-9].*\"";
            else if (q == "480p") qualityArg = "--select-audio best --select-video \"res=.*x(4|5|6)[0-9].*\"";
            else if (q == "360p") qualityArg = "--select-audio best --select-video \"res=.*x(3|4)[0-9].*\"";

            finalCmd += $" {qualityArg}";

            string tempDirBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_temp");
            string tDir = Path.Combine(tempDirBase, SanitizeFileName(originalLectureName));
            Directory.CreateDirectory(tDir);

            string cmd = finalCmd.Replace("\\\"", "\"").Replace("\\\\", "\\");
            
            // Rewrite save directory and name inside the command to use temp directory
            cmd = Regex.Replace(cmd, @"--save-dir\s+""[^""]+""", $"--save-dir \"{tDir}\"");
            cmd = Regex.Replace(cmd, @"--save-name\s+""[^""]+""", $"--save-name \"{safeName}\"");

            string exePath = Path.Combine(engineDir, "N_m3u8DL-RE.exe");
            string arguments = cmd;
            if (arguments.StartsWith("N_m3u8DL-RE.exe ")) arguments = arguments.Substring(16);
            if (arguments.StartsWith("N_m3u8DL-RE ")) arguments = arguments.Substring(12);

            arguments += $" --tmp-dir \"{tDir}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = engineDir
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    AppLogger.LogInfo($"[DRM] Download started for {originalLectureName} . This may take a few minutes...");

                    process.Start();



                    Func<StreamReader, Task> readStream = async (reader) =>
                    {
                        var buffer = new char[256];
                        var sb = new StringBuilder();
                        double lastReportedPerc = -1;
                        double vidPerc = 0;
                        double audPerc = 0;
                        bool hasAudio = false;
                        DateTime lastUiUpdate = DateTime.MinValue;
                        while (true)
                        {
                            int count = await reader.ReadAsync(buffer, 0, buffer.Length);
                            if (count == 0) break;

                            for (int i = 0; i < count; i++)
                            {
                                char c = buffer[i];
                                if (c == '\r' || c == '\n')
                                {
                                    string line = sb.ToString();
                                    sb.Clear();

                                    if (!string.IsNullOrWhiteSpace(line))
                                    {
                                        var pm = Regex.Match(line, @"(\d+(?:\.\d+)?)%");

                                        if (pm.Success)
                                        {
                                            if (double.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double perc))
                                            {
                                                if (line.Contains("Vid")) vidPerc = perc;
                                                else if (line.Contains("Aud")) { audPerc = perc; hasAudio = true; }
                                                else vidPerc = perc; // fallback

                                                double displayPerc = vidPerc;
                                                if (vidPerc >= 100 && hasAudio)
                                                {
                                                    displayPerc = audPerc;
                                                }

                                                if (displayPerc != lastReportedPerc)
                                                {
                                                    lastReportedPerc = displayPerc;
                                                    
                                                    // Throttle UI updates to 10 FPS to prevent WPF Dispatcher deadlock
                                                    if ((DateTime.UtcNow - lastUiUpdate).TotalMilliseconds >= 100 || displayPerc >= 100 || displayPerc <= 0)
                                                    {
                                                        lastUiUpdate = DateTime.UtcNow;

                                                        var speedMatches = Regex.Matches(line, @"(\d+(?:\.\d+)?)\s*(GB/s|MB/s|KB/s|B/s|GBps|MBps|KBps|Bps)", RegexOptions.IgnoreCase);
                                                        string speedText = speedMatches.Count > 0 ? speedMatches[speedMatches.Count - 1].Value.Trim() : "";
                                                        var m = Regex.Match(speedText, @"\d+(\.\d+)?");
                                                        double sp = 0.0;
                                                        if (m.Success)
                                                        {
                                                            string speed = m.Value;
                                                            sp = double.Parse(speed);
                                                        }
                                                        
                                                        string u= new string(speedText.Where(char.IsLetter).ToArray());
                                                        double bytesPerSec = 0.0;
                                                        if (speedMatches.Count>0)
                                                        {

                                                                string unit =u.ToUpper(); 

                                                                switch (unit)
                                                                {
                                                                    case "GBPS":
                                                                        bytesPerSec = sp * 1024 * 1024 * 1024;
                                                                        break;
                                                                    case "MBPS":
                                                                        bytesPerSec = sp * 1024 * 1024;
                                                                        break;
                                                                    case "KBPS":
                                                                        bytesPerSec = sp * 1024;
                                                                        break;
                                                                    default:
                                                                        bytesPerSec = sp;
                                                                        break;
                                                                }
                                                            
                                                        }

                                                        OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                                        {
                                                            CourseName = courseIdStr,
                                                            CurrentLecture = safeName,
                                                            Percentage = displayPerc,
                                                            CompletedLectures = completed,
                                                            TotalLectures = total,
                                                            Status = FormatSpeed(bytesPerSec)
                                                        });
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    sb.Append(c);
                                }
                            }
                        }
                    };

                    var outTask = Task.Run(() => readStream(process.StandardOutput));
                    var errTask = Task.Run(() => readStream(process.StandardError));

                    while (!process.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            break;
                        }
                        Thread.Sleep(500);
                    }

                    // Move final muxed file from temp dir to save dir
                    string outputMkv = Path.Combine(tDir, safeName + ".mkv");

                    if (File.Exists(outputMkv))
                    {
                        File.Move(outputMkv, targetMkv);
                    }
                   
                }
            }
            catch { }
            finally
            {
                if (File.Exists(targetMkv))
                {
                    try { if (Directory.Exists(tDir)) Directory.Delete(tDir, true); } catch { }
                }
            }
        }



        private async Task DownloadCaptionsAsync(string courseIdStr, int completed, int total, List<UdemyCaption> captions, string saveDir, string safeName, List<string>? selectedLocales, CancellationToken token)
        {
            if (captions == null || captions.Count == 0) return;
            if (!SettingsManager.Current.DownloadSubtitles) return;
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            List<string> targetLangsList;
            if (selectedLocales != null)
            {
                targetLangsList = selectedLocales.Select(l => l.ToLower()).ToList();
            }
            else
            {
                string targetLang = SettingsManager.Current.SubtitleLanguage?.Trim().ToLower() ?? "all";
                targetLangsList = targetLang.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(l => l.Trim().ToLower())
                                                 .ToList();
            }

            foreach (var caption in captions)
            {
                if (token.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(caption.url) || string.IsNullOrEmpty(caption.locale_id)) continue;

                if (targetLangsList.Count > 0 && !targetLangsList.Contains("all"))
                {
                    string locale = caption.locale_id.ToLower();
                    bool matches = false;
                    foreach (var lang in targetLangsList)
                    {
                        if (locale == lang || locale.StartsWith(lang + "_") || locale.StartsWith(lang + "-"))
                        {
                            matches = true;
                            break;
                        }
                    }
                    if (!matches) continue;
                }

                string extension = caption.url.Contains(".srt") ? ".srt" : ".vtt"; // Udemy typically uses vtt, but just in case
                string targetFile = Path.Combine(saveDir, $"{safeName}.{caption.locale_id}{extension}");
                
                bool fileExists = File.Exists(targetFile);
                if (!fileExists)
                {
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, caption.url);
                        using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var stream = await response.Content.ReadAsStreamAsync())
                            {
                                await stream.CopyToAsync(fs, 8192, token);
                            }
                        }
                        fileExists = true;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogInfo($"[Captions] Failed to download caption {caption.locale_id} for {safeName}: {ex.Message}");
                    }
                }

                if (fileExists)
                {
                    // Trigger translation to Arabic if this is not already an Arabic subtitle and setting is enabled
                    string locale = caption.locale_id.ToLower();
                    if (!locale.StartsWith("ar") && SettingsManager.Current.TranslateToArabic)
                    {
                        string arabicFile = Path.Combine(saveDir, $"{safeName}.ar{extension}");
                        if (!File.Exists(arabicFile))
                        {
                            string srcLang = locale;
                            int idx = srcLang.IndexOfAny(new[] { '_', '-' });
                            if (idx > 0) srcLang = srcLang.Substring(0, idx);

                            if (SettingsManager.Current.TranslationMethod == "API")
                            {
                                AppLogger.LogInfo($"[Translator] Translating {caption.locale_id} subtitles to Arabic online via API...");
                                await TranslateSubtitleFileOnlineAsync(courseIdStr, completed, total, safeName, targetFile, arabicFile, srcLang, "ar");
                            }
                            else
                            {
                                AppLogger.LogInfo($"[Translator] Translating {caption.locale_id} subtitles to Arabic offline via local model...");
                                await TranslateSubtitleFileAsync(courseIdStr, completed, total, safeName, targetFile, arabicFile, srcLang, "ar");
                            }
                        }
                    }
                }
            }
        }

        private async Task TranslateSubtitleFileAsync(string courseIdStr, int completed, int total, string safeName, string inputPath, string outputPath, string srcLang, string tgtLang)
        {
            try
            {
                if (!File.Exists(inputPath)) return;

                // Check for local translator engine and model files
                string translatorExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translator.exe");
                string qwenModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qwen-model");
                string hpltModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hplt-model");
                string nllbModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nllb-model");
                string modelPath = Directory.Exists(qwenModelPath) ? qwenModelPath : (Directory.Exists(hpltModelPath) ? hpltModelPath : nllbModelPath);

                if (File.Exists(translatorExe) && Directory.Exists(modelPath))
                {
                    // Flores-200 language code mapping for NLLB
                    string srcFlores = MapLanguageToFlores(srcLang);
                    string tgtFlores = MapLanguageToFlores(tgtLang);

                    AppLogger.LogInfo($"[Translator] Translating {srcLang} to {tgtLang} offline using local NLLB model...");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = translatorExe,
                        Arguments = $"--input \"{inputPath}\" --output \"{outputPath}\" --model \"{modelPath}\" --src_lang \"{srcFlores}\" --tgt_lang \"{tgtFlores}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = false,
                        RedirectStandardOutput = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            // Read translator output line-by-line to parse progress
                            while (true)
                            {
                                string line = await process.StandardOutput.ReadLineAsync();
                                if (line == null) break;

                                if (line.StartsWith("PROGRESS: "))
                                {
                                    string pctStr = line.Substring(10).Trim();
                                    if (double.TryParse(pctStr, out double translatePct))
                                    {
                                        OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                        {
                                            CourseName = courseIdStr,
                                            CurrentLecture = safeName + " (Translating Subtitles...)",
                                            Percentage = translatePct,
                                            CompletedLectures = completed,
                                            TotalLectures = total,
                                            Status = $"Translating: {Math.Round(translatePct)}%"
                                        });
                                    }
                                }
                            }

                            await process.WaitForExitAsync();
                            if (process.ExitCode == 0)
                            {
                                AppLogger.LogInfo($"[Translator] Subtitles translated successfully offline.");
                                return;
                            }
                            else
                            {
                                AppLogger.LogInfo($"[Translator] Local translation failed (Exit Code {process.ExitCode}). Falling back to Google Translate...");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[Translator] Local translation exception: {ex.Message}. Falling back to Google Translate...");
            }

            // Fallback to online translation
            AppLogger.LogInfo($"[Translator] Translating subtitles online using Google Translate...");
            await TranslateSubtitleFileOnlineAsync(courseIdStr, completed, total, safeName, inputPath, outputPath, srcLang, tgtLang);
        }

        private async Task TranslateSubtitleFileOnlineAsync(string courseIdStr, int completed, int total, string safeName, string inputPath, string outputPath, string srcLang, string tgtLang)
        {
            try
            {
                if (!File.Exists(inputPath)) return;
                var lines = await File.ReadAllLinesAsync(inputPath);
                var translatedLines = new List<string>();
                
                int totalBlocks = lines.Count(l => l.Contains("-->"));
                int currentBlock = 0;

                var textBuffer = new List<string>();
                
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    
                    // Header of VTT
                    if (i == 0 && line.StartsWith("WEBVTT"))
                    {
                        translatedLines.Add(lines[i]);
                        continue;
                    }
                    
                    // Check if it is a timing line or an index line or empty line
                    bool isTiming = line.Contains("-->");
                    bool isIndex = int.TryParse(line, out _);
                    bool isEmpty = string.IsNullOrWhiteSpace(line);
                    
                    if (isTiming || isIndex || isEmpty)
                    {
                        // We found a non-text line. If we have text in the buffer, translate and flush it first!
                        if (textBuffer.Count > 0)
                        {
                            string combinedText = string.Join(" ", textBuffer);
                            string translatedText = await TranslateTextGoogleAsync(combinedText, srcLang, tgtLang);
                            translatedLines.Add(translatedText);
                            textBuffer.Clear();

                            currentBlock++;
                            if (totalBlocks > 0)
                            {
                                double translatePct = (double)currentBlock / totalBlocks * 100;
                                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                {
                                    CourseName = courseIdStr,
                                    CurrentLecture = safeName + " (Translating Subtitles...)",
                                    Percentage = translatePct,
                                    CompletedLectures = completed,
                                    TotalLectures = total,
                                    Status = $"Translating: {Math.Round(translatePct)}%"
                                });
                            }
                        }
                        translatedLines.Add(lines[i]);
                    }
                    else
                    {
                        // It is a text line. Add it to the buffer.
                        textBuffer.Add(lines[i]);
                    }
                }
                
                // Flush any remaining text at the end of the file
                if (textBuffer.Count > 0)
                {
                    string combinedText = string.Join(" ", textBuffer);
                    string translatedText = await TranslateTextGoogleAsync(combinedText, srcLang, tgtLang);
                    translatedLines.Add(translatedText);

                    currentBlock++;
                    if (totalBlocks > 0)
                    {
                        double translatePct = (double)currentBlock / totalBlocks * 100;
                        if (translatePct > 100) translatePct = 100;
                        OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                        {
                            CourseName = courseIdStr,
                            CurrentLecture = safeName + " (Translating Subtitles...)",
                            Percentage = translatePct,
                            CompletedLectures = completed,
                            TotalLectures = total,
                            Status = $"Translating: {Math.Round(translatePct)}%"
                        });
                    }
                }
                
                await File.WriteAllLinesAsync(outputPath, translatedLines);
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[Translator] Online translation error: {ex.Message}");
            }
        }

        private async Task<string> TranslateTextGoogleAsync(string text, string srcLang, string tgtLang)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={srcLang}&tl={tgtLang}&dt=t&q={Uri.EscapeDataString(text)}";
                var response = await httpClient.GetStringAsync(url);
                
                var array = Newtonsoft.Json.Linq.JArray.Parse(response);
                if (array != null && array.Count > 0 && array[0] != null)
                {
                    var segments = array[0];
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    foreach (var segment in segments)
                    {
                        if (segment != null && segment[0] != null)
                        {
                            sb.Append(segment[0].ToString());
                        }
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[Translator] Google Translate API error: {ex.Message}");
            }
            return text;
        }

        private string MapLanguageToFlores(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "eng_Latn";
            string clean = lang.ToLower().Trim();
            
            // Handle cases where we receive full locales like "en-US" or "en_US"
            int idx = clean.IndexOfAny(new[] { '_', '-' });
            if (idx > 0) clean = clean.Substring(0, idx);

            switch (clean)
            {
                case "ar": return "arb_Arab";
                case "en": return "eng_Latn";
                case "fr": return "fra_Latn";
                case "es": return "spa_Latn";
                case "de": return "deu_Latn";
                case "it": return "ita_Latn";
                case "pt": return "por_Latn";
                case "ru": return "rus_Cyrl";
                case "tr": return "tur_Latn";
                case "zh": return "zho_Hans";
                case "ja": return "jpn_Jpan";
                case "ko": return "kor_Hani";
                case "hi": return "hin_Deva";
                default: return "eng_Latn"; // fallback to English
            }
        }

        private async Task DownloadAttachmentAsync(string courseIdStr, UdemyAsset attachment, string saveDir, CancellationToken token, int completed, int total)
        {
            if (attachment.download_urls == null || !attachment.download_urls.ContainsKey(attachment.asset_type)) return;
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            string dlUrl = attachment.download_urls[attachment.asset_type].FirstOrDefault()?.file;
            if (string.IsNullOrEmpty(dlUrl)) return;

            string fname = SanitizeFileName(attachment.filename ?? attachment.title);
            if (!fname.Contains(".") && dlUrl.Contains(".")) fname += Path.GetExtension(new Uri(dlUrl).LocalPath);
            
            string targetFile = Path.Combine(saveDir, fname);
            if (File.Exists(targetFile)) return;

            AppLogger.LogInfo($"[Course {courseIdStr}] Downloading attachment: {fname}");
            try
            {
                var fi = new FileInfo(targetFile + ".khaled");
                long existingLength = fi.Exists ? fi.Length : 0;

                var request = new HttpRequestMessage(HttpMethod.Get, dlUrl);
                if (existingLength > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                }

                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && totalBytes.HasValue)
                        totalBytes += existingLength;
                    else if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        existingLength = 0;

                    using (var fs = new FileStream(targetFile + ".khaled", existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[8192];
                        long totalRead = existingLength;
                        long intervalRead = 0;
                        int bytesRead;
                        var sw = Stopwatch.StartNew();

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytesRead, token);
                            totalRead += bytesRead;
                            intervalRead += bytesRead;

                            if (sw.ElapsedMilliseconds >= 1000)
                            {
                                double pct = totalBytes.HasValue ? (double)totalRead / totalBytes.Value * 100 : 0;
                                double bytesPerSec = intervalRead / (sw.ElapsedMilliseconds / 1000.0);
                                
                                OnCourseProgress?.Invoke(this, new CourseProgressEventArgs
                                {
                                    CourseName = courseIdStr,
                                    CurrentLecture = fname,
                                    Percentage = pct,
                                    CompletedLectures = completed,
                                    TotalLectures = total,
                                    Status = FormatSpeed(bytesPerSec)
                                });
                                
                                intervalRead = 0;
                                sw.Restart();
                            }
                        }
                    }
                }
                if (File.Exists(targetFile + ".khaled")) File.Move(targetFile + ".khaled", targetFile);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.LogInfo($"[Course {courseIdStr}] Failed to download attachment {fname}: {ex.Message}"); }
        }

        private async Task<bool> CheckInternetAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    using (var response = await client.GetAsync("https://www.google.com", HttpCompletionOption.ResponseHeadersRead))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
