using System;
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
    public partial class MainWindow : Window
    {
        private readonly OnvifPtzService _ptzService;
        private readonly CameraSettings _settings;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private bool _isVideoPlaying;
        private CancellationTokenSource? _autoDetectCts;

        public MainWindow()
        {
            InitializeComponent();

            _ptzService = new OnvifPtzService();
            _ptzService.StatusChanged += PtzService_StatusChanged;
            _ptzService.ErrorOccurred += PtzService_ErrorOccurred;
            _ptzService.StreamUrlDiscovered += PtzService_StreamUrlDiscovered;

            _settings = CameraSettings.Load();
            LoadSettings();
            UpdateConnectionIndicator(false);
            
            // Initialize LibVLC after window is loaded (deferred initialization)
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialize LibVLC with proper path handling
            try
            {
                var exeLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var exeDir = !string.IsNullOrEmpty(exeLocation) 
                    ? System.IO.Path.GetDirectoryName(exeLocation) 
                    : System.AppDomain.CurrentDomain.BaseDirectory;
                
                var libVlcPaths = new[]
                {
                    System.IO.Path.Combine(exeDir ?? "", "libvlc", "win-x64"),
                    System.IO.Path.Combine(exeDir ?? "", "runtimes", "win-x64", "native"),
                    exeDir ?? ""
                };

                string? libVlcPath = null;
                foreach (var path in libVlcPaths)
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        var libvlcDll = System.IO.Path.Combine(path, "libvlc.dll");
                        if (System.IO.File.Exists(libvlcDll))
                        {
                            libVlcPath = path;
                            break;
                        }
                    }
                }

                if (libVlcPath != null)
                {
                    Core.Initialize(libVlcPath);
                    System.Diagnostics.Debug.WriteLine($"LibVLC initialized from: {libVlcPath}");
                }
                else
                {
                    // Try default initialization (searches common paths)
                    Core.Initialize();
                    System.Diagnostics.Debug.WriteLine("LibVLC initialized with default search");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LibVLC initialization warning: {ex.Message}");
                UpdateStatus("⚠️ LibVLC not available - video streaming disabled. PTZ controls will still work.", true);
                // Continue without LibVLC - video streaming won't work but UI will load
            }
        }

        private void LoadSettings()
        {
            HostTextBox.Text = _settings.Host;
            PortTextBox.Text = _settings.Port.ToString();
            UsernameTextBox.Text = _settings.Username;
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

            var success = await _ptzService.ConnectAsync(host, port, username, password);

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

                if (_libVLC == null)
                {
                    _libVLC = new LibVLC("--network-caching=200", "--rtsp-tcp", "--no-audio", "--verbose=0", "--rtsp-timeout=3");
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
                media.AddOption(":network-caching=200");

                var playbackStarted = new TaskCompletionSource<bool>();
                _mediaPlayer.Playing += (s, e) => playbackStarted.TrySetResult(true);
                _mediaPlayer.EncounteredError += (s, e) => playbackStarted.TrySetResult(false);
                _mediaPlayer.EndReached += (s, e) => playbackStarted.TrySetResult(false);

                VideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.Play(media);

                var timeoutTask = Task.Delay(3000, cancellationToken);
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

        private void StartVideo()
        {
            try
            {
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

                if (_libVLC == null)
                {
                    _libVLC = new LibVLC("--network-caching=300", "--rtsp-tcp", "--no-audio");
                }

                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Dispose();
                }

                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                VideoView.MediaPlayer = _mediaPlayer;

                using var media = new Media(_libVLC, new Uri(streamUrl));
                _mediaPlayer.Play(media);

                _isVideoPlaying = true;
                NoVideoOverlay.Visibility = Visibility.Collapsed;
                StartVideoButton.IsEnabled = false;
                StopVideoButton.IsEnabled = true;

                UpdateStatus("Video stream started", false);
                SaveSettings();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to start video: {ex.Message}", true);
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
            _libVLC?.Dispose();
            _ptzService.Dispose();
        }
    }
}

