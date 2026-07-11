using System;
using System.Collections.Generic;

namespace UdemyKicker
{
    public class UdemyCourse
    {
        public int id { get; set; }
        public string title { get; set; }
        public string url { get; set; }
        public string image_480x270 { get; set; }
        public int num_lectures { get; set; }
        public int completion_ratio { get; set; }
        public int estimated_content_length { get; set; }
        public double rating { get; set; }
    }

    public class UdemyUser
    {
        public int id { get; set; }
        public string title { get; set; }
        public string display_name { get; set; }
        public string image_50x50 { get; set; }
        public string image_100x100 { get; set; }
        public string initials { get; set; }
        public int num_subscribed_courses { get; set; }
        public int num_completed_video_lectures { get; set; }
        public string email { get; set; }
        public DateTime created { get; set; }
    }

    public class UdemyApiResponse<T>
    {
        public int count { get; set; }
        public string next { get; set; }
        public List<T> results { get; set; }
    }

    public class UdemyCurriculumItem
    {
        public int id { get; set; }
        public string _class { get; set; } // "chapter", "lecture", or "quiz"
        public string title { get; set; }
        public string url { get; set; }
        public UdemyAsset asset { get; set; }
        public List<UdemyAsset> supplementary_assets { get; set; }

        // Quiz-specific fields (populated when _class == "quiz")
        public string type { get; set; }          // "simple-quiz" | "practice-test"
        public int? num_assessments { get; set; } // Number of questions in quiz

        // Populated after fetching quiz assessments from API
        [Newtonsoft.Json.JsonIgnore]
        public List<UdemyQuizAssessment> quiz_assessments { get; set; }
    }

    // Quiz question model fetched from /api-2.0/quizzes/{id}/assessments/
    public class UdemyQuizAssessment
    {
        public int id { get; set; }
        public string assessment_type { get; set; } // "multiple-choice", "true-false", "fill-in-the-blank"
        public UdemyQuizPrompt prompt { get; set; }
        public List<string> correct_response { get; set; } // correct answer index(es) as strings
    }

    public class UdemyQuizPrompt
    {
        public string question { get; set; }        // Question text (may contain HTML)
        public List<string> answers { get; set; }   // Answer choices list
        public string feedback { get; set; }         // Explanation for correct answer
    }


    public class UdemyAsset
    {
        public int id { get; set; }
        public string asset_type { get; set; } // "Video", "Article", etc.
        public string title { get; set; }
        public string body { get; set; } // Embedded HTML text content for Articles
        public string media_license_token { get; set; }
        public List<UdemyMediaSource> media_sources { get; set; }
        public Dictionary<string, List<UdemyMediaSource>> stream_urls { get; set; }
        public Dictionary<string, List<UdemyMediaSource>> download_urls { get; set; }
        public string filename { get; set; }
        public List<UdemyCaption> captions { get; set; }
    }

    public class UdemyCaption
    {
        public int id { get; set; }
        public string url { get; set; }
        public string title { get; set; }
        public string locale_id { get; set; }
        public string _class { get; set; }
    }

    public class UdemyMediaSource
    {
        public string type { get; set; } // "video/mp4", "application/x-mpegURL"
        public string label { get; set; } // "720", "480", "1080", "Auto"
        public string file { get; set; }
        public string src { get; set; } // used sometimes instead of file
    }

    public class CourseItem
    {
        public string course { get; set; }
        public string lecture { get; set; }
        public string command { get; set; }
        public string pssh { get; set; }
        public string license_url { get; set; }
        public bool IsMissingKey => command != null && command.Contains("XXXXX");
    }
}
