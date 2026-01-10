using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using PTZCameraControl.Models;
using PTZCameraControl.Services;

namespace PTZCameraControl
{
    /// <summary>
    /// Main Window for PTZ Camera Control Application
    /// Handles UI, ONVIF camera communication, and video streaming via LibVLC
    /// </summary>
    public partial class MainWindow : Window
    {
        // Service instances for ONVIF communication and camera discovery
        private readonly OnvifPtzService _ptzService;              // ONVIF PTZ control service
        private readonly OnvifDiscoveryService _discoveryService;  // ONVIF device discovery service
        private readonly CameraSettings _settings;                 // Persistent camera settings
        
        // LibVLC components for video streaming
        private LibVLC? _libVLC;                                   // LibVLC core instance (must be initialized before use)
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;      // Media player for video playback
        private bool _isVideoPlaying;                              // Track video playback state
        private bool _libVlcInitialized = false;                  // Flag to track LibVLC Core initialization
        
        // Cancellation tokens for async operations
        private CancellationTokenSource? _autoDetectCts;           // For canceling RTSP stream auto-detection
        private CancellationTokenSource? _discoveryCts;            // For canceling camera discovery

        /// <summary>
        /// Initialize main window and set up services
        /// LibVLC initialization is deferred until window is loaded (MainWindow_Loaded event)
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            // Initialize ONVIF PTZ service and subscribe to events
            _ptzService = new OnvifPtzService();
            _ptzService.StatusChanged += PtzService_StatusChanged;
            _ptzService.ErrorOccurred += PtzService_ErrorOccurred;
            _ptzService.StreamUrlDiscovered += PtzService_StreamUrlDiscovered;

            // Initialize ONVIF discovery service for finding cameras on network
            _discoveryService = new OnvifDiscoveryService();
            _discoveryService.CameraDiscovered += DiscoveryService_CameraDiscovered;

            // Load saved camera settings
            _settings = CameraSettings.Load();
            LoadSettings();
            UpdateConnectionIndicator(false);
            
            // Initialize LibVLC after window is loaded (deferred initialization)
            // This ensures the window is fully initialized before loading native libraries
            this.Loaded += MainWindow_Loaded;
        }

        /// <summary>
        /// Initialize LibVLC Core with proper path handling
        /// LibVLC requires native DLLs (libvlc.dll, libvlccore.dll) to be in the correct location
        /// Searches multiple common paths where the native libraries might be located
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if already initialized (prevent double initialization)
                if (_libVlcInitialized)
                {
                    System.Diagnostics.Debug.WriteLine("LibVLC already initialized");
                    return;
                }

                // Get executable directory to search for LibVLC native libraries
                var exeLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var exeDir = !string.IsNullOrEmpty(exeLocation) 
                    ? System.IO.Path.GetDirectoryName(exeLocation) 
                    : System.AppDomain.CurrentDomain.BaseDirectory;
                
                // Search paths where LibVLC native libraries might be located
                // Order matters - check most specific paths first
                var libVlcPaths = new[]
                {
                    System.IO.Path.Combine(exeDir ?? "", "libvlc", "win-x64"),           // Standard deployment path
                    System.IO.Path.Combine(exeDir ?? "", "runtimes", "win-x64", "native"), // NuGet package path
                    exeDir ?? ""                                                           // Executable directory
                };

