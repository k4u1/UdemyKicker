using System;

using System.Collections.Generic;

using System.ComponentModel;

using System.Linq;

using System.Windows;

using System.Windows.Controls;

using UdemyKicker;



namespace UdemyKickerWPF.Views

{

    public partial class ContentSelectionWindow : Wpf.Ui.Controls.FluentWindow

    {

        public HashSet<int> SelectedLectureIds { get; private set; } = new HashSet<int>();

        private List<ChapterItem> chaptersList = new List<ChapterItem>();

        private bool isBulkUpdating = false;



        public ContentSelectionWindow(string courseTitle, List<UdemyCurriculumItem> curriculum)

        {

            InitializeComponent();

            lblCourseTitle.Text = courseTitle;

            LoadCurriculum(curriculum);

            UpdateSelectAllButtonLabel();

        }



        private void LoadCurriculum(List<UdemyCurriculumItem> curriculum)

        {

            ChapterItem currentChapter = null;

            int chapterIndex = 1;

            int lectureIndex = 1;



            bool attachmentsOnly = SettingsManager.Current.DownloadAttachmentsOnly;



            foreach (var item in curriculum)

            {

                if (item._class == "chapter")

                {

                    currentChapter = new ChapterItem

                    {

                        Title = $"{chapterIndex}. {item.title}"

                    };

                    chaptersList.Add(currentChapter);

                    chapterIndex++;

                    lectureIndex = 1;

                }

                else if (attachmentsOnly)

                {

                    // If we are downloading attachments only, we ONLY add attachments!

                    if (item._class == "lecture" && item.supplementary_assets != null && item.supplementary_assets.Count > 0)

                    {

                        if (currentChapter == null)

                        {

                            currentChapter = new ChapterItem

                            {

                                Title = $"{chapterIndex}. General Lectures"

                            };

                            chaptersList.Add(currentChapter);

                            chapterIndex++;

                        }



                        foreach (var att in item.supplementary_assets)

                        {

                            string safeAttName = att.filename ?? att.title ?? "Attachment";

                            string ext = System.IO.Path.GetExtension(safeAttName).ToUpper().Replace(".", "");

                            if (string.IsNullOrEmpty(ext)) ext = "File";



                            var lectureItem = new LectureItem

                            {

                                Id = att.id,

                                IndexStr = $"{chapterIndex - 1}.{lectureIndex}",

                                Title = $"{item.title} - [Attachment: {safeAttName}]",

                                Type = ext,

                                Parent = currentChapter

                            };

                            currentChapter.Lectures.Add(lectureItem);

                            lectureIndex++;

                        }

                    }

                }

                else if ((item._class == "lecture" && item.asset != null) || item._class == "quiz")

                {

                    if (currentChapter == null)

                    {

                        currentChapter = new ChapterItem

                        {

                            Title = $"{chapterIndex}. General Lectures"

                        };

                        chaptersList.Add(currentChapter);

                        chapterIndex++;

                    }



                    string assetType = item.asset?.asset_type?.ToLower() ?? "quiz";

                    string typeLabel = assetType == "quiz" ? "Quiz" : (string.IsNullOrEmpty(item.asset.media_license_token) ? "Normal" : "Protected");



                    var lectureItem = new LectureItem

                    {

                        Id = item.id,

                        IndexStr = $"{chapterIndex - 1}.{lectureIndex}",

                        Title = item.title,

                        Type = typeLabel,

                        Parent = currentChapter

                    };

                    currentChapter.Lectures.Add(lectureItem);

                    lectureIndex++;

                }

            }



            // Remove any chapters that ended up with 0 items (e.g. they had no attachments)

            chaptersList.RemoveAll(ch => ch.Lectures.Count == 0);



            lstChapters.ItemsSource = chaptersList;

            UpdateSelectedCount();

        }



        private void ChapterCheckbox_Changed(object sender, RoutedEventArgs e)

        {

            if (isBulkUpdating) return;

            UpdateSelectAllButtonLabel();

        }



        private void LectureCheckbox_Changed(object sender, RoutedEventArgs e)

        {

            if (isBulkUpdating) return;

            UpdateSelectAllButtonLabel();

        }



        private void BtnToggleSelectAll_Click(object sender, RoutedEventArgs e)

        {

            isBulkUpdating = true;

            bool targetState = btnToggleSelectAll.Content.ToString() == "Select All";



            foreach (var ch in chaptersList)

            {

                ch.IsSelected = targetState;

            }



            isBulkUpdating = false;

            UpdateSelectAllButtonLabel();

        }



        private void UpdateSelectedCount()

        {

            int count = chaptersList.Sum(ch => ch.Lectures.Count(l => l.IsSelected));

            lblSelectedCount.Text = count.ToString();

        }



        private void UpdateSelectAllButtonLabel()

        {

            bool anyUnselected = chaptersList.Any(ch => ch.Lectures.Any(l => !l.IsSelected));

            btnToggleSelectAll.Content = anyUnselected ? "Select All" : "Deselect All";

            UpdateSelectedCount();

        }



        private void BtnCancel_Click(object sender, RoutedEventArgs e)

        {

            DialogResult = false;

            Close();

        }



        private void BtnStart_Click(object sender, RoutedEventArgs e)

        {

            SelectedLectureIds.Clear();

            foreach (var ch in chaptersList)

            {

                foreach (var l in ch.Lectures)

                {

                    if (l.IsSelected)

                    {

                        SelectedLectureIds.Add(l.Id);

                    }

                }

            }



            if (SelectedLectureIds.Count == 0)

            {

                MessageBox.Show("Please select at least one lesson to download.", "No Lessons Selected", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;

            }



            DialogResult = true;

            Close();

        }

    }



    public class ChapterItem : INotifyPropertyChanged

    {

        public string Title { get; set; }

        public List<LectureItem> Lectures { get; set; } = new List<LectureItem>();



        private bool? _isSelected = true;

        public bool? IsSelected

        {

            get => _isSelected;

            set

            {

                if (_isSelected != value)

                {

                    _isSelected = value;

                    OnPropertyChanged(nameof(IsSelected));



                    if (value.HasValue)

                    {

                        foreach (var lecture in Lectures)

                        {

                            lecture.SetSelectedWithoutNotification(value.Value);

                        }

                    }

                }

            }

        }



        public void CheckState()

        {

            bool allSelected = true;

            bool noneSelected = true;



            foreach (var l in Lectures)

            {

                if (l.IsSelected) noneSelected = false;

                else allSelected = false;

            }



            if (allSelected) _isSelected = true;

            else if (noneSelected) _isSelected = false;

            else _isSelected = null;



            OnPropertyChanged(nameof(IsSelected));

        }



        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    }



    public class LectureItem : INotifyPropertyChanged

    {

        public int Id { get; set; }

        public string IndexStr { get; set; }

        public string Title { get; set; }

        public string Type { get; set; }

        public ChapterItem Parent { get; set; }



        private bool _isSelected = true;

        public bool IsSelected

        {

            get => _isSelected;

            set

            {

                if (_isSelected != value)

                {

                    _isSelected = value;

                    OnPropertyChanged(nameof(IsSelected));

                    Parent?.CheckState();

                }

            }

        }



        public void SetSelectedWithoutNotification(bool val)

        {

            if (_isSelected != val)

            {

                _isSelected = val;

                OnPropertyChanged(nameof(IsSelected));

            }

        }



        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    }

}