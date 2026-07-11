using System.Windows.Documents;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdemyKicker;

namespace UdemyKickerWPF.Views
{
    public partial class PlayerView : Page
    {
        // Path settings
        private readonly string _downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "udemyKicker");
        
        // VLC instances
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private bool _isSliderTracking = false;

        // Active Course Metadata
        private JObject _activeCourseJson;
        private string _activeCourseDir = "";
        private string _activeCourseId = "";
        private JToken _activeLectureToken = null;

        public PlayerView()
        {
            InitializeComponent();
            
            // Initialize VLC core
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                vlcVideoView.MediaPlayer = _mediaPlayer;

                // Bind player time/position changed events
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"VLC player failed to initialize: {ex.Message}");
            }

            // Listen for window keydowns for media keyboard shortcuts
            Loaded += (s, e) =>
            {
                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    parentWindow.PreviewKeyDown += ParentWindow_KeyDown;
                }
            };
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshCourseLibraryAsync();
            try
            {
                // Initialize WebView2
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_player_webview");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await wvDocViewer.EnsureCoreWebView2Async(env);
            }
            catch { }
        }

        // ════════════════════════════ SCREEN 1: COURSE LIBRARY ════════════════════════════

        private async Task RefreshCourseLibraryAsync()
        {
            wpLocalCourses.Children.Clear();
            lblNoLocalCourses.Visibility = Visibility.Collapsed;

            if (!Directory.Exists(_downloadsRoot))
            {
                lblNoLocalCourses.Visibility = Visibility.Visible;
                return;
            }

            var courseFolders = Directory.GetDirectories(_downloadsRoot);
            int validCoursesCount = 0;

            foreach (var folder in courseFolders)
            {
                try
                {
                    JObject courseData = null;
                    string jsonPath = Path.Combine(folder, "course_data.json");
                    string playerHtmlPath = Path.Combine(folder, "player.html");
                    string playerExePath = Path.Combine(folder, "player.exe");

                    // 1. Priority: Check course_data.json
                    if (File.Exists(jsonPath))
                    {
                        string rawJson = await File.ReadAllTextAsync(jsonPath);
                        courseData = JObject.Parse(rawJson);
                    }
                    // 2. Priority: Extract from player.html
                    else if (File.Exists(playerHtmlPath))
                    {
                        courseData = ExtractJsonFromPlayerHtml(playerHtmlPath);
                    }
                    // 3. Priority: Extract from player.exe
                    else if (File.Exists(playerExePath))
                    {
                        courseData = ExtractJsonFromPlayerExe(playerExePath);
                    }
                    // 4. Priority: Build curriculum dynamically from folder files
                    else
                    {
                        courseData = GenerateCurriculumFromFolderStructure(folder);
                    }

                    if (courseData != null)
                    {
                        string courseTitle = courseData["course_title"]?.ToString() ?? Path.GetFileName(folder);

                        // Sync course root location dynamically
                        courseData["course_root"] = folder;

                        // Load dynamic progress
                        var progress = CourseDatabaseManager.LoadProgress(folder);
                        var completedList = progress["completed_lectures"] as JArray ?? new JArray();

                        // Count progress stats
                        int totalLectures = 0;
                        int completedLectures = 0;
                        var sections = courseData["sections"] as JArray;
                        if (sections != null)
                        {
                            foreach (var s in sections)
                            {
                                var lectures = s["lectures"] as JArray;
                                if (lectures != null)
                                {
                                    foreach (var l in lectures)
                                    {
                                        totalLectures++;
                                        string idStr = l["id"]?.ToString() ?? "";
                                        string pathStr = l["local_video_path"]?.ToString() ?? "";
                                        
                                        bool isLecCompleted = false;
                                        if (!string.IsNullOrEmpty(idStr) && completedList.Any(t => t.ToString() == idStr))
                                            isLecCompleted = true;
                                        else if (!string.IsNullOrEmpty(pathStr) && completedList.Any(t => t.ToString() == pathStr))
                                            isLecCompleted = true;

                                        if (isLecCompleted) completedLectures++;
                                    }
                                }
                            }
                        }

                        // Load thumbnail if cached
                        string thumbPath = "";
                        string safeName = string.Join("_", courseTitle.Split(Path.GetInvalidFileNameChars()));
                        string cachedThumb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_thumbnails", safeName + ".png");
                        if (File.Exists(cachedThumb))
                        {
                            thumbPath = cachedThumb;
                        }

                        // Create Card UI
                        var card = CreateLibraryCard(courseTitle, folder, totalLectures, completedLectures, thumbPath, courseData);
                        wpLocalCourses.Children.Add(card);
                        validCoursesCount++;
                    }
                }
                catch { }
            }

            if (validCoursesCount == 0)
            {
                lblNoLocalCourses.Visibility = Visibility.Visible;
            }
        }

        private JObject ExtractJsonFromPlayerHtml(string htmlPath)
        {
            try
            {
                string html = File.ReadAllText(htmlPath);
                int startIdx = html.IndexOf("window.CourseData =");
                if (startIdx == -1) startIdx = html.IndexOf("var CourseData =");
                if (startIdx == -1) return null;

                startIdx = html.IndexOf("{", startIdx);
                if (startIdx == -1) return null;

                int braceCount = 0;
                int endIdx = -1;
                for (int i = startIdx; i < html.Length; i++)
                {
                    if (html[i] == '{') braceCount++;
                    else if (html[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0)
                        {
                            endIdx = i;
                            break;
                        }
                    }
                }

                if (endIdx != -1)
                {
                    string jsonStr = html.Substring(startIdx, endIdx - startIdx + 1);
                    return JObject.Parse(jsonStr);
                }
            }
            catch { }
            return null;
        }

        private JObject ExtractJsonFromPlayerExe(string exePath)
        {
            try
            {
                byte[] src = File.ReadAllBytes(exePath);
                byte[] find = Encoding.UTF8.GetBytes("__UDEMYKICKER_DATA__");
                int idx = FindBytesBackwards(src, find);
                if (idx != -1)
                {
                    int start = idx + find.Length;
                    string jsonStr = Encoding.UTF8.GetString(src, start, src.Length - start);
                    return JObject.Parse(jsonStr);
                }
            }
            catch { }
            return null;
        }

        private int FindBytesBackwards(byte[] src, byte[] find)
        {
            if (src == null || find == null || src.Length < find.Length) return -1;
            for (int i = src.Length - find.Length; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < find.Length; j++)
                {
                    if (src[i + j] != find[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private JObject GenerateCurriculumFromFolderStructure(string courseDir)
        {
            try
            {
                var courseObj = new JObject();
                courseObj["course_title"] = Path.GetFileName(courseDir);
                courseObj["course_root"] = courseDir;

                var sectionsList = new JArray();
                var subDirs = Directory.GetDirectories(courseDir)
                    .OrderBy(d => Path.GetFileName(d))
                    .ToList();

                if (subDirs.Count > 0)
                {
                    foreach (var dir in subDirs)
                    {
                        var sectionObj = new JObject();
                        sectionObj["title"] = Path.GetFileName(dir);
                        var lecturesList = new JArray();

                        var files = Directory.GetFiles(dir)
                            .OrderBy(f => Path.GetFileName(f))
                            .ToList();

                        int lecId = 1;
                        foreach (var file in files)
                        {
                            string ext = Path.GetExtension(file).ToLower();
                            if (ext == ".mp4" || ext == ".mkv" || ext == ".avi")
                            {
                                var lecObj = new JObject();
                                string filenameWithoutExt = Path.GetFileNameWithoutExtension(file);
                                lecObj["id"] = lecId++;
                                lecObj["title"] = filenameWithoutExt;
                                lecObj["type"] = "video";
                                lecObj["is_downloaded"] = true;
                                lecObj["local_video_path"] = $"{Path.GetFileName(dir)}/{Path.GetFileName(file)}";

                                // Subtitles
                                string subPath = Path.Combine(dir, filenameWithoutExt + ".vtt");
                                if (File.Exists(subPath))
                                {
                                    lecObj["local_subtitle_path"] = $"{Path.GetFileName(dir)}/{filenameWithoutExt}.vtt";
                                }
                                else
                                {
                                    subPath = Path.Combine(dir, filenameWithoutExt + ".srt");
                                    if (File.Exists(subPath))
                                        lecObj["local_subtitle_path"] = $"{Path.GetFileName(dir)}/{filenameWithoutExt}.srt";
                                }

                                // Attachments (PDF/ZIP files)
                                var attachmentsList = new JArray();
                                var pdfPath = Path.Combine(dir, filenameWithoutExt + ".pdf");
                                if (File.Exists(pdfPath))
                                {
                                    var att = new JObject();
                                    att["filename"] = filenameWithoutExt + ".pdf";
                                    att["local_path"] = $"{Path.GetFileName(dir)}/{filenameWithoutExt}.pdf";
                                    attachmentsList.Add(att);
                                }
                                var zipPath = Path.Combine(dir, filenameWithoutExt + ".zip");
                                if (File.Exists(zipPath))
                                {
                                    var att = new JObject();
                                    att["filename"] = filenameWithoutExt + ".zip";
                                    att["local_path"] = $"{Path.GetFileName(dir)}/{filenameWithoutExt}.zip";
                                    attachmentsList.Add(att);
                                }
                                lecObj["attachments"] = attachmentsList;

                                lecturesList.Add(lecObj);
                            }
                        }

                        if (lecturesList.Count > 0)
                        {
                            sectionObj["lectures"] = lecturesList;
                            sectionsList.Add(sectionObj);
                        }
                    }
                }
                else
                {
                    // Flat files inside root
                    var sectionObj = new JObject();
                    sectionObj["title"] = "General Content";
                    var lecturesList = new JArray();

                    var files = Directory.GetFiles(courseDir)
                        .OrderBy(f => Path.GetFileName(f))
                        .ToList();

                    int lecId = 1;
                    foreach (var file in files)
                    {
                        string ext = Path.GetExtension(file).ToLower();
                        if (ext == ".mp4" || ext == ".mkv" || ext == ".avi")
                        {
                            var lecObj = new JObject();
                            string filenameWithoutExt = Path.GetFileNameWithoutExtension(file);
                            lecObj["id"] = lecId++;
                            lecObj["title"] = filenameWithoutExt;
                            lecObj["type"] = "video";
                            lecObj["is_downloaded"] = true;
                            lecObj["local_video_path"] = Path.GetFileName(file);

                            string subPath = Path.Combine(courseDir, filenameWithoutExt + ".vtt");
                            if (File.Exists(subPath)) lecObj["local_subtitle_path"] = filenameWithoutExt + ".vtt";

                            lecturesList.Add(lecObj);
                        }
                    }

                    if (lecturesList.Count > 0)
                    {
                        sectionObj["lectures"] = lecturesList;
                        sectionsList.Add(sectionObj);
                    }
                }

                if (sectionsList.Count > 0)
                {
                    courseObj["sections"] = sectionsList;
                    return courseObj;
                }
            }
            catch { }
            return null;
        }

        private Border CreateLibraryCard(string title, string folder, int total, int completed, string thumbPath, JObject courseJson)
        {
            // Calculate completion percent
            int pct = total > 0 ? (int)Math.Round((double)completed / total * 100) : 0;

            // Design Card Container
            var border = new Border
            {
                Width = 220,
                Height = 240,
                Margin = new Thickness(0, 0, 16, 16),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 78, 222, 163)), // #284edea3
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };

            // Background Gradient
            var grad = new System.Windows.Media.LinearGradientBrush();
            grad.StartPoint = new Point(0, 0);
            grad.EndPoint = new Point(1, 1);
            grad.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(10, 17, 14), 0));
            grad.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(20, 32, 26), 0.5));
            grad.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(8, 12, 10), 1));
            border.Background = grad;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Thumbnail Image
            var imgBorder = new Border { CornerRadius = new CornerRadius(12, 12, 0, 0), ClipToBounds = true };
            var img = new Image { Stretch = System.Windows.Media.Stretch.UniformToFill };
            if (!string.IsNullOrEmpty(thumbPath))
            {
                try { img.Source = new BitmapImage(new Uri(thumbPath)); } catch { }
            }
            imgBorder.Child = img;
            Grid.SetRow(imgBorder, 0);
            grid.Children.Add(imgBorder);

            // 2. Info area
            var infoStack = new StackPanel { Margin = new Thickness(12) };

            var txtTitle = new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var txtLecs = new TextBlock
            {
                Text = $"{total} lectures • {completed} watched",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(186, 204, 176)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10)
            };

            // Progress bar
            var prgGrid = new Grid();
            prgGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            prgGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txtPct = new TextBlock
            {
                Text = $"{pct}% Completed",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 3)
            };

            var bar = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Value = pct,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)),
                BorderThickness = new Thickness(0)
            };

            infoStack.Children.Add(txtTitle);
            infoStack.Children.Add(txtLecs);
            infoStack.Children.Add(txtPct);
            infoStack.Children.Add(bar);

            Grid.SetRow(infoStack, 1);
            grid.Children.Add(infoStack);
            border.Child = grid;

            // Click interaction
            border.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    OpenCoursePlayer(folder, courseJson);
                }
            };

            return border;
        }

        private async void TxtCourseSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = txtCourseSearch.Text.ToLowerInvariant().Trim();
            foreach (Border card in wpLocalCourses.Children)
            {
                var grid = card.Child as Grid;
                if (grid == null) continue;
                var infoStack = grid.Children.OfType<StackPanel>().FirstOrDefault();
                if (infoStack == null) continue;
                var txtTitle = infoStack.Children.OfType<TextBlock>().FirstOrDefault();
                if (txtTitle == null) continue;

                if (string.IsNullOrEmpty(query) || txtTitle.Text.ToLowerInvariant().Contains(query))
                {
                    card.Visibility = Visibility.Visible;
                }
                else
                {
                    card.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ════════════════════════════ SCREEN 2: COURSE CONTENT PLAYER ════════════════════════════

        private void OpenCoursePlayer(string folder, JObject json)
        {
            _activeCourseDir = folder;
            _activeCourseJson = json;
            _activeCourseId = json["course_root"]?.ToString() ?? folder;

            lblActiveCourseTitle.Text = json["course_title"]?.ToString() ?? Path.GetFileName(folder);

            // Load and merge local NoSQL course-level progress
            LoadAndMergeProgress(folder);

            // Configure local host mapping inside Webview to server files smoothly
            try
            {
                wvDocViewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "udemykicker.local",
                    _activeCourseDir,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            LibraryGrid.Visibility = Visibility.Collapsed;
            PlayerGrid.Visibility = Visibility.Visible;

            lblActiveLectureTitle.Text = "Select a lecture to play";
            btnMarkComplete.Visibility = Visibility.Collapsed;
            HideAllViewPanels();
            panelDefault.Visibility = Visibility.Visible;

            RenderCurriculumSidebar();
        }

        private void LoadAndMergeProgress(string folder)
        {
            try
            {
                var progress = CourseDatabaseManager.LoadProgress(folder);
                var completedList = progress["completed_lectures"] as JArray ?? new JArray();
                var notesDict = progress["lecture_notes"] as JObject ?? new JObject();

                var sections = _activeCourseJson["sections"] as JArray;
                if (sections != null)
                {
                    foreach (var s in sections)
                    {
                        var lectures = s["lectures"] as JArray;
                        if (lectures != null)
                        {
                            foreach (var l in lectures)
                            {
                                string idStr = l["id"]?.ToString() ?? "";
                                string pathStr = l["local_video_path"]?.ToString() ?? "";

                                bool isCompleted = false;
                                if (!string.IsNullOrEmpty(idStr) && completedList.Any(t => t.ToString() == idStr))
                                    isCompleted = true;
                                else if (!string.IsNullOrEmpty(pathStr) && completedList.Any(t => t.ToString() == pathStr))
                                    isCompleted = true;

                                l["is_completed"] = isCompleted;

                                string key = !string.IsNullOrEmpty(idStr) ? idStr : pathStr;
                                if (!string.IsNullOrEmpty(key) && notesDict.ContainsKey(key))
                                {
                                    l["notes"] = notesDict[key];
                                }
                                else
                                {
                                    l["notes"] = new JArray();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveProgressToDb()
        {
            if (_activeCourseJson == null || string.IsNullOrEmpty(_activeCourseDir)) return;

            try
            {
                var progress = new JObject();
                var completedList = new JArray();
                var notesDict = new JObject();

                var sections = _activeCourseJson["sections"] as JArray;
                if (sections != null)
                {
                    foreach (var s in sections)
                    {
                        var lectures = s["lectures"] as JArray;
                        if (lectures != null)
                        {
                            foreach (var l in lectures)
                            {
                                string idStr = l["id"]?.ToString() ?? "";
                                string pathStr = l["local_video_path"]?.ToString() ?? "";
                                string key = !string.IsNullOrEmpty(idStr) ? idStr : pathStr;

                                bool isCompleted = l["is_completed"] != null && (bool)l["is_completed"];
                                if (isCompleted)
                                {
                                    if (!string.IsNullOrEmpty(idStr)) completedList.Add(idStr);
                                    else if (!string.IsNullOrEmpty(pathStr)) completedList.Add(pathStr);
                                }

                                var notes = l["notes"] as JArray;
                                if (notes != null && notes.Count > 0 && !string.IsNullOrEmpty(key))
                                {
                                    notesDict[key] = notes;
                                }
                            }
                        }
                    }
                }

                progress["completed_lectures"] = completedList;
                progress["lecture_notes"] = notesDict;

                CourseDatabaseManager.SaveProgress(_activeCourseDir, progress);
            }
            catch { }
        }

        private void BtnBackToLibrary_Click(object sender, RoutedEventArgs e)
        {
            StopVlcPlayback();
            PlayerGrid.Visibility = Visibility.Collapsed;
            LibraryGrid.Visibility = Visibility.Visible;
            _ = RefreshCourseLibraryAsync();
        }

        private void HideAllViewPanels()
        {
            panelVideo.Visibility = Visibility.Collapsed;
            panelWeb.Visibility = Visibility.Collapsed;
            panelZip.Visibility = Visibility.Collapsed;
            panelDefault.Visibility = Visibility.Collapsed;
        }

        // Renders chapters and lectures in the left sidebar
        private void RenderCurriculumSidebar()
        {
            spCurriculum.Children.Clear();
            var sections = _activeCourseJson["sections"] as JArray;
            if (sections == null) return;

            int totalLectures = 0;
            int completedLectures = 0;

            foreach (var s in sections)
            {
                string sTitle = s["title"]?.ToString() ?? "";
                
                // Chapter Header label
                var lblChapter = new TextBlock
                {
                    Text = sTitle.ToUpper(),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                };
                spCurriculum.Children.Add(lblChapter);

                var lectures = s["lectures"] as JArray;
                if (lectures == null) continue;

                foreach (var lec in lectures)
                {
                    totalLectures++;
                    bool isCompleted = lec["is_completed"] != null && (bool)lec["is_completed"];
                    if (isCompleted) completedLectures++;

                    string lecTitle = lec["title"]?.ToString() ?? "";
                    string lecType = lec["type"]?.ToString() ?? "video";
                    string relativeVideo = lec["local_video_path"]?.ToString() ?? "";
                    bool isDownloaded = (lec["is_downloaded"] != null && (bool)lec["is_downloaded"]) ||
                                        (!string.IsNullOrEmpty(relativeVideo) && File.Exists(Path.Combine(_activeCourseDir, relativeVideo)));

                    // Lecture Button
                    var btn = new Button
                    {
                        Style = FindResource("SidebarLecStyle") as Style,
                        Tag = lec
                    };

                    // Highlight active playing lecture
                    bool isActive = _activeLectureToken != null &&
                                    (
                                      (_activeLectureToken["id"] != null && lec["id"] != null && _activeLectureToken["id"].ToString() == lec["id"].ToString()) ||
                                      (_activeLectureToken["local_video_path"] != null && lec["local_video_path"] != null && _activeLectureToken["local_video_path"].ToString() == lec["local_video_path"].ToString())
                                    );

                    if (isActive)
                    {
                        btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 78, 222, 163)); // distinct semi-transparent green highlight!
                        btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163));
                        btn.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        btn.Background = System.Windows.Media.Brushes.Transparent;
                        btn.BorderThickness = new Thickness(0);
                    }

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Icon
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Text
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // Complete check mark

                    // Icon
                    string iconSymbol = "PlayCircle24";
                    System.Windows.Media.Brush iconBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)); // green
                    if (lecType == "article")
                    {
                        iconSymbol = "DocumentText24";
                        iconBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(213, 184, 255)); // purple
                    }
                    else if (lecType == "quiz")
                    {
                        iconSymbol = "QuestionCircle24";
                        iconBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 227, 163)); // orange
                    }

                    var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = (Wpf.Ui.Controls.SymbolRegular)Enum.Parse(typeof(Wpf.Ui.Controls.SymbolRegular), iconSymbol), Foreground = iconBrush, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(icon, 0);
                    grid.Children.Add(icon);

                    // Title Text
                    var txt = new TextBlock
                    {
                        Text = lecTitle,
                        Foreground = isDownloaded || lecType != "video" ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(txt, 1);
                    grid.Children.Add(txt);

                    // Checkmark if watched
                    if (isCompleted)
                    {
                        var check = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(check, 2);
                        grid.Children.Add(check);
                    }

                    btn.Content = grid;
                    btn.Click += LectureButton_Click;

                    // Disable click for uncompleted video downloads
                    if (!isDownloaded && lecType == "video")
                    {
                        btn.IsEnabled = false;
                        btn.Opacity = 0.5;
                    }

                    spCurriculum.Children.Add(btn);

                    // Render attachments list under lecture button if available
                    var attachments = lec["attachments"] as JArray;
                    if (attachments != null && attachments.Count > 0)
                    {
                        foreach (var att in attachments)
                        {
                            string attName = att["filename"]?.ToString() ?? "Attachment";
                            string relPath = att["local_path"]?.ToString() ?? "";

                            var btnAtt = new Button
                            {
                                Style = FindResource("SidebarLecStyle") as Style,
                                Margin = new Thickness(24, 1, 8, 1),
                                Tag = att
                            };

                            var gridAtt = new Grid();
                            gridAtt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // Icon
                            gridAtt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Text

                            // Determine attachment icon
                            string attIcon = "Document20";
                            System.Windows.Media.Brush attBrush = System.Windows.Media.Brushes.Gray;
                            string ext = Path.GetExtension(attName).ToLower();
                            if (ext == ".pdf") { attIcon = "DocumentPdf20"; attBrush = System.Windows.Media.Brushes.Red; }
                            else if (ext == ".zip" || ext == ".rar") { attIcon = "FolderZip20"; attBrush = System.Windows.Media.Brushes.Orange; }

                            var iconAtt = new Wpf.Ui.Controls.SymbolIcon 
                            { 
                                Symbol = (Wpf.Ui.Controls.SymbolRegular)Enum.Parse(typeof(Wpf.Ui.Controls.SymbolRegular), attIcon), 
                                Foreground = attBrush, 
                                FontSize = 12, 
                                VerticalAlignment = VerticalAlignment.Center 
                            };
                            Grid.SetColumn(iconAtt, 0);
                            gridAtt.Children.Add(iconAtt);

                            var txtAtt = new TextBlock
                            {
                                Text = attName,
                                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(186, 204, 176)),
                                FontSize = 11,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(6, 0, 0, 0)
                            };
                            Grid.SetColumn(txtAtt, 1);
                            gridAtt.Children.Add(txtAtt);

                            btnAtt.Content = gridAtt;
                            btnAtt.Click += AttachmentButton_Click;
                            spCurriculum.Children.Add(btnAtt);
                        }
                    }
                }
            }

            // Sync overall sidebar progress bar
            lblSidebarProgressText.Text = $"{completedLectures} / {totalLectures} lectures";
            barSidebarProgress.Value = totalLectures > 0 ? ((double)completedLectures / totalLectures * 100) : 0;
        }

        private void LectureButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is JToken lec)
            {
                PlayLecture(lec);
            }
        }

        private void PlayLecture(JToken lec)
        {
            _activeLectureToken = lec;
            StopVlcPlayback();
            HideAllViewPanels();

            string lecTitle = lec["title"]?.ToString() ?? "";
            string lecType = lec["type"]?.ToString() ?? "video";
            lblActiveLectureTitle.Text = lecTitle;

            // Setup icons
            string iconSymbol = "PlayCircle24";
            if (lecType == "article") iconSymbol = "DocumentText24";
            else if (lecType == "quiz") iconSymbol = "QuestionCircle24";
            icoLectureType.Symbol = (Wpf.Ui.Controls.SymbolRegular)Enum.Parse(typeof(Wpf.Ui.Controls.SymbolRegular), iconSymbol);

            // Toggle Mark Complete Button visibility
            bool isCompleted = lec["is_completed"] != null && (bool)lec["is_completed"];
            btnMarkComplete.Content = isCompleted ? "Complete (تمت المشاهدة) ✓" : "Mark Complete (تم المشاهدة)";
            btnMarkComplete.Background = isCompleted 
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 78, 222, 163))
                : System.Windows.Media.Brushes.Transparent;
            btnMarkComplete.Visibility = Visibility.Visible;

            if (lecType == "video")
            {
                panelVideo.Visibility = Visibility.Visible;
                string relativeVideo = lec["local_video_path"]?.ToString() ?? "";
                string absoluteVideoPath = Path.Combine(_activeCourseDir, relativeVideo);

                if (File.Exists(absoluteVideoPath))
                {
                    InitializeVlcPlayerAndPlay(absoluteVideoPath, lec);
                    PopulateLectureNotesList(); // Renders the note list for the active video
                }
                else
                {
                    MessageBox.Show("Video file could not be found locally.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (lecType == "article")
            {
                panelWeb.Visibility = Visibility.Visible;
                string articleHtml = lec["article_html"]?.ToString() ?? "<p>No text content available.</p>";

                // Estimate reading time (avg 200 words/min)
                int wordCount = System.Text.RegularExpressions.Regex.Replace(articleHtml, "<[^>]*>", " ")
                    .Split(new char[]{' ','\n','\r','\t'}, StringSplitOptions.RemoveEmptyEntries).Length;
                int readMinutes = Math.Max(1, wordCount / 200);

                string html = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap' rel='stylesheet'>
    <style>
        :root {{
            --accent: #4EDEA3;
            --accent-dim: rgba(78,222,163,0.15);
            --bg: #020805;
            --bg-card: #0B1610;
            --bg-code: #111B17;
            --border: rgba(78,222,163,0.15);
            --text: #E0E8E4;
            --text-dim: #8AA89E;
            --purple: #D5B8FF;
        }}
        * {{ box-sizing: border-box; margin: 0; padding: 0; }}
        html {{ scroll-behavior: smooth; }}

        /* ── Scroll progress bar ── */
        #progress-bar {{
            position: fixed;
            top: 0; left: 0;
            height: 3px;
            width: 0%;
            background: linear-gradient(90deg, #4EDEA3, #7BFFD4);
            z-index: 100;
            transition: width 0.1s;
        }}

        body {{
            font-family: 'Outfit', 'Segoe UI', system-ui, sans-serif;
            background: var(--bg);
            color: var(--text);
            padding: 0 0 60px;
            line-height: 1.8;
            font-size: 15px;
        }}

        /* ── Sticky header ── */
        .sticky-header {{
            position: sticky;
            top: 0;
            background: rgba(2,8,5,0.92);
            backdrop-filter: blur(12px);
            border-bottom: 1px solid var(--border);
            padding: 12px 40px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            z-index: 90;
        }}
        .header-title {{
            font-size: 13px;
            font-weight: 600;
            color: var(--accent);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            max-width: 70%;
        }}
        .header-meta {{
            font-size: 11px;
            color: var(--text-dim);
            display: flex;
            align-items: center;
            gap: 12px;
            flex-shrink: 0;
        }}
        .badge-article {{
            background: rgba(213,184,255,0.15);
            border: 1px solid rgba(213,184,255,0.3);
            color: var(--purple);
            font-size: 10px;
            font-weight: 700;
            padding: 2px 8px;
            border-radius: 20px;
            letter-spacing: 0.5px;
            text-transform: uppercase;
        }}

        /* ── Main content ── */
        .content {{
            max-width: 820px;
            margin: 0 auto;
            padding: 36px 40px;
        }}

        h1, h2, h3, h4, h5, h6 {{
            color: #fff;
            font-weight: 700;
            line-height: 1.3;
            margin: 1.8em 0 0.6em;
        }}
        h1 {{ font-size: 26px; }}
        h2 {{ font-size: 21px; padding-bottom: 8px; border-bottom: 1px solid var(--border); }}
        h3 {{ font-size: 17px; color: var(--accent); }}
        h4, h5, h6 {{ font-size: 15px; }}

        p {{ margin: 0.9em 0; }}

        a {{ color: var(--accent); text-decoration: none; }}
        a:hover {{ text-decoration: underline; opacity: 0.85; }}

        strong, b {{ font-weight: 700; color: #fff; }}
        em, i {{ font-style: italic; color: #c8ddd6; }}

        ul, ol {{
            padding-left: 1.5em;
            margin: 0.9em 0;
        }}
        li {{ margin: 0.3em 0; }}
        li::marker {{ color: var(--accent); }}

        /* ── Code blocks ── */
        pre {{
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-left: 3px solid var(--accent);
            border-radius: 8px;
            padding: 16px 20px;
            overflow-x: auto;
            margin: 1.2em 0;
            position: relative;
        }}
        pre code {{
            font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
            font-size: 13px;
            color: #D5B8FF;
            line-height: 1.7;
            background: none;
            padding: 0;
            border: none;
        }}
        code {{
            font-family: 'JetBrains Mono', Consolas, monospace;
            font-size: 12.5px;
            color: var(--purple);
            background: rgba(213,184,255,0.1);
            border: 1px solid rgba(213,184,255,0.2);
            padding: 2px 6px;
            border-radius: 4px;
        }}

        /* ── Blockquote ── */
        blockquote {{
            border-left: 3px solid var(--accent);
            margin: 1.2em 0;
            padding: 12px 20px;
            background: var(--accent-dim);
            border-radius: 0 8px 8px 0;
            color: #bde8d4;
            font-style: italic;
        }}

        /* ── Tables ── */
        table {{
            width: 100%;
            border-collapse: collapse;
            margin: 1.2em 0;
            font-size: 13.5px;
        }}
        th {{
            background: var(--accent-dim);
            color: var(--accent);
            font-weight: 700;
            padding: 10px 14px;
            text-align: left;
            border-bottom: 1px solid rgba(78,222,163,0.3);
        }}
        td {{
            padding: 9px 14px;
            border-bottom: 1px solid var(--border);
            color: var(--text);
        }}
        tr:hover td {{ background: rgba(78,222,163,0.04); }}

        /* ── Images ── */
        img {{
            max-width: 100%;
            height: auto;
            border-radius: 8px;
            border: 1px solid var(--border);
            margin: 1em 0;
            display: block;
        }}

        /* ── Horizontal rule ── */
        hr {{
            border: none;
            border-top: 1px solid var(--border);
            margin: 2em 0;
        }}

        /* ── Callout boxes ── */
        .note-box {{
            background: rgba(78,222,163,0.06);
            border: 1px solid rgba(78,222,163,0.25);
            border-radius: 8px;
            padding: 14px 18px;
            margin: 1.2em 0;
            font-size: 14px;
        }}
    </style>
</head>
<body>
    <div id='progress-bar'></div>

    <div class='sticky-header'>
        <span class='header-title'>{lecTitle}</span>
        <div class='header-meta'>
            <span class='badge-article'>Article</span>
            <span>📖 {readMinutes} min read · {wordCount} words</span>
        </div>
    </div>

    <div class='content'>
        {articleHtml}
    </div>

    <script>
        // Scroll progress bar
        window.addEventListener('scroll', () => {{
            const scrollTop = document.documentElement.scrollTop || document.body.scrollTop;
            const scrollHeight = document.documentElement.scrollHeight - document.documentElement.clientHeight;
            const progress = scrollHeight > 0 ? (scrollTop / scrollHeight) * 100 : 0;
            document.getElementById('progress-bar').style.width = progress + '%';
        }});
    </script>
</body>
</html>";
                wvDocViewer.NavigateToString(html);
            }

            else if (lecType == "quiz")
            {
                panelWeb.Visibility = Visibility.Visible;
                var questions = lec["quiz_questions"] as JArray;
                RenderQuizInWebview(lecTitle, questions);
            }

            // Refresh curriculum sidebar selection highlights
            RenderCurriculumSidebar();
        }

                private void BtnMarkComplete_Click(object sender, RoutedEventArgs e)
        {
            if (_activeLectureToken == null) return;

            // 1. Mark as completed
            _activeLectureToken["is_completed"] = true;

            // Save to central local database
            SaveProgressToDb();

            // Re-render sidebar to update checks
            RenderCurriculumSidebar();

            // 2. Automatically navigate to next lecture
            PlayNextLecture();
        }

private bool _isCompletedState(JToken token)
        {
            return token["is_completed"] == null || !(bool)token["is_completed"];
        }

        // ════════════════════════════ VLC PLAYER INTEGRATION ════════════════════════════

        private void InitializeVlcPlayerAndPlay(string filePath, JToken lec)
        {
            try
            {
                sliderVlcProgress.Value = 0;
                lblVlcTime.Text = "00:00 / 00:00";
                
                // Clear old subtitles combo box
                cmbVlcSubtitles.SelectionChanged -= CmbVlcSubtitles_SelectionChanged;
                cmbVlcSubtitles.Items.Clear();
                cmbVlcSubtitles.Items.Add(new ComboBoxItem { Content = "Off (مغلق)", Tag = -1 });
                cmbVlcSubtitles.SelectedIndex = 0;
                cmbVlcSubtitles.SelectionChanged += CmbVlcSubtitles_SelectionChanged;

                var media = new Media(_libVLC, new Uri(filePath));
                
                // Look for subtitles file (.vtt or .srt)
                string relativeSubtitle = lec["local_subtitle_path"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(relativeSubtitle))
                {
                    string absSubPath = Path.Combine(_activeCourseDir, relativeSubtitle);
                    if (File.Exists(absSubPath))
                    {
                        // Add subtitle track dynamically to media
                        media.AddOption($":sub-file={absSubPath}");
                    }
                }

                _mediaPlayer.Play(media);
                icoVlcPlay.Symbol = Wpf.Ui.Controls.SymbolRegular.Pause24;
                
                // Load subtitle tracks inside combobox in a short delay so VLC has analyzed tracks
                Task.Delay(1000).ContinueWith((t) =>
                {
                    Dispatcher.Invoke(() => PopulateSubtitleTracksList());
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to play video: {ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PopulateSubtitleTracksList()
        {
            try
            {
                cmbVlcSubtitles.SelectionChanged -= CmbVlcSubtitles_SelectionChanged;
                cmbVlcSubtitles.Items.Clear();
                cmbVlcSubtitles.Items.Add(new ComboBoxItem { Content = "Off (مغلق)", Tag = -1 });

                var tracks = _mediaPlayer.SpuDescription;
                int selectedIdx = 0;
                int index = 1;

                if (tracks != null)
                {
                    foreach (var track in tracks)
                    {
                        if (track.Id == -1) continue; // skip off track

                        var item = new ComboBoxItem { Content = track.Name ?? $"Track {index}", Tag = track.Id };
                        cmbVlcSubtitles.Items.Add(item);

                        // Select default subtitles automatically
                        if (track.Name != null && (track.Name.ToLower().Contains("arabic") || track.Name.ToLower().Contains("ar")))
                        {
                            selectedIdx = index;
                        }
                        index++;
                    }
                }

                cmbVlcSubtitles.SelectedIndex = selectedIdx;
                cmbVlcSubtitles.SelectionChanged += CmbVlcSubtitles_SelectionChanged;

                // Apply initial subtitle track selection
                if (selectedIdx > 0 && cmbVlcSubtitles.SelectedItem is ComboBoxItem cbi && cbi.Tag is int trackId)
                {
                    _mediaPlayer.SetSpu(trackId);
                }
            }
            catch { }
        }

        private void StopVlcPlayback()
        {
            if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Stop();
            }
        }

        private void MediaPlayer_TimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_isSliderTracking) return;

            Dispatcher.Invoke(() =>
            {
                long timeMs = e.Time;
                long totalMs = _mediaPlayer.Length;

                if (totalMs > 0)
                {
                    double pct = (double)timeMs / totalMs * 100;
                    sliderVlcProgress.Value = pct;
                }

                lblVlcTime.Text = $"{FormatVlcTime(timeMs)} / {FormatVlcTime(totalMs)}";
                
                // Update live notes timestamp indicator dynamically
                lblNoteTimestamp.Text = $"at {FormatVlcTime(timeMs)}";
            });
        }

        private void MediaPlayer_LengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                lblVlcTime.Text = $"00:00 / {FormatVlcTime(e.Length)}";
            });
        }

        private void MediaPlayer_EndReached(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                icoVlcPlay.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
                sliderVlcProgress.Value = 100;
                
                // Automatically mark video complete on play completion!
                if (_activeLectureToken != null && (_activeLectureToken["is_completed"] == null || !(bool)_activeLectureToken["is_completed"]))
                {
                    _activeLectureToken["is_completed"] = true;
                    SaveProgressToDb();
                    RenderCurriculumSidebar();
                }
            });
        }

        private string FormatVlcTime(long timeMs)
        {
            var span = TimeSpan.FromMilliseconds(timeMs);
            if (span.Hours > 0)
                return $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
            return $"{span.Minutes:D2}:{span.Seconds:D2}";
        }

        private void BtnVlcPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                icoVlcPlay.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
            }
            else
            {
                _mediaPlayer.Play();
                icoVlcPlay.Symbol = Wpf.Ui.Controls.SymbolRegular.Pause24;
            }
        }

        private void BtnVlcRewind_Click(object sender, RoutedEventArgs e) => SeekRelative(-10000);
        private void BtnVlcForward_Click(object sender, RoutedEventArgs e) => SeekRelative(10000);

        private void SeekRelative(long deltaMs)
        {
            long current = _mediaPlayer.Time;
            long target = Math.Max(0, Math.Min(_mediaPlayer.Length, current + deltaMs));
            _mediaPlayer.Time = target;
        }

        private void SliderVlcProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderTracking = true;
        }

        private void SliderVlcProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSliderTracking = false;
            double pct = sliderVlcProgress.Value;
            long targetMs = (long)(pct / 100 * _mediaPlayer.Length);
            _mediaPlayer.Time = targetMs;
        }

        private void SliderVlcProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isSliderTracking) return;
            // Update time label dynamically on slide
            double pct = sliderVlcProgress.Value;
            long targetMs = (long)(pct / 100 * _mediaPlayer.Length);
            lblVlcTime.Text = $"{FormatVlcTime(targetMs)} / {FormatVlcTime(_mediaPlayer.Length)}";
        }

        private void CmbVlcSubtitles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbVlcSubtitles.SelectedItem is ComboBoxItem item && item.Tag is int trackId)
            {
                _mediaPlayer.SetSpu(trackId);
            }
        }

        private void BtnVlcFullscreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleVlcFullscreen();
        }

        private void VlcVideoView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                ToggleVlcFullscreen();
            }
        }

        private void ToggleVlcFullscreen()
        {
            // Toggle window state to maximized/normal borderless for full immersion
            var parentWin = Window.GetWindow(this);
            if (parentWin != null)
            {
                if (parentWin.WindowState == WindowState.Maximized && parentWin.WindowStyle == WindowStyle.None)
                {
                    parentWin.WindowState = WindowState.Normal;
                    parentWin.WindowStyle = WindowStyle.SingleBorderWindow; // restore
                }
                else
                {
                    parentWin.WindowStyle = WindowStyle.None;
                    parentWin.WindowState = WindowState.Maximized;
                }
            }
        }

        // Listen for standard player shortcut keys globally in parent window context
        private void ParentWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (PlayerGrid.Visibility != Visibility.Visible || panelVideo.Visibility != Visibility.Visible) return;

            // Do NOT intercept keyboard events when user is typing in the notes RichTextBox
            // This allows spacebar to insert spaces in notes without pausing the video
            if (rtbNoteText != null && rtbNoteText.IsKeyboardFocusWithin)
                return;

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;
                case Key.Left:
                    SeekRelative(-10000); // 10s backward
                    e.Handled = true;
                    break;
                case Key.Right:
                    SeekRelative(10000);  // 10s forward
                    e.Handled = true;
                    break;
                case Key.F:
                    ToggleVlcFullscreen();
                    e.Handled = true;
                    break;
            }
        }

        // ════════════════════════════ PANEL B: QUIZZES IN WEBVIEW ════════════════════════════

        private void RenderQuizInWebview(string title, JArray questions)
        {
            // Build quiz type label
            string quizTypeLabel = "Quiz";

            var qJson = questions != null ? questions.ToString(Newtonsoft.Json.Formatting.None) : "[]";

            string html = $@"
<!DOCTYPE html>
<html lang='ar' dir='rtl'>
<head>
    <meta charset='UTF-8'>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;700&display=swap' rel='stylesheet'>
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; }}
        body {{
            font-family: 'Outfit', 'Segoe UI', sans-serif;
            background: #020805;
            color: #E5E2E3;
            padding: 28px 32px;
            line-height: 1.6;
        }}
        .quiz-header {{
            display: flex;
            align-items: center;
            gap: 12px;
            margin-bottom: 24px;
            padding-bottom: 16px;
            border-bottom: 1px solid rgba(78,222,163,0.2);
        }}
        .quiz-badge {{
            background: rgba(78,222,163,0.15);
            border: 1px solid rgba(78,222,163,0.4);
            color: #4EDEA3;
            font-size: 11px;
            font-weight: 700;
            padding: 3px 10px;
            border-radius: 20px;
            letter-spacing: 1px;
            text-transform: uppercase;
        }}
        h2 {{ color: #fff; font-size: 20px; font-weight: 700; }}
        .q-count {{ color: #bbcabf; font-size: 13px; margin-top: 2px; }}

        .q-card {{
            background: #0B1610;
            border: 1px solid rgba(78,222,163,0.15);
            border-radius: 10px;
            padding: 20px;
            margin-bottom: 18px;
            transition: border-color 0.2s;
        }}
        .q-card.correct-card {{ border-color: rgba(78,222,163,0.6); }}
        .q-card.wrong-card   {{ border-color: rgba(255,80,80,0.5); }}

        .q-num {{
            font-size: 11px;
            font-weight: 700;
            color: #4EDEA3;
            letter-spacing: 0.5px;
            margin-bottom: 8px;
        }}
        .q-text {{
            font-size: 14px;
            font-weight: 600;
            margin-bottom: 14px;
            color: #E5E2E3;
            line-height: 1.6;
        }}

        .opt {{
            display: flex;
            align-items: center;
            gap: 12px;
            padding: 11px 16px;
            background: #111B17;
            border: 1px solid rgba(78,222,163,0.15);
            border-radius: 7px;
            margin-bottom: 8px;
            cursor: pointer;
            font-size: 13px;
            transition: all 0.15s;
            color: #D9E4DE;
        }}
        .opt:hover:not(.disabled) {{ background: #192D25; border-color: rgba(78,222,163,0.4); }}
        .opt.selected {{
            background: rgba(78,222,163,0.15);
            border-color: #4EDEA3;
            color: #fff;
        }}
        .opt.correct-opt {{
            background: rgba(78,222,163,0.2);
            border-color: #4EDEA3;
            color: #4EDEA3;
            font-weight: 700;
        }}
        .opt.wrong-opt {{
            background: rgba(255,80,80,0.15);
            border-color: #ff5050;
            color: #ff9090;
        }}
        .opt-icon {{ font-size: 16px; flex-shrink: 0; }}
        .opt-letter {{
            width: 24px; height: 24px;
            border-radius: 50%;
            background: rgba(78,222,163,0.1);
            display: flex; align-items: center; justify-content: center;
            font-size: 11px; font-weight: 700; color: #4EDEA3;
            flex-shrink: 0;
        }}

        .feedback-box {{
            display: none;
            margin-top: 12px;
            padding: 12px 16px;
            background: rgba(78,222,163,0.08);
            border-left: 3px solid #4EDEA3;
            border-radius: 0 6px 6px 0;
            font-size: 13px;
            color: #bde8d4;
        }}
        .feedback-box.show {{ display: block; }}
        .feedback-label {{ font-weight: 700; color: #4EDEA3; margin-bottom: 4px; }}

        .btn-submit {{
            background: #4EDEA3;
            color: #002918;
            font-weight: 700;
            font-size: 14px;
            border: none;
            padding: 13px 32px;
            border-radius: 8px;
            cursor: pointer;
            margin-top: 8px;
            transition: all 0.2s;
        }}
        .btn-submit:hover {{ background: #3ec890; transform: translateY(-1px); }}
        .btn-submit:disabled {{ background: #2a4a3a; color: #6a8a7a; cursor: default; transform: none; }}

        .score-card {{
            display: none;
            margin-top: 24px;
            padding: 24px;
            background: #0B1610;
            border: 1px solid rgba(78,222,163,0.3);
            border-radius: 12px;
            text-align: center;
        }}
        .score-card.show {{ display: block; }}
        .score-num {{ font-size: 48px; font-weight: 700; color: #4EDEA3; }}
        .score-label {{ color: #bbcabf; font-size: 14px; margin-top: 4px; }}
        .score-msg {{ font-size: 16px; font-weight: 600; margin-top: 12px; }}
    </style>
</head>
<body>
    <div class='quiz-header'>
        <span class='quiz-badge'>Quiz</span>
        <div>
            <h2>{title}</h2>
            <div class='q-count' id='qCountLabel'></div>
        </div>
    </div>

    <div id='quizContainer'></div>

    <button class='btn-submit' id='btnSubmit' onclick='submitQuiz()'>Submit Answers &nbsp; ✓</button>

    <div class='score-card' id='scoreCard'>
        <div class='score-num' id='scoreNum'>0%</div>
        <div class='score-label' id='scoreLabel'>0 / 0 correct</div>
        <div class='score-msg' id='scoreMsg'></div>
    </div>

    <script>
        const questions = {qJson};
        const answers = {{}};
        let submitted = false;

        const letters = ['A','B','C','D','E','F','G','H'];

        document.getElementById('qCountLabel').textContent = questions.length + ' question' + (questions.length !== 1 ? 's' : '');

        const container = document.getElementById('quizContainer');
        questions.forEach((q, qi) => {{
            const card = document.createElement('div');
            card.className = 'q-card';
            card.id = 'qcard-' + qi;

            const qText = (q.question || '').replace(/<[^>]*>/g, '');

            card.innerHTML = `
                <div class='q-num'>Question ${{qi+1}} of ${{questions.length}}</div>
                <div class='q-text'>${{qText}}</div>
            `;

            const opts = q.options || [];
            opts.forEach((opt, oi) => {{
                const el = document.createElement('div');
                el.className = 'opt';
                el.id = 'opt-' + qi + '-' + oi;
                el.innerHTML = `<span class='opt-letter'>${{letters[oi] || oi}}</span><span>${{opt}}</span>`;
                el.onclick = () => {{
                    if (submitted) return;
                    card.querySelectorAll('.opt').forEach(o => o.classList.remove('selected'));
                    el.classList.add('selected');
                    answers[qi] = oi.toString();
                }};
                card.appendChild(el);
            }});

            // Feedback box (hidden until submit)
            const fb = document.createElement('div');
            fb.className = 'feedback-box';
            fb.id = 'fb-' + qi;
            const feedbackText = (q.feedback || '').replace(/<[^>]*>/g, '');
            if (feedbackText) {{
                fb.innerHTML = `<div class='feedback-label'>💡 Explanation</div>${{feedbackText}}`;
            }}
            card.appendChild(fb);

            container.appendChild(card);
        }});

        function submitQuiz() {{
            if (submitted) return;
            submitted = true;

            document.getElementById('btnSubmit').disabled = true;
            document.getElementById('btnSubmit').textContent = 'Submitted ✓';

            let correct = 0;
            questions.forEach((q, qi) => {{
                const userAns = answers[qi];
                const correctList = q.correct_response || [];
                const isCorrect = correctList.includes(userAns);

                const card = document.getElementById('qcard-' + qi);
                card.classList.add(isCorrect ? 'correct-card' : 'wrong-card');

                // Highlight correct answer(s) green
                correctList.forEach(cr => {{
                    const cEl = document.getElementById('opt-' + qi + '-' + cr);
                    if (cEl) {{ cEl.classList.remove('selected'); cEl.classList.add('correct-opt'); cEl.querySelector('.opt-letter').textContent = '✅'; }}
                }});

                // Mark user's wrong answer red
                if (userAns !== undefined && !correctList.includes(userAns)) {{
                    const wEl = document.getElementById('opt-' + qi + '-' + userAns);
                    if (wEl) {{ wEl.classList.add('wrong-opt'); wEl.querySelector('.opt-letter').textContent = '❌'; }}
                }}

                // Show feedback
                const fb = document.getElementById('fb-' + qi);
                if (fb && fb.innerHTML.trim()) fb.classList.add('show');

                if (isCorrect) correct++;
            }});

            // Score display
            const pct = questions.length > 0 ? Math.round(correct / questions.length * 100) : 0;
            const scoreCard = document.getElementById('scoreCard');
            scoreCard.classList.add('show');
            document.getElementById('scoreNum').textContent = pct + '%';
            document.getElementById('scoreLabel').textContent = correct + ' / ' + questions.length + ' correct';

            let msg = '';
            if (pct === 100) msg = '🎉 Perfect score! Excellent work!';
            else if (pct >= 80) msg = '✅ Great job! You know this material well.';
            else if (pct >= 60) msg = '📚 Good effort! Review the highlighted questions.';
            else msg = '💪 Keep practicing — you\'ll get it!';
            document.getElementById('scoreMsg').textContent = msg;
            document.getElementById('scoreMsg').style.color = pct >= 80 ? '#4EDEA3' : '#ffb347';

            scoreCard.scrollIntoView({{behavior: 'smooth', block: 'nearest'}});
        }}
    </script>
</body>
</html>";
            wvDocViewer.NavigateToString(html);
        }

        // ════════════════════════════ SCREEN 3: ME & STATISTICS ════════════════════════════

        private async void BtnMe_Click(object sender, RoutedEventArgs e)
        {
            StopVlcPlayback();
            PlayerGrid.Visibility = Visibility.Collapsed;
            MeGrid.Visibility = Visibility.Visible;

            await PopulateMeStatsDashboardAsync();
        }

        private void BtnBackFromMe_Click(object sender, RoutedEventArgs e)
        {
            MeGrid.Visibility = Visibility.Collapsed;
            PlayerGrid.Visibility = Visibility.Visible;
        }

        private async Task PopulateMeStatsDashboardAsync()
        {
            spMeCoursesProgress.Children.Clear();

            if (!Directory.Exists(_downloadsRoot)) return;

            var folders = Directory.GetDirectories(_downloadsRoot);
            int totalStarted = 0;
            int totalCompleted = 0;

            int overallLecturesTotal = 0;
            int overallLecturesCompleted = 0;

            foreach (var folder in folders)
            {
                try
                {
                    JObject courseData = null;
                    string jsonPath = Path.Combine(folder, "course_data.json");
                    string playerHtmlPath = Path.Combine(folder, "player.html");
                    string playerExePath = Path.Combine(folder, "player.exe");

                    if (File.Exists(jsonPath))
                    {
                        string rawJson = await File.ReadAllTextAsync(jsonPath);
                        courseData = JObject.Parse(rawJson);
                    }
                    else if (File.Exists(playerHtmlPath))
                    {
                        courseData = ExtractJsonFromPlayerHtml(playerHtmlPath);
                    }
                    else if (File.Exists(playerExePath))
                    {
                        courseData = ExtractJsonFromPlayerExe(playerExePath);
                    }
                    else
                    {
                        courseData = GenerateCurriculumFromFolderStructure(folder);
                    }

                    if (courseData != null)
                    {
                        string title = courseData["course_title"]?.ToString() ?? Path.GetFileName(folder);
                        
                        // Load progress.db for stats
                        var progress = CourseDatabaseManager.LoadProgress(folder);
                        var completedList = progress["completed_lectures"] as JArray ?? new JArray();
                        
                        int total = 0;
                        int completed = 0;

                        var sections = courseData["sections"] as JArray;
                        if (sections != null)
                        {
                            foreach (var s in sections)
                            {
                                var lectures = s["lectures"] as JArray;
                                if (lectures != null)
                                {
                                    foreach (var l in lectures)
                                    {
                                        total++;
                                        if (l["is_completed"] != null && (bool)l["is_completed"])
                                            completed++;
                                    }
                                }
                            }
                        }

                        if (total > 0)
                        {
                            totalStarted++;
                            if (completed == total) totalCompleted++;

                            overallLecturesTotal += total;
                            overallLecturesCompleted += completed;

                            // Add course progress element
                            var itemStack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

                            var gridText = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                            gridText.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            gridText.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

                            var txtTitle = new TextBlock { Text = title, Foreground = System.Windows.Media.Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                            Grid.SetColumn(txtTitle, 0);
                            gridText.Children.Add(txtTitle);

                            double pct = (double)completed / total * 100;
                            var txtPct = new TextBlock { Text = $"{completed}/{total} ({Math.Round(pct)}%)", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)), FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
                            Grid.SetColumn(txtPct, 1);
                            gridText.Children.Add(txtPct);

                            var prg = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = pct, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)), BorderThickness = new Thickness(0) };

                            itemStack.Children.Add(gridText);
                            itemStack.Children.Add(prg);
                            spMeCoursesProgress.Children.Add(itemStack);
                        }
                    }
                }
                catch { }
            }

            // Bind overall Stats cards
            lblMeStartedCount.Text = totalStarted.ToString();
            lblMeCompletedCount.Text = totalCompleted.ToString();

            lblMeTotalLecturesText.Text = $"{overallLecturesCompleted}/{overallLecturesTotal}";
            double overallPct = overallLecturesTotal > 0 ? ((double)overallLecturesCompleted / overallLecturesTotal * 100) : 0;
            barMeOverallProgress.Value = overallPct;
            lblMeOverallPct.Text = $"{Math.Round(overallPct)}% Completed";
        }

        // ════════════════════════════ ATTACHMENT / EXTRACTION HELPERS ════════════════════════════

        private void AttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is JToken att)
            {
                string attName = att["filename"]?.ToString() ?? "Attachment";
                string relPath = att["local_path"]?.ToString() ?? "";
                OpenAttachment(attName, relPath);
            }
        }

        private void OpenAttachment(string filename, string relativePath)
        {
            StopVlcPlayback();
            HideAllViewPanels();
            lblActiveLectureTitle.Text = filename;
            icoLectureType.Symbol = Wpf.Ui.Controls.SymbolRegular.Document24;
            btnMarkComplete.Visibility = Visibility.Collapsed;

            string ext = Path.GetExtension(filename).ToLower();
            string fullPath = Path.Combine(_activeCourseDir, relativePath);

            if (!File.Exists(fullPath))
            {
                MessageBox.Show("The attachment file could not be found locally.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ext == ".pdf")
            {
                panelWeb.Visibility = Visibility.Visible;
                wvDocViewer.Source = new Uri($"https://udemykicker.local/{relativePath}");
            }
            else if (ext == ".html" || ext == ".htm")
            {
                panelWeb.Visibility = Visibility.Visible;
                wvDocViewer.Source = new Uri($"https://udemykicker.local/{relativePath}");
            }
            else if (ext == ".txt" || ext == ".log" || ext == ".ini" || ext == ".json" || ext == ".py" || ext == ".cs" || ext == ".js")
            {
                try
                {
                    panelWeb.Visibility = Visibility.Visible;
                    string fileContent = File.ReadAllText(fullPath);
                    string escapedContent = System.Net.WebUtility.HtmlEncode(fileContent);

                    string html = $@"
<!DOCTYPE html>
<html>
<head>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@400;600&family=JetBrains+Mono:wght@400;500&display=swap' rel='stylesheet'>
    <style>
        body {{
            font-family: 'Outfit', 'Segoe UI', sans-serif;
            background: #020805;
            color: #E5E2E3;
            padding: 30px;
            line-height: 1.7;
        }}
        h2 {{ color: #4EDEA3; font-weight: 700; margin-bottom: 20px; }}
        pre {{
            background: #111B17;
            border: 1px solid rgba(78,222,163,0.18);
            padding: 16px;
            border-radius: 8px;
            overflow-x: auto;
            white-space: pre-wrap;
            word-wrap: break-word;
            font-family: 'JetBrains Mono', Consolas, monospace;
            font-size: 13px;
            color: #D5B8FF;
        }}
    </style>
</head>
<body>
    <h2>{filename}</h2>
    <pre>{escapedContent}</pre>
</body>
</html>";
                    wvDocViewer.NavigateToString(html);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not read text file: {ex.Message}");
                }
            }
            else if (ext == ".zip" || ext == ".rar")
            {
                panelZip.Visibility = Visibility.Visible;
                spZipContents.Children.Clear();

                if (ext == ".zip")
                {
                    try
                    {
                        using (ZipArchive archive = ZipFile.OpenRead(fullPath))
                        {
                            // Add an Extract All button at top
                            var btnExtractAll = new Button
                            {
                                Content = "Extract All Files (استخراج الكل)",
                                Padding = new Thickness(14, 8, 14, 8),
                                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)),
                                Foreground = System.Windows.Media.Brushes.Black,
                                FontWeight = FontWeights.Bold,
                                BorderThickness = new Thickness(0),
                                Margin = new Thickness(0, 0, 0, 16),
                                HorizontalAlignment = HorizontalAlignment.Left
                            };
                            btnExtractAll.Click += (s, e) => ExtractAllZip(fullPath);
                            spZipContents.Children.Add(btnExtractAll);

                            foreach (ZipArchiveEntry entry in archive.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name)) continue; // skip folders

                                var gridRow = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                                gridRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                                gridRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                                gridRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                                var txtName = new TextBlock { Text = entry.FullName, Foreground = System.Windows.Media.Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                                Grid.SetColumn(txtName, 0);
                                gridRow.Children.Add(txtName);

                                double sizeKb = (double)entry.Length / 1024;
                                string sizeStr = sizeKb > 1024 ? $"{(sizeKb / 1024):0.0} MB" : $"{sizeKb:0.0} KB";
                                var txtSize = new TextBlock { Text = sizeStr, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(186, 204, 176)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                                Grid.SetColumn(txtSize, 1);
                                gridRow.Children.Add(txtSize);

                                var btnExtract = new Button
                                {
                                    Content = "Extract File",
                                    Padding = new Thickness(8, 4, 8, 4),
                                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 78, 222, 163)),
                                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)),
                                    BorderThickness = new Thickness(0),
                                    Margin = new Thickness(10, 0, 0, 0)
                                };
                                string entryFullName = entry.FullName;
                                btnExtract.Click += (s, e) => ExtractZipEntry(fullPath, entryFullName);
                                Grid.SetColumn(btnExtract, 2);
                                gridRow.Children.Add(btnExtract);

                                spZipContents.Children.Add(gridRow);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var txtErr = new TextBlock { Text = $"Failed to read ZIP: {ex.Message}", Foreground = System.Windows.Media.Brushes.Red };
                        spZipContents.Children.Add(txtErr);
                    }
                }
                else
                {
                    // RAR fallback: show extract button for RAR
                    var txtRar = new TextBlock { Text = "RAR archive browsing is not supported natively. You can open the archive folder to extract it using WinRAR.", Foreground = System.Windows.Media.Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 16) };
                    var btnOpenFolder = new Button { Content = "Open Folder containing RAR", Padding = new Thickness(12, 6, 12, 6) };
                    btnOpenFolder.Click += (s, e) =>
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                    };
                    spZipContents.Children.Add(txtRar);
                    spZipContents.Children.Add(btnOpenFolder);
                }
            }
            else
            {
                // Open all other files in default system handler (Images, code files, docx etc)
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
                    panelDefault.Visibility = Visibility.Visible;
                    lblActiveLectureTitle.Text = "Opening file in external application...";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open file: {ex.Message}");
                }
            }
        }

        private void ExtractZipEntry(string zipPath, string entryName)
        {
            try
            {
                string extractDir = Path.Combine(Path.GetDirectoryName(zipPath), Path.GetFileNameWithoutExtension(zipPath) + "_extracted");
                Directory.CreateDirectory(extractDir);

                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.GetEntry(entryName);
                    if (entry != null)
                    {
                        string targetFile = Path.Combine(extractDir, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
                        entry.ExtractToFile(targetFile, true);

                        MessageBox.Show($"File extracted successfully to:\n{targetFile}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetFile}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Extraction failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExtractAllZip(string zipPath)
        {
            try
            {
                string extractDir = Path.Combine(Path.GetDirectoryName(zipPath), Path.GetFileNameWithoutExtension(zipPath) + "_extracted");
                ZipFile.ExtractToDirectory(zipPath, extractDir, true);

                MessageBox.Show($"All files extracted successfully to:\n{extractDir}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start("explorer.exe", extractDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Extraction failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════ LECTURE NOTES INTEGRATION ════════════════════════════

        private void PopulateLectureNotesList()
        {
            spLectureNotes.Children.Clear();
            if (_activeLectureToken == null) return;

            var notes = _activeLectureToken["notes"] as JArray;
            if (notes == null || notes.Count == 0)
            {
                var noNotesTxt = new TextBlock
                {
                    Text = "No notes added yet. Type a note on the left to save key moments!",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 187, 202, 191)),
                    FontSize = 12,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                spLectureNotes.Children.Add(noNotesTxt);
                return;
            }

            var sortedNotes = notes.OrderBy(n => (long)(n["time_ms"] ?? 0)).ToList();

            foreach (var note in sortedNotes)
            {
                long timeMs = (long)(note["time_ms"] ?? 0);
                string plainText = note["text"]?.ToString() ?? "";
                string xamlText = note["xaml"]?.ToString() ?? "";

                // Outer card border for each note
                var cardBorder = new Border
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(8),
                    Background = new SolidColorBrush(Color.FromRgb(10, 20, 15)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 78, 222, 163)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };

                var cardGrid = new Grid();
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header (time + delete)
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Note content

                // Header row
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Timestamp button
                var btnTime = new Button
                {
                    Content = FormatVlcTime(timeMs),
                    Background = new SolidColorBrush(Color.FromArgb(20, 78, 222, 163)),
                    Foreground = new SolidColorBrush(Color.FromRgb(78, 222, 163)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(78, 222, 163)),
                    BorderThickness = new Thickness(1),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                btnTime.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3)) } });
                btnTime.Click += (s, ev) =>
                {
                    if (_mediaPlayer != null) _mediaPlayer.Time = timeMs;
                };
                Grid.SetColumn(btnTime, 0);
                headerGrid.Children.Add(btnTime);

                // Delete button
                var btnDelete = new Button
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Delete Note"
                };
                var delIcon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24, Foreground = Brushes.Red, FontSize = 12 };
                btnDelete.Content = delIcon;
                JToken capturedNote = note;
                btnDelete.Click += (s, ev) =>
                {
                    notes.Remove(capturedNote);
                    SaveProgressToDb();
                    PopulateLectureNotesList();
                };
                Grid.SetColumn(btnDelete, 1);
                headerGrid.Children.Add(btnDelete);

                Grid.SetRow(headerGrid, 0);
                cardGrid.Children.Add(headerGrid);

                // Note content: use RichTextBox in read-only mode to show formatted text
                var rtbDisplay = new RichTextBox
                {
                    IsReadOnly = true,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Padding = new Thickness(0),
                    IsDocumentEnabled = true,
                    Focusable = false,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                // Load rich xaml if available, else fallback to plain text
                if (!string.IsNullOrEmpty(xamlText))
                {
                    LoadXamlIntoRichTextBox(rtbDisplay, xamlText);
                }
                else
                {
                    rtbDisplay.Document.Blocks.Clear();
                    rtbDisplay.Document.Blocks.Add(new Paragraph(new Run(plainText)));
                }

                Grid.SetRow(rtbDisplay, 1);
                cardGrid.Children.Add(rtbDisplay);

                cardBorder.Child = cardGrid;
                spLectureNotes.Children.Add(cardBorder);
            }
        }

        private void BtnSaveNote_Click(object sender, RoutedEventArgs e)
        {
            if (_activeLectureToken == null || _mediaPlayer == null) return;

            // Extract plain text + XAML rich text from RichTextBox
            string plainText = GetRichTextBoxPlainText(rtbNoteText).Trim();
            if (string.IsNullOrWhiteSpace(plainText)) return;

            // Save as XAML so formatting is preserved
            string xamlText = GetRichTextBoxXaml(rtbNoteText);

            long timeMs = _mediaPlayer.Time;

            var notes = _activeLectureToken["notes"] as JArray;
            if (notes == null)
            {
                notes = new JArray();
                _activeLectureToken["notes"] = notes;
            }

            var noteObj = new JObject();
            noteObj["time_ms"] = timeMs;
            noteObj["text"] = plainText;   // plain text for display/search
            noteObj["xaml"] = xamlText;    // rich XAML for display in notes list
            notes.Add(noteObj);

            SaveProgressToDb();

            // Clear the RichTextBox
            rtbNoteText.Document.Blocks.Clear();
            rtbNoteText.Document.Blocks.Add(new Paragraph());

            PopulateLectureNotesList();
        }

        // ---- RichTextBox Helpers ----

        private string GetRichTextBoxPlainText(RichTextBox rtb)
        {
            if (rtb?.Document == null) return "";
            var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            return range.Text ?? "";
        }

        private string GetRichTextBoxXaml(RichTextBox rtb)
        {
            if (rtb?.Document == null) return "";
            try
            {
                var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                using var ms = new System.IO.MemoryStream();
                range.Save(ms, System.Windows.DataFormats.Xaml);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch { return ""; }
        }

        private void LoadXamlIntoRichTextBox(RichTextBox rtb, string xaml)
        {
            if (rtb?.Document == null || string.IsNullOrEmpty(xaml)) return;
            try
            {
                var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xaml));
                range.Load(ms, System.Windows.DataFormats.Xaml);
            }
            catch { }
        }

        private void PlayNextLecture()
        {
            if (_activeLectureToken == null || _activeCourseJson == null) return;

            var allLectures = new List<JToken>();
            var sections = _activeCourseJson["sections"] as JArray;
            if (sections != null)
            {
                foreach (var s in sections)
                {
                    var lectures = s["lectures"] as JArray;
                    if (lectures != null)
                    {
                        foreach (var l in lectures)
                        {
                            allLectures.Add(l);
                        }
                    }
                }
            }

            int currentIdx = -1;
            string currentId = _activeLectureToken["id"]?.ToString();
            string currentPath = _activeLectureToken["local_video_path"]?.ToString();

            for (int i = 0; i < allLectures.Count; i++)
            {
                string itemId = allLectures[i]["id"]?.ToString();
                string itemPath = allLectures[i]["local_video_path"]?.ToString();

                if ((!string.IsNullOrEmpty(currentId) && itemId == currentId) || 
                    (!string.IsNullOrEmpty(currentPath) && itemPath == currentPath))
                {
                    currentIdx = i;
                    break;
                }
            }

            if (currentIdx != -1 && currentIdx + 1 < allLectures.Count)
            {
                var nextLec = allLectures[currentIdx + 1];
                PlayLecture(nextLec);
            }
            else
            {
                MessageBox.Show("Congratulations! You have completed the last lecture in this course! 🎉\n(تهانينا! لقد أكملت المحاضرة الأخيرة في الكورس!)", "Course Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ════════════════════════════ TEXT FORMATTING (WYSIWYG via RichTextBox) ════════════════════════════

        private void BtnFormatBold_Click(object sender, RoutedEventArgs e)
        {
            ApplyRichTextFormatting(rtbNoteText, TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
        }

        private void BtnFormatItalic_Click(object sender, RoutedEventArgs e)
        {
            ApplyRichTextFormatting(rtbNoteText, TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);
        }

        private void BtnFormatCode_Click(object sender, RoutedEventArgs e)
        {
            // Apply code style: monospace font + distinct background  
            if (rtbNoteText == null) return;
            var selection = rtbNoteText.Selection;
            if (selection.IsEmpty) return;

            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Consolas"));
            selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(213, 184, 255)));
            selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(25, 25, 25)));
            rtbNoteText.Focus();
        }

        private void ApplyRichTextFormatting<T>(RichTextBox rtb, DependencyProperty property, T applyValue, T defaultValue)
        {
            if (rtb == null) return;
            var selection = rtb.Selection;
            if (selection.IsEmpty) return;

            // Check current value and toggle
            object current = selection.GetPropertyValue(property);
            bool isApplied = current != DependencyProperty.UnsetValue && current.Equals(applyValue);

            selection.ApplyPropertyValue(property, isApplied ? (object)defaultValue : applyValue);
            rtb.Focus();
        }

        private void ParseMarkdownToInlines(TextBlock textBlock, string markdown)
        {
            textBlock.Inlines.Clear();
            if (string.IsNullOrEmpty(markdown))
            {
                textBlock.Text = "";
                return;
            }
            // Fallback: just show plain text. Formatting is now in xaml field.
            textBlock.Text = markdown;
        }
    }
}

namespace UdemyKickerWPF.Views
{
    public static class CourseDatabaseManager
    {
        private static readonly object LockObj = new object();

        public static JObject LoadProgress(string courseDir)
        {
            lock (LockObj)
            {
                string dbPath = Path.Combine(courseDir, "progress.db");
                try
                {
                    if (File.Exists(dbPath))
                    {
                        string json = File.ReadAllText(dbPath, Encoding.UTF8);
                        return JObject.Parse(json);
                    }
                }
                catch { }

                var doc = new JObject();
                doc["completed_lectures"] = new JArray();
                doc["lecture_notes"] = new JObject();
                return doc;
            }
        }

        public static void SaveProgress(string courseDir, JObject progressDoc)
        {
            lock (LockObj)
            {
                string dbPath = Path.Combine(courseDir, "progress.db");
                try
                {
                    File.WriteAllText(dbPath, progressDoc.ToString(Formatting.Indented), Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
