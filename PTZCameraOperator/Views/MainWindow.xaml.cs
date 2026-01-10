using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PTZCameraOperator.Models;
using PTZCameraOperator.Services;

namespace PTZCameraOperator.Views
{
    /// <summary>
    /// Main Control Window for PTZ Camera Operator Application
    /// Handles camera connection, PTZ controls, and opens video observation window
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly OnvifPtzService _ptzService;
        private readonly OnvifDiscoveryService _discoveryService;
        private readonly CameraSettings _settings;
        private VideoWindow? _videoWindow;
        private CancellationTokenSource? _ptzMoveCts;
        private CancellationTokenSource? _discoveryCts;

        public MainWindow()
        {
            InitializeComponent();

            _ptzService = new OnvifPtzService();
            _ptzService.StatusChanged += PtzService_StatusChanged;
            _ptzService.ErrorOccurred += PtzService_ErrorOccurred;
            _ptzService.StreamUrlDiscovered += PtzService_StreamUrlDiscovered;

            _discoveryService = new OnvifDiscoveryService();
            _discoveryService.CameraDiscovered += DiscoveryService_CameraDiscovered;

            _settings = CameraSettings.Load();
            LoadSettings();
            UpdateConnectionIndicator(false);

            // Update speed labels when sliders change
            PanTiltSpeedSlider.ValueChanged += (s, e) => PanTiltSpeedLabel.Text = PanTiltSpeedSlider.Value.ToString("F1");
            ZoomSpeedSlider.ValueChanged += (s, e) => ZoomSpeedLabel.Text = ZoomSpeedSlider.Value.ToString("F1");

            // Set focus to window for keyboard shortcuts and ensure it fits on screen
            this.Loaded += (s, e) =>
            {
                this.Focus();
                EnsureWindowFitsOnScreen();
            };
        }

        private void EnsureWindowFitsOnScreen()
        {
            // Get screen dimensions
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // If window is larger than screen, resize it
            if (this.Width > screenWidth)
            {
                this.Width = screenWidth * 0.95; // Use 95% of screen width
            }
            if (this.Height > screenHeight)
            {
                this.Height = screenHeight * 0.95; // Use 95% of screen height
            }

            // Ensure window is centered and visible
            if (this.Left < 0) this.Left = 0;
            if (this.Top < 0) this.Top = 0;
            if (this.Left + this.Width > screenWidth)
                this.Left = screenWidth - this.Width;
            if (this.Top + this.Height > screenHeight)
                this.Top = screenHeight - this.Height;
        }

