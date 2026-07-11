using System;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Media;

using Wpf.Ui.Controls;

using UdemyKicker;

using UdemyKickerWPF.Views;

using System.Diagnostics;

using System.Windows;

using System.Windows.Navigation;

using System.Linq;



namespace UdemyKickerWPF

{

    public partial class MainWindow : FluentWindow

    {

        private UdemyUser _currentUser;

        

        private HomeView _homeView;

        private LibraryView _libraryView;

        private SettingsView _settingsView;

        private LoggerView _loggerView;

        private TranslatorView _translatorView;

        private PlayerView _playerView;



        private const double TOTAL_LICENSE_DAYS = 30.0;

        private System.Windows.Threading.DispatcherTimer? _countdownTimer;

        private DateTime _licenseExpirationLocal;

        private DateTime _verifiedNetworkTimeOnStart;

        private DateTime _localTimeOnStart;

        private double _lastSidebarWidth = 220;



        public MainWindow(UdemyUser? user, DateTime licenseExpiration)

        {

            _currentUser = user;

            _licenseExpirationLocal = licenseExpiration;

            InitializeComponent();



            _homeView = new HomeView();

            _libraryView = new LibraryView();

            _settingsView = new SettingsView(user);

            _loggerView = new LoggerView();

            _translatorView = new TranslatorView();

            _playerView = new PlayerView();



            AppLogger.OnLog = _loggerView.LogInfo;

            AppLogger.OnSwitchToLibraryView = () => {

                if (Dispatcher.CheckAccess())

                {

                    RootFrame.Content = _libraryView;

                }

                else

                {

                    Dispatcher.Invoke(() => { RootFrame.Content = _libraryView; });

                }

            };



            BrowserHost.FetchUdemyApiAsyncHandler = FetchUdemyApiAsync;

            BrowserHost.FetchDrmCommandAsyncHandler = FetchDrmCommandAsync;



            // Monitor sidebar width changes for TitleBar margin

            SidebarBorder.SizeChanged += SidebarBorder_SizeChanged;

            

            _ = InitializeWebViewAsync();

        }



        private void SidebarBorder_SizeChanged(object sender, SizeChangedEventArgs e)

        {

            if (Math.Abs(e.NewSize.Width - _lastSidebarWidth) > 1)

            {

                _lastSidebarWidth = e.NewSize.Width;

                UpdateTitleBarMargin();

            }

        }



        private void UpdateTitleBarMargin()
        {
            if (MainTitleBar != null)
            {
                MainTitleBar.Margin = new Thickness(24, 12, 140, 12);
            }
        }



        private void tele(object sender, RequestNavigateEventArgs e)

        {

            string url = e.Uri.AbsoluteUri;

            Process.Start(new ProcessStartInfo

            {

                FileName = url,

                UseShellExecute = true

            });

            e.Handled = true;

        }

    

    private async System.Threading.Tasks.Task InitializeWebViewAsync()

