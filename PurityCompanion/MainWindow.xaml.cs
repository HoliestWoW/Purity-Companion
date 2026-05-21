using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Linq;

namespace PurityCompanion
{
    public partial class MainWindow : Window
    {
        private readonly string ClientId = "c045bac65c9f45f19d3d65e62c0b1b4f";
        private readonly string PythonServerUrl = "https://purity.pythonanywhere.com";
        private readonly string CurrentAppVersion = "1.2.0";

        private string? CurrentSessionId;
        private string? VerifiedBattleTag;

        // Timers properly declared inside the class
        private DispatcherTimer? PollingTimer;
        private DispatcherTimer? statusClearTimer;
        private DispatcherTimer? heartbeatTimer;
        private DispatcherTimer? updatePollingTimer;

        private static readonly HttpClient httpClient = new HttpClient();

        private System.Windows.Forms.NotifyIcon? trayIcon;
        private FileSystemWatcher? wowWatcher;
        private bool minimizeToTray = true;
        private bool showNotifications = true;
        private DateTime lastEventTime = DateTime.MinValue;
        private string lastEventPath = "";
        private string CustomWowPath = "";

        public MainWindow()
        {
            InitializeComponent();
            SetupSystemTray();
            LoadSettings();
        }

        private string? ResolveWowAccountPath(string selectedPath)
        {
            if (selectedPath.EndsWith("Account", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(selectedPath)) return selectedPath;
            }

            if (selectedPath.EndsWith("WTF", StringComparison.OrdinalIgnoreCase))
            {
                string testPath = Path.Combine(selectedPath, "Account");
                if (Directory.Exists(testPath)) return testPath;
            }

            if (selectedPath.EndsWith("_classic_era_", StringComparison.OrdinalIgnoreCase))
            {
                string testPath = Path.Combine(selectedPath, "WTF", "Account");
                if (Directory.Exists(testPath)) return testPath;
            }

            if (selectedPath.EndsWith("World of Warcraft", StringComparison.OrdinalIgnoreCase) ||
                selectedPath.EndsWith("WoW", StringComparison.OrdinalIgnoreCase))
            {
                string testPath = Path.Combine(selectedPath, "_classic_era_", "WTF", "Account");
                if (Directory.Exists(testPath)) return testPath;
            }

            string fallbackPath = Path.Combine(selectedPath, "_classic_era_", "WTF", "Account");
            if (Directory.Exists(fallbackPath)) return fallbackPath;

            return null;
        }

        private void PromptForWowFolder(string popupMessage = "WoW not found on C:\\ drive. Please select your World of Warcraft folder.")
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = popupMessage;
                    dialog.ShowNewFolderButton = false;

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        string? resolvedPath = ResolveWowAccountPath(dialog.SelectedPath);

