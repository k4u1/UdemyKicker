using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UdemyKicker;

namespace UdemyKickerWPF
{
    public static class PlayerGenerator
    {
        public static string GenerateCourseCurriculumJson(string courseName, List<UdemyCurriculumItem> curriculum, string downloadRoot)
        {
            var courseObj = new Dictionary<string, object>();
            courseObj["course_title"] = courseName;

            string safeCourseName = SanitizeFileName(courseName);
            string courseDir = Path.Combine(downloadRoot, safeCourseName);

            // Store absolute path so player.exe can locate videos even when moved
            courseObj["course_root"] = courseDir;

            var sectionsList = new List<object>();

            string currentChapter = "Chapter 1";
            int chapterIndex = 1;
            int lectureIndex = 1;

            List<object> currentLecturesList = null;

            foreach (var item in curriculum)
            {
                if (item._class == "chapter")
                {
                    currentChapter = SanitizeFileName($"{chapterIndex}.{item.title}");
                    chapterIndex++;
                    lectureIndex = 1;

                    var sectionObj = new Dictionary<string, object>();
                    sectionObj["title"] = item.title;
                    currentLecturesList = new List<object>();
                    sectionObj["lectures"] = currentLecturesList;

                    sectionsList.Add(sectionObj);
                    continue;
                }

                if (item._class == "lecture")
                {
                    if (currentLecturesList == null)
                    {
                        var sectionObj = new Dictionary<string, object>();
                        sectionObj["title"] = "General";
                        currentLecturesList = new List<object>();
                        sectionObj["lectures"] = currentLecturesList;
                        sectionsList.Add(sectionObj);
                    }

                    var lecObj = new Dictionary<string, object>();
                    lecObj["id"] = item.id;
                    lecObj["title"] = item.title;

                    string assetType = item.asset?.asset_type?.ToLower() ?? "video";
                    // Map video mashup to video for standard HTML5 play
                    if (assetType == "videomashup") assetType = "video";
                    lecObj["type"] = assetType;

                    string safeLectureName = SanitizeFileName($"{chapterIndex - 1}.{lectureIndex} {item.title}");
                    string saveDir = Path.Combine(courseDir, currentChapter);

                    bool isDownloaded = false;
                    string localVideoPath = "";
                    string localSubtitlePath = "";

                    if (assetType == "video")
                    {
                        bool isEncrypted = !string.IsNullOrEmpty(item.asset?.media_license_token);
                        string ext = isEncrypted ? ".mkv" : ".mp4";
                        string fullVideoPath = Path.Combine(saveDir, safeLectureName + ext);

                        if (File.Exists(fullVideoPath))
                        {
                            isDownloaded = true;
                            localVideoPath = $"{currentChapter}/{safeLectureName}{ext}";

                            if (Directory.Exists(saveDir))
                            {
                                var subFiles = Directory.GetFiles(saveDir, safeLectureName + ".*.vtt");
                                if (subFiles.Length == 0)
                                {
                                    subFiles = Directory.GetFiles(saveDir, safeLectureName + ".*.srt");
                                }
                                if (subFiles.Length > 0)
                                {
                                    localSubtitlePath = $"{currentChapter}/{Path.GetFileName(subFiles[0])}";
                                }
                            }
                        }
                    }
                    else if (assetType == "article")
                    {
                        isDownloaded = true;
                        lecObj["article_html"] = item.asset?.body ?? "<p>No text content available.</p>";
                    }

                    lecObj["is_downloaded"] = isDownloaded;
                    lecObj["local_video_path"] = localVideoPath;
                    lecObj["local_subtitle_path"] = localSubtitlePath;

                    // Attachments
                    var attachmentsList = new List<object>();
                    if (item.supplementary_assets != null)
                    {
                        foreach (var att in item.supplementary_assets)
                        {
                            string safeAttName = SanitizeFileName(att.filename ?? att.title ?? "attachment");
                            string fullAttPath = Path.Combine(saveDir, safeAttName);
                            if (File.Exists(fullAttPath))
                            {
                                var attObj = new Dictionary<string, object>();
                                attObj["filename"] = safeAttName;
                                attObj["local_path"] = $"{currentChapter}/{safeAttName}";
                                attachmentsList.Add(attObj);
                            }
                        }
                    }
                    lecObj["attachments"] = attachmentsList;

                    currentLecturesList.Add(lecObj);
                    lectureIndex++;
                }
                else if (item._class == "quiz")
                {
                    // Quiz / Practice Test item - embed questions with answers for offline use
                    if (currentLecturesList == null)
                    {
                        var sectionObj = new Dictionary<string, object>();
                        sectionObj["title"] = "General";
                        currentLecturesList = new List<object>();
                        sectionObj["lectures"] = currentLecturesList;
                        sectionsList.Add(sectionObj);
                    }

                    var quizObj = new Dictionary<string, object>();
                    quizObj["id"] = item.id;
                    quizObj["title"] = item.title;
                    quizObj["type"] = "quiz";
                    quizObj["quiz_type"] = item.type ?? "simple-quiz"; // "simple-quiz" | "practice-test"
                    quizObj["is_downloaded"] = true; // Quizzes are always "downloaded" as JSON data
                    quizObj["local_video_path"] = "";
                    quizObj["local_subtitle_path"] = "";
                    quizObj["attachments"] = new List<object>();

                    // Embed quiz questions with answer choices and correct answers
                    var questionsList = new List<object>();
                    if (item.quiz_assessments != null && item.quiz_assessments.Count > 0)
                    {
                        foreach (var assessment in item.quiz_assessments)
                        {
                            var qObj = new Dictionary<string, object>();
                            qObj["id"] = assessment.id;
                            qObj["assessment_type"] = assessment.assessment_type ?? "multiple-choice";
                            qObj["question"] = assessment.prompt?.question ?? "";
                            qObj["options"] = assessment.prompt?.answers ?? new List<string>();
                            qObj["correct_response"] = assessment.correct_response ?? new List<string>();
                            qObj["feedback"] = assessment.prompt?.feedback ?? "";
                            questionsList.Add(qObj);
                        }
                    }
                    quizObj["quiz_questions"] = questionsList;

                    currentLecturesList.Add(quizObj);
                    lectureIndex++;
                }

            }

            courseObj["sections"] = sectionsList;

            return JsonConvert.SerializeObject(courseObj, Formatting.Indented);
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

        public static void UpdatePlayerExecutable(string courseDirectory, string jsonCurriculum)
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(courseDirectory))
                {
                    Directory.CreateDirectory(courseDirectory);
                }

