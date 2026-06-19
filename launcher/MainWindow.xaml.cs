using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SeapowerMultiplayer.Launcher.Services;

namespace SeapowerMultiplayer.Launcher
{
    public partial class MainWindow : Window
    {
        private const string CurrentVersion = LauncherVersions.LauncherVersion;
        private const string TrailerResourcePath =
            "pack://application:,,,/Assets/Video/sea-power-launch-trailer.mp4";

        private sealed record PlaylistTrack(
            string Title,
            string Artist,
            string ResourcePath,
            string FileName);

        private static readonly PlaylistTrack[] Playlist =
        {
            new(
                "Crashing Down",
                "Joyner Lucas",
                "pack://application:,,,/Assets/Music/01-crashing-down.mp3",
                "01-crashing-down.mp3"),
            new(
                "Cali Man",
                "Cali Man",
                "pack://application:,,,/Assets/Music/02-cali-man.mp3",
                "02-cali-man.mp3"),
            new(
                "GTA 6",
                "Joyner Lucas",
                "pack://application:,,,/Assets/Music/03-gta-6.mp3",
                "03-gta-6.mp3"),
            new(
                "I'm Ill",
                "Joyner Lucas",
                "pack://application:,,,/Assets/Music/04-im-ill.mp3",
                "04-im-ill.mp3"),
        };

        private readonly ConfigManager _config = new();
        private readonly MediaPlayer _musicPlayer = new();
        private readonly DispatcherTimer _videoLoopTimer;
        private readonly DispatcherTimer _bootTimer;
        private readonly DispatcherTimer _lobbyRefreshTimer;
        private readonly ObservableCollection<PublicLobby> _publicLobbies = new();
        private bool _gameRunning;
        private bool _activePluginConflict;
        private UpdateInfo? _pendingUpdate;
        private string _lastIP = "127.0.0.1";
        private bool _trackTransitioning;
        private bool _configLoaded;
        private bool _settingsDrawerOpen;
        private bool _lobbyBrowserOpen;
        private bool _lobbyRefreshInFlight;
        private int _bootStep;

        private static readonly string[] BootLines =
        {
            "POWER DISTRIBUTION BUS ................. ONLINE",
            "SPY-1 PHASED ARRAY .................... CALIBRATED",
            "COMMAND AND DECISION CORE ............. ONLINE",
            "STEAM MATCHMAKING LINK ................ STANDBY",
            "RAILWAY OPERATIONS DIRECTORY .......... ACQUIRING",
            "FOUR-PLAYER CONTROL AUTHORITY ......... VERIFIED",
            "TACTICAL DISPLAY ...................... READY",
        };

        public MainWindow()
        {
            LauncherDiagnostics.Trace("MainWindow constructor entered.");
            InitializeComponent();
            LauncherDiagnostics.Trace("MainWindow InitializeComponent completed.");
            TxtVersion.Text = $"v{CurrentVersion}";
            LobbyList.ItemsSource = _publicLobbies;

            _videoLoopTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _videoLoopTimer.Tick += VideoLoopTimer_Tick;

            _bootTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(330),
            };
            _bootTimer.Tick += BootTimer_Tick;

