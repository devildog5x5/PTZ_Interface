using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;
using PTZCameraOperator.Services;

namespace PTZCameraOperator.Views
{
    /// <summary>
    /// Video Observation Window for Operator
    /// Displays live camera video feed using LibVLC
    /// </summary>
    public partial class VideoWindow : Window
    {
        private readonly OnvifPtzService _ptzService;
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _libVlcInitialized = false;
        private CancellationTokenSource? _autoDetectCts;

        public VideoWindow(OnvifPtzService ptzService)
        {
            InitializeComponent();
            _ptzService = ptzService;
            
            this.Loaded += VideoWindow_Loaded;
            this.Closing += VideoWindow_Closing;
        }

        private void VideoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeLibVLC();
            EnsureWindowFitsOnScreen();
        }

        private void EnsureWindowFitsOnScreen()
        {
            // Get screen dimensions
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // If window is larger than screen, resize it
            if (this.Width > screenWidth)
            {
                this.Width = screenWidth * 0.9; // Use 90% of screen width for video
            }
            if (this.Height > screenHeight)
            {
                this.Height = screenHeight * 0.9; // Use 90% of screen height for video
            }

            // Ensure window is centered and visible
            if (this.Left < 0) this.Left = 0;
            if (this.Top < 0) this.Top = 0;
            if (this.Left + this.Width > screenWidth)
                this.Left = screenWidth - this.Width;
            if (this.Top + this.Height > screenHeight)
                this.Top = screenHeight - this.Height;
        }

        private void InitializeLibVLC()
        {
            try
            {
                if (_libVlcInitialized) return;

                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var libVlcPaths = new[]
                {
                    Path.Combine(exeDir, "libvlc", "win-x64"),
                    Path.Combine(exeDir, "runtimes", "win-x64", "native"),
                    exeDir
                };

                string? libVlcPath = null;
                foreach (var path in libVlcPaths)
                {
                    if (Directory.Exists(path))
                    {
                        var libvlcDll = Path.Combine(path, "libvlc.dll");
                        if (File.Exists(libvlcDll))
                        {
                            libVlcPath = path;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(libVlcPath))
                {
                    var libvlccoreDll = Path.Combine(libVlcPath, "libvlccore.dll");
                    if (File.Exists(libvlccoreDll))
                    {
                        Core.Initialize(libVlcPath);
                        _libVlcInitialized = true;
                    }
                }
                else
                {
                    Core.Initialize();
                    _libVlcInitialized = true;
                }

                if (_libVlcInitialized)
                {
                    _libVLC = new LibVLC(
                        "--network-caching=1000",
                        "--rtsp-tcp",
                        "--rtsp-timeout=10"
                    );
                    _mediaPlayer = new MediaPlayer(_libVLC);
                    VideoView.MediaPlayer = _mediaPlayer;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize LibVLC: {ex.Message}\n\nVideo streaming will not be available.", 
                    "LibVLC Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void SetStreamUrl(string streamUrl)
        {
            Dispatcher.Invoke(() =>
            {
                StreamUrlTextBox.Text = streamUrl;
            });
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var streamUrl = StreamUrlTextBox.Text.Trim();
            if (string.IsNullOrEmpty(streamUrl))
            {
                MessageBox.Show("Please enter a stream URL or use Auto Detect.", "No Stream URL", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await StartVideo(streamUrl);
        }

        private async Task<bool> StartVideo(string streamUrl)
        {
            if (!_libVlcInitialized || _libVLC == null || _mediaPlayer == null)
            {
                MessageBox.Show("LibVLC not initialized. Video streaming unavailable.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            try
            {
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                PlaceholderText.Visibility = Visibility.Collapsed;
                StreamStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.Yellow);

                var media = new Media(_libVLC, streamUrl, FromType.FromLocation);
                _mediaPlayer.Play(media);

                // Wait a bit to see if playback starts
                await Task.Delay(3000);

                if (_mediaPlayer.IsPlaying)
                {
                    StreamStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Colors.Green);
                    return true;
                }
                else
                {
                    StopVideo();
                    MessageBox.Show("Failed to start video stream. Check the stream URL and network connection.", 
                        "Stream Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                StopVideo();
                MessageBox.Show($"Error starting video: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }

        private void StopVideo()
        {
            try
            {
                _mediaPlayer?.Stop();
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                PlaceholderText.Visibility = Visibility.Visible;
                StreamStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.Red);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping video: {ex.Message}");
            }
        }

        private async void AutoDetectButton_Click(object sender, RoutedEventArgs e)
        {
            await TryAutoDetectStream();
        }

        private async Task TryAutoDetectStream()
        {
            // Use public properties instead of reflection
            if (!_ptzService.IsConnected || string.IsNullOrEmpty(_ptzService.Host))
            {
                MessageBox.Show("Please connect to camera first.", "Not Connected", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var hostIp = _ptzService.Host;
            var username = _ptzService.Username;
            var password = _ptzService.Password;

            var encodedPassword = Uri.EscapeDataString(password);
            var creds = !string.IsNullOrEmpty(username) ? $"{username}:{encodedPassword}@" : "";

            // Try common RTSP ports and paths
            var rtspPort = 554; // Default RTSP port
            var urls = new[]
            {
                $"rtsp://{creds}{hostIp}:{rtspPort}/onvif1",
                $"rtsp://{creds}{hostIp}:{rtspPort}/Streaming/Channels/101",
                $"rtsp://{creds}{hostIp}:{rtspPort}/Streaming/Channels/102",
                $"rtsp://{creds}{hostIp}:{rtspPort}/cam/realmonitor?channel=1&subtype=0",
                $"rtsp://{creds}{hostIp}:{rtspPort}/h264/ch1/main/av_stream",
                $"rtsp://{creds}{hostIp}:{rtspPort}/stream1",
                $"rtsp://{creds}{hostIp}:{rtspPort}/live",
                $"rtsp://{creds}{hostIp}:{rtspPort}/",
                // Also try alternative RTSP ports
                $"rtsp://{creds}{hostIp}:8554/onvif1",
                $"rtsp://{creds}{hostIp}:8554/Streaming/Channels/101",
            };

            _autoDetectCts?.Cancel();
            _autoDetectCts = new CancellationTokenSource();
            var token = _autoDetectCts.Token;

            AutoDetectButton.IsEnabled = false;
            StartButton.IsEnabled = false;

            bool found = false;
            for (int i = 0; i < urls.Length && !token.IsCancellationRequested; i++)
            {
                StreamUrlTextBox.Text = urls[i];
                if (await StartVideo(urls[i]))
                {
                    found = true;
                    break;
                }
                await Task.Delay(200, token);
            }

            AutoDetectButton.IsEnabled = true;
            StartButton.IsEnabled = true;

            if (!found)
            {
                MessageBox.Show("Auto-detect failed. Please enter stream URL manually.", 
                    "Auto-Detect Failed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void StreamUrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Enable/disable start button based on URL
            StartButton.IsEnabled = !string.IsNullOrWhiteSpace(StreamUrlTextBox.Text);
        }

        private void VideoWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            StopVideo();
            _autoDetectCts?.Cancel();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }
    }
}
