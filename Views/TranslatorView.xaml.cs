using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Net.Http;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UdemyKickerWPF.Views
{
    public partial class TranslatorView : Page
    {
        private string? _selectedFilePath;
        private static readonly HttpClient _httpClient = new HttpClient();
        private Process? _activeProcess;
        private bool _isTranslating = false;
        private List<string> _detectedGpus = new List<string>();

        public TranslatorView()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _ = RunHardwareDiagnosticsAsync();
        }

        private async Task RunHardwareDiagnosticsAsync()
        {
            try
            {
                // 1. Get CPU Name from Registry (Zero-overhead)
                string cpuName = "Unknown CPU";
                try
                {
                    var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (cpuKey != null)
                    {
                        cpuName = cpuKey.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
                    }
                }
                catch { }

                // 2. Get GPU Name(s) using WMIC
                _detectedGpus.Clear();
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "wmic",
                        Arguments = "path win32_VideoController get Name",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var process = Process.Start(psi))
                    {
                        if (process != null)
                        {
                            string output = await process.StandardOutput.ReadToEndAsync();
                            await process.WaitForExitAsync();
                            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            var names = lines.Where(l => !string.IsNullOrWhiteSpace(l) && l.Trim() != "Name").Select(l => l.Trim()).ToList();
                            if (names.Count > 0)
                            {
                                _detectedGpus.AddRange(names);
                            }
                        }
                    }
                }
                catch { }

                // Display info on UI thread
                Dispatcher.Invoke(() =>
                {
                    lblCpuName.Text = cpuName;
                    lblGpuName.Text = _detectedGpus.Count > 0 ? string.Join(" & ", _detectedGpus) : "Standard Graphics Adapter";

                    // Populate Target Execution Device ComboBox
                    cmbTargetDevice.Items.Clear();
                    cmbTargetDevice.Items.Add("CPU (Software Mode)");
                    for (int i = 0; i < _detectedGpus.Count; i++)
                    {
                        cmbTargetDevice.Items.Add($"GPU {i}: {_detectedGpus[i]}");
                    }
                    cmbTargetDevice.SelectedIndex = 0; // Default to CPU

                    // Evaluate CPU Rating
                    string cpuLower = cpuName.ToLower();
                    string cpuRating = "Medium (Standard Speed)";
                    bool isExcellentCpu = false;
                    bool isGoodCpu = false;

                    if (cpuLower.Contains("i7") || cpuLower.Contains("i9") || cpuLower.Contains("ryzen 7") || cpuLower.Contains("ryzen 9") || cpuLower.Contains("xeon"))
                    {
                        cpuRating = "Excellent (Fast Core Performance)";
                        lblCpuRating.Foreground = System.Windows.Media.Brushes.LightGreen;
                        isExcellentCpu = true;
                    }
                    else if (cpuLower.Contains("i5") || cpuLower.Contains("ryzen 5"))
                    {
                        cpuRating = "Good (Stable Translation)";
                        lblCpuRating.Foreground = System.Windows.Media.Brushes.Teal;
                        isGoodCpu = true;
                    }
                    else
                    {
                        lblCpuRating.Foreground = System.Windows.Media.Brushes.Orange;
                    }
                    lblCpuRating.Text = cpuRating;

                    // Evaluate GPU & CUDA Compatibility
                    bool hasCuda = _detectedGpus.Any(g => {
                        string gl = g.ToLower();
                        return gl.Contains("nvidia") || gl.Contains("geforce") || gl.Contains("rtx") || gl.Contains("gtx") || gl.Contains("quadro");
                    });

                    if (hasCuda)
                    {
                        lblCudaSupport.Text = "Supported (Hardware Acceleration Active)";
                        lblCudaSupport.Foreground = System.Windows.Media.Brushes.LightGreen;
                        lblGpuRating.Text = "Excellent (CUDA Capable)";
                        lblGpuRating.Foreground = System.Windows.Media.Brushes.LightGreen;

                        // Set default selection to the CUDA GPU if found
                        for (int i = 0; i < _detectedGpus.Count; i++)
                        {
                            string gl = _detectedGpus[i].ToLower();
                            if (gl.Contains("nvidia") || gl.Contains("geforce") || gl.Contains("rtx") || gl.Contains("gtx") || gl.Contains("quadro"))
                            {
                                cmbTargetDevice.SelectedIndex = i + 1;
                                break;
                            }
                        }
                    }
                    else
                    {
                        lblCudaSupport.Text = "Unsupported (CPU Fallback Mode)";
                        lblCudaSupport.Foreground = System.Windows.Media.Brushes.Orange;
                        lblGpuRating.Text = "Integrated / Non-CUDA Adapter";
                        lblGpuRating.Foreground = System.Windows.Media.Brushes.Orange;
                    }

                    UpdateSpeedStats();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    lblCpuName.Text = "Error loading info";
                    lblGpuName.Text = "Error loading info";
                });
            }
        }

        private void UpdateSpeedStats()
        {
            if (cmbTranslateMethod == null || cmbTargetDevice == null || lblCpuName == null || lblEstimatedTime == null || lblSpeedIndex == null) return;

            if (cmbTranslateMethod.SelectedIndex == 1) // Online API
            {
                lblEstimatedTime.Text = "~0.15 seconds / sentence";
                lblSpeedIndex.Text = "API Direct (High Speed Network)";
                lblSpeedIndex.Foreground = System.Windows.Media.Brushes.LightGreen;
                return;
            }

            int deviceIndex = cmbTargetDevice.SelectedIndex;
            if (deviceIndex == 0) // CPU Selected
            {
                string cpuLower = lblCpuName.Text?.ToLower() ?? "";
                if (cpuLower.Contains("i7") || cpuLower.Contains("i9") || cpuLower.Contains("ryzen 7") || cpuLower.Contains("ryzen 9") || cpuLower.Contains("xeon"))
                {
                    lblEstimatedTime.Text = "1.5 ~ 2.2 seconds / sentence";
                    lblSpeedIndex.Text = "100% (Baseline)";
                    lblSpeedIndex.Foreground = System.Windows.Media.Brushes.Teal;
                }
                else if (cpuLower.Contains("i5") || cpuLower.Contains("ryzen 5"))
                {
                    lblEstimatedTime.Text = "2.8 ~ 4.0 seconds / sentence";
                    lblSpeedIndex.Text = "75% (Good CPU)";
                    lblSpeedIndex.Foreground = System.Windows.Media.Brushes.Teal;
                }
                else
                {
                    lblEstimatedTime.Text = "5.0 ~ 7.5 seconds / sentence";
                    lblSpeedIndex.Text = "35% (Slow CPU)";
                    lblSpeedIndex.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            else // GPU Selected
            {
                string selectedGpuName = cmbTargetDevice.SelectedItem?.ToString() ?? "";
                string gpuLower = selectedGpuName.ToLower();
                bool isNvidia = gpuLower.Contains("nvidia") || gpuLower.Contains("geforce") || gpuLower.Contains("rtx") || gpuLower.Contains("gtx") || gpuLower.Contains("quadro");

                if (isNvidia)
                {
                    lblEstimatedTime.Text = "0.2 ~ 0.5 seconds / sentence";
                    lblSpeedIndex.Text = "450% (CUDA Accelerated)";
                    lblSpeedIndex.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                else
                {
                    lblEstimatedTime.Text = "2.8 ~ 4.0 seconds / sentence";
                    lblSpeedIndex.Text = "75% (Non-CUDA GPU Fallback)";
                    lblSpeedIndex.Foreground = System.Windows.Media.Brushes.Teal;
                }
            }

            UpdateTranslationTimeEstimate();
        }

        private double GetCurrentSpeedFactor()
        {
            if (cmbTranslateMethod == null || cmbTargetDevice == null) return 2.0;

            if (cmbTranslateMethod.SelectedIndex == 1) // Online API
            {
                return 0.15;
            }

            int deviceIndex = cmbTargetDevice.SelectedIndex;
            if (deviceIndex == 0) // CPU
            {
                string cpuLower = lblCpuName?.Text?.ToLower() ?? "";
                if (cpuLower.Contains("i7") || cpuLower.Contains("i9") || cpuLower.Contains("ryzen 7") || cpuLower.Contains("ryzen 9") || cpuLower.Contains("xeon"))
                    return 2.0;
                if (cpuLower.Contains("i5") || cpuLower.Contains("ryzen 5"))
                    return 3.8;
                return 6.5;
            }
            else // GPU
            {
                string selectedGpu = cmbTargetDevice.SelectedItem?.ToString() ?? "";
                string gpuLower = selectedGpu.ToLower();
                if (gpuLower.Contains("nvidia") || gpuLower.Contains("geforce") || gpuLower.Contains("rtx") || gpuLower.Contains("gtx") || gpuLower.Contains("quadro"))
                {
                    return 0.35; // CUDA GPU speed
                }
                return 3.8; // Non-CUDA GPU (falls back to CPU speed)
            }
        }

        private void UpdateTranslationTimeEstimate()
        {
            if (lblFileEstDuration == null) return;
            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
            {
                lblFileEstDuration.Text = "~0s";
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(_selectedFilePath);
                int cuesCount = lines.Count(l => l.Contains("-->"));
                double speedFactor = GetCurrentSpeedFactor();
                double totalSeconds = cuesCount * speedFactor;

                if (totalSeconds < 60)
                {
                    lblFileEstDuration.Text = $"~{Math.Round(totalSeconds)} seconds";
                }
                else
                {
                    int mins = (int)(totalSeconds / 60);
                    int secs = (int)(totalSeconds % 60);
                    lblFileEstDuration.Text = $"~{mins}m {secs}s";
                }
            }
            catch
            {
                lblFileEstDuration.Text = "Unknown";
            }
        }

        private void CmbTargetDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSpeedStats();
        }

        private void CmbTranslateMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTranslateMethod == null || cmbTargetDevice == null) return;
            if (cmbTranslateMethod.SelectedIndex == 1) // Online API
            {
                cmbTargetDevice.IsEnabled = false;
            }
            else
            {
                cmbTargetDevice.IsEnabled = true;
            }
            UpdateSpeedStats();
        }

        #region Drag & Drop Events
        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropZoneOutline.Stroke = System.Windows.Media.Brushes.LightGreen;
                DropZone.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 78, 222, 163));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            DropZoneOutline.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(102, 78, 222, 163));
            DropZone.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(13, 78, 222, 163));
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZone_DragLeave(sender, e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    LoadFile(files[0]);
                }
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseForFile();
        }

        private void BrowseForFile()
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Subtitle Files (*.srt;*.vtt)|*.srt;*.vtt",
                Title = "Select Subtitle File"
            };
            if (dlg.ShowDialog() == true)
            {
                LoadFile(dlg.FileName);
            }
        }
        #endregion

        private void LoadFile(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            if (ext == ".srt" || ext == ".vtt")
            {
                _selectedFilePath = path;
                lblFileName.Text = Path.GetFileName(path);
                lblFilePath.Text = path;

                // Load Subtitle detailed statistics
                try
                {
                    long sizeBytes = new FileInfo(path).Length;
                    double sizeKb = sizeBytes / 1024.0;
                    lblFileSize.Text = $"{sizeKb:0.0} KB";
                    lblFileFormat.Text = ext == ".vtt" ? "WebVTT Subtitle" : "SRT Subtitle";

                    string[] lines = File.ReadAllLines(path);
                    int cuesCount = lines.Count(l => l.Contains("-->"));
                    lblFileCues.Text = $"{cuesCount} blocks";
                }
                catch (Exception ex)
                {
                    lblFileSize.Text = "Unknown";
                    lblFileFormat.Text = "Unknown";
                    lblFileCues.Text = "Unknown";
                }

                FilePanel.Visibility = Visibility.Visible;
                DropZone.Visibility = Visibility.Collapsed;

                txtConsole.Text = $"Loaded File: {Path.GetFileName(path)}\nReady to translate.";
                UpdateTranslationTimeEstimate();
            }
            else
            {
                MessageBox.Show("Please select a valid subtitle file (.srt or .vtt only)!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearFile_Click(object sender, RoutedEventArgs e)
        {
            _selectedFilePath = null;
            FilePanel.Visibility = Visibility.Collapsed;
            DropZone.Visibility = Visibility.Visible;
            pbProgress.Value = 0;
            lblProgressStatus.Text = "Ready";
            txtConsole.Text = "Ready to begin...";
        }

        private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranslating)
            {
                // Cancel Action
                CancelTranslation();
                return;
            }

            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
            {
                MessageBox.Show("Please select a subtitle file first!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isTranslating = true;
            btnTranslate.Content = "Cancel Translation";
            btnTranslate.Background = System.Windows.Media.Brushes.DarkRed;
            btnTranslate.Foreground = System.Windows.Media.Brushes.White;

            pbProgress.Value = 0;
            lblProgressStatus.Text = "Initializing Translation...";
            txtConsole.Clear();

            string inputPath = _selectedFilePath;
            string ext = Path.GetExtension(inputPath);
            string baseDir = Path.GetDirectoryName(inputPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(baseDir, $"{baseName}.ar{ext}");

            bool useLocal = cmbTranslateMethod.SelectedIndex == 0;

            if (useLocal)
            {
                _ = Task.Run(async () => await TranslateOfflineAsync(inputPath, outputPath));
            }
            else
            {
                _ = Task.Run(async () => await TranslateOnlineAsync(inputPath, outputPath));
            }
        }

        private void CancelTranslation()
        {
            LogToConsole("\n[Cancel] Translation execution cancelled by user.");
            try
            {
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill(true); // Terminate process tree
                }
            }
            catch (Exception ex)
            {
                LogToConsole($"[Warning] Failed to terminate translator process: {ex.Message}");
            }

            SetProgressStatus("Translation Cancelled", 0);
            ResetTranslateButtonState();
        }

        private void ResetTranslateButtonState()
        {
            Dispatcher.Invoke(() =>
            {
                _isTranslating = false;
                btnTranslate.Content = "Translate Now";
                btnTranslate.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163));
                btnTranslate.Foreground = System.Windows.Media.Brushes.Black;
                btnTranslate.IsEnabled = true;
            });
        }

        private async Task TranslateOfflineAsync(string inputPath, string outputPath)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string translatorExe = Path.Combine(baseDir, "translator.exe");

                // Locate model
                string qwenModelPath = Path.Combine(baseDir, "qwen-model");
                string hpltModelPath = Path.Combine(baseDir, "hplt-model");
                string nllbModelPath = Path.Combine(baseDir, "nllb-model");
                string modelPath = Directory.Exists(qwenModelPath) ? qwenModelPath : (Directory.Exists(hpltModelPath) ? hpltModelPath : nllbModelPath);

                if (!File.Exists(translatorExe))
                {
                    LogToConsole("Error: translator.exe was not found in the application directory!");
                    SetProgressStatus("Offline Translation Failed", 0);
                    ResetTranslateButtonState();
                    return;
                }

                if (!Directory.Exists(modelPath))
                {
                    LogToConsole("Error: No local translation model (hplt-model, qwen-model or nllb-model) folder was found!");
                    SetProgressStatus("Offline Translation Failed", 0);
                    ResetTranslateButtonState();
                    return;
                }

                LogToConsole($"Starting local translator: {Path.GetFileName(translatorExe)}");
                LogToConsole($"Using Model: {Path.GetFileName(modelPath)}");
                LogToConsole($"Input File: {inputPath}");
                LogToConsole($"Output File: {outputPath}\n");

                var startInfo = new ProcessStartInfo
                {
                    FileName = translatorExe,
                    Arguments = $"--input \"{inputPath}\" --output \"{outputPath}\" --model \"{modelPath}\" --src_lang \"eng_Latn\" --tgt_lang \"arb_Arab\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                // Apply targeted execution hardware
                int selectedDeviceIndex = 0;
                Dispatcher.Invoke(() => { selectedDeviceIndex = cmbTargetDevice.SelectedIndex; });

                if (selectedDeviceIndex == 0) // Force CPU
                {
                    startInfo.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "";
                    LogToConsole("Targeting Execution Device: CPU Mode (CUDA disabled)");
                }
                else // Select specific GPU
                {
                    int gpuIndex = selectedDeviceIndex - 1;
                    startInfo.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = gpuIndex.ToString();
                    LogToConsole($"Targeting Execution Device: GPU Index {gpuIndex} ({_detectedGpus[gpuIndex]})");
                }

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        _activeProcess = process;

                        process.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                LogToConsole("[Python Error] " + e.Data);
                            }
                        };
                        process.BeginErrorReadLine();

                        while (true)
                        {
                            string? line = await process.StandardOutput.ReadLineAsync();
                            if (line == null) break;

                            LogToConsole(line);

                            if (line.StartsWith("PROGRESS: "))
                            {
                                string pctStr = line.Substring(10).Trim();
                                if (double.TryParse(pctStr, out double translatePct))
                                {
                                    SetProgressStatus($"Translating Subtitles: {Math.Round(translatePct)}%", translatePct);
                                }
                            }
                        }

                        await process.WaitForExitAsync();
                        if (process.ExitCode == 0)
                        {
                            LogToConsole("\n✨ Translation completed successfully! Subtitle file saved beside the original.");
                            SetProgressStatus("Translation Completed Successfully", 100);
                        }
                        else
                        {
                            if (_isTranslating) // If not cancelled but exited with error
                            {
                                LogToConsole($"\n❌ Translator execution failed. Exit Code: {process.ExitCode}");
                                SetProgressStatus("Offline Translation Failed", 0);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isTranslating)
                {
                    LogToConsole($"Offline Translation Error: {ex.Message}");
                    SetProgressStatus("Error during Translation", 0);
                }
            }
            finally
            {
                ResetTranslateButtonState();
            }
        }

        private async Task TranslateOnlineAsync(string inputPath, string outputPath)
        {
            try
            {
                LogToConsole("Starting Google Translate API Online Translation...\n");
                LogToConsole($"Input File: {inputPath}");
                LogToConsole($"Output File: {outputPath}\n");

                if (!File.Exists(inputPath)) return;
                var lines = await File.ReadAllLinesAsync(inputPath);
                var translatedLines = new System.Collections.Generic.List<string>();

                int totalBlocks = lines.Count(l => l.Contains("-->"));
                int currentBlock = 0;

                var textBuffer = new System.Collections.Generic.List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!_isTranslating) return; // Checked if cancelled

                    string line = lines[i].Trim();

                    if (i == 0 && line.StartsWith("WEBVTT"))
                    {
                        translatedLines.Add(lines[i]);
                        continue;
                    }

                    bool isTiming = line.Contains("-->");
                    bool isIndex = int.TryParse(line, out _);
                    bool isEmpty = string.IsNullOrWhiteSpace(line);

                    if (isTiming || isIndex || isEmpty)
                    {
                        if (textBuffer.Count > 0)
                        {
                            string combinedText = string.Join(" ", textBuffer);
                            string translatedText = await TranslateTextGoogleAsync(combinedText, "en", "ar");
                            translatedLines.Add(translatedText);

                            LogToConsole($"[Original]: {combinedText}");
                            LogToConsole($"[Arabic]: {translatedText}\n");

                            textBuffer.Clear();

                            currentBlock++;
                            if (totalBlocks > 0)
                            {
                                double pct = (double)currentBlock / totalBlocks * 100;
                                SetProgressStatus($"Translating Online: {Math.Round(pct)}%", pct);
                            }
                        }
                        translatedLines.Add(lines[i]);
                    }
                    else
                    {
                        textBuffer.Add(lines[i]);
                    }
                }

                if (textBuffer.Count > 0 && _isTranslating)
                {
                    string combinedText = string.Join(" ", textBuffer);
                    string translatedText = await TranslateTextGoogleAsync(combinedText, "en", "ar");
                    translatedLines.Add(translatedText);

                    LogToConsole($"[Original]: {combinedText}");
                    LogToConsole($"[Arabic]: {translatedText}\n");

                    currentBlock++;
                    double pct = 100;
                    SetProgressStatus("Translating Online: 100%", pct);
                }

                if (_isTranslating)
                {
                    await File.WriteAllLinesAsync(outputPath, translatedLines);
                    LogToConsole("\n✨ Online translation completed successfully! Subtitle file saved beside the original.");
                    SetProgressStatus("Translation Completed Successfully", 100);
                }
            }
            catch (Exception ex)
            {
                if (_isTranslating)
                {
                    LogToConsole($"Online Translation Error: {ex.Message}");
                    SetProgressStatus("Online Translation Failed", 0);
                }
            }
            finally
            {
                ResetTranslateButtonState();
            }
        }

        private async Task<string> TranslateTextGoogleAsync(string text, string srcLang, string tgtLang)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={srcLang}&tl={tgtLang}&dt=t&q={Uri.EscapeDataString(text)}";
                string response = await _httpClient.GetStringAsync(url);

                var array = JArray.Parse(response);
                if (array != null && array.Count > 0 && array[0] != null)
                {
                    var segments = array[0];
                    StringBuilder sb = new StringBuilder();
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
                LogToConsole($"[Warning] Google Translate API error: {ex.Message}");
            }
            return text;
        }

        private void LogToConsole(string text)
        {
            Dispatcher.Invoke(() =>
            {
                txtConsole.AppendText(text + "\n");
                txtConsole.ScrollToEnd();
            });
        }

        private void SetProgressStatus(string status, double value)
        {
            Dispatcher.Invoke(() =>
            {
                lblProgressStatus.Text = status;
                pbProgress.Value = value;
            });
        }
    }
}
