using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace UdemyKicker
{
    public static class UdemyApiManager
    {
        public static string AccessToken { get; set; } = string.Empty;

        public static string GetCleanBearerToken()
        {
            if (string.IsNullOrWhiteSpace(AccessToken)) return "";
            string token = AccessToken.Trim();
            string bearerToken = "";

            if (token.StartsWith("["))
            {
                try
                {
                    var cookiesArray = Newtonsoft.Json.Linq.JArray.Parse(token);
                    foreach (var c in cookiesArray)
                    {
                        string name = c["name"]?.ToString();
                        string value = c["value"]?.ToString()?.Replace("\"", "\\\"");
                        if (name == "access_token" && value != null)
                        {
                            bearerToken = value.Trim('\\', '"');
                            break;
                        }
                    }
                }
                catch { }
            }
            else
            {
                bearerToken = token;
                if (bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearerToken = bearerToken.Substring(7).Trim();
            }
            return bearerToken;
        }

        public static async Task<string> RunCurlAsync(string url, string token)
        {
            try
            {
                // Proxy all API requests through MainForm's authenticated WebView2 browser
                // This perfectly bypasses Cloudflare!
                return await BrowserHost.FetchUdemyApiAsync(url);
            }
            catch (Exception ex)
            {
                return $"{{\"detail\": \"API exception: {ex.Message}\"}}";
            }
        }

        public static async Task<UdemyUser> GetUserDetailsAsync()
        {
            if (string.IsNullOrWhiteSpace(AccessToken)) return null;

            string cleanToken = AccessToken.Trim();
            if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                cleanToken = cleanToken.Substring(7).Trim();

            try
            {
                string json = await RunCurlAsync("https://www.udemy.com/api-2.0/users/me/?fields[user]=@all", cleanToken);
                if (!string.IsNullOrEmpty(json) && !json.Contains("detail"))
                {
                    return JsonConvert.DeserializeObject<UdemyUser>(json);
                }
            }
            catch { }
            return null;
        }

        public static string NextSubscribedUrl { get; set; } = "";
        public static string NextEnrolledUrl { get; set; } = "";

        public static void ResetPagination()
        {
            NextSubscribedUrl = "https://www.udemy.com/api-2.0/users/me/subscribed-courses?page_size=20&ordering=-last_accessed&fields[course]=title,num_lectures,image_480x270,estimated_content_length,url,rating";
            NextEnrolledUrl = "https://www.udemy.com/api-2.0/users/me/subscription-course-enrollments?page_size=20&ordering=-last_accessed&fields[course]=title,num_lectures,image_480x270,estimated_content_length,url,rating";
        }

        public static async Task<List<UdemyCourse>> GetNextCoursesBatchAsync()
        {
            if (string.IsNullOrWhiteSpace(AccessToken)) return new List<UdemyCourse>();
            
            string cleanToken = AccessToken.Trim();
            if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                cleanToken = cleanToken.Substring(7).Trim();

            try
            {
                if (!string.IsNullOrEmpty(NextSubscribedUrl))
                {
                    string json = await RunCurlAsync(NextSubscribedUrl, cleanToken);
                    var apiResponse = JsonConvert.DeserializeObject<UdemyApiResponse<UdemyCourse>>(json);
                    NextSubscribedUrl = apiResponse?.next;
                    if (apiResponse?.results != null && apiResponse.results.Count > 0)
                        return apiResponse.results;
                }

                if (!string.IsNullOrEmpty(NextEnrolledUrl))
                {
                    string json = await RunCurlAsync(NextEnrolledUrl, cleanToken);
                    var apiResponse = JsonConvert.DeserializeObject<UdemyApiResponse<UdemyCourse>>(json);
                    NextEnrolledUrl = apiResponse?.next;
                    if (apiResponse?.results != null && apiResponse.results.Count > 0)
                        return apiResponse.results;
                }
            }
            catch { }

            return new List<UdemyCourse>();
        }

        private static async Task<List<T>> GetAllPagesAsync<T>(string initialUrl, string token, Action<string> onRawJson = null)
        {
            var allItems = new List<T>();
            string currentUrl = initialUrl;
            
            while (!string.IsNullOrEmpty(currentUrl))
            {
                string json = await RunCurlAsync(currentUrl, token);
                onRawJson?.Invoke(json); // Capture raw JSON for debugging

                if (string.IsNullOrEmpty(json) || json.Contains("<title>Just a moment...</title>")) 
                    break;
                
                try
                {
                    var apiResponse = JsonConvert.DeserializeObject<UdemyApiResponse<T>>(json);
                    if (apiResponse?.results != null) 
                        allItems.AddRange(apiResponse.results);
                    
                    currentUrl = apiResponse?.next;
                }
                catch
                {
                    break;
                }
            }
            return allItems;
        }

        private static readonly Dictionary<int, (List<UdemyCurriculumItem> items, string debugJson)> curriculumCache = new Dictionary<int, (List<UdemyCurriculumItem> items, string debugJson)>();

        public static async Task<(List<UdemyCurriculumItem> items, string debugJson)> GetCourseCurriculumAsync(int courseId)
        {
            if (curriculumCache.TryGetValue(courseId, out var cached))
            {
                return cached;
            }

            string debugJson = "";
            if (string.IsNullOrWhiteSpace(AccessToken)) return (new List<UdemyCurriculumItem>(), debugJson);

            string cleanToken = AccessToken.Trim();
            if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                cleanToken = cleanToken.Substring(7).Trim();

            string lastJson = "";
            try
            {
                string url = $"https://www.udemy.com/api-2.0/courses/{courseId}/cached-subscriber-curriculum-items?page_size=200&fields%5Blecture%5D=id,title,asset,supplementary_assets,url&fields%5Basset%5D=asset_type,title,filename,body,captions,media_sources,stream_urls,download_urls,external_url,media_license_token&fields%5Bquiz%5D=id,title,type,num_assessments";
                
                var items = await GetAllPagesAsync<UdemyCurriculumItem>(url, cleanToken, json => lastJson = json);
                
                if (items.Count == 0) 
                {
                    debugJson = lastJson; // Pass raw JSON back if it failed
                    File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "debug_curriculum.txt"), debugJson);
                }
                else
                {
                    curriculumCache[courseId] = (items, debugJson);
                }
                
                return (items, debugJson);
            }
            catch { }
            return (new List<UdemyCurriculumItem>(), debugJson);
        }

        /// <summary>
        /// Fetches all quiz questions (assessments) for a given quiz ID.
        /// Returns a list of UdemyQuizAssessment with question text, choices, correct answer, and feedback.
        /// </summary>
        public static async Task<List<UdemyQuizAssessment>> GetQuizAssessmentsAsync(int quizId)
        {
            try
            {
                string url = $"https://www.udemy.com/api-2.0/quizzes/{quizId}/assessments/?page_size=200&fields[assessment]=id,assessment_type,prompt,correct_response";
                var allItems = new List<UdemyQuizAssessment>();
                string currentUrl = url;

                while (!string.IsNullOrEmpty(currentUrl))
                {
                    string json = await RunCurlAsync(currentUrl, "");
                    if (string.IsNullOrEmpty(json) || json.Contains("<title>Just a moment")) break;

                    try
                    {
                        var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                        var results = parsed["results"] as Newtonsoft.Json.Linq.JArray;
                        if (results != null)
                        {
                            foreach (var item in results)
                            {
                                var assessment = new UdemyQuizAssessment
                                {
                                    id = item["id"] != null ? (int)item["id"] : 0,
                                    assessment_type = item["assessment_type"]?.ToString() ?? "multiple-choice",
                                    correct_response = item["correct_response"]?.ToObject<List<string>>() ?? new List<string>()
                                };


                                var promptToken = item["prompt"];
                                if (promptToken != null)
                                {
                                    assessment.prompt = new UdemyQuizPrompt
                                    {
                                        question = promptToken["question"]?.ToString() ?? "",
                                        answers   = promptToken["answers"]?.ToObject<List<string>>() ?? new List<string>(),
                                        feedback  = promptToken["feedback"]?.ToString() ?? ""
                                    };
                                }

                                allItems.Add(assessment);
                            }
                        }

                        currentUrl = parsed["next"]?.ToString();
                    }
                    catch { break; }
                }

                AppLogger.LogInfo($"[Quiz {quizId}] Fetched {allItems.Count} assessments.");
                return allItems;
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"[Quiz {quizId}] Failed to fetch assessments: {ex.Message}");
                return new List<UdemyQuizAssessment>();
            }
        }

        /// <summary>
        /// Fetches the full HTML body of an Article lecture if it wasn't included in the curriculum response.
        /// Uses the lecture asset endpoint to get the body field.
        /// </summary>
        public static async Task<string> GetArticleBodyAsync(int lectureId)
        {
            try
            {
                string url = $"https://www.udemy.com/api-2.0/users/me/subscribed-courses-v2/{lectureId}/lectures/{lectureId}/?fields[lecture]=asset&fields[asset]=asset_type,body,title";
                // Try the assets endpoint directly
                string assetUrl = $"https://www.udemy.com/api-2.0/assets/{lectureId}/?fields[asset]=asset_type,body,title";
                string json = await RunCurlAsync(assetUrl, "");

                if (!string.IsNullOrEmpty(json) && json.Contains("\"body\""))
                {
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                    string body = parsed["body"]?.ToString();
                    if (!string.IsNullOrEmpty(body)) return body;
                }
            }
            catch { }
            return null;
        }

        public static async Task<string> DownloadAndCacheThumbnailAsync(string courseName, string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;


                string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UdemyKicker_Cache", "Thumbnails");
                Directory.CreateDirectory(cacheDir);

                string safeName = string.Join("_", courseName.Split(Path.GetInvalidFileNameChars()));
                string localPath = Path.Combine(cacheDir, safeName + ".jpg");

                if (File.Exists(localPath)) return localPath;

                var startInfo = new ProcessStartInfo
                {
                    FileName = "curl.exe",
                    Arguments = $"-s -o \"{localPath}\" \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    await Task.Run(() => process.WaitForExit());
                }

                if (File.Exists(localPath)) return localPath;
            }
            catch { }
            return null;
        }
    }
}
