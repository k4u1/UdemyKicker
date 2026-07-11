using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using UdemyKicker;

namespace UdemyKickerWPF.Views
{
    public class SubtitleItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string LocaleId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string LatestLabel { get; set; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class SubtitleSelectionWindow : Wpf.Ui.Controls.FluentWindow
    {
        public List<string> SelectedLocales { get; private set; } = new List<string>();
        private List<SubtitleItem> _items = new List<SubtitleItem>();
        private string _courseId;
        private bool _isUpdatingAll = false;

        public SubtitleSelectionWindow(string courseId, string courseTitle, List<SubtitleItem> availableItems)
        {
            InitializeComponent();
            _courseId = courseId;
            lblCourseTitle.Text = courseTitle;

            var history = CourseSubtitlesHistory.GetLastChosen(courseId);
            bool hasHistory = history != null && history.Count > 0;

            string defaultLang = SettingsManager.Current.SubtitleLanguage?.Trim().ToLower() ?? "all";

            foreach (var item in availableItems)
            {
                if (hasHistory)
                {
                    if (history.Contains(item.LocaleId))
                    {
                        item.IsSelected = true;
                        item.LatestLabel = " latest chosen";
                    }
                }
                else
                {
                    if (defaultLang == "all")
                    {
                        item.IsSelected = true;
                    }
                    else
                    {
                        string[] defaultLangs = defaultLang.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                         .Select(l => l.Trim())
                                                         .ToArray();
                        item.IsSelected = defaultLangs.Any(lang => item.LocaleId == lang || item.LocaleId.StartsWith(lang + "_") || item.LocaleId.StartsWith(lang + "-"));
                    }
                }
                _items.Add(item);
            }

            lstLanguages.ItemsSource = _items;
            UpdateSelectAllState();
        }

        private void ChkSelectAll_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingAll) return;
            _isUpdatingAll = true;
            foreach (var item in _items)
            {
                item.IsSelected = true;
            }
            _isUpdatingAll = false;
        }

        private void ChkSelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingAll) return;
            _isUpdatingAll = true;
            foreach (var item in _items)
            {
                item.IsSelected = false;
            }
            _isUpdatingAll = false;
        }

        private void SubtitleCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSelectAllState();
        }

        private void UpdateSelectAllState()
        {
            if (_isUpdatingAll) return;
            _isUpdatingAll = true;
            chkSelectAll.IsChecked = _items.All(i => i.IsSelected) ? true : (_items.Any(i => i.IsSelected) ? (bool?)null : false);
            _isUpdatingAll = false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedLocales = _items.Where(i => i.IsSelected).Select(i => i.LocaleId).ToList();
            if (SelectedLocales.Count == 0)
            {
                MessageBox.Show("Please select at least one subtitle language or cancel.", "Select Subtitles", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CourseSubtitlesHistory.SaveChosen(_courseId, SelectedLocales);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