                string targetExe = Path.Combine(courseDirectory, "player.exe");

                // Read clean compiled bytes of the independent player from embedded resources
                byte[] cleanBytes = GetCleanExeBytes();
                if (cleanBytes == null || cleanBytes.Length == 0) return;

                byte[] markerBytes = Encoding.UTF8.GetBytes("__UDEMYKICKER_DATA__");
                byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonCurriculum);

                // Write clean bytes + marker + JSON data
                using (var fs = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(cleanBytes, 0, cleanBytes.Length);
                    fs.Write(markerBytes, 0, markerBytes.Length);
                    fs.Write(jsonBytes, 0, jsonBytes.Length);
                }

                // Save JSON directly in course_data.json for main app player tab
                try
                {
                    string jsonPath = Path.Combine(courseDirectory, "course_data.json");
                    File.WriteAllText(jsonPath, jsonCurriculum, Encoding.UTF8);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write player.exe: {ex.Message}");
            }
        }

        private static byte[] GetCleanExeBytes()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string[] resourceNames = assembly.GetManifestResourceNames();
                string resourceName = resourceNames.FirstOrDefault(r => r.EndsWith("UdemyPlayer.exe"));

                if (resourceName != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            byte[] buffer = new byte[stream.Length];
                            stream.Read(buffer, 0, buffer.Length);
                            return buffer;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load UdemyPlayer resource: {ex.Message}");
            }
            return null;
        }

        private static int FindBytesBackwards(byte[] src, byte[] find)
        {
            if (src == null || find == null || src.Length < find.Length) return -1;
            for (int i = src.Length - find.Length; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < find.Length; j++)
                {
                    if (src[i + j] != find[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
