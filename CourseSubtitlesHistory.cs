using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UdemyKicker
{
    public static class CourseSubtitlesHistory
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "udemyKicker_course_subs.json"
        );

        public static List<string> GetLastChosen(string courseId)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                    if (dict != null && dict.TryGetValue(courseId, out var list))
                    {
                        return list;
                    }
                }
            }
            catch { }
            return new List<string>();
        }

        public static void SaveChosen(string courseId, List<string> locales)
        {
            try
            {
                Dictionary<string, List<string>> dict = null;
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    dict = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                }
                if (dict == null)
                {
                    dict = new Dictionary<string, List<string>>();
                }
                dict[courseId] = locales;
                string newJson = JsonConvert.SerializeObject(dict, Formatting.Indented);
                File.WriteAllText(FilePath, newJson);
            }
            catch { }
        }
    }
}