        private readonly HashSet<Key> _pressedKeys = new HashSet<Key>();

        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_ptzService.IsConnected) return;

            if (_pressedKeys.Contains(e.Key)) return; // Already handling this key
            _pressedKeys.Add(e.Key);

            float panSpeed = 0, tiltSpeed = 0, zoomSpeed = 0;
            var panTiltSpeed = (float)PanTiltSpeedSlider.Value;
            var zoomSpeedValue = (float)ZoomSpeedSlider.Value;

            switch (e.Key)
            {
                case Key.W:
                case Key.Up:
                    tiltSpeed = panTiltSpeed;
                    break;
                case Key.S:
                case Key.Down:
                    tiltSpeed = -panTiltSpeed;
                    break;
                case Key.A:
                case Key.Left:
                    panSpeed = -panTiltSpeed;
                    break;
                case Key.D:
                case Key.Right:
                    panSpeed = panTiltSpeed;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    zoomSpeed = zoomSpeedValue;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    zoomSpeed = -zoomSpeedValue;
                    break;
                case Key.H:
                    await _ptzService.GoToHomeAsync();
                    UpdateStatus("Moving to home position...");
                    return;
                default:
                    _pressedKeys.Remove(e.Key);
                    return;
            }

            if (panSpeed != 0 || tiltSpeed != 0 || zoomSpeed != 0)
            {
                await _ptzService.ContinuousMoveAsync(panSpeed, tiltSpeed, zoomSpeed);
            }
        }

        private async void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (!_ptzService.IsConnected) return;

            _pressedKeys.Remove(e.Key);

            // Stop movement when PTZ keys are released
            if (e.Key == Key.W || e.Key == Key.S || e.Key == Key.A || e.Key == Key.D ||
                e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right ||
                e.Key == Key.OemPlus || e.Key == Key.OemMinus || e.Key == Key.Add || e.Key == Key.Subtract ||
                e.Key == Key.Space)
            {
                await _ptzService.StopAsync();
            }
        }

        private void LoadSettings()
        {
            HostTextBox.Text = _settings.Host;
            PortTextBox.Text = _settings.Port.ToString();
            UsernameTextBox.Text = _settings.Username;
            PasswordBox.Password = !string.IsNullOrEmpty(_settings.Password) ? _settings.Password : "XTL.a1.1000!";
            PanTiltSpeedSlider.Value = _settings.PanSpeed;
            ZoomSpeedSlider.Value = _settings.ZoomSpeed;
        }

        private void SaveSettings()
        {
            _settings.Host = HostTextBox.Text;
            if (int.TryParse(PortTextBox.Text, out int port))
                _settings.Port = port;
            _settings.Username = UsernameTextBox.Text;
            _settings.Password = PasswordBox.Password;
            _settings.PanSpeed = (float)PanTiltSpeedSlider.Value;
            _settings.ZoomSpeed = (float)ZoomSpeedSlider.Value;
            _settings.Save();
        }

        private void UpdateConnectionIndicator(bool connected)
        {
            ConnectionIndicator.Fill = new SolidColorBrush(connected ? Colors.Green : Colors.Red);
            ConnectionLabel.Text = connected ? "ONLINE" : "OFFLINE";
            UpdatePtzControlsEnabled(connected);
        }

        private void UpdatePtzControlsEnabled(bool enabled)
        {
            // Enable/disable all PTZ control buttons based on connection status
            TiltUpButton.IsEnabled = enabled;
            TiltDownButton.IsEnabled = enabled;
            PanLeftButton.IsEnabled = enabled;
            PanRightButton.IsEnabled = enabled;
            ZoomInButton.IsEnabled = enabled;
            ZoomOutButton.IsEnabled = enabled;
            HomeButton.IsEnabled = enabled;
            SetHomeButton.IsEnabled = enabled;
            
            if (!enabled)
            {
                UpdateStatus("PTZ controls disabled - connect to camera first", true);
            }
        }

        private void UpdateStatus(string message, bool isError = false)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var prefix = isError ? "❌" : "ℹ️";
            StatusText.Text += $"[{timestamp}] {prefix} {message}\n";
            StatusText.ScrollToEnd();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            UpdateStatus("Connecting to camera...");

            var host = HostTextBox.Text;
            if (!int.TryParse(PortTextBox.Text, out int port))
            {
                UpdateStatus("Invalid port number", true);
                ConnectButton.IsEnabled = true;
                return;
            }

            var username = UsernameTextBox.Text;
            var password = PasswordBox.Password;

            var connected = await _ptzService.ConnectAsync(host, port, username, password);
            
            if (connected)
            {
                UpdateConnectionIndicator(true);
                UpdateStatus("Connected successfully!");
                SaveSettings();
            }
            else
            {
                UpdateConnectionIndicator(false);
                UpdateStatus("Connection failed", true);
            }

            ConnectButton.IsEnabled = true;
        }

        private async void PingButton_Click(object sender, RoutedEventArgs e)
        {
            var host = HostTextBox.Text;
            UpdateStatus($"Pinging {host}...");

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                
                if (reply.Status == IPStatus.Success)
                {
                    UpdateStatus($"Ping successful - {reply.RoundtripTime}ms");
                }
                else
                {
                    UpdateStatus($"Ping failed - {reply.Status}", true);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Ping error: {ex.Message}", true);
            }
        }

        private void OpenVideoWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_videoWindow == null || !_videoWindow.IsLoaded)
            {
                _videoWindow = new VideoWindow(_ptzService);
                _videoWindow.Closed += (s, args) => _videoWindow = null;
                _videoWindow.Show();
            }
            else
            {
                _videoWindow.Activate();
            }
        }

        private async void PtzButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;

            var button = sender as System.Windows.Controls.Button;
            var action = button?.Tag?.ToString() ?? "";

            _ptzMoveCts?.Cancel();
            _ptzMoveCts = new CancellationTokenSource();

            float panSpeed = 0, tiltSpeed = 0, zoomSpeed = 0;
            var panTiltSpeed = (float)PanTiltSpeedSlider.Value;
            var zoomSpeedValue = (float)ZoomSpeedSlider.Value;

            switch (action)
            {
                case "TiltUp": tiltSpeed = panTiltSpeed; break;
                case "TiltDown": tiltSpeed = -panTiltSpeed; break;
                case "PanLeft": panSpeed = -panTiltSpeed; break;
                case "PanRight": panSpeed = panTiltSpeed; break;
                case "ZoomIn": zoomSpeed = zoomSpeedValue; break;
                case "ZoomOut": zoomSpeed = -zoomSpeedValue; break;
            }

            await _ptzService.ContinuousMoveAsync(panSpeed, tiltSpeed, zoomSpeed);
        }

        private async void PtzButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.StopAsync();
        }

        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.GoToHomeAsync();
            UpdateStatus("Moving to home position...");
        }

        private async void SetHomeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected) return;
            await _ptzService.SetHomeAsync();
            UpdateStatus("Home position set");
        }

        private void PtzService_StatusChanged(object? sender, string message)
        {
            Dispatcher.Invoke(() => UpdateStatus(message));
        }

        private void PtzService_ErrorOccurred(object? sender, string error)
        {
            Dispatcher.Invoke(() => UpdateStatus(error, true));
        }

        private void PtzService_StreamUrlDiscovered(object? sender, string streamUrl)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus($"Stream URL discovered: {streamUrl}");
                if (_videoWindow != null && _videoWindow.IsLoaded)
                {
                    _videoWindow.SetStreamUrl(streamUrl);
                }
            });
        }

        private async void DiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            DiscoveryButton.IsEnabled = false;
            StopDiscoveryButton.IsEnabled = true;
            DiscoveredCamerasListBox.Items.Clear();
            UpdateStatus("Starting camera discovery...");

            _discoveryCts?.Cancel();
            _discoveryCts = new CancellationTokenSource();

            try
            {
                await _discoveryService.DiscoverAsync(5, _discoveryCts.Token);
                UpdateStatus("Camera discovery completed.");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Camera discovery cancelled.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Discovery error: {ex.Message}", true);
            }
            finally
            {
                DiscoveryButton.IsEnabled = true;
                StopDiscoveryButton.IsEnabled = false;
            }
        }

        private void StopDiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            _discoveryCts?.Cancel();
            _discoveryService.StopDiscovery();
            DiscoveryButton.IsEnabled = true;
            StopDiscoveryButton.IsEnabled = false;
            UpdateStatus("Discovery stopped.");
        }

        private void DiscoveryService_CameraDiscovered(object? sender, DiscoveredCamera camera)
        {
            Dispatcher.Invoke(() =>
            {
                DiscoveredCamerasListBox.Items.Add(camera);
                UpdateStatus($"Discovered: {camera}");
            });
        }

        private void DiscoveredCamerasListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DiscoveredCamerasListBox.SelectedItem is DiscoveredCamera camera)
            {
                HostTextBox.Text = camera.IPAddress;
                PortTextBox.Text = camera.Port.ToString();
            }
        }

        private void DiscoveredCamerasListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as System.Windows.Controls.ListBox;
            if (listBox == null) return;

            var hit = System.Windows.Media.VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
            if (hit?.VisualHit == null) return;

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
                HostTextBox.Text = camera.IPAddress;
                PortTextBox.Text = camera.Port.ToString();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _ptzService?.Dispose();
            _discoveryService?.Dispose();
            _videoWindow?.Close();
            SaveSettings();
        }
    }
}
