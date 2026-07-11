using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdemyKicker;
using Wpf.Ui.Controls;

namespace UdemyKickerWPF.Views
{
    public partial class LoginWindow : FluentWindow
    {
        // ─── Storage paths ──────────────────────────────────────────────────────────
        private static readonly string AppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// Token-only file (plain string, used as Bearer header).
        private string tokenPath = Path.Combine(AppDataDir, "udemyKicker_token.txt");

        /// Full cookies JSON file — injected into the internal WebView2 on startup.
        public static readonly string CookiesJsonPath = Path.Combine(AppDataDir, "udemyKicker_cookies.json");

        // ─── Tool & scan config ─────────────────────────────────────────────────────
        private static readonly string CookieScoopPath = ResourceManager.GetCookieScoopPath();

        private static readonly string[] BrowsersToScan = { "chrome", "edge", "firefox" };

        private CancellationTokenSource _scanCts = new CancellationTokenSource();

        // Maps browser → raw cookies JArray (all cookies, not just access_token)
        private readonly Dictionary<string, JArray> _discoveredCookies = new Dictionary<string, JArray>();

        private static readonly HttpClient _httpClient = new HttpClient();

        // ─── Constructor ────────────────────────────────────────────────────────────
        public LoginWindow()
        {
            InitializeComponent();
        }

        // ─── Window Loaded ──────────────────────────────────────────────────────────
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Task.Run(() => ScanBrowsersAsync(_scanCts.Token));
        }

        // ─── Background Cookie Discovery ────────────────────────────────────────────

        private async Task ScanBrowsersAsync(CancellationToken ct)
        {
            if (!File.Exists(CookieScoopPath))
            {
                Dispatcher.Invoke(() =>
                {
                    pnlScanning.Visibility = Visibility.Collapsed;
                    txtNoAccounts.Visibility = Visibility.Visible;
                    txtNoAccounts.Text = "cookie-scoop.exe not found next to application.";
                });
                return;
            }

            var seenTokens = new HashSet<string>();
            int totalFound = 0;
            var debugLog = new System.Text.StringBuilder();
            debugLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] Starting browser scan...");
            debugLog.AppendLine($"cookie-scoop path: {CookieScoopPath} | exists: {File.Exists(CookieScoopPath)}");