                // Find the directory containing libvlc.dll
                string? libVlcPath = null;
                foreach (var path in libVlcPaths)
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        var libvlcDll = System.IO.Path.Combine(path, "libvlc.dll");
                        if (System.IO.File.Exists(libvlcDll))
                        {
                            libVlcPath = path;
                            System.Diagnostics.Debug.WriteLine($"Found libvlc.dll at: {libvlcDll}");
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(libVlcPath))
                {
                    // Verify both required DLLs exist before initializing
                    var libvlcDll = System.IO.Path.Combine(libVlcPath, "libvlc.dll");
                    var libvlccoreDll = System.IO.Path.Combine(libVlcPath, "libvlccore.dll");
                    
                    if (!System.IO.File.Exists(libvlccoreDll))
                    {
                        throw new System.IO.FileNotFoundException($"libvlccore.dll not found in {libVlcPath}. Both libvlc.dll and libvlccore.dll are required.");
                    }

                    // Initialize LibVLC Core with explicit path
                    // CRITICAL: This must succeed before any LibVLC instance creation
                    Core.Initialize(libVlcPath);
                    _libVlcInitialized = true;
                    System.Diagnostics.Debug.WriteLine($"LibVLC Core initialized from: {libVlcPath}");
                    System.Diagnostics.Debug.WriteLine($"Verified libvlc.dll: {libvlcDll}");
                    System.Diagnostics.Debug.WriteLine($"Verified libvlccore.dll: {libvlccoreDll}");
                    UpdateStatus("LibVLC initialized successfully", false);
                }
                else
                {
                    // Try default initialization (LibVLC searches common system paths)
                    // This is a fallback if libraries aren't in expected locations
                    System.Diagnostics.Debug.WriteLine("LibVLC path not found, trying default initialization");
                    try
                    {
                        Core.Initialize();
                        _libVlcInitialized = true;
                        System.Diagnostics.Debug.WriteLine("LibVLC initialized with default search");
                        UpdateStatus("LibVLC initialized successfully", false);
                    }
                    catch (Exception defaultInitEx)
                    {
                        throw new Exception($"Default LibVLC initialization failed. Native libraries not found. Error: {defaultInitEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // LibVLC initialization failed - disable video streaming but allow PTZ controls to work
                _libVlcInitialized = false;
                System.Diagnostics.Debug.WriteLine($"LibVLC initialization error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                UpdateStatus($"⚠️ LibVLC initialization failed: {ex.Message}. Video streaming disabled. PTZ controls will still work.", true);
                // Continue without LibVLC - video streaming won't work but UI will load
            }
        }

        private void LoadSettings()
        {
            HostTextBox.Text = _settings.Host;
            PortTextBox.Text = _settings.Port.ToString();
            UsernameTextBox.Text = _settings.Username;
            // Load password from settings, or use default if empty
            PasswordBox.Password = !string.IsNullOrEmpty(_settings.Password) ? _settings.Password : "XTL.a1.1000!";
            PanTiltSpeedSlider.Value = _settings.PanSpeed;
            ZoomSpeedSlider.Value = _settings.ZoomSpeed;
            if (!string.IsNullOrEmpty(_settings.StreamUrl))
            {
                StreamUrlTextBox.Text = _settings.StreamUrl;
            }
        }

        private void SaveSettings()
        {
            _settings.Host = HostTextBox.Text;
            if (int.TryParse(PortTextBox.Text, out int port))
            {
                _settings.Port = port;
            }
            _settings.Username = UsernameTextBox.Text;
            _settings.Password = PasswordBox.Password;
            _settings.PanSpeed = (float)PanTiltSpeedSlider.Value;
            _settings.ZoomSpeed = (float)ZoomSpeedSlider.Value;
            _settings.StreamUrl = StreamUrlTextBox.Text;
            _settings.Save();
        }

        private void UpdateConnectionIndicator(bool connected)
        {
            if (connected)
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                ConnectionLabel.Text = "ONLINE";
            }
            else
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 23, 68));
                ConnectionLabel.Text = "OFFLINE";
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ptzService.IsConnected)
            {
                _ptzService.Disconnect();
                StopVideo();
                ConnectButton.Content = "⚡ CONNECT";
                UpdateConnectionIndicator(false);
                UpdateControlsState(false);
                return;
            }

            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "CONNECTING...";

            var host = HostTextBox.Text.Trim();
            if (!int.TryParse(PortTextBox.Text, out int port))
            {
                port = 80;
            }
            var username = UsernameTextBox.Text.Trim();
            var password = PasswordBox.Password;
            
            // Try to extract ONVIF username from username field if format is "username,onvifusername"
            // Or check if there's a separate ONVIF username field
            string? onvifUsername = null;
            if (username.Contains(','))
            {
                var parts = username.Split(',');
                username = parts[0].Trim();
                onvifUsername = parts.Length > 1 ? parts[1].Trim() : null;
            }
            // Common ONVIF usernames will be tried automatically if connection fails

            var success = await _ptzService.ConnectAsync(host, port, username, password, onvifUsername);

            if (success)
            {
                ConnectButton.Content = "⛔ DISCONNECT";
                UpdateConnectionIndicator(true);
                UpdateControlsState(true);
                SaveSettings();
            }
            else
            {
                ConnectButton.Content = "⚡ CONNECT";
                UpdateConnectionIndicator(false);
                UpdateControlsState(false);
            }

            ConnectButton.IsEnabled = true;
        }

        private void UpdateControlsState(bool connected)
        {
            HostTextBox.IsEnabled = !connected;
            PortTextBox.IsEnabled = !connected;
            UsernameTextBox.IsEnabled = !connected;
            PasswordBox.IsEnabled = !connected;
        }

        /// <summary>
        /// Pings the camera IP address to test network connectivity
        /// 
        /// Uses ICMP ping to verify the camera is reachable on the network.
        /// This is useful for troubleshooting connection issues before attempting ONVIF connection.
        /// 
        /// Timeout: 3 seconds
        /// </summary>
        private async void PingButton_Click(object sender, RoutedEventArgs e)
        {
            var host = HostTextBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                UpdateStatus("Please enter a camera IP address", true);
                return;
            }

            // Disable button during ping to prevent multiple simultaneous pings
            PingButton.IsEnabled = false;
            PingButton.Content = "PINGING...";
            UpdateStatus($"Pinging {host}...", false);

            try
            {
                // Send ICMP ping with 3 second timeout
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);

                if (reply.Status == IPStatus.Success)
                {
                    // Success - show response time
                    UpdateStatus($"✓ Ping successful! Response time: {reply.RoundtripTime}ms", false);
                }
                else
                {
                    // Failed - show failure reason (TimedOut, DestinationUnreachable, etc.)
                    UpdateStatus($"✗ Ping failed: {reply.Status}", true);
                }
            }
            catch (Exception ex)
            {
                // Network error or other exception
                UpdateStatus($"✗ Ping error: {ex.Message}", true);
            }
            finally
            {
                // Re-enable button
                PingButton.IsEnabled = true;
                PingButton.Content = "🔍 PING";
            }
        }

        /// <summary>
        /// Start ONVIF camera discovery on the local network
        /// Uses WS-Discovery (UDP multicast) to find ONVIF-compatible cameras
        /// Discovery runs for 5 seconds and listens for camera responses
        /// </summary>
        private async void DiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            // Update UI to show discovery in progress
            DiscoveryButton.IsEnabled = false;
            DiscoveryButton.Content = "DISCOVERING...";
            StopDiscoveryButton.IsEnabled = true;
            DiscoveredCamerasListBox.Items.Clear();
            UpdateStatus("Discovering ONVIF cameras on network...", false);

            // Cancel any existing discovery and start new one
            _discoveryCts?.Cancel();
            _discoveryCts = new CancellationTokenSource();

            try
            {
                // Start discovery - sends UDP multicast probe and listens for responses
                // Timeout of 5 seconds should be enough for most networks
                var cameras = await _discoveryService.DiscoverAsync(timeoutSeconds: 5, _discoveryCts.Token);

                if (_discoveryCts.Token.IsCancellationRequested)
                {
                    UpdateStatus("Discovery cancelled.", false);
                }
                else if (cameras.Count == 0)
                {
                    UpdateStatus("No cameras found. Make sure cameras are ONVIF compatible and on the same network.", true);
                }
                else
                {
                    UpdateStatus($"Found {cameras.Count} camera(s)", false);
                    
                    // Populate list box
                    foreach (var camera in cameras)
                    {
                        DiscoveredCamerasListBox.Items.Add(camera);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Discovery error: {ex.Message}", true);
            }
            finally
            {
                DiscoveryButton.IsEnabled = true;
                DiscoveryButton.Content = "🔎 DISCOVER";
                StopDiscoveryButton.IsEnabled = false;
            }
        }

        private void StopDiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            _discoveryCts?.Cancel();
            _discoveryService.StopDiscovery();
            DiscoveryButton.IsEnabled = true;
            DiscoveryButton.Content = "🔎 DISCOVER";
            StopDiscoveryButton.IsEnabled = false;
            UpdateStatus("Discovery stopped.", false);
        }

        private void DiscoveryService_CameraDiscovered(object? sender, DiscoveredCamera camera)
        {
            Dispatcher.Invoke(() =>
            {
                // Check if camera already in list (avoid duplicates)
                bool exists = false;
                foreach (DiscoveredCamera item in DiscoveredCamerasListBox.Items)
                {
                    if (item.Endpoint == camera.Endpoint)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    DiscoveredCamerasListBox.Items.Add(camera);
                    UpdateStatus($"Discovered: {camera}", false);
                }
            });
        }

        private void DiscoveredCamerasListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DiscoveredCamerasListBox.SelectedItem is DiscoveredCamera camera)
            {
                // Populate connection fields with discovered camera info
                HostTextBox.Text = camera.IPAddress;
                PortTextBox.Text = camera.Port.ToString();
            }
        }

        private void DiscoveredCamerasListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Get the item that was double-clicked (even if not selected)
            var listBox = sender as System.Windows.Controls.ListBox;
            if (listBox == null) return;
            
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
            if (hit?.VisualHit == null) return;
            
            // Walk up the visual tree to find the ListBoxItem
            var item = hit.VisualHit;
            while (item != null && !(item is System.Windows.Controls.ListBoxItem))
            {
                item = System.Windows.Media.VisualTreeHelper.GetParent(item);
            }
            
            if (item is System.Windows.Controls.ListBoxItem listBoxItem)
            {
                listBox.SelectedItem = listBoxItem.Content;
            }
            
            if (DiscoveredCamerasListBox.SelectedItem is DiscoveredCamera camera)
            {
                // Auto-fill connection fields with discovered camera info
                HostTextBox.Text = camera.IPAddress;
                PortTextBox.Text = camera.Port.ToString();
                
                // Optional: Auto-connect could be added here
                // ConnectButton_Click(sender, e);
            }
        }

        #region Video Playback

        private void PtzService_StreamUrlDiscovered(object? sender, string streamUrl)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(StreamUrlTextBox.Text))
                {
                    StreamUrlTextBox.Text = streamUrl;
                }
            });
        }

        private void StartVideoButton_Click(object sender, RoutedEventArgs e) => StartVideo();
        private void StopVideoButton_Click(object sender, RoutedEventArgs e) => StopVideo();
        private void AutoDetectButton_Click(object sender, RoutedEventArgs e) => TryAutoDetectStream();
        private void StopDetectButton_Click(object sender, RoutedEventArgs e) => StopAutoDetect();

        private void StopAutoDetect()
        {
            _autoDetectCts?.Cancel();
            UpdateStatus("Auto-detect stopped.", false);
        }

        private string[] GetCommonRtspUrls()
        {
            var host = HostTextBox.Text.Trim();
            var username = UsernameTextBox.Text.Trim();
            var password = PasswordBox.Password;
            var encodedPassword = Uri.EscapeDataString(password);
            var creds = !string.IsNullOrEmpty(username) ? $"{username}:{encodedPassword}@" : "";

            return new[]
            {
                $"rtsp://{creds}{host}:554/onvif1",
                $"rtsp://{creds}{host}:554/Streaming/Channels/101",
                $"rtsp://{creds}{host}:554/Streaming/Channels/102",
                $"rtsp://{creds}{host}:554/cam/realmonitor?channel=1&subtype=0",
                $"rtsp://{creds}{host}:554/h264/ch1/main/av_stream",
                $"rtsp://{creds}{host}:554/stream1",
                $"rtsp://{creds}{host}:554/live",
                $"rtsp://{creds}{host}:554/",
            };
        }

        private async void TryAutoDetectStream()
        {
            var urls = GetCommonRtspUrls();
            _autoDetectCts?.Cancel();
            _autoDetectCts = new CancellationTokenSource();
            var token = _autoDetectCts.Token;

            AutoDetectButton.IsEnabled = false;
            StartVideoButton.IsEnabled = false;
            StopDetectButton.IsEnabled = true;
            AutoDetectButton.Content = "🔍 DETECTING...";

            UpdateStatus($"Testing {urls.Length} RTSP URL patterns...", false);

            int attempted = 0;
            bool found = false;

            try
            {
                foreach (var url in urls)
                {
                    if (token.IsCancellationRequested) break;

                    attempted++;
                    var displayUrl = url.Contains("@") ? 
                        url.Substring(0, url.IndexOf("://") + 3) + "***@" + url.Substring(url.IndexOf('@') + 1) : url;

                    UpdateStatus($"[{attempted}/{urls.Length}] Trying: {displayUrl.Substring(0, Math.Min(45, displayUrl.Length))}...", false);
                    StreamUrlTextBox.Text = url;

                    if (await TryStartVideoAsync(url, token))
                    {
                        UpdateStatus($"✓ Found working stream!", false);
                        SaveSettings();
                        found = true;
                        break;
                    }

                    try { await Task.Delay(200, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }

            AutoDetectButton.Content = "🔍 AUTO DETECT";
            AutoDetectButton.IsEnabled = true;
            StartVideoButton.IsEnabled = true;
            StopDetectButton.IsEnabled = false;

            if (!found && !token.IsCancellationRequested)
            {
                UpdateStatus($"Auto-detect failed after {attempted} attempts.", true);
            }
        }

        private async Task<bool> TryStartVideoAsync(string streamUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return false;

                // Ensure Core is initialized before creating LibVLC instance
                if (!_libVlcInitialized)
                {
                    return false;
                }

                if (_libVLC == null)
                {
                    try
                    {
                        // CRITICAL: Ensure Core is initialized before creating LibVLC instance
                        // The "Failed to perform instanciation" error means Core.Initialize() wasn't called
                        // or the native libraries aren't accessible
                        if (!_libVlcInitialized)
                        {
                            // Try to initialize now as a fallback
                            var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                            var libVlcPath = System.IO.Path.Combine(exeDir, "libvlc", "win-x64");
                            
                            var libvlcDll = System.IO.Path.Combine(libVlcPath, "libvlc.dll");
                            var libvlccoreDll = System.IO.Path.Combine(libVlcPath, "libvlccore.dll");
                            
                            if (System.IO.Directory.Exists(libVlcPath) && 
                                System.IO.File.Exists(libvlcDll) && 
                                System.IO.File.Exists(libvlccoreDll))
                            {
                                System.Diagnostics.Debug.WriteLine($"Fallback init: Initializing from {libVlcPath}");
                                Core.Initialize(libVlcPath);
                                _libVlcInitialized = true;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("Fallback init: Trying default initialization");
                                Core.Initialize();
                                _libVlcInitialized = true;
                            }
                        }

                        if (!_libVlcInitialized)
                        {
                            System.Diagnostics.Debug.WriteLine("ERROR: Core.Initialize() was not successful");
                            return false;
                        }

                        System.Diagnostics.Debug.WriteLine("Creating LibVLC instance...");
                        _libVLC = new LibVLC(
                            "--network-caching=1000",  // Increased for slow networks
                            "--rtsp-tcp",
                            "--rtsp-timeout=10000",   // 10 second timeout
                            "--no-audio",
                            "--quiet",
                            "--avcodec-skip-frame=0",
                            "--avcodec-skip-idct=0",
                            "--no-ffmpeg-resync",
                            "--avcodec-hw=none",
                            "--intf=dummy",
                            "--no-stats"
                        );
                        System.Diagnostics.Debug.WriteLine("LibVLC instance created successfully");
                    }
                    catch (System.TypeInitializationException typeEx)
                    {
                        // This exception is thrown when native libraries can't be loaded
                        System.Diagnostics.Debug.WriteLine($"LibVLC TypeInitializationException: {typeEx}");
                        if (typeEx.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Inner: {typeEx.InnerException}");
                        }
                        return false;
                    }
                    catch (System.DllNotFoundException dllEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"LibVLC DllNotFoundException: {dllEx}");
                        return false;
                    }
                    catch (Exception libVlcEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"LibVLC creation error: {libVlcEx}");
                        System.Diagnostics.Debug.WriteLine($"Stack trace: {libVlcEx.StackTrace}");
                        if (libVlcEx.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Inner exception: {libVlcEx.InnerException}");
                        }
                        return false;
                    }
                }

                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }

                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

                using var media = new Media(_libVLC, new Uri(streamUrl));
                media.AddOption(":rtsp-tcp");
                media.AddOption(":network-caching=1000");  // Increased for slow networks
                media.AddOption(":rtsp-timeout=10000");    // 10 second timeout
                media.AddOption(":avcodec-skip-frame=0");
                media.AddOption(":avcodec-skip-idct=0");
                media.AddOption(":no-ffmpeg-resync");

                var playbackStarted = new TaskCompletionSource<bool>();
                _mediaPlayer.Playing += (s, e) => playbackStarted.TrySetResult(true);
                _mediaPlayer.EncounteredError += (s, e) => playbackStarted.TrySetResult(false);
                _mediaPlayer.EndReached += (s, e) => playbackStarted.TrySetResult(false);

                VideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.Play(media);

                var timeoutTask = Task.Delay(15000, cancellationToken); // Increased timeout for slow networks
                var completedTask = await Task.WhenAny(playbackStarted.Task, timeoutTask);

                if (playbackStarted.Task.IsCompleted && playbackStarted.Task.Result)
                {
                    _isVideoPlaying = true;
                    NoVideoOverlay.Visibility = Visibility.Collapsed;
                    StartVideoButton.IsEnabled = false;
                    StopVideoButton.IsEnabled = true;
                    return true;
                }
                else
                {
                    CleanupMediaPlayer();
                    return false;
                }
            }
            catch
            {
                CleanupMediaPlayer();
                return false;
            }
        }

        private void CleanupMediaPlayer()
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    VideoView.MediaPlayer = null;
                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Starts video streaming from the RTSP URL
        /// 
        /// Process:
        /// 1. Validates LibVLC is initialized
        /// 2. Gets stream URL from UI or generates default RTSP URL
        /// 3. Creates/uses existing LibVLC instance with optimized settings for slow networks
        /// 4. Creates MediaPlayer and starts playback
        /// 5. Updates UI to show video and enable stop button
        /// 
        /// Network settings optimized for wireless/slow networks:
        /// - 1000ms network caching buffer
        /// - 10 second RTSP timeout
        /// - TCP transport for reliability
        /// </summary>
        private void StartVideo()
        {
            try
            {
                // Ensure Core is initialized before creating LibVLC instance
                // This prevents "Failed to perform instanciation" errors
                if (!_libVlcInitialized)
                {
                    UpdateStatus("LibVLC not initialized. Please restart the application.", true);
                    return;
                }

                var streamUrl = StreamUrlTextBox.Text.Trim();

                if (string.IsNullOrEmpty(streamUrl))
                {
                    var host = HostTextBox.Text.Trim();
                    var username = UsernameTextBox.Text.Trim();
                    var password = PasswordBox.Password;

                    if (!string.IsNullOrEmpty(host))
                    {
                        var encodedPassword = Uri.EscapeDataString(password);
                        streamUrl = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                            ? $"rtsp://{username}:{encodedPassword}@{host}:554/Streaming/Channels/101"
                            : $"rtsp://{host}:554/Streaming/Channels/101";
                        StreamUrlTextBox.Text = streamUrl;
                    }
                    else
                    {
                        UpdateStatus("Please enter a stream URL or connect first", true);
                        return;
                    }
                }

                // Create LibVLC instance if it doesn't exist
                // LibVLC instance is shared and reused for all video streams
                if (_libVLC == null)
                {
                    try
                    {
                        // CRITICAL: Ensure Core is initialized before creating LibVLC instance
                        // The "Failed to perform instanciation" error means Core.Initialize() wasn't called
                        // or the native libraries aren't accessible when creating the instance
                        if (!_libVlcInitialized)
                        {
                            // Try to initialize now as a fallback - verify both required DLLs exist
                            var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                            var libVlcPath = System.IO.Path.Combine(exeDir, "libvlc", "win-x64");
                            
                            var libvlcDll = System.IO.Path.Combine(libVlcPath, "libvlc.dll");
                            var libvlccoreDll = System.IO.Path.Combine(libVlcPath, "libvlccore.dll");
                            
                            if (System.IO.Directory.Exists(libVlcPath) && 
                                System.IO.File.Exists(libvlcDll) && 
                                System.IO.File.Exists(libvlccoreDll))
                            {
                                System.Diagnostics.Debug.WriteLine($"StartVideo: Initializing from {libVlcPath}");
                                Core.Initialize(libVlcPath);
                                _libVlcInitialized = true;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("StartVideo: Trying default initialization");
                                Core.Initialize();
                                _libVlcInitialized = true;
                            }
                        }

                        if (!_libVlcInitialized)
                        {
                            throw new Exception("LibVLC Core not initialized. Cannot create LibVLC instance.");
                        }

                        // Create LibVLC instance with options optimized for slow networks
                        // Network caching increased to 1000ms to buffer more data for slow connections
                        // RTSP timeout increased to 10 seconds to accommodate slow wireless networks
                        System.Diagnostics.Debug.WriteLine("StartVideo: Creating LibVLC instance...");
                        _libVLC = new LibVLC(
                            "--network-caching=1000",  // Increased buffer for slow networks (default is ~300ms)
                            "--rtsp-tcp",              // Use TCP for RTSP (more reliable than UDP)
                            "--rtsp-timeout=10000",    // 10 second timeout for RTSP connections
                            "--no-audio",              // Disable audio (PTZ cameras typically don't have audio)
                            "--quiet",                 // Suppress LibVLC console output
                            "--avcodec-skip-frame=0",  // Don't skip frames
                            "--avcodec-skip-idct=0",   // Don't skip IDCT
                            "--no-ffmpeg-resync",      // Disable FFmpeg resync (can cause issues)
                            "--avcodec-hw=none",       // Disable hardware acceleration (more compatible)
                            "--intf=dummy",            // No user interface (we use WPF UI)
                            "--no-stats"               // Disable statistics collection
                        );
                        System.Diagnostics.Debug.WriteLine("StartVideo: LibVLC instance created successfully");
                    }
                    catch (System.TypeInitializationException typeEx)
                    {
                        // This is the "Failed to perform instanciation" error
                        var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                        var expectedPath = System.IO.Path.Combine(exeDir, "libvlc", "win-x64");
                        var errorMsg = $"LibVLC initialization failed: {typeEx.Message}. Ensure libvlc.dll and libvlccore.dll exist in {expectedPath}";
                        if (typeEx.InnerException != null)
                        {
                            errorMsg += $"\nInner error: {typeEx.InnerException.Message}";
                        }
                        UpdateStatus(errorMsg, true);
                        System.Diagnostics.Debug.WriteLine($"StartVideo: TypeInitializationException: {typeEx}");
                        if (typeEx.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Inner: {typeEx.InnerException}");
                        }
                        return;
                    }
                    catch (System.DllNotFoundException dllEx)
                    {
                        var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                        var expectedPath = System.IO.Path.Combine(exeDir, "libvlc", "win-x64");
                        UpdateStatus($"LibVLC native libraries not found. Expected: {expectedPath}\\libvlc.dll and libvlccore.dll. Error: {dllEx.Message}", true);
                        System.Diagnostics.Debug.WriteLine($"StartVideo: DllNotFoundException: {dllEx}");
                        return;
                    }
                    catch (Exception libVlcEx)
                    {
                        var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                        var expectedPath = System.IO.Path.Combine(exeDir, "libvlc", "win-x64");
                        var errorMsg = $"Failed to create LibVLC instance: {libVlcEx.Message}\nEnsure libvlc.dll and libvlccore.dll are in: {expectedPath}";
                        if (libVlcEx.InnerException != null)
                        {
                            errorMsg += $"\nInner error: {libVlcEx.InnerException.Message}";
                        }
                        UpdateStatus(errorMsg, true);
                        System.Diagnostics.Debug.WriteLine($"StartVideo: LibVLC creation error: {libVlcEx}");
                        System.Diagnostics.Debug.WriteLine($"Stack trace: {libVlcEx.StackTrace}");
                        if (libVlcEx.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Inner exception: {libVlcEx.InnerException}");
                        }
                        return;
                    }
                }

                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Dispose();
                }

                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                
                // Suppress FFmpeg errors in media player
                _mediaPlayer.EncounteredError += (s, e) =>
                {
                    // Ignore FFmpeg-related errors silently
                    System.Diagnostics.Debug.WriteLine($"Media player error (suppressed): {e}");
                };
                
                VideoView.MediaPlayer = _mediaPlayer;

                using var media = new Media(_libVLC, new Uri(streamUrl));
                media.AddOption(":rtsp-tcp");
                media.AddOption(":network-caching=300");
                media.AddOption(":avcodec-skip-frame=0");
                media.AddOption(":avcodec-skip-idct=0");
                media.AddOption(":no-ffmpeg-resync");
                
                _mediaPlayer.Play(media);

                _isVideoPlaying = true;
                NoVideoOverlay.Visibility = Visibility.Collapsed;
                StartVideoButton.IsEnabled = false;
                StopVideoButton.IsEnabled = true;

                UpdateStatus("Video stream started", false);
                SaveSettings();
            }
            catch (System.DllNotFoundException dllEx)
            {
                var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                UpdateStatus($"LibVLC native libraries not found. Please ensure libvlc.dll is in: {System.IO.Path.Combine(exeDir, "libvlc", "win-x64")}. Error: {dllEx.Message}", true);
                System.Diagnostics.Debug.WriteLine($"DllNotFoundException: {dllEx}");
            }
            catch (System.TypeInitializationException typeEx)
            {
                UpdateStatus($"LibVLC initialization failed. Please restart the application. Error: {typeEx.Message}", true);
                System.Diagnostics.Debug.WriteLine($"TypeInitializationException: {typeEx}");
                if (typeEx.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Inner Exception: {typeEx.InnerException.Message}");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to start video: {ex.Message}", true);
                System.Diagnostics.Debug.WriteLine($"Exception: {ex}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private void StopVideo()
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    VideoView.MediaPlayer = null;
                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }

                _isVideoPlaying = false;
                NoVideoOverlay.Visibility = Visibility.Visible;
                StartVideoButton.IsEnabled = true;
                StopVideoButton.IsEnabled = false;

                UpdateStatus("Video stream stopped", false);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error stopping video: {ex.Message}", true);
            }
        }

        #endregion

        #region PTZ Movement

        private async void Up_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync(0, (float)PanTiltSpeedSlider.Value, 0);
        }

        private async void Down_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync(0, -(float)PanTiltSpeedSlider.Value, 0);
        }

        private async void Left_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync(-(float)PanTiltSpeedSlider.Value, 0, 0);
        }

        private async void Right_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync((float)PanTiltSpeedSlider.Value, 0, 0);
        }

        private async void UpLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            float speed = (float)PanTiltSpeedSlider.Value;
            await _ptzService.ContinuousMoveAsync(-speed, speed, 0);
        }

        private async void UpRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            float speed = (float)PanTiltSpeedSlider.Value;
            await _ptzService.ContinuousMoveAsync(speed, speed, 0);
        }

        private async void DownLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            float speed = (float)PanTiltSpeedSlider.Value;
            await _ptzService.ContinuousMoveAsync(-speed, -speed, 0);
        }

        private async void DownRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            float speed = (float)PanTiltSpeedSlider.Value;
            await _ptzService.ContinuousMoveAsync(speed, -speed, 0);
        }

        private async void Direction_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.StopAsync();
        }

        private async void ZoomIn_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync(0, 0, (float)ZoomSpeedSlider.Value);
        }

        private async void ZoomOut_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.ContinuousMoveAsync(0, 0, -(float)ZoomSpeedSlider.Value);
        }

        private async void Zoom_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.StopAsync();
        }

        #endregion

        #region Absolute Position

        private async void GoToPosition_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected)
            {
                UpdateStatus("Not connected to camera", true);
                return;
            }

            await _ptzService.AbsoluteMoveAsync((float)PanSlider.Value, (float)TiltSlider.Value, (float)ZoomSlider.Value);
        }

        private async void GetPosition_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected)
            {
                UpdateStatus("Not connected to camera", true);
                return;
            }

            var position = await _ptzService.GetPositionAsync();
            if (position.HasValue)
            {
                PanSlider.Value = position.Value.Pan;
                TiltSlider.Value = position.Value.Tilt;
                ZoomSlider.Value = position.Value.Zoom;
                PositionText.Text = $"P:{position.Value.Pan:F2} T:{position.Value.Tilt:F2} Z:{position.Value.Zoom:F2}";
            }
        }

        private async void Home_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected)
            {
                UpdateStatus("Not connected to camera", true);
                return;
            }
            await _ptzService.GoToHomeAsync();
        }

        private async void SetHome_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected)
            {
                UpdateStatus("Not connected to camera", true);
                return;
            }

            var result = MessageBox.Show("Set the current position as the home position?", "Confirm Set Home",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _ptzService.SetHomeAsync();
            }
        }

        #endregion

        #region Status Updates

        private void PtzService_StatusChanged(object? sender, string message)
        {
            Dispatcher.Invoke(() => UpdateStatus(message, false));
        }

        private void PtzService_ErrorOccurred(object? sender, string message)
        {
            Dispatcher.Invoke(() => UpdateStatus(message, true));
        }

        private void UpdateStatus(string message, bool isError)
        {
            StatusText.Text = message;
            StatusText.Foreground = isError
                ? (SolidColorBrush)FindResource("ErrorRedBrush")
                : (SolidColorBrush)FindResource("TextSecondaryBrush");
        }

        #endregion

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopVideo();
            _discoveryCts?.Cancel();
            _libVLC?.Dispose();
            _ptzService.Dispose();
            _discoveryService.Dispose();
        }
    }
}