        {

            try

            {

                string userDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_browser");

                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions();

                options.AreBrowserExtensionsEnabled = true;

                options.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --enable-features=Widevine";

                

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

                await webView.EnsureCoreWebView2Async(env);



                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;



                // Load the DRM capture browser extension (same as the WinForms version)

                try

                {

                    string extensionPath = ResourceManager.GetExtensionPath();

                    await webView.CoreWebView2.Profile.AddBrowserExtensionAsync(extensionPath);

                    AppLogger.LogInfo("DRM browser extension loaded successfully.");

                }

                catch (Exception extEx)

                {

                    AppLogger.LogInfo("Failed to load DRM extension: " + extEx.Message);

                }



                // ── Inject cookies into internal WebView2 ──────────────────────────────

                // Priority 1: Full cookies JSON saved by LoginWindow (cookie-scoop or browser login)

                string cookiesJsonPath = UdemyKickerWPF.Views.LoginWindow.CookiesJsonPath;

                bool cookiesInjected = false;



                if (System.IO.File.Exists(cookiesJsonPath))

                {

                    try

                    {

                        string jsonText = System.IO.File.ReadAllText(cookiesJsonPath);

                        var cookiesArray = Newtonsoft.Json.Linq.JArray.Parse(jsonText);

                        var cookieManager = webView.CoreWebView2.CookieManager;

                        int injected = 0;



                        foreach (var c in cookiesArray)

                        {

                            string name   = c["name"]?.ToString();

                            string value  = c["value"]?.ToString();

                            string domain = c["domain"]?.ToString();

                            string path   = c["path"]?.ToString() ?? "/";



                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(domain))

                                continue;



                            // Normalise domain: WebView2 requires leading dot for subdomain cookies

                            if (!domain.StartsWith(".") && !domain.StartsWith("http"))

                                domain = "." + domain;



                            try

                            {

                                var cookie = cookieManager.CreateCookie(name, value, domain, path);

                                if (c["secure"]   != null) cookie.IsSecure   = (bool)c["secure"];

                                if (c["httpOnly"] != null) cookie.IsHttpOnly = (bool)c["httpOnly"];

                                cookieManager.AddOrUpdateCookie(cookie);

                                injected++;

                            }

                            catch { /* skip invalid cookies */ }

                        }



                        AppLogger.LogInfo($"[WebView] Injected {injected} cookies from saved session.");

                        cookiesInjected = true;

                    }

                    catch (Exception ex)

                    {

                        AppLogger.LogInfo("[WebView] Failed to inject cookies JSON: " + ex.Message);

                    }

                }



                // Priority 2: Fall back to injecting just the access_token from the token file

                if (!cookiesInjected)

                {

                    string rawToken = UdemyApiManager.AccessToken?.Trim();

                    if (!string.IsNullOrEmpty(rawToken))

                    {

                        string cleanToken = rawToken;

                        if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))

                            cleanToken = cleanToken.Substring(7).Trim();



                        var cookieManager = webView.CoreWebView2.CookieManager;

                        var cookie = cookieManager.CreateCookie("access_token", cleanToken, ".udemy.com", "/");

                        cookie.IsSecure = true;

                        cookieManager.AddOrUpdateCookie(cookie);

                        AppLogger.LogInfo("[WebView] Injected access_token cookie (token-only fallback).");

                    }

                }

                