            _lobbyRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4),
            };
            _lobbyRefreshTimer.Tick += async (_, _) =>
            {
                if (_lobbyBrowserOpen)
                    await RefreshPublicLobbiesAsync();
            };

            _musicPlayer.MediaOpened += (_, _) => _trackTransitioning = false;
            _musicPlayer.MediaEnded += (_, _) =>
            {
                if (!_trackTransitioning)
                    ChangeTrack(1, autoPlay: true);
            };
            _musicPlayer.MediaFailed += (_, args) =>
            {
                _trackTransitioning = false;
                TxtStatus.Text = $"Music playback failed: {args.ErrorException?.Message}";
            };

            Loaded += OnLoaded;
            Closed += OnClosed;
            LauncherDiagnostics.Trace("MainWindow constructor completed.");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Maximized
                && e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Application.Current.Shutdown();

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LauncherDiagnostics.Trace("MainWindow Loaded entered.");
            EnterBorderlessFullscreen();
            _config.Load();
            _config.Settings.DirectoryUrl =
                ServiceEndpointResolver.Resolve(_config.Settings.DirectoryUrl);
            _config.Save();
            ApplyConfigToUI();
            _configLoaded = true;
            StartBootSequence();
            StartBattleVideo();
            StartMusicIfEnabled();

            if (string.IsNullOrEmpty(_config.Settings.GameDirectory) ||
                !GameDetector.IsValidGameDir(_config.Settings.GameDirectory))
            {
                var detected = GameDetector.AutoDetect();
                if (detected != null)
                {
                    _config.Settings.GameDirectory = detected;
                    _config.Save();
                }
            }

            TxtGamePath.Text = _config.Settings.GameDirectory ?? "";

            if (!string.IsNullOrEmpty(_config.Settings.GameDirectory))
                GameLauncher.CleanupProxy(_config.Settings.GameDirectory);

            UpdateInstallStatus();
            _ = LoadDirectoryConfigurationAsync();
            _ = LobbyServiceClient.TrackAsync(
                _config.Settings.DirectoryUrl,
                "launcher_started");

            if (Environment.GetCommandLineArgs().Contains("--post-update"))
                await PostUpdateRepairAsync();

            _ = CheckForUpdateAsync();
            LauncherDiagnostics.Trace("MainWindow Loaded completed.");
        }

        private void EnterBorderlessFullscreen()
        {
            WindowState = WindowState.Normal;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            LauncherDiagnostics.Trace("MainWindow Closed; shutting down media and application.");
            _videoLoopTimer.Stop();
            _bootTimer.Stop();
            _lobbyRefreshTimer.Stop();
            BattleVideo.Stop();
            BattleVideo.Close();
            _musicPlayer.Stop();
            _musicPlayer.Close();
            if (!Dispatcher.HasShutdownStarted)
                Application.Current.Shutdown();
        }

        private void StartBattleVideo()
        {
            try
            {
                var trailerPath = ExtractTrailer();
                BattleVideo.Source = new Uri(trailerPath, UriKind.Absolute);
                BattleVideo.Position = TimeSpan.Zero;
                BattleVideo.Play();
            }
            catch (Exception ex)
            {
                BattleVideo.Visibility = Visibility.Collapsed;
                BrollImage.Visibility = Visibility.Visible;
                TxtStatus.Text = $"Video unavailable: {ex.Message}";
            }
        }

        private static string ExtractTrailer()
        {
            var videoDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaPowerFourPlayer", "video-v1");
            Directory.CreateDirectory(videoDir);
            var videoPath = Path.Combine(videoDir, "sea-power-launch-trailer.mp4");

            var resource = Application.GetResourceStream(new Uri(TrailerResourcePath));
            if (resource == null)
                throw new FileNotFoundException("The embedded Steam trailer was not found.");

            using (resource.Stream)
            {
                if (!File.Exists(videoPath) ||
                    new FileInfo(videoPath).Length != resource.Stream.Length)
                {
                    using var output = File.Create(videoPath);
                    resource.Stream.CopyTo(output);
                }
            }

            return videoPath;
        }

        private void BattleVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            BrollImage.Visibility = Visibility.Collapsed;
            BattleVideo.Visibility = Visibility.Visible;
            _videoLoopTimer.Start();
        }

        private void BattleVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            BattleVideo.Stop();
            BattleVideo.Position = TimeSpan.Zero;
            BattleVideo.Play();
        }

        private void BattleVideo_MediaFailed(
            object sender,
            System.Windows.ExceptionRoutedEventArgs e)
        {
            BattleVideo.Visibility = Visibility.Collapsed;
            BrollImage.Visibility = Visibility.Visible;
            _videoLoopTimer.Stop();
            TxtStatus.Text = "Windows could not play the trailer; using fallback artwork.";
        }

        private void VideoLoopTimer_Tick(object? sender, EventArgs e)
        {
            if (!BattleVideo.NaturalDuration.HasTimeSpan)
                return;

            var duration = BattleVideo.NaturalDuration.TimeSpan;
            if (duration <= TimeSpan.Zero)
                return;

            if (BattleVideo.Position >= duration - TimeSpan.FromMilliseconds(650))
            {
                BattleVideo.Stop();
                BattleVideo.Position = TimeSpan.Zero;
                BattleVideo.Play();
            }
        }

        private void StartBootSequence()
        {
            _bootStep = 0;
            TxtBootLog.Text = "";
            TxtBootLine.Text = "ESTABLISHING COMMAND BUS...";
            TxtBootPercent.Text = "000%";
            BootProgress.Width = 0;
            BootOverlay.Visibility = Visibility.Visible;
            BootOverlay.Opacity = 1;
            _bootTimer.Start();
        }

        private void BootTimer_Tick(object? sender, EventArgs e)
        {
            BootSweepTransform.Angle = (BootSweepTransform.Angle + 28) % 360;
            if (_bootStep < BootLines.Length)
            {
                TxtBootLog.Text += (_bootStep == 0 ? "" : Environment.NewLine)
                                   + BootLines[_bootStep];
                TxtBootLine.Text = BootLines[_bootStep].Replace(".", "").Trim();
                _bootStep++;
                var percent = (int)Math.Round(_bootStep * 100.0 / BootLines.Length);
                TxtBootPercent.Text = $"{percent:000}%";
                if (BootProgress.Parent is FrameworkElement rail)
                    BootProgress.Width = rail.ActualWidth * percent / 100.0;
                return;
            }

            _bootTimer.Stop();
            TxtBootLine.Text = "COMBAT SYSTEM READY";
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
            {
                BeginTime = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) => BootOverlay.Visibility = Visibility.Collapsed;
            BootOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void StartMusicIfEnabled()
        {
            try
            {
                OpenCurrentTrack(_config.Settings.MusicEnabled);
            }
            catch
            {
                TxtStatus.Text = "The launcher opened, but Windows could not initialize music playback.";
            }
        }

        private void OpenCurrentTrack(bool autoPlay)
        {
            int index = Math.Clamp(_config.Settings.MusicTrackIndex, 0, Playlist.Length - 1);
            _config.Settings.MusicTrackIndex = index;
            var track = Playlist[index];
            var mediaPath = ExtractTrack(track);

            _trackTransitioning = true;
            _musicPlayer.Stop();
            _musicPlayer.Close();
            _musicPlayer.Open(new Uri(mediaPath, UriKind.Absolute));
            ApplyAudioLevel();
            if (autoPlay)
                _musicPlayer.Play();

            TxtTrackTitle.Text = track.Title;
            TxtTrackArtist.Text = track.Artist;
            BtnPlayPause.Content = autoPlay ? "Ⅱ" : "▶";
            _config.Save();
        }

        private static string ExtractTrack(PlaylistTrack track)
        {
            var mediaDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaPowerFourPlayer", "music-v1");
            Directory.CreateDirectory(mediaDir);
            var mediaPath = Path.Combine(mediaDir, track.FileName);

            var resource = Application.GetResourceStream(new Uri(track.ResourcePath));
            if (resource == null)
                throw new FileNotFoundException($"Embedded track was not found: {track.Title}");

            if (!File.Exists(mediaPath) || new FileInfo(mediaPath).Length != resource.Stream.Length)
            {
                using var output = File.Create(mediaPath);
                resource.Stream.CopyTo(output);
            }
            resource.Stream.Dispose();
            return mediaPath;
        }

        private void ChangeTrack(int delta, bool autoPlay)
        {
            int count = Playlist.Length;
            _config.Settings.MusicTrackIndex =
                (_config.Settings.MusicTrackIndex + delta + count) % count;
            _config.Settings.MusicEnabled = autoPlay;
            OpenCurrentTrack(autoPlay);
        }

        private void BtnPreviousTrack_Click(object sender, RoutedEventArgs e)
            => ChangeTrack(-1, autoPlay: true);

        private void BtnNextTrack_Click(object sender, RoutedEventArgs e)
            => ChangeTrack(1, autoPlay: true);

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            _config.Settings.MusicEnabled = !_config.Settings.MusicEnabled;
            if (_config.Settings.MusicEnabled)
            {
                _musicPlayer.Play();
                BtnPlayPause.Content = "Ⅱ";
            }
            else
            {
                _musicPlayer.Pause();
                BtnPlayPause.Content = "▶";
            }
            _config.Save();
        }

        private void VolumeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_configLoaded)
                return;

            _config.Settings.MusicVolume = Math.Clamp(e.NewValue, 0, 1);
            ApplyAudioLevel();
            _config.Save();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            _config.Settings.MusicMuted = !_config.Settings.MusicMuted;
            ApplyAudioLevel();
            _config.Save();
        }

        private void ApplyAudioLevel()
        {
            _musicPlayer.Volume = _config.Settings.MusicMuted
                ? 0
                : Math.Clamp(_config.Settings.MusicVolume, 0, 1);
            BtnMute.Content = _config.Settings.MusicMuted ? "SOUND" : "MUTE";
            BtnMute.ToolTip = _config.Settings.MusicMuted ? "Unmute music" : "Mute music";
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
            => OpenSettingsDrawer();

        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveUIToConfig();
            CloseSettingsDrawer();
            ValidateNetworkSettings();
        }

        private void SettingsScrim_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            SaveUIToConfig();
            CloseSettingsDrawer();
            ValidateNetworkSettings();
        }

        private void OpenSettingsDrawer()
        {
            if (_settingsDrawerOpen)
                return;
            if (_lobbyBrowserOpen)
                CloseLobbyBrowser();

            _settingsDrawerOpen = true;
            SettingsScrim.Visibility = Visibility.Visible;
            SettingsDrawer.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SettingsDrawer.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
                {
                    EasingFunction = ease,
                });

            if (SettingsDrawer.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(474, 0, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = ease,
                    });
            }
        }

        private void CloseSettingsDrawer()
        {
            if (!_settingsDrawerOpen)
                return;

            _settingsDrawerOpen = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var opacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = ease,
            };
            opacity.Completed += (_, _) =>
            {
                SettingsDrawer.Visibility = Visibility.Collapsed;
                SettingsScrim.Visibility = Visibility.Collapsed;
            };
            SettingsDrawer.BeginAnimation(OpacityProperty, opacity);

            if (SettingsDrawer.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(0, 474, TimeSpan.FromMilliseconds(210))
                    {
                        EasingFunction = ease,
                    });
            }
        }

        private async Task LoadDirectoryConfigurationAsync()
        {
            var serviceConfig = await LobbyServiceClient.GetConfigAsync(
                _config.Settings.DirectoryUrl);
            if (serviceConfig == null)
                return;

            TxtAnnouncement.Text = string.IsNullOrWhiteSpace(serviceConfig.Announcement)
                ? "JOIN THE SEAS // PUBLIC OPERATIONS ONLINE"
                : serviceConfig.Announcement.ToUpperInvariant();

            if (serviceConfig.MaintenanceMode)
                TxtStatus.Text = "Public lobby service is in maintenance mode.";
        }

        private async void BtnLobbies_Click(object sender, RoutedEventArgs e)
        {
            OpenLobbyBrowser();
            await RefreshPublicLobbiesAsync();
        }

        private void BtnCloseLobbies_Click(object sender, RoutedEventArgs e)
            => CloseLobbyBrowser();

        private void LobbyScrim_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
            => CloseLobbyBrowser();

        private async void BtnRefreshLobbies_Click(object sender, RoutedEventArgs e)
            => await RefreshPublicLobbiesAsync();

        private void LobbyList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
            => BtnJoinPublicLobby.IsEnabled =
                LobbyList.SelectedItem is PublicLobby && !_gameRunning;

        private void OpenLobbyBrowser()
        {
            if (_lobbyBrowserOpen)
                return;
            if (_settingsDrawerOpen)
                CloseSettingsDrawer();

            _lobbyBrowserOpen = true;
            _lobbyRefreshTimer.Start();
            LobbyScrim.Visibility = Visibility.Visible;
            LobbyBrowser.Visibility = Visibility.Visible;
            TxtPublicLobbyName.Text = _config.Settings.PublicLobbyName;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            LobbyBrowser.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = ease,
                });
            if (LobbyBrowser.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(-780, 0, TimeSpan.FromMilliseconds(300))
                    {
                        EasingFunction = ease,
                    });
            }

            _ = LobbyServiceClient.TrackAsync(
                _config.Settings.DirectoryUrl,
                "lobby_list_opened");
        }

        private void CloseLobbyBrowser()
        {
            if (!_lobbyBrowserOpen)
                return;

            _lobbyBrowserOpen = false;
            _lobbyRefreshTimer.Stop();
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = ease,
            };
            fade.Completed += (_, _) =>
            {
                LobbyBrowser.Visibility = Visibility.Collapsed;
                LobbyScrim.Visibility = Visibility.Collapsed;
            };
            LobbyBrowser.BeginAnimation(OpacityProperty, fade);
            if (LobbyBrowser.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(0, -780, TimeSpan.FromMilliseconds(210))
                    {
                        EasingFunction = ease,
                    });
            }
        }

        private async Task RefreshPublicLobbiesAsync()
        {
            if (_lobbyRefreshInFlight)
                return;

            _lobbyRefreshInFlight = true;
            var selectedLobbyId =
                (LobbyList.SelectedItem as PublicLobby)?.SteamLobbyId;
            LobbyList.IsEnabled = false;
            LobbyEmptyState.Visibility = Visibility.Visible;
            TxtLobbyEmptyTitle.Text = "SCANNING PUBLIC OPERATIONS";
            TxtLobbyEmptyDetail.Text = "Contacting the lobby directory...";
            TxtLobbyServiceStatus.Text = "DIRECTORY LINK ACTIVE";
            BtnJoinPublicLobby.IsEnabled = false;

            try
            {
                var result = await LobbyServiceClient.GetLobbiesAsync(
                    _config.Settings.DirectoryUrl);
                _publicLobbies.Clear();
                foreach (var lobby in result.Lobbies.Where(x => x.PlayerCount < x.MaxPlayers))
                    _publicLobbies.Add(lobby);

                if (!string.IsNullOrEmpty(selectedLobbyId))
                    LobbyList.SelectedItem = _publicLobbies.FirstOrDefault(
                        x => x.SteamLobbyId == selectedLobbyId);

                LobbyEmptyState.Visibility = _publicLobbies.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                TxtLobbyEmptyTitle.Text = "NO OPEN OPERATIONS";
                TxtLobbyEmptyDetail.Text = "Create a public lobby to bring a fleet online.";
                TxtLobbyServiceStatus.Text =
                    $"DIRECTORY ONLINE // {_publicLobbies.Count} OPEN OPERATION{(_publicLobbies.Count == 1 ? "" : "S")}";
            }
            catch (Exception ex)
            {
                _publicLobbies.Clear();
                LobbyEmptyState.Visibility = Visibility.Visible;
                TxtLobbyEmptyTitle.Text = "DIRECTORY UNAVAILABLE";
                TxtLobbyEmptyDetail.Text = ex is System.Net.Http.HttpRequestException
                    ? "Set the Railway domain, then refresh. Steam invites still work."
                    : "Steam invites and normal launch remain available.";
                var endpoint = ServiceEndpointResolver.Resolve(
                    _config.Settings.DirectoryUrl);
                TxtLobbyServiceStatus.Text = $"SERVICE OFFLINE // {endpoint}";
            }
            finally
            {
                LobbyList.IsEnabled = true;
                _lobbyRefreshInFlight = false;
            }
        }

        private async void BtnCreatePublicLobby_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidInstall())
                return;

            RbSteam.IsChecked = true;
            RbHost.IsChecked = true;
            ChkAutoConnect.IsChecked = false;
            TxtMissileHz.Text = "20";
            TxtUnitHz.Text = "10";
            _config.Settings.PublicLobbyName = NormalizeLobbyName(TxtPublicLobbyName.Text);
            TxtPublicLobbyName.Text = _config.Settings.PublicLobbyName;
            SaveUIToConfig();

            var escapedName = _config.Settings.PublicLobbyName.Replace("\"", "");
            GameCommandService.WriteCreatePublicLobby(
                _config.Settings.PublicLobbyName,
                openInviteOverlay: true);
            TxtLobbyServiceStatus.Text =
                $"CREATING PUBLIC OPERATION // {_config.Settings.PublicLobbyName.ToUpperInvariant()}";
            await LaunchGameAsync(
                $"+sp4p_public_host +sp4p_lobby_name \"{escapedName}\"",
                "Starting public Steam operation...");
        }

        private async void BtnJoinPublicLobby_Click(object sender, RoutedEventArgs e)
        {
            if (LobbyList.SelectedItem is not PublicLobby selected)
                return;

            await JoinPublicLobbyAsync(selected);
        }

        private async void BtnJoinLobbyRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { CommandParameter: PublicLobby selected })
                return;

            LobbyList.SelectedItem = selected;
            await JoinPublicLobbyAsync(selected);
        }

        private async Task JoinPublicLobbyAsync(PublicLobby selected)
        {
            if (_gameRunning || !ValidInstall())
                return;

            RbSteam.IsChecked = true;
            RbClient.IsChecked = true;
            ChkAutoConnect.IsChecked = false;
            SaveUIToConfig();
            GameCommandService.WriteJoinLobby(selected.SteamLobbyId);
            CloseLobbyBrowser();

            await LobbyServiceClient.TrackAsync(
                _config.Settings.DirectoryUrl,
                "join_attempted",
                selected.SteamLobbyId,
                selected.LobbyName);
            await LaunchGameAsync(
                $"+connect_lobby {selected.SteamLobbyId}",
                $"Joining {selected.LobbyName}...");
        }

        private static string NormalizeLobbyName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "Open Fleet" : value.Trim();
            name = new string(name.Where(c => !char.IsControl(c) && c != '"').ToArray());
            return name.Length <= 64 ? name : name[..64];
        }

        private async Task PostUpdateRepairAsync()
        {
            var gameDir = _config.Settings.GameDirectory;
            if (string.IsNullOrEmpty(gameDir) || !GameDetector.IsValidGameDir(gameDir))
                return;

            var progress = new Progress<string>(message => TxtStatus.Text = message);
            try
            {
                await Installer.RepairAsync(gameDir, progress);
                TxtStatus.Text = "Update installed.";
                UpdateInstallStatus();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Update repair failed: {ex.Message}";
            }
        }

        private async Task CheckForUpdateAsync()
        {
            var update = await UpdateService.CheckForUpdateAsync(CurrentVersion);
            if (update == null)
                return;

            _pendingUpdate = update;
            TxtUpdateInfo.Text = $"Launcher v{update.Version} is available.";
            PnlUpdate.Visibility = Visibility.Visible;
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null)
                return;

            SetControlsEnabled(false);
            BtnUpdate.IsEnabled = false;
            var progress = new Progress<string>(message => TxtStatus.Text = message);

            try
            {
                await UpdateService.ApplyUpdateAsync(_pendingUpdate, progress);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Update failed: {ex.Message}";
                SetControlsEnabled(true);
                BtnUpdate.IsEnabled = true;
            }
        }

        private void ApplyConfigToUI()
        {
            var isSteam = _config.Settings.Transport == "Steam";
            RbSteam.IsChecked = isSteam;
            RbDirectIP.IsChecked = !isSteam;
            PnlDirectIP.Visibility = isSteam ? Visibility.Collapsed : Visibility.Visible;

            RbHost.IsChecked = _config.Settings.IsHost;
            RbClient.IsChecked = !_config.Settings.IsHost;
            TxtHostIP.Text = _config.Settings.HostIP;
            TxtPort.Text = _config.Settings.Port.ToString();
            ChkAutoConnect.IsChecked = _config.Settings.AutoConnect;
            ChkTimeVote.IsChecked = _config.Settings.TimeVote;
            ChkPvP.IsChecked = _config.Settings.PvP;
            TxtMissileHz.Text = _config.Settings.MissileStateHz.ToString();
            TxtUnitHz.Text = _config.Settings.UnitStateHz.ToString();
            MissileRateSlider.Value = _config.Settings.MissileStateHz;
            UnitRateSlider.Value = _config.Settings.UnitStateHz;
            TxtPlayerName.Text = _config.Settings.PlayerName;
            TxtPublicLobbyName.Text = _config.Settings.PublicLobbyName;
            VolumeSlider.Value = _config.Settings.MusicVolume;
            ApplyAudioLevel();
            TxtHostIP.IsEnabled = !_config.Settings.IsHost;
            RbHostBlue.IsChecked = _config.Settings.PreferredTeam != 1;
            RbHostRed.IsChecked = _config.Settings.PreferredTeam == 1;
            PnlHostTeam.Visibility = _config.Settings.IsHost
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SaveUIToConfig()
        {
            _config.Settings.Transport = RbSteam.IsChecked == true ? "Steam" : "LiteNetLib";
            _config.Settings.IsHost = RbHost.IsChecked == true;
            _config.Settings.HostIP = TxtHostIP.Text.Trim();
            if (int.TryParse(TxtPort.Text.Trim(), out var port))
                _config.Settings.Port = port;

            _config.Settings.AutoConnect = ChkAutoConnect.IsChecked == true;
            _config.Settings.TimeVote = ChkTimeVote.IsChecked == true;
            _config.Settings.PvP = ChkPvP.IsChecked == true;
            if (int.TryParse(TxtMissileHz.Text.Trim(), out var missileHz))
                _config.Settings.MissileStateHz = Math.Clamp(missileHz, 1, 60);
            if (int.TryParse(TxtUnitHz.Text.Trim(), out var unitHz))
                _config.Settings.UnitStateHz = Math.Clamp(unitHz, 1, 60);

            _config.Settings.PlayerName = string.IsNullOrWhiteSpace(TxtPlayerName.Text)
                ? Environment.UserName
                : TxtPlayerName.Text.Trim();
            _config.Settings.PublicLobbyName =
                NormalizeLobbyName(TxtPublicLobbyName.Text);
            _config.Settings.PreferredSlot = 0;
            _config.Settings.PreferredTeam = _config.Settings.IsHost
                ? RbHostRed.IsChecked == true ? 1 : 0
                : 255;
            _config.Settings.PreferredRole = 0;
            _config.Save();
        }

        private void UpdateInstallStatus()
        {
            var gameDir = TxtGamePath.Text;
            if (string.IsNullOrEmpty(gameDir) || !GameDetector.IsValidGameDir(gameDir))
            {
                StatusDot.Fill = FindResource("ErrorRed") as SolidColorBrush;
                TxtInstallStatus.Text = "Game not found";
                BtnInstall.IsEnabled = false;
                BtnLaunch.IsEnabled = false;
                return;
            }

            BtnInstall.IsEnabled = true;

            var bepinex = GameDetector.IsBepInExInstalled(gameDir);
            var mod = GameDetector.IsModInstalled(gameDir);
            var proxy = GameDetector.IsProxyStored(gameDir);
            _activePluginConflict = Installer.HasActiveConflictingPlugin(gameDir);

            if (bepinex && mod && proxy && !_activePluginConflict)
            {
                StatusDot.Fill = FindResource("SuccessGreen") as SolidColorBrush;
                TxtInstallStatus.Text = "Installed";
                BtnInstall.Content = "Repair";
                BtnLaunch.IsEnabled = !_gameRunning;
            }
            else if (_activePluginConflict)
            {
                StatusDot.Fill = FindResource("ErrorRed") as SolidColorBrush;
                TxtInstallStatus.Text = "Repair removes old mod";
                BtnInstall.Content = "Repair";
                BtnLaunch.IsEnabled = false;
            }
            else if (bepinex && !proxy)
            {
                StatusDot.Fill = FindResource("WarningOrange") as SolidColorBrush;
                TxtInstallStatus.Text = "Repair needed";
                BtnInstall.Content = "Repair";
                BtnLaunch.IsEnabled = false;
            }
            else
            {
                StatusDot.Fill = FindResource("WarningOrange") as SolidColorBrush;
                TxtInstallStatus.Text = "Not installed";
                BtnInstall.Content = "Install";
                BtnLaunch.IsEnabled = false;
            }
        }

        private void Transport_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlDirectIP == null)
                return;

            PnlDirectIP.Visibility = RbSteam.IsChecked == true
                ? Visibility.Collapsed
                : Visibility.Visible;
            ValidateNetworkSettings();
        }

        private void Role_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtHostIP == null)
                return;

            if (TxtHostIP.Text != "0.0.0.0" && IsValidIP(TxtHostIP.Text))
                _lastIP = TxtHostIP.Text;

            TxtHostIP.IsEnabled = RbClient.IsChecked == true;
            TxtHostIP.Text = RbHost.IsChecked == true ? "0.0.0.0" : _lastIP;
            PnlHostTeam.Visibility = RbHost.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            ValidateNetworkSettings();
        }

        private static bool IsValidIP(string ip)
        {
            if (ip == "0.0.0.0")
                return true;
            return System.Net.IPAddress.TryParse(ip, out _) && ip.Count(c => c == '.') == 3;
        }

        private static bool IsValidPort(string enteredPort)
            => int.TryParse(enteredPort, out var port) && port is >= 1 and <= 65535;

        private static bool IsValidHz(string entered)
            => int.TryParse(entered.Trim(), out var hz) && hz is >= 1 and <= 60;

        private void TxtHostIP_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtHostIP.BorderBrush = string.IsNullOrEmpty(TxtHostIP.Text) || !IsValidIP(TxtHostIP.Text)
                ? Brushes.Red
                : FindResource("BorderColor") as Brush;
            ValidateNetworkSettings();
        }

        private void TxtPort_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtPort.BorderBrush = !IsValidPort(TxtPort.Text)
                ? Brushes.Red
                : FindResource("BorderColor") as Brush;
            ValidateNetworkSettings();
        }

        private void TxtHz_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox box)
            {
                box.BorderBrush = IsValidHz(box.Text)
                    ? FindResource("BorderColor") as Brush
                    : Brushes.Red;

                if (int.TryParse(box.Text, out var hz))
                {
                    if (box == TxtMissileHz && MissileRateSlider != null &&
                        Math.Abs(MissileRateSlider.Value - hz) > 0.1)
                        MissileRateSlider.Value = hz;
                    if (box == TxtUnitHz && UnitRateSlider != null &&
                        Math.Abs(UnitRateSlider.Value - hz) > 0.1)
                        UnitRateSlider.Value = hz;
                }
            }
            ValidateNetworkSettings();
        }

        private void RateSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            var value = ((int)Math.Round(e.NewValue)).ToString();
            if (sender == MissileRateSlider && TxtMissileHz != null &&
                TxtMissileHz.Text != value)
                TxtMissileHz.Text = value;
            if (sender == UnitRateSlider && TxtUnitHz != null &&
                TxtUnitHz.Text != value)
                TxtUnitHz.Text = value;
        }

        private void ValidateNetworkSettings()
        {
            if (BtnLaunch == null || TxtHostIP == null || TxtPort == null ||
                TxtMissileHz == null || TxtUnitHz == null || TxtStatus == null)
                return;

            var directValid = RbSteam.IsChecked == true ||
                              (IsValidIP(TxtHostIP.Text) && IsValidPort(TxtPort.Text));
            var ratesValid = IsValidHz(TxtMissileHz.Text) && IsValidHz(TxtUnitHz.Text);
            var installed = ValidInstall(updateUi: false);

            BtnLaunch.IsEnabled = directValid && ratesValid && installed && !_gameRunning;
            if (!directValid || !ratesValid)
                TxtStatus.Text = "Check the highlighted connection settings.";
            else if (installed && !_gameRunning)
                TxtStatus.Text = "Ready";
        }

        private bool ValidInstall(bool updateUi = true)
        {
            var gameDir = TxtGamePath.Text;
            var valid = !string.IsNullOrEmpty(gameDir) &&
                        GameDetector.IsValidGameDir(gameDir) &&
                        GameDetector.IsBepInExInstalled(gameDir) &&
                        GameDetector.IsModInstalled(gameDir) &&
                        GameDetector.IsProxyStored(gameDir) &&
                        !_activePluginConflict;
            if (!valid && updateUi)
                UpdateInstallStatus();
            return valid;
        }

        private void BtnDiscord_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/rMMnwJHc8w")
            {
                UseShellExecute = true,
            });
        }

        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            var window = new FeedbackWindow(TxtGamePath.Text) { Owner = this };
            window.ShowDialog();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Sea Power.exe",
                Filter = "Sea Power|Sea Power.exe",
                FileName = "Sea Power.exe",
            };

            if (dialog.ShowDialog() != true)
                return;

            var directory = Path.GetDirectoryName(dialog.FileName)!;
            TxtGamePath.Text = directory;
            _config.Settings.GameDirectory = directory;
            _config.Save();
            UpdateInstallStatus();
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var gameDir = TxtGamePath.Text;
            if (string.IsNullOrEmpty(gameDir))
                return;

            SetControlsEnabled(false);
            var progress = new Progress<string>(message => TxtStatus.Text = message);

            try
            {
                var alreadyInstalled = GameDetector.IsBepInExInstalled(gameDir) &&
                                       GameDetector.IsProxyStored(gameDir);
                if (alreadyInstalled)
                    await Installer.RepairAsync(gameDir, progress);
                else
                    await Installer.InstallAsync(gameDir, progress);

                SaveUIToConfig();
                ConfigManager.WriteBepInExConfig(gameDir, _config.Settings);
                TxtStatus.Text = "Installation complete.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Installation failed: {ex.Message}";
                MessageBox.Show(
                    ex.Message,
                    "Installation failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetControlsEnabled(true);
                UpdateInstallStatus();
            }
        }

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
            => await LaunchGameAsync();

        private async Task LaunchGameAsync(
            string arguments = "",
            string launchStatus = "Launching Sea Power...")
        {
            var gameDir = TxtGamePath.Text;
            if (!ValidInstall())
                return;

            if (_config.Settings.AcknowledgedVersion != CurrentVersion)
            {
                var disclaimer = new DisclaimerWindow { Owner = this };
                if (disclaimer.ShowDialog() != true)
                    return;

                _config.Settings.AcknowledgedVersion = CurrentVersion;
                _config.Save();
            }

            SaveUIToConfig();

            try
            {
                ConfigManager.WriteBepInExConfig(gameDir, _config.Settings);
                TxtStatus.Text = launchStatus;
                _gameRunning = true;
                SetControlsEnabled(false);
                _musicPlayer.Pause();

                await GameLauncher.LaunchAsync(gameDir, () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _gameRunning = false;
                        TxtStatus.Text = "Ready";
                        SetControlsEnabled(true);
                        UpdateInstallStatus();
                        if (_config.Settings.MusicEnabled)
                            _musicPlayer.Play();
                    });
                }, arguments);

                TxtStatus.Text = "Sea Power is running.";
                _ = LobbyServiceClient.TrackAsync(
                    _config.Settings.DirectoryUrl,
                    "game_launched",
                    detail: arguments);
            }
            catch (Exception ex)
            {
                _gameRunning = false;
                TxtStatus.Text = $"Launch failed: {ex.Message}";
                SetControlsEnabled(true);
                UpdateInstallStatus();
                if (_config.Settings.MusicEnabled)
                    _musicPlayer.Play();
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            BtnInstall.IsEnabled = enabled;
            BtnLaunch.IsEnabled = enabled && ValidInstall(updateUi: false) && !_gameRunning;
            BtnBrowse.IsEnabled = enabled;
            RbSteam.IsEnabled = enabled;
            RbDirectIP.IsEnabled = enabled;
            RbHost.IsEnabled = enabled;
            RbClient.IsEnabled = enabled;
            TxtHostIP.IsEnabled = enabled && RbClient.IsChecked == true;
            TxtPort.IsEnabled = enabled;
            TxtPlayerName.IsEnabled = enabled;
            RbHostBlue.IsEnabled = enabled && RbHost.IsChecked == true;
            RbHostRed.IsEnabled = enabled && RbHost.IsChecked == true;
            ChkAutoConnect.IsEnabled = enabled;
            ChkTimeVote.IsEnabled = enabled;
            ChkPvP.IsEnabled = enabled;
            TxtMissileHz.IsEnabled = enabled;
            TxtUnitHz.IsEnabled = enabled;
            BtnFeedback.IsEnabled = enabled;
            BtnDiscord.IsEnabled = enabled;
            BtnCreatePublicLobby.IsEnabled = enabled && ValidInstall(updateUi: false);
            BtnJoinPublicLobby.IsEnabled =
                enabled && LobbyList.SelectedItem is PublicLobby && !_gameRunning;
        }
    }
}
