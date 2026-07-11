using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UdemyKickerWPF.Views
{
    public partial class LoggerView : Page
    {
        public LoggerView()
        {
            InitializeComponent();
        }

        public void LogInfo(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLogs.AppendText($"[{System.DateTime.Now:HH:mm:ss}] {message}\n");
                txtLogs.ScrollToEnd();
            });
        }

        private void BtnClear_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            txtLogs.Clear();
        }

        private void BtnSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Simple save log functionality
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                DefaultExt = ".txt"
            };
            if (dialog.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(dialog.FileName, txtLogs.Text);
            }
        }
    }
}