                webView.Source = new Uri("https://www.udemy.com");

            }

            catch (Exception ex)

            {

                AppLogger.LogInfo("Failed to init WebView2: " + ex.Message);

            }

        }



        private static System.Threading.Tasks.TaskCompletionSource<string> apiTcs;

        private static System.Threading.Tasks.TaskCompletionSource<string> drmCommandTcs;

        private static readonly System.Threading.SemaphoreSlim webViewSemaphore = new System.Threading.SemaphoreSlim(1, 1);



        private void CoreWebView2_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)

        {

            try

            {

                string message = e.TryGetWebMessageAsString();

                if (message != null)

                {

                    if (message.StartsWith("DRM_CMD:"))

                    {

                        if (drmCommandTcs != null && !drmCommandTcs.Task.IsCompleted)

                            drmCommandTcs.SetResult(message.Substring(8));

                    }

                    else if (message.StartsWith("API_RES:"))

                    {

                        if (apiTcs != null && !apiTcs.Task.IsCompleted)

                            apiTcs.SetResult(message.Substring(8));

                    }

                    else if (message.StartsWith("API_ERR:"))

                    {

                        if (apiTcs != null && !apiTcs.Task.IsCompleted)

                            apiTcs.SetResult(null);

                    }

                }

            }

            catch { }

        }



        private async System.Threading.Tasks.Task<string> FetchUdemyApiAsync(string url)

        {

            if (webView == null) return null;



            if (!Dispatcher.CheckAccess())

            {

                return await Dispatcher.Invoke(() => FetchUdemyApiAsync(url));

            }



            if (webView.CoreWebView2 == null) return null;

            

            await webViewSemaphore.WaitAsync();

            try

            {

                apiTcs = new System.Threading.Tasks.TaskCompletionSource<string>();

                string safeUrl = url.Replace("'", "\\'");

                string js = $@"

                    fetch('{safeUrl}')

                    .then(r => r.text())

                    .then(d => window.chrome.webview.postMessage('API_RES:' + d))

                    .catch(e => window.chrome.webview.postMessage('API_ERR:' + e));

                ";

                

                await webView.CoreWebView2.ExecuteScriptAsync(js);



                var resultTask = await System.Threading.Tasks.Task.WhenAny(apiTcs.Task, System.Threading.Tasks.Task.Delay(15000));

                return (resultTask == apiTcs.Task) ? apiTcs.Task.Result : null;

            }

            catch { return null; }

            finally { webViewSemaphore.Release(); }

        }



        private async System.Threading.Tasks.Task<string> FetchDrmCommandAsync(string url)

        {

            if (webView == null) return null;



            if (!Dispatcher.CheckAccess())

            {

                return await Dispatcher.Invoke(() => FetchDrmCommandAsync(url));

            }



            await webViewSemaphore.WaitAsync();

            try

            {

                drmCommandTcs = new System.Threading.Tasks.TaskCompletionSource<string>();

                

                // Switch to the browser view so Widevine and extension can initialize correctly

                // and the user can see the progress

                RootFrame.Visibility = Visibility.Collapsed;

                webView.Visibility = Visibility.Visible;

                System.Windows.Controls.Panel.SetZIndex(webView, 999);



                string jsCode = $@"

                    document.addEventListener('drmCmdReady', (event) => {{

                        if (event.detail) window.chrome.webview.postMessage('DRM_CMD:' + event.detail);

                    }});

                ";

                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(jsCode);

                webView.Source = new Uri(url);



                var completedTask = await System.Threading.Tasks.Task.WhenAny(drmCommandTcs.Task, System.Threading.Tasks.Task.Delay(45000));

                

                // Restore the original view (hide the browser)

                webView.Visibility = Visibility.Collapsed;

                RootFrame.Visibility = Visibility.Visible;



                return (completedTask == drmCommandTcs.Task) ? await drmCommandTcs.Task : null;

            }

            finally { webViewSemaphore.Release(); }

        }



        private void Window_Loaded(object sender, RoutedEventArgs e)

        {

            RootFrame.Content = _libraryView;

            HighlightSidebarButton("Home");



            // Update UI for active license instantly

            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 222, 163)); // Green

            StatusText.Text = "License Active";

            SidebarBorder.Visibility = Visibility.Visible;

            MainContentAreaGrid.Visibility = Visibility.Visible;



            // Start countdown timer

            _localTimeOnStart = DateTime.Now;

            _verifiedNetworkTimeOnStart = DateTime.Now;

            StartCountdownTimer();



            // Run background fetcher to revalidate user details and cache them

            _ = FetchUserAndPopulateUiAsync();

}



            private void HighlightSidebarButton(string tag)

            {

                if (SidebarStackPanel == null) return;

                foreach (var child in SidebarStackPanel.Children)

                {

                    if (child is System.Windows.Controls.Button btn)

                    {

                        string btnTag = btn.Tag?.ToString() ?? "";

                        bool isSelected = btnTag == tag;



                        // Find the Indicator border in the button's template

                        var indicator = btn.Template?.FindName("Indicator", btn) as Border;

                        if (indicator != null)

                        {

                            indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;

                        }



                        // Update foreground/background via triggers in style - just ensure visual refresh

                        if (isSelected)

                        {

                            btn.Foreground = new SolidColorBrush(Color.FromRgb(78, 222, 163)); // #4edea3

                            btn.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x4e, 0xde, 0xa3)); // #1A4edea3

                        }

                        else

                        {

                            btn.Foreground = new SolidColorBrush(Color.FromRgb(187, 202, 191)); // #bbcabf

                            btn.Background = Brushes.Transparent;

                        }

                    }

                }

            }



        private async System.Threading.Tasks.Task FetchUserAndPopulateUiAsync()

        {

            try

            {

                int retries = 0;

                while ((webView == null || webView.CoreWebView2 == null) && retries < 10)

                {

                    await System.Threading.Tasks.Task.Delay(500);

                    retries++;

                }



                if (webView != null && webView.CoreWebView2 != null)

                {

                    var user = await UdemyApiManager.GetUserDetailsAsync();

                    if (user != null)

                    {

                        _currentUser = user;

                        Dispatcher.Invoke(() =>

                        {

                            _settingsView.UpdateUserData(user);

                        });



                        // Cache the user details locally

                        try

                        {

                            string cachePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_user.json");

                            string json = Newtonsoft.Json.JsonConvert.SerializeObject(user);

                            System.IO.File.WriteAllText(cachePath, json);

                        }

                        catch { }

                    }

                }

            }

            catch { }

        }



        private void StartCountdownTimer()

        {

            _countdownTimer = new System.Windows.Threading.DispatcherTimer();

            _countdownTimer.Interval = TimeSpan.FromSeconds(1);

            _countdownTimer.Tick += CountdownTimer_Tick;

            _countdownTimer.Start();



            // Run first tick immediately to avoid delay

            CountdownTimer_Tick(this, EventArgs.Empty);

        }



        private void CountdownTimer_Tick(object? sender, EventArgs e)

        {

            var elapsed = DateTime.Now - _localTimeOnStart;

            var currentVerifiedTime = _verifiedNetworkTimeOnStart + elapsed;

            var remaining = _licenseExpirationLocal - currentVerifiedTime;



            if (remaining <= TimeSpan.Zero)

            {

                _countdownTimer?.Stop();

                

                Dispatcher.Invoke(() =>

                {

                    TimeRemainingText.Text = "0 Days and 00:00:00";

                    StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 85, 85)); // Red

                    StatusText.Text = "License Expired";



                    System.Windows.MessageBox.Show(

                        "Your license has expired. The application will now terminate.",

                        "License Expired",

                        System.Windows.MessageBoxButton.OK,

                        System.Windows.MessageBoxImage.Warning

                    );

                    Environment.Exit(0);

                });

            }

            else

            {

                double remainingDays = remaining.TotalDays;

                Dispatcher.Invoke(() =>

                {

                    // Cap at TOTAL_LICENSE_DAYS for progress bar

                    TimeRemainingText.Text = $"{(int)remainingDays} Days and {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";

                });

            }

        }



        private void NavItem_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                if (sender is System.Windows.Controls.Button navItem)

                {

                    string tag = navItem.Tag?.ToString();

                    

                    // Log clicks to AppData folder (which is always writable)

                    try

                    {

                        string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UdemyKicker_Logs");

                        System.IO.Directory.CreateDirectory(logDir);

                        string logPath = System.IO.Path.Combine(logDir, "navigation_debug.log");

                        System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Clicked button Tag: {tag}\n");

                    }

                    catch { }



                    if (tag == "Logout")

                    {

                        string tokenPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "udemyKicker_token.txt");

                        if (System.IO.File.Exists(tokenPath)) System.IO.File.Delete(tokenPath);

                        string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

                        if (!string.IsNullOrEmpty(exePath))

                        {

                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });

                        }

                        Application.Current.Shutdown();

                        return;

                    }



                    // Hide WebView by default unless Browser is selected

                    if (tag != "Browser")

                    {

                        RootFrame.Visibility = Visibility.Visible;

                        webView.Visibility = Visibility.Collapsed;

                    }



                    if (tag != "Logout")

                    {

                        HighlightSidebarButton(tag);

                    }



                    object targetView = null;

                    switch (tag)

                    {

                        case "Home":

                            targetView = _libraryView; // Home tab shows Course Library

                            break;

                        case "Library":

                            targetView = _homeView; // Downloads tab shows Active Downloads

                            break;

                        case "Logger":

                            targetView = _loggerView;

                            break;

                        case "Settings":

                            targetView = _settingsView;

                            break;

                        case "Translator":

                            targetView = _translatorView;

                            break;

                        case "Player":

                            targetView = _playerView;

                            break;

                        case "Browser":

                            RootFrame.Visibility = Visibility.Collapsed;

                            webView.Visibility = Visibility.Visible;

                            break;

                    }



                    if (targetView != null)

                    {

                        if (RootFrame.Content != targetView)

                        {

                            RootFrame.Content = targetView;

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                try

                {

                    string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "navigation_debug.log");

                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EXCEPTION: {ex}\n");

                }

                catch { }

                System.Windows.MessageBox.Show($"Navigation failed: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}", "Navigation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

            }

        }

    }

}