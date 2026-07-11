using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using UdemyKicker;

namespace UdemyKickerWPF.Views
{
    public partial class SplashWindow : Wpf.Ui.Controls.FluentWindow
    {
        private DateTime _licenseExpirationLocal;
        private bool _isProceeding = false;
        private static string UserCachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_user.json");

        public SplashWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Task.Run(() => ToolExtractor.ExtractTools());

            lblStatus.Text = "Checking License...";
            bool isLicenseValid = await CheckLicenseAsync();
            if (!isLicenseValid)
            {
                Environment.Exit(0);
                return;
            }

            // License is valid, check if logged in (token exists)
            string tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_token.txt");
            if (!File.Exists(tokenPath))
            {
                ProceedToLogin();
                return;
            }

            // We are logged in, try loading cached user profile details to start instantly
            lblStatus.Text = "Loading User Profile...";
            UdemyUser? cachedUser = null;
            if (File.Exists(UserCachePath))
            {
                try
                {
                    string cachedJson = File.ReadAllText(UserCachePath);
                    cachedUser = Newtonsoft.Json.JsonConvert.DeserializeObject<UdemyUser>(cachedJson);
                }
                catch { }
            }

            if (cachedUser != null)
            {
                // We have a cached user profile, proceed to main instantly!
                ProceedToMain(cachedUser);
            }
            else
            {
                // No cached profile, fetch it from API
                await InitializeWebViewAndFetchData();
            }
        }

        private async Task<bool> CheckLicenseAsync()
        {
            DateTime localNetworkTime;
            try
            {
                localNetworkTime = await GetSafeNetworkTimeAsync();
            }
            catch (Exception)
            {
                System.Windows.MessageBox.Show(
                    "Network Time Verification failed. Please check your internet connection and try again.",
                    "Time Spoofing Protection",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error
                );
                return false;
            }

            var licenseManager = new UdemyKicker.LicenseManager();
            string savedKey = licenseManager.GetSavedKey();
            string clientHwid = licenseManager.GetHWID();

            if (string.IsNullOrEmpty(savedKey))
            {
                CopyHwidAndShowFailMessage(clientHwid, "No license key found.");
                return false;
            }

            var (isValid, message, expiryDate) = await licenseManager.ValidateKeyAsync(savedKey, localNetworkTime);

            if (isValid)
            {
                _licenseExpirationLocal = expiryDate;
                return true;
            }
            else
            {
                CopyHwidAndShowFailMessage(clientHwid, message);
                return false;
            }
        }

        private void CopyHwidAndShowFailMessage(string clientHwid, string reason)
        {
            Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetDataObject(clientHwid, true);
                        break;
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                    catch { break; }
                }
            });

            System.Windows.MessageBox.Show(
                $"No valid license found or hardware ID mismatch ({reason}).\n\nYour Hardware ID (HWID) has been copied to the clipboard:\n{clientHwid}\n\nPlease provide this HWID to support for activation.",
                "License Validation Failure",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error
            );
        }

        private async Task<DateTime> GetSafeNetworkTimeAsync()
        {
            using (var client = new System.Net.Http.HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(7);
                try
                {
                    var response = await client.GetAsync("https://worldtimeapi.org/api/timezone/Etc/UTC");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                        string dtStr = data.datetime ?? data.utc_datetime;
                        if (!string.IsNullOrEmpty(dtStr))
                        {
                            return DateTime.Parse(dtStr).ToLocalTime();
                        }
                    }
                }
                catch { }

                try
                {
                    var response = await client.GetAsync("https://google.com");
                    if (response.Headers.Date.HasValue)
                    {
                        return response.Headers.Date.Value.UtcDateTime.ToLocalTime();
                    }
                }
                catch { }
            }
            throw new Exception("Unable to retrieve safe network time.");
        }

        private async Task InitializeWebViewAndFetchData()
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions();
                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_ext");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                
                string cleanToken = UdemyApiManager.GetCleanBearerToken();
                if (!string.IsNullOrEmpty(cleanToken))
                {
                    var cookieManager = webView.CoreWebView2.CookieManager;
                    var cookie = cookieManager.CreateCookie("access_token", cleanToken, ".udemy.com", "/");
                    cookie.IsSecure = true;
                    cookieManager.AddOrUpdateCookie(cookie);
                }

                var navTcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? navHandler = null;
                navHandler = (s, args) =>
                {
                    webView.NavigationCompleted -= navHandler;
                    navTcs.TrySetResult(args.IsSuccess);
                };
                webView.NavigationCompleted += navHandler;

                webView.Source = new Uri("https://www.udemy.com/robots.txt");

                // Wait for navigation with a 4-second timeout
                await Task.WhenAny(navTcs.Task, Task.Delay(4000));

                string jsCode = @"
                    fetch('https://www.udemy.com/api-2.0/users/me/?fields[user]=@all')
                    .then(r => r.text())
                    .then(d => window.chrome.webview.postMessage(d))
                    .catch(e => window.chrome.webview.postMessage('ERROR'));
                ";
                await webView.CoreWebView2.ExecuteScriptAsync(jsCode);

                // Timeout for API
                await Task.Delay(3000);
                if (this.IsLoaded)
                {
                    ProceedToMain(null);
                }
            }
            catch (Exception)
            {
                ProceedToMain(null);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                if (!string.IsNullOrEmpty(json) && !json.Contains("detail") && !json.Contains("ERROR"))
                {
                    var user = Newtonsoft.Json.JsonConvert.DeserializeObject<UdemyUser>(json);
                    ProceedToMain(user);
                }
                else
                {
                    ProceedToMain(null);
                }
            }
            catch
            {
                ProceedToMain(null);
            }
        }

        private void ProceedToMain(UdemyUser? user)
        {
            if (_isProceeding) return;
            _isProceeding = true;

            var mainWin = new MainWindow(user, _licenseExpirationLocal);
            mainWin.Show();
            this.Close();
        }

        private void ProceedToLogin()
        {
            if (_isProceeding) return;
            _isProceeding = true;

            var login = new LoginWindow();
            login.Show();
            this.Close();
        }
    }
}