            foreach (var browser in BrowsersToScan)
            {
                if (ct.IsCancellationRequested) break;

                var profiles = GetBrowserProfiles(browser);
                foreach (var profile in profiles)
                {
                    if (ct.IsCancellationRequested) break;

                    Dispatcher.Invoke(() => txtScanStatus.Text = $"Scanning {browser} ({profile})...");

                    // Fetch ALL cookies for this specific profile
                    var (token, allCookies) = await ExtractCookiesFromBrowserProfileAsync(browser, profile, ct);

                    debugLog.AppendLine($"[{browser} ({profile})] token={token?.Substring(0, Math.Min(20, token?.Length ?? 0))}... cookies={allCookies?.Count ?? 0}");

                    if (string.IsNullOrWhiteSpace(token) || seenTokens.Contains(token))
                        continue;

                    seenTokens.Add(token);

                    string key = $"{browser}_{profile}";
                    if (allCookies != null && allCookies.Count > 0)
                        _discoveredCookies[key] = allCookies;

                    if (ct.IsCancellationRequested) break;

                    // Try to get user details — but DO NOT skip the account if it fails
                    var (displayName, photoUrl) = await FetchUserDataAsync(token, allCookies, ct);
                    debugLog.AppendLine($"[{browser} ({profile})] displayName={displayName}, photoUrl={photoUrl}");

                    // Fall back to a generic name if API is blocked (e.g. Cloudflare)
                    string profileLabel = profile == "Default" ? "" : $" - {profile}";
                    string capitalizedBrowser = char.ToUpper(browser[0]) + browser.Substring(1);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = $"Udemy Account ({capitalizedBrowser}{profileLabel})";
                    }
                    else
                    {
                        displayName = $"{displayName} ({capitalizedBrowser}{profileLabel})";
                    }

                    totalFound++;
                    string capturedToken = token;
                    string capturedKey = key;
                    string capturedBrowser = browser;
                    string capturedName = displayName;
                    string capturedPhoto = photoUrl;

                    Dispatcher.Invoke(() => AddAccountButton(capturedName, capturedKey, capturedToken, capturedBrowser));

                    // Load first detected account's image to the circular background
                    if (totalFound == 1 && !string.IsNullOrWhiteSpace(capturedPhoto))
                    {
                        _ = LoadTopProfileImageAsync(capturedPhoto);
                    }
                }
            }

            // Write debug log
            try
            {
                string logPath = Path.Combine(AppDataDir, "udemyKicker_scan_debug.txt");
                File.WriteAllText(logPath, debugLog.ToString());
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                pnlScanning.Visibility = Visibility.Collapsed;
                if (totalFound == 0)
                    txtNoAccounts.Visibility = Visibility.Visible;
            });
        }

        private List<string> GetBrowserProfiles(string browser)
        {
            var profiles = new List<string>();
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                if (browser == "chrome")
                {
                    string chromeDir = Path.Combine(localAppData, "Google", "Chrome", "User Data");
                    if (Directory.Exists(chromeDir))
                    {
                        foreach (var dir in Directory.GetDirectories(chromeDir))
                        {
                            string name = Path.GetFileName(dir);
                            if (name == "Default" || name.StartsWith("Profile "))
                            {
                                profiles.Add(name);
                            }
                        }
                    }
                }
                else if (browser == "edge")
                {
                    string edgeDir = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
                    if (Directory.Exists(edgeDir))
                    {
                        foreach (var dir in Directory.GetDirectories(edgeDir))
                        {
                            string name = Path.GetFileName(dir);
                            if (name == "Default" || name.StartsWith("Profile "))
                            {
                                profiles.Add(name);
                            }
                        }
                    }
                }
                else if (browser == "firefox")
                {
                    string firefoxDir = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
                    if (Directory.Exists(firefoxDir))
                    {
                        foreach (var dir in Directory.GetDirectories(firefoxDir))
                        {
                            string name = Path.GetFileName(dir);
                            profiles.Add(name);
                        }
                    }
                }
            }
            catch { }

            if (profiles.Count == 0)
            {
                profiles.Add("Default");
            }

            return profiles;
        }

        /// <summary>
        /// Runs cookie-scoop for the given browser and profile fetching ALL Udemy cookies.
        /// Returns (access_token value, full cookies JArray).
        /// </summary>
        private async Task<(string token, JArray allCookies)> ExtractCookiesFromBrowserProfileAsync(string browser, string profile, CancellationToken ct)
        {
            try
            {
                string profileArgName = $"--{browser}-profile";
                var psi = new ProcessStartInfo
                {
                    FileName = CookieScoopPath,
                    // No --names filter → grab every cookie for udemy.com
                    Arguments = $"--url https://www.udemy.com --browsers {browser} {profileArgName} \"{profile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit(), ct);

                if (string.IsNullOrWhiteSpace(output)) return (null, null);

                // Parse JSON: { "cookies": [ { "name": "...", "value": "...", "domain": "...", ... } ] }
                var root = JObject.Parse(output);
                var cookies = root["cookies"] as JArray;
                if (cookies == null || cookies.Count == 0) return (null, null);

                // Find access_token among the full set
                string accessToken = null;
                foreach (var c in cookies)
                {
                    if (c["name"]?.ToString() == "access_token")
                    {
                        accessToken = c["value"]?.ToString();
                        break;
                    }
                }

                return (accessToken, cookies);
            }
            catch { }
            return (null, null);
        }

        /// <summary>
        /// Fetches the user's display name and profile photo URL using an embedded hidden WebView2 + JS fetch().
        /// This is IDENTICAL to how SplashWindow.xaml.cs fetches user data.
        /// </summary>
        private async Task<(string displayName, string photoUrl)> FetchUserDataAsync(string accessToken, JArray allCookies, CancellationToken ct)
        {
            try
            {
                // Must run on UI thread — WebView2 requires it
                return await Dispatcher.Invoke(async () =>
                {
                    try
                    {
                        // ── Init hidden WebView2 (same as SplashWindow) ──────────────────
                        var options = new CoreWebView2EnvironmentOptions();
                        var userDataFolder = Path.Combine(AppDataDir, "udemyKicker_ext");
                        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                        await webViewApi.EnsureCoreWebView2Async(env);

                        // ── Inject all browser cookies ───────────────────────────────────
                        var cookieManager = webViewApi.CoreWebView2.CookieManager;

                        // Inject access_token as a minimum
                        var tokenCookie = cookieManager.CreateCookie("access_token", accessToken, ".udemy.com", "/");
                        tokenCookie.IsSecure = true;
                        cookieManager.AddOrUpdateCookie(tokenCookie);

                        // Inject all other cookies from cookie-scoop
                        if (allCookies != null)
                        {
                            foreach (var c in allCookies)
                            {
                                string n = c["name"]?.ToString();
                                string v = c["value"]?.ToString();
                                string d = c["domain"]?.ToString();
                                string p = c["path"]?.ToString() ?? "/";
                                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(v) || string.IsNullOrEmpty(d)) continue;
                                if (!d.StartsWith(".")) d = "." + d;
                                try
                                {
                                    var ck = cookieManager.CreateCookie(n, v, d, p);
                                    if (c["secure"] != null) ck.IsSecure = (bool)c["secure"];
                                    cookieManager.AddOrUpdateCookie(ck);
                                }
                                catch { }
                            }
                        }

                        // ── Navigate to udemy.com and wait (same as SplashWindow) ────────
                        var tcs = new TaskCompletionSource<string>();

                        EventHandler<CoreWebView2WebMessageReceivedEventArgs> handler = (s, e2) =>
                        {
                            try
                            {
                                string msg = e2.TryGetWebMessageAsString();
                                tcs.TrySetResult(msg);
                            }
                            catch { tcs.TrySetResult(null); }
                        };

                        try
                        {
                            webViewApi.CoreWebView2.WebMessageReceived += handler;

                            webViewApi.Source = new Uri("https://www.udemy.com");
                            await Task.Delay(2500); // wait for page to load

                            // ── Execute fetch() from inside the browser — same JS as SplashWindow ──
                            string js = @"
                                fetch('https://www.udemy.com/api-2.0/users/me/?fields[user]=@all')
                                .then(r => r.text())
                                .then(d => window.chrome.webview.postMessage(d))
                                .catch(e => window.chrome.webview.postMessage('ERROR'));
                            ";
                            await webViewApi.CoreWebView2.ExecuteScriptAsync(js);

                            // ── Wait for result (max 8s) ─────────────────────────────────────
                            var completed = await Task.WhenAny(tcs.Task, Task.Delay(8000));
                            if (completed == tcs.Task)
                            {
                                string json = tcs.Task.Result;
                                if (!string.IsNullOrWhiteSpace(json) && !json.Contains("ERROR"))
                                {
                                    var obj = JObject.Parse(json);
                                    string displayName = obj["display_name"]?.ToString() ?? obj["name"]?.ToString();
                                    string photoUrl = obj["image_100x100"]?.ToString() ?? obj["image_50x50"]?.ToString();
                                    return (displayName ?? "", photoUrl ?? "");
                                }
                            }
                        }
                        finally
                        {
                            webViewApi.CoreWebView2.WebMessageReceived -= handler;
                        }

                        return ("", "");
                    }
                    catch { return ("", ""); }
                });
            }
            catch { }
            return ("", "");
        }

        /// <summary>
        /// Loads the profile picture from the URL and applies it to the circular top avatar background.
        /// </summary>
        private async Task LoadTopProfileImageAsync(string url)
        {
            try
            {
                byte[] data = await _httpClient.GetByteArrayAsync(url);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(data);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze(); // Make thread-safe

                        avatarBorder.Background = new ImageBrush(bitmap)
                        {
                            Stretch = Stretch.UniformToFill
                        };
                        iconAvatar.Visibility = Visibility.Collapsed; // Hide fallback icon
                    }
                    catch { }
                });
            }
            catch { }
        }

        // ─── UI: Add account button ──────────────────────────────────────────────────

        private void AddAccountButton(string displayName, string key, string token, string browser)
        {
            string iconSymbol;
            if (browser == "firefox") iconSymbol = "TabDesktop24";
            else if (browser == "edge") iconSymbol = "Globe24";
            else iconSymbol = "Globe24";

            // ── Browser icon ──────────────────────────────────────────────────────────
            var icon = new SymbolIcon
            {
                Symbol = Enum.Parse<SymbolRegular>(iconSymbol),
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4e, 0xde, 0xa3)),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // ── "Login as" prefix (muted) ─────────────────────────────────────────────
            var prefixBlock = new System.Windows.Controls.TextBlock
            {
                Text = "Login as",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xbb, 0xca, 0xbf)),
                FontSize = 14,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };

            // ── Glowing username ──────────────────────────────────────────────────────
            var glowColor = Color.FromRgb(0x4e, 0xde, 0xa3);
            var nameBlock = new System.Windows.Controls.TextBlock
            {
                Text = displayName,
                Foreground = new SolidColorBrush(glowColor),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = glowColor,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.85
                }
            };

            // ── Inline row: prefix + glowing name ────────────────────────────────────
            var nameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(prefixBlock);
            nameRow.Children.Add(nameBlock);

            // ── Browser label below ───────────────────────────────────────────────────
            var browserBlock = new System.Windows.Controls.TextBlock
            {
                Text = $"via {char.ToUpper(browser[0]) + browser.Substring(1)}",
                Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0x4e, 0xde, 0xa3)),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 2, 0, 0)
            };

            var labelsPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelsPanel.Children.Add(nameRow);
            labelsPanel.Children.Add(browserBlock);

            // ── Arrow ─────────────────────────────────────────────────────────────────
            var arrow = new SymbolIcon
            {
                Symbol = SymbolRegular.ArrowRight24,
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0x4e, 0xde, 0xa3)),
                VerticalAlignment = VerticalAlignment.Center
            };

            // ── Layout ───────────────────────────────────────────────────────────────
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(labelsPanel, 1);
            Grid.SetColumn(arrow, 2);

            grid.Children.Add(icon);
            grid.Children.Add(labelsPanel);
            grid.Children.Add(arrow);

            var btn = new System.Windows.Controls.Button
            {
                Content = grid,
                Margin = new Thickness(0, 0, 0, 10),
                Style = (Style)Resources["BrowserLoginBtnStyle"]
            };

            string capturedToken = token;
            string capturedKey = key;
            btn.Click += (s, e) => LoginWithDiscoveredCookies(capturedToken, capturedKey);

            pnlAccountButtons.Children.Add(btn);
        }

        // ─── Login helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Saves the access_token AND the full cookies JSON, then proceeds.
        /// Called when the user clicks a browser-discovered account button.
        /// </summary>
        private void LoginWithDiscoveredCookies(string token, string browser)
        {
            _scanCts.Cancel();

            // Save plain token (used as Bearer in API calls)
            File.WriteAllText(tokenPath, token);
            UdemyApiManager.AccessToken = token;

            // Save full cookies JSON for injection into internal WebView2
            if (_discoveredCookies.TryGetValue(browser, out var cookies) && cookies != null)
            {
                File.WriteAllText(CookiesJsonPath, cookies.ToString(Formatting.None));
            }

            ProceedToSplash();
        }

        /// <summary>
        /// Called by the manual token input button — no cookies JSON to save.
        /// </summary>
        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtToken.Text))
            {
                System.Windows.MessageBox.Show("Please enter a token.");
                return;
            }

            _scanCts.Cancel();
            File.WriteAllText(tokenPath, txtToken.Text);
            UdemyApiManager.AccessToken = txtToken.Text;

            // Clear any stale cookies file so we don't inject old cookies
            if (File.Exists(CookiesJsonPath)) File.Delete(CookiesJsonPath);

            ProceedToSplash();
        }

        private async void BtnBrowser_Click(object sender, RoutedEventArgs e)
        {
            pnlManual.Visibility = Visibility.Collapsed;
            pnlBrowser.Visibility = Visibility.Visible;

            var options = new CoreWebView2EnvironmentOptions();
            var userDataFolder = Path.Combine(AppDataDir, "udemyKicker_ext");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await webView.EnsureCoreWebView2Async(env);

            webView.Source = new Uri("https://www.udemy.com/join/login-popup/");
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            pnlBrowser.Visibility = Visibility.Collapsed;
            pnlManual.Visibility = Visibility.Visible;
            if (webView != null && webView.CoreWebView2 != null)
                webView.Source = new Uri("about:blank");
        }

        /// <summary>
        /// After logging in via the embedded browser, extract and save ALL cookies
        /// so they can be injected into the main internal WebView2 later.
        /// </summary>
        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            string src = webView.Source?.ToString() ?? "";
            if (!src.StartsWith("https://www.udemy.com") || src.Contains("login")) return;

            var wv2Cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.udemy.com");

            string accessToken = null;
            var cookieList = new JArray();

            foreach (var c in wv2Cookies)
            {
                // Build a compatible JSON object matching cookie-scoop's schema
                var obj = new JObject
                {
                    ["name"] = c.Name,
                    ["value"] = c.Value,
                    ["domain"] = c.Domain,
                    ["path"] = c.Path,
                    ["secure"] = c.IsSecure,
                    ["httpOnly"] = c.IsHttpOnly
                };
                cookieList.Add(obj);

                if (c.Name == "access_token")
                    accessToken = c.Value;
            }

            if (accessToken == null) return;

            _scanCts.Cancel();

            // Persist token
            File.WriteAllText(tokenPath, accessToken);
            UdemyApiManager.AccessToken = accessToken;

            // Persist full cookies for injection into main WebView2
            File.WriteAllText(CookiesJsonPath, cookieList.ToString(Formatting.None));

            ProceedToSplash();
        }

        private void ProceedToSplash()
        {
            var splash = new SplashWindow();
            splash.Show();
            this.Close();
        }
    }
}