                        if (resolvedPath != null)
                        {
                            CustomWowPath = resolvedPath;
                            SaveSettings();
                            PostLogEvent($"WoW path successfully mapped to: {CustomWowPath}", System.Windows.Media.Colors.LimeGreen);
                            InitializeLiveBackgroundWatcher();
                        }
                        else
                        {
                            System.Windows.MessageBox.Show("Could not find a Classic Era installation inside the folder you selected. Please try again.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Error);
                            PostLogEvent("⚠️ Failed to map WoW folder. Syncing disabled.", System.Windows.Media.Colors.Red, true);
                        }
                    }
                    else
                    {
                        PostLogEvent("⚠️ Folder selection canceled. Syncing disabled.", System.Windows.Media.Colors.Red, true);
                    }
                }
            });
        }

        private void ChangeWowPathButton_Click(object sender, RoutedEventArgs e)
        {
            PromptForWowFolder("Manual Override: Please select your World of Warcraft folder location.");
        }

        // --- 1. PREMIUM LOGGING ENGINE ---
        private void PostLogEvent(string description, System.Windows.Media.Color displayColor, bool useNotificationBanner = true)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogHistoryText.AppendText($"[{timestamp}] {description}\r\n");
                LogHistoryText.ScrollToEnd();

                if (useNotificationBanner)
                {
                    StatusText.Text = description;
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(displayColor);

                    statusClearTimer?.Stop();
                    statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    statusClearTimer.Tick += (s, e) =>
                    {
                        StatusText.Text = wowWatcher != null ? "Background monitoring active. Awaiting level 60 runs..." : "Awaiting sync configurations...";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(138, 138, 154));
                        statusClearTimer.Stop();
                    };
                    statusClearTimer.Start();
                }
            });
        }

        private string GetAddonIntegrityHash()
        {
            try
            {
                if (string.IsNullOrEmpty(CustomWowPath) || !Directory.Exists(CustomWowPath)) return "FOLDER_MISSING";

                string wowBaseFolder = Directory.GetParent(CustomWowPath)?.Parent?.FullName ?? "";
                string addonDir = Path.Combine(wowBaseFolder, "Interface", "AddOns", "Purity");

                if (!Directory.Exists(addonDir)) return "FOLDER_MISSING";

                var files = Directory.GetFiles(addonDir, "*.lua").OrderBy(f => f).ToList();

                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] combinedHash = new byte[32];
                    foreach (var file in files)
                    {
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            byte[] fileHash = sha256.ComputeHash(stream);
                            for (int i = 0; i < 32; i++) combinedHash[i] ^= fileHash[i];
                        }
                    }

                    return BitConverter.ToString(combinedHash).Replace("-", "").ToLower();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Hash Error: {ex.Message}");
                return "ERROR";
            }
        }

        private async Task CheckForUpdates(bool isBackgroundCheck = false)
        {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync($"{PythonServerUrl}/api/check_update");
                if (response.IsSuccessStatusCode)
                {
                    string jsonResult = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(jsonResult))
                    {
                        string latestVersion = doc.RootElement.GetProperty("latest_version").GetString() ?? CurrentAppVersion;
                        string downloadUrl = doc.RootElement.GetProperty("download_url").GetString() ?? "";

                        if (latestVersion != CurrentAppVersion && !string.IsNullOrEmpty(downloadUrl))
                        {
                            PostLogEvent($"UPDATE REQUIRED: Version {latestVersion} is available!", System.Windows.Media.Colors.Cyan);

                            if (isBackgroundCheck && this.Visibility != Visibility.Visible)
                            {
                                if (showNotifications)
                                    trayIcon?.ShowBalloonTip(10000, "Update Available", $"Version {latestVersion} is ready. Open the app to install it.", System.Windows.Forms.ToolTipIcon.Info);
                            }
                            else
                            {
                                var result = System.Windows.MessageBox.Show(
                                    $"Version {latestVersion} is available! Would you like to download and restart the app automatically now?",
                                    "Update Available",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Information);

                                if (result == MessageBoxResult.Yes)
                                {
                                    await DownloadAndInstallUpdate(downloadUrl);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("Update check failed: " + ex.Message); }
        }

        private async Task DownloadAndInstallUpdate(string directExeUrl)
        {
            try
            {
                PostLogEvent("Downloading new version... Please wait.", System.Windows.Media.Colors.Yellow);
                VerifyButton.IsEnabled = false;

                // 1. Download the new .exe to a temporary file
                byte[] fileBytes = await httpClient.GetByteArrayAsync(directExeUrl);
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string updateExePath = currentExePath + ".update";

                File.WriteAllBytes(updateExePath, fileBytes);

                // 2. Create the invisible batch script
                string batPath = Path.Combine(Path.GetDirectoryName(currentExePath) ?? "", "updater.bat");
                string exeName = Path.GetFileName(currentExePath);
                string updateExeName = Path.GetFileName(updateExePath);

                string[] batScript =
                {
                    "@echo off",
                    "timeout /t 2 /nobreak > NUL",
                    $"del \"{exeName}\"",
                    $"move /y \"{updateExeName}\" \"{exeName}\"",
                    $"start \"\" \"{exeName}\"",
                    "del \"%~f0\""
                };
                File.WriteAllLines(batPath, batScript);

                // 3. Launch the batch script silently
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = batPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(startInfo);

                // 4. Kill the current app
                if (wowWatcher != null) wowWatcher.Dispose();
                trayIcon?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                PostLogEvent($"Auto-Update Failed: {ex.Message}", System.Windows.Media.Colors.Red);
                VerifyButton.IsEnabled = true;
            }
        }

        private void LogToggleLink_Click(object sender, RoutedEventArgs e)
        {
            if (LogHistoryPanel.Visibility == Visibility.Visible)
            {
                LogHistoryPanel.Visibility = Visibility.Collapsed;
                LogToggleLink.Content = "▼ View Session Activity History Log";
            }
            else
            {
                LogHistoryPanel.Visibility = Visibility.Visible;
                LogToggleLink.Content = "▲ Hide Session Activity History Log";
                LogScrollViewer.ScrollToEnd();
            }
        }

        // --- 2. SETTINGS & TRAY LOGIC ---
        private void SetupSystemTray()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/PurityCompanion;component/PurityIcon.ico")).Stream;
                trayIcon.Icon = new System.Drawing.Icon(iconStream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Tray Icon Error: " + ex.Message);
                trayIcon.Icon = System.Drawing.SystemIcons.Shield;
            }

            trayIcon.Text = "Purity Companion";
            trayIcon.Visible = true;

            trayIcon.DoubleClick += (s, args) => { this.Show(); this.WindowState = WindowState.Normal; };
            trayIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            trayIcon.ContextMenuStrip.Items.Add("Open Companion", null, (s, args) => { this.Show(); this.WindowState = WindowState.Normal; });
            trayIcon.ContextMenuStrip.Items.Add("Exit Product", null, (s, args) => {
                if (wowWatcher != null) wowWatcher.Dispose();
                trayIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (minimizeToTray)
            {
                e.Cancel = true;
                this.Hide();
                if (showNotifications)
                {
                    trayIcon?.ShowBalloonTip(3000, "Purity Companion", "Monitoring background processes cleanly.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                if (wowWatcher != null) wowWatcher.Dispose();
                trayIcon?.Dispose();
                base.OnClosing(e);
            }
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            OptionsPanel.Visibility = OptionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            minimizeToTray = TrayCheckBox.IsChecked ?? true;
            showNotifications = NotifyCheckBox.IsChecked ?? true;
            SaveSettings();
        }

        private string GetAppDataFolder()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PurityCompanion");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return folder;
        }

        private void LoadSettings()
        {
            string settingsFile = Path.Combine(GetAppDataFolder(), "settings.txt");
            if (File.Exists(settingsFile))
            {
                string[] lines = File.ReadAllLines(settingsFile);
                if (lines.Length >= 2)
                {
                    bool.TryParse(lines[0], out minimizeToTray);
                    bool.TryParse(lines[1], out showNotifications);
                }
                if (lines.Length >= 3 && Directory.Exists(lines[2]))
                {
                    CustomWowPath = lines[2];
                }
            }
            TrayCheckBox.IsChecked = minimizeToTray;
            NotifyCheckBox.IsChecked = showNotifications;
            PostLogEvent("Client settings loaded successfully.", System.Windows.Media.Colors.Gray, false);
        }

        private void SaveSettings()
        {
            string settingsFile = Path.Combine(GetAppDataFolder(), "settings.txt");
            File.WriteAllLines(settingsFile, new[] { minimizeToTray.ToString(), showNotifications.ToString(), CustomWowPath });
        }

        // --- 3. PERSISTENT LOGIN & STARTUP ---
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckForUpdates(isBackgroundCheck: false);

            updatePollingTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            updatePollingTimer.Tick += async (s, args) => await CheckForUpdates(isBackgroundCheck: true);
            updatePollingTimer.Start();

            string defaultCPath = @"C:\Program Files (x86)\World of Warcraft\_classic_era_\WTF\Account";

            if (string.IsNullOrEmpty(CustomWowPath))
            {
                if (Directory.Exists(defaultCPath))
                {
                    CustomWowPath = defaultCPath;
                    SaveSettings();
                    PostLogEvent("Found default WoW installation on C:\\ drive.", System.Windows.Media.Colors.Gray, false);
                }
                else
                {
                    PromptForWowFolder();
                }
            }
            else if (!Directory.Exists(CustomWowPath))
            {
                PostLogEvent("⚠️ Saved WoW folder is missing. Requesting new location.", System.Windows.Media.Colors.Yellow, true);
                PromptForWowFolder();
            }

            string authFile = Path.Combine(GetAppDataFolder(), "auth.txt");
            if (File.Exists(authFile))
            {
                VerifiedBattleTag = File.ReadAllText(authFile);
                UpdateUIForSuccessfulAuth();
                InitializeLiveBackgroundWatcher();
                InitializeHeartbeatEngine();
            }
            else
            {
                PostLogEvent("App started. Standing by for Battle.net onboarding sequence.", System.Windows.Media.Colors.Gray, false);
            }
        }

        // --- CONTINUOUS SURVEILLANCE HEARTBEAT ENGINE ---
        private void InitializeHeartbeatEngine()
        {
            if (heartbeatTimer != null) return;

            heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            heartbeatTimer.Tick += async (s, e) => await ExecuteHeartbeatTransaction();
            heartbeatTimer.Start();

            PostLogEvent("Zero-Trust Heartbeat timeline verification synchronization active.", System.Windows.Media.Colors.LimeGreen, false);
        }

        private async Task ExecuteHeartbeatTransaction()
        {
            if (string.IsNullOrEmpty(VerifiedBattleTag)) return;
            if (string.IsNullOrEmpty(CustomWowPath) || !Directory.Exists(CustomWowPath)) return;

            try
            {
                bool isWowRunning = Process.GetProcessesByName("WowClassic").Length > 0;
                string[] charFiles = Directory.GetFiles(CustomWowPath, "Purity.lua", SearchOption.AllDirectories);

                foreach (string file in charFiles)
                {
                    string fileContent = File.ReadAllText(file);

                    Match optInMatch = Regex.Match(fileContent, @"\[\s*[""']isOptedIn[""']\s*\]\s*=\s*(true|false)");
                    Match seqMatch = Regex.Match(fileContent, @"\[\s*[""']sequenceID[""']\s*\]\s*=\s*([0-9]+)");

                    bool isOptedIn = optInMatch.Success && optInMatch.Groups[1].Value == "true";
                    int sequenceId = seqMatch.Success ? int.Parse(seqMatch.Groups[1].Value) : 0;

                    if (sequenceId == 0 || !isOptedIn) continue;

                    DirectoryInfo? savedVarsFolder = Directory.GetParent(file);
                    DirectoryInfo? charFolder = savedVarsFolder?.Parent;
                    string charName = charFolder?.Name ?? "Unknown";

                    if (charName == "Account" || charName == "SavedVariables") continue;

                    string realmName = charFolder?.Parent?.Name ?? "UnknownRealm";

                    Match guidMatch = Regex.Match(fileContent, @"\[\s*[""']playerGUID[""']\s*\]\s*=\s*[""']([^""']+)[""']");
                    Match timeMatch = Regex.Match(fileContent, @"\[\s*[""']totalPlayedTime[""']\s*\]\s*=\s*([0-9.]+)");
                    Match levelMatch = Regex.Match(fileContent, @"\[\s*[""']currentLevel[""']\s*\]\s*=\s*([0-9]+)");
                    Match titleMatch = Regex.Match(fileContent, @"\[\s*[""']challengeTitle[""']\s*\]\s*=\s*[""']([^""']+)[""']");

                    if (guidMatch.Success)
                    {
                        double totalPlayed = timeMatch.Success ? double.Parse(timeMatch.Groups[1].Value) : 0.0;
                        int currentLevel = levelMatch.Success ? int.Parse(levelMatch.Groups[1].Value) : 1;
                        string challengeType = titleMatch.Success ? titleMatch.Groups[1].Value : "Unknown Challenge";

                        var payload = new
                        {
                            battletag = VerifiedBattleTag,
                            character_name = charName,
                            realm_name = realmName,
                            player_guid = guidMatch.Groups[1].Value,
                            sequence_id = sequenceId,
                            total_played = totalPlayed,
                            is_playing = isWowRunning,
                            level = currentLevel,
                            challenge_type = challengeType,
                            integrityHash = GetAddonIntegrityHash()
                        };

                        string jsonPayload = JsonSerializer.Serialize(payload);
                        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                        HttpResponseMessage response = await httpClient.PostAsync($"{PythonServerUrl}/api/heartbeat", content);

                        if (response.IsSuccessStatusCode)
                        {
                            if (isWowRunning)
                            {
                                PostLogEvent($"Active Telemetry Synced: {charName} ({realmName}) [Seq: {sequenceId}]", System.Windows.Media.Colors.SpringGreen, false);
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                        {
                            PostLogEvent($"⏳ Sync Delay on {charName}: Older file detected. Waiting for Cloud Drive to update...", System.Windows.Media.Colors.Yellow, false);
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            PostLogEvent($"⛔ Server Rejected {charName}: Character is permanently blocked.", System.Windows.Media.Colors.Red, false);
                        }
                        else
                        {
                            PostLogEvent($"Server Error tracking {charName}: {response.StatusCode}", System.Windows.Media.Colors.Orange, false);
                        }
                    }
                }
            }
            catch (Exception ex) { PostLogEvent($"Heartbeat Error: {ex.Message}", System.Windows.Media.Colors.Red, false); }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (wowWatcher != null) { wowWatcher.EnableRaisingEvents = false; wowWatcher.Dispose(); wowWatcher = null; }
            string authFile = Path.Combine(GetAppDataFolder(), "auth.txt");
            if (File.Exists(authFile)) File.Delete(authFile);

            PostLogEvent($"Account {VerifiedBattleTag} forgotten. Session identity flushed.", System.Windows.Media.Colors.Red);

            VerifiedBattleTag = null;
            AuthStatusText.Text = "Not Connected";
            AuthStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);

            VerifyButton.Content = "Link Battle.net";
            VerifyButton.IsEnabled = true;
            VerifyButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0078D4"));

            AutomateSyncButton.IsEnabled = false;
            AutomateSyncButton.Content = "Enable Sync & Auto-Upload";
            OptionsPanel.Visibility = Visibility.Collapsed;
        }

        // --- 4. OAUTH SYSTEM ---
        private void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentSessionId = Guid.NewGuid().ToString();
            string redirectUri = $"{PythonServerUrl}/auth/callback";
            string bnetUrl = $"https://oauth.battle.net/authorize?client_id={ClientId}&redirect_uri={redirectUri}&response_type=code&scope=openid&state={CurrentSessionId}";

            Process.Start(new ProcessStartInfo { FileName = bnetUrl, UseShellExecute = true });

            PostLogEvent("Triggered browser validation framework link.", System.Windows.Media.Colors.Yellow);
            AuthStatusText.Text = "Waiting for browser...";
            AuthStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Yellow);
            VerifyButton.IsEnabled = false;

            PollingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            PollingTimer.Tick += async (s, args) => await CheckServerForBattleTag();
            PollingTimer.Start();
        }

        private async Task CheckServerForBattleTag()
        {
            try
            {
                string checkUrl = $"{PythonServerUrl}/api/check_auth?state={CurrentSessionId}";
                HttpResponseMessage response = await httpClient.GetAsync(checkUrl);

                if (response.IsSuccessStatusCode)
                {
                    string jsonResult = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(jsonResult))
                    {
                        if (doc.RootElement.GetProperty("status").GetString() == "success")
                        {
                            VerifiedBattleTag = doc.RootElement.GetProperty("battletag").GetString() ?? "UnknownUser";
                            File.WriteAllText(Path.Combine(GetAppDataFolder(), "auth.txt"), VerifiedBattleTag);

                            if (PollingTimer != null) PollingTimer.Stop();
                            UpdateUIForSuccessfulAuth();
                            InitializeLiveBackgroundWatcher();
                            InitializeHeartbeatEngine();
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("Polling error: " + ex.Message); }
        }

        private void UpdateUIForSuccessfulAuth()
        {
            AuthStatusText.Text = $"Connected: {VerifiedBattleTag}";
            AuthStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LimeGreen);
            VerifyButton.Content = "Authenticated";
            VerifyButton.IsEnabled = false;
            VerifyButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGray);

            AutomateSyncButton.IsEnabled = true;
            PostLogEvent($"Secure profile successfully assigned to target identity: {VerifiedBattleTag}", System.Windows.Media.Colors.LimeGreen, false);
        }

        // --- 5. AUTOMATED LIVE SURVEILLANCE & ANTI-CHEAT ---
        private void InitializeLiveBackgroundWatcher()
        {
            if (string.IsNullOrEmpty(CustomWowPath) || !Directory.Exists(CustomWowPath)) return;
            if (wowWatcher != null) return;

            wowWatcher = new FileSystemWatcher();
            wowWatcher.Path = CustomWowPath;
            wowWatcher.Filter = "Purity.lua";
            wowWatcher.IncludeSubdirectories = true;
            wowWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            wowWatcher.Changed += OnWowFileChanged;
            wowWatcher.EnableRaisingEvents = true;

            AutomateSyncButton.Content = "Sync Active & Monitoring";
            PostLogEvent("IO Surveillance Engine online. Listening for local World of Warcraft transaction signatures...", System.Windows.Media.Colors.LimeGreen);
            // string devHash = GetAddonIntegrityHash();
            // PostLogEvent($"[DEV] Current Addon Integrity Hash: {devHash}", System.Windows.Media.Colors.Cyan, false);
        }

        private void OnWowFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath == lastEventPath && (DateTime.Now - lastEventTime).TotalSeconds < 2.0)
            {
                return;
            }

            lastEventPath = e.FullPath;
            lastEventTime = DateTime.Now;

            System.Windows.Application.Current.Dispatcher.Invoke(async () =>
            {
                string[] pathParts = e.FullPath.Split(Path.DirectorySeparatorChar);
                string charName = pathParts.Length >= 3 ? pathParts[pathParts.Length - 3] : "Unknown";
                string realmName = pathParts.Length >= 4 ? pathParts[pathParts.Length - 4] : "Unknown";

                PostLogEvent($"Detected write cycle modifications for {charName} ({realmName}). Scanning...", System.Windows.Media.Colors.Yellow);

                string fileContent = "";
                for (int i = 0; i < 8; i++)
                {
                    try
                    {
                        fileContent = File.ReadAllText(e.FullPath);
                        break;
                    }
                    catch (IOException) { await Task.Delay(250); }
                }

                if (string.IsNullOrEmpty(fileContent)) return;

                if (Regex.IsMatch(fileContent, @"\[""status""\]\s*=\s*""Failed"""))
                {
                    Match reasonMatch = Regex.Match(fileContent, @"\[""failureReason""\]\s*=\s*""([^""]+)""");
                    string reason = reasonMatch.Success ? reasonMatch.Groups[1].Value : "Unknown Transgression";

                    PostLogEvent($"CRITICAL: Run failure flag parsed on {charName}. Transmitting server report...", System.Windows.Media.Colors.Red);
                    await ReportRunFailureToServer(charName, realmName, reason);
                    return;
                }

                string scanResult = await ScanAndUploadPendingRun(fileContent, realmName);

                if (scanResult == "success")
                {
                    PostLogEvent($"SUCCESS: Level 60 run verified for {charName} and exported to leaderboard layout!", System.Windows.Media.Colors.LimeGreen);
                }
                else if (scanResult.StartsWith("Rejected:"))
                {
                    PostLogEvent($"API ERROR: Submission block rejected. Details: {scanResult}", System.Windows.Media.Colors.Yellow);
                }
                else
                {
                    PostLogEvent("File verification parameters complete. Save metrics match server values.", System.Windows.Media.Colors.LimeGreen, false);
                }
            });
        }

        // --- 6. SECURE TRANSMISSION ROUTINES ---
        private async Task<string> ScanAndUploadPendingRun(string fileContent, string realmName)
        {
            try
            {
                Match match = Regex.Match(fileContent, @"\[""PendingLeaderboardUpload""\]\s*=\s*""([^""']+)""");
                if (match.Success)
                {
                    string verificationString = match.Groups[1].Value;

                    var payload = new
                    {
                        verification_string = verificationString,
                        battletag = VerifiedBattleTag ?? "UnknownUser",
                        realm_name = realmName,
                        opt_out_name = false,
                        source = "app"
                    };

                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await httpClient.PostAsync($"{PythonServerUrl}/verify", content);

                    if (response.IsSuccessStatusCode)
                    {
                        if (showNotifications) trayIcon?.ShowBalloonTip(5000, "Purity Run Uploaded!", "Your successful level 60 verification hash has been recorded.", System.Windows.Forms.ToolTipIcon.Info);
                        return "success";
                    }

                    string errorResponse = await response.Content.ReadAsStringAsync();
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(errorResponse))
                            return "Rejected: " + (doc.RootElement.GetProperty("message").GetString() ?? "Invalid data formatting.");
                    }
                    catch { return "Rejected: Server transaction parsing failure."; }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
            return "none";
        }

        private async Task ReportRunFailureToServer(string character, string realm, string reason)
        {
            try
            {
                var payload = new
                {
                    battletag = VerifiedBattleTag ?? "UnassignedAccount",
                    character_name = character,
                    realm_name = realm,
                    failure_reason = reason
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                HttpResponseMessage response = await httpClient.PostAsync($"{PythonServerUrl}/report_failure", content);

                if (response.IsSuccessStatusCode)
                {
                    PostLogEvent($"Anti-Cheat Ledger Synced: Permanent failure record locked on server database for {character}.", System.Windows.Media.Colors.Red);
                }
                else
                {
                    PostLogEvent("Warning: Server death ledger link returned non-success status code.", System.Windows.Media.Colors.Yellow);
                }
            }
            catch (Exception ex) { PostLogEvent($"Network configuration intercept error on death dispatch: {ex.Message}", System.Windows.Media.Colors.Red); }
        }

        // --- 7. INITIAL OPT-IN OVERHAUL SYNC (Run Once) ---
        private async void AutomateSyncButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AutomateSyncButton.Content = "Scanning & Syncing...";
                AutomateSyncButton.IsEnabled = false;

                if (string.IsNullOrEmpty(CustomWowPath) || !Directory.Exists(CustomWowPath))
                {
                    System.Windows.MessageBox.Show("Could not automatically locate World of Warcraft.", "Auto-Discovery Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ResetSyncButton();
                    return;
                }

                string? cloudFolder = DetermineCloudPath();
                if (string.IsNullOrEmpty(cloudFolder)) { ResetSyncButton(); return; }

                string[] accountFolders = Directory.GetDirectories(CustomWowPath);
                foreach (string accountFolder in accountFolders)
                {
                    string accountName = new DirectoryInfo(accountFolder).Name;

                    string globalVarsOriginal = Path.Combine(accountFolder, @"SavedVariables\Purity_GlobalSettings.lua");
                    if (File.Exists(globalVarsOriginal) && !IsSymbolicLink(globalVarsOriginal))
                    {
                        if (!Directory.Exists(cloudFolder)) Directory.CreateDirectory(cloudFolder);
                        string globalVarsCloud = Path.Combine(cloudFolder, $"Purity_GlobalSettings_{accountName}.lua");
                        if (!File.Exists(globalVarsCloud)) File.Copy(globalVarsOriginal, globalVarsCloud);
                        CreateSymlinkAsAdmin(globalVarsOriginal, globalVarsCloud);
                    }

                    string[] charFiles = Directory.GetFiles(accountFolder, "Purity.lua", SearchOption.AllDirectories);
                    foreach (string charVarsOriginal in charFiles)
                    {
                        string[] pathParts = charVarsOriginal.Split(Path.DirectorySeparatorChar);
                        string charName = pathParts.Length >= 3 ? pathParts[pathParts.Length - 3] : "UnknownChar";
                        string realmName = pathParts.Length >= 4 ? pathParts[pathParts.Length - 4] : "UnknownRealm";

                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                string content = File.ReadAllText(charVarsOriginal);
                                await ScanAndUploadPendingRun(content, realmName);
                                break;
                            }
                            catch { await Task.Delay(100); }
                        }

                        if (IsSymbolicLink(charVarsOriginal)) continue;

                        if (!Directory.Exists(cloudFolder)) Directory.CreateDirectory(cloudFolder);
                        string cleanCloudRealm = realmName.Replace(" ", "");

                        string charVarsCloud = Path.Combine(cloudFolder, $"Purity_{accountName}_{cleanCloudRealm}_{charName}.lua");
                        if (!File.Exists(charVarsCloud)) File.Copy(charVarsOriginal, charVarsCloud);
                        CreateSymlinkAsAdmin(charVarsOriginal, charVarsCloud);
                    }
                }

                InitializeLiveBackgroundWatcher();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetSyncButton();
            }
        }

        private void ResetSyncButton()
        {
            AutomateSyncButton.Content = "Enable Sync & Auto-Upload";
            AutomateSyncButton.IsEnabled = true;
        }

        // --- CLOUD PROVIDER & SYMLINK MODULES ---
        private string? DetermineCloudPath()
        {
            if (CloudProviderCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem && selectedItem.Content != null)
            {
                string selectedProvider = selectedItem.Content.ToString() ?? "Custom Folder...";
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (selectedProvider == "OneDrive")
                {
                    string[] oneDriveDirs = System.IO.Directory.GetDirectories(userProfile, "OneDrive*");
                    if (oneDriveDirs.Length == 1) return Path.Combine(oneDriveDirs[0], "PuritySaves");
                    else if (oneDriveDirs.Length > 1) return PromptUserForFolder("Multiple OneDrive folders detected. Please select one:");
                    else return PromptUserForFolder("OneDrive not found. Please select your OneDrive folder:");
                }
                else if (selectedProvider == "Google Drive")
                {
                    System.Collections.Generic.List<string> gDrivePaths = new System.Collections.Generic.List<string>();
                    foreach (var drive in System.IO.DriveInfo.GetDrives())
                    {
                        if (drive.IsReady && drive.VolumeLabel != null && drive.VolumeLabel.Contains("Google Drive"))
                            gDrivePaths.Add(Path.Combine(drive.Name, "My Drive"));
                    }

                    string localGDrive = Path.Combine(userProfile, "Google Drive");
                    if (System.IO.Directory.Exists(localGDrive)) gDrivePaths.Add(localGDrive);

                    if (gDrivePaths.Count == 1) return Path.Combine(gDrivePaths[0], "PuritySaves");
                    else if (gDrivePaths.Count > 1) return PromptUserForFolder("Multiple Google Drive locations detected. Please select one:");
                    else return PromptUserForFolder("Google Drive not found. Please select your Google Drive folder:");
                }
            }
            return PromptUserForFolder("Please select the base folder where you want to store your Purity saves:");
        }

        private string? PromptUserForFolder(string description)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.UseDescriptionForTitle = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    return Path.Combine(dialog.SelectedPath, "PuritySaves");
            }
            return null;
        }

        private bool IsSymbolicLink(string path)
        {
            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;

            DirectoryInfo? dirInfo = fileInfo.Directory;
            while (dirInfo != null)
            {
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                dirInfo = dirInfo.Parent;
            }
            return false;
        }

        private void CreateSymlinkAsAdmin(string originalPath, string targetCloudPath)
        {
            string backupPath = originalPath + ".backup";
            if (File.Exists(originalPath)) File.Move(originalPath, backupPath);

            string mklinkCommand = $"/c mklink \"{originalPath}\" \"{targetCloudPath}\"";
            ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", mklinkCommand)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(processInfo);
            if (process != null) process.WaitForExit();

            if (IsSymbolicLink(originalPath)) File.Delete(backupPath);
            else
            {
                File.Move(backupPath, originalPath);
                throw new InvalidOperationException("Failed to create symbolic link.");
            }
        }
    }
}