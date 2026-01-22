using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using PTZCameraOperator.Models;
using PTZCameraOperator.Services;
using CameraInfo = PTZCameraOperator.Models.CameraInfo;

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
        private readonly CameraDiagnosticService _diagnosticService;
        private readonly CameraIdentificationService _identificationService;
        private readonly CameraSettings _settings;
        private DiagnosticWindow? _diagnosticWindow;
        private Models.CameraInfo? _currentCameraInfo;
        private CancellationTokenSource? _ptzMoveCts;
        private CancellationTokenSource? _discoveryCts;
        private CancellationTokenSource? _autoDetectCts;
        
        // Video streaming
        private bool _libVlcInitialized = false;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;

        public MainWindow()
        {
            InitializeComponent();

            _ptzService = new OnvifPtzService();
            _ptzService.StatusChanged += PtzService_StatusChanged;
            _ptzService.ErrorOccurred += PtzService_ErrorOccurred;
            _ptzService.StreamUrlDiscovered += PtzService_StreamUrlDiscovered;

            _discoveryService = new OnvifDiscoveryService();
            _discoveryService.CameraDiscovered += DiscoveryService_CameraDiscovered;

            _diagnosticService = new CameraDiagnosticService();
            _diagnosticService.DiagnosticMessage += DiagnosticService_DiagnosticMessage;
            
            _identificationService = new CameraIdentificationService();
            _identificationService.StatusChanged += (s, msg) => 
            {
                Dispatcher.Invoke(() =>
                {
                    EnsureDiagnosticWindow();
                    _diagnosticWindow?.AppendDiagnosticMessage($"[{DateTime.Now:HH:mm:ss}] {msg}");
                });
            };

            _settings = CameraSettings.Load();
            LoadSettings();
            UpdateConnectionIndicator(false);

            // Update speed labels when sliders change
            PanTiltSpeedSlider.ValueChanged += (s, e) => PanTiltSpeedLabel.Text = PanTiltSpeedSlider.Value.ToString("F1");
            ZoomSpeedSlider.ValueChanged += (s, e) => ZoomSpeedLabel.Text = ZoomSpeedSlider.Value.ToString("F1");
            
            // Initialize video
            InitializeLibVLC();

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
            // Handle fullscreen toggle (F11) - works regardless of connection
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            // Handle preset shortcuts (Ctrl+1-9) - works even when not connected for UI feedback
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                if (e.Key >= Key.D1 && e.Key <= Key.D9)
                {
                    int presetNumber = e.Key - Key.D0;
                    if (_ptzService.IsConnected)
                    {
                        GoToPresetAsync(presetNumber);
                    }
                    else
                    {
                        MessageBox.Show($"❌ Cannot go to preset {presetNumber}\n\nNot connected to camera. Please connect first.", 
                            "Go to Preset Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    e.Handled = true;
                    return;
                }
            }

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
                    if (!_ptzService.IsConnected) 
                    {
                        UpdateStatus("❌ Cannot go to home - not connected", true);
                        return;
                    }
                    await _ptzService.GoToHomeAsync();
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
            PortTextBox.Text = _settings.Port > 0 ? _settings.Port.ToString() : "";
            UsernameTextBox.Text = _settings.Username;
            PasswordBox.Password = _settings.Password ?? "";
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
            
            // Update camera info display
            if (connected && _currentCameraInfo != null)
            {
                var cameraName = _currentCameraInfo.GetDisplayName();
                if (!string.IsNullOrEmpty(cameraName))
                {
                    CameraInfoLabel.Text = cameraName;
                    CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f0a500")); // AccentGold
                }
                else
                {
                    CameraInfoLabel.Text = "Camera connected";
                    CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8b949e")); // TextMuted
                }
            }
            else
            {
                CameraInfoLabel.Text = "No camera connected";
                CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8b949e")); // TextMuted
            }
        }

        private void PresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string presetStr && int.TryParse(presetStr, out int presetNumber))
            {
                GoToPresetAsync(presetNumber);
            }
        }
        
        private async void GoToPresetAsync(int presetNumber)
        {
            if (!_ptzService.IsConnected)
            {
                MessageBox.Show($"❌ Cannot go to preset {presetNumber}\n\nNot connected to camera. Please connect first.", 
                    "Go to Preset Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            EnsureDiagnosticWindow();
            _diagnosticWindow?.AppendDiagnosticMessage($"[{DateTime.Now:HH:mm:ss}] 🏠 Request to go to preset {presetNumber}...");
            
            var success = await _ptzService.GoToPresetAsync(presetNumber);
            
            Dispatcher.Invoke(() =>
            {
                EnsureDiagnosticWindow();
                if (success)
                {
                    var msg = $"[{DateTime.Now:HH:mm:ss}] ✅ Camera moving to preset {presetNumber}!";
                    _diagnosticWindow?.AppendDiagnosticMessage(msg);
                    UpdateStatus($"✅ Moving to preset {presetNumber}!", false);
                }
                else
                {
                    var msg = $"[{DateTime.Now:HH:mm:ss}] ❌ Failed to go to preset {presetNumber}";
                    _diagnosticWindow?.AppendDiagnosticMessage(msg);
                    UpdateStatus($"❌ Failed to go to preset {presetNumber}", true);
                }
            });
        }
        
        private void SetPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (PresetSelectorComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && 
                item.Tag is string presetStr && int.TryParse(presetStr, out int presetNumber))
            {
                SetPresetAsync(presetNumber);
            }
            else
            {
                MessageBox.Show("Please select a preset number (1-9) from the dropdown.", 
                    "No Preset Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        
        private async void SetPresetAsync(int presetNumber)
        {
            if (!_ptzService.IsConnected)
            {
                MessageBox.Show($"❌ Cannot set preset {presetNumber}\n\nNot connected to camera. Please connect first.", 
                    "Set Preset Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            EnsureDiagnosticWindow();
            _diagnosticWindow?.AppendDiagnosticMessage($"[{DateTime.Now:HH:mm:ss}] ⚙️ Setting preset {presetNumber}...");
            
            var success = await _ptzService.SetPresetAsync(presetNumber);
            
            Dispatcher.Invoke(() =>
            {
                EnsureDiagnosticWindow();
                if (success)
                {
                    var msg = $"[{DateTime.Now:HH:mm:ss}] ✅ ✅ ✅ PRESET {presetNumber} SET SUCCESSFULLY!";
                    _diagnosticWindow?.AppendDiagnosticMessage("");
                    _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _diagnosticWindow?.AppendDiagnosticMessage(msg);
                    _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _diagnosticWindow?.AppendDiagnosticMessage("");
                    
                    MessageBox.Show($"✅ SUCCESS!\n\nPreset {presetNumber} has been saved successfully.\n\nYou can now use the preset {presetNumber} button to return to this position.", 
                        "Preset Set", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateStatus($"✅ Preset {presetNumber} set successfully!", false);
                }
                else
                {
                    var msg = $"[{DateTime.Now:HH:mm:ss}] ❌ ❌ ❌ SET PRESET {presetNumber} FAILED!";
                    _diagnosticWindow?.AppendDiagnosticMessage("");
                    _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _diagnosticWindow?.AppendDiagnosticMessage(msg);
                    _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _diagnosticWindow?.AppendDiagnosticMessage("");
                    
                    MessageBox.Show($"❌ FAILED\n\nCould not save preset {presetNumber}.\n\nThe camera may not support presets, or the command format is incorrect.\n\nCheck the diagnostic window for detailed error messages.", 
                        "Set Preset Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"❌ Failed to set preset {presetNumber}", true);
                }
            });
        }
        
        private void PresetSelectorComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Enable/disable SET button based on selection
            // This is handled by IsEnabled binding in XAML
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
            
            // Enable preset buttons
            Preset1Button.IsEnabled = enabled;
            Preset2Button.IsEnabled = enabled;
            Preset3Button.IsEnabled = enabled;
            Preset4Button.IsEnabled = enabled;
            Preset5Button.IsEnabled = enabled;
            Preset6Button.IsEnabled = enabled;
            Preset7Button.IsEnabled = enabled;
            Preset8Button.IsEnabled = enabled;
            Preset9Button.IsEnabled = enabled;
            PresetSelectorComboBox.IsEnabled = enabled;
            
            // Explicitly ensure SetHomeButton is enabled when connected
            if (enabled)
            {
                System.Diagnostics.Debug.WriteLine($"PTZ controls enabled - SetHomeButton.IsEnabled = {SetHomeButton.IsEnabled}");
            }
            
            if (!enabled)
            {
                UpdateStatus("PTZ controls disabled - connect to camera first", true);
            }
        }

        private void UpdateStatus(string message, bool isError = false)
        {
            // Log to debug output
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {(isError ? "❌" : "ℹ️")} {message}");
            
            // Also show in diagnostic window if it's open, or open it automatically
            Dispatcher.Invoke(() =>
            {
                EnsureDiagnosticWindow();
                _diagnosticWindow?.AppendDiagnosticMessage($"[{DateTime.Now:HH:mm:ss}] {(isError ? "❌" : "ℹ️")} {message}");
            });
        }
        
        private void EnsureDiagnosticWindow()
        {
            if (_diagnosticWindow == null || !_diagnosticWindow.IsLoaded)
            {
                _diagnosticWindow = new DiagnosticWindow();
                _diagnosticWindow.Closed += (s, args) => _diagnosticWindow = null;
                _diagnosticWindow.Show();
            }
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

            // First, identify the camera
            EnsureDiagnosticWindow();
            _diagnosticWindow?.AppendDiagnosticMessage("");
            _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _diagnosticWindow?.AppendDiagnosticMessage("🔍 CAMERA IDENTIFICATION");
            _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            
            _currentCameraInfo = await _identificationService.IdentifyCameraAsync(host, port, username, password);
            
            // Display discovered information
            _diagnosticWindow?.AppendDiagnosticMessage("");
            _diagnosticWindow?.AppendDiagnosticMessage("📋 DISCOVERED CAMERA INFORMATION:");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Manufacturer: {_currentCameraInfo.Manufacturer}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Model: {_currentCameraInfo.Model}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Firmware: {_currentCameraInfo.FirmwareVersion}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Serial: {_currentCameraInfo.SerialNumber}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Device Name: {_currentCameraInfo.DeviceName}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Supports ONVIF: {_currentCameraInfo.SupportsONVIF}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Supports PTZ: {_currentCameraInfo.SupportsPTZ}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Supports Presets: {_currentCameraInfo.SupportsPresets}");
            _diagnosticWindow?.AppendDiagnosticMessage("");
            _diagnosticWindow?.AppendDiagnosticMessage("🎯 RECOMMENDED INTERFACE:");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Interface: {_currentCameraInfo.RecommendedInterface}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Endpoint: {_currentCameraInfo.RecommendedEndpoint}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Protocol: {_currentCameraInfo.RecommendedProtocol}");
            _diagnosticWindow?.AppendDiagnosticMessage($"   Port: {_currentCameraInfo.RecommendedPort}");
            _diagnosticWindow?.AppendDiagnosticMessage("");
            _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _diagnosticWindow?.AppendDiagnosticMessage("");
            
            UpdateStatus($"Camera identified: {_currentCameraInfo.GetDisplayName()}");
            UpdateStatus($"Using recommended interface: {_currentCameraInfo.RecommendedInterface}");

            // Update camera info display immediately after identification
            Dispatcher.Invoke(() =>
            {
                var cameraName = _currentCameraInfo.GetDisplayName();
                if (!string.IsNullOrEmpty(cameraName))
                {
                    CameraInfoLabel.Text = $"{cameraName} (Identifying...)";
                    CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#58a6ff")); // AccentBlue
                }
            });

            // Now connect using the recommended interface
            var connected = await _ptzService.ConnectAsync(host, port, username, password);
            
            if (connected)
            {
                UpdateConnectionIndicator(true);
                UpdateStatus($"✅ Connected to {_currentCameraInfo.GetDisplayName()} via {_currentCameraInfo.RecommendedInterface}!");
                SaveSettings();
                
                // Show camera info in diagnostic window
                _diagnosticWindow?.AppendDiagnosticMessage($"✅ Successfully connected to {_currentCameraInfo.GetDisplayName()}");
                _diagnosticWindow?.AppendDiagnosticMessage($"   Using interface: {_currentCameraInfo.RecommendedInterface}");
            }
            else
            {
                UpdateConnectionIndicator(false);
                UpdateStatus("Connection failed", true);
                _diagnosticWindow?.AppendDiagnosticMessage("❌ Connection failed - check credentials and network");
                
                // Clear camera info on failure
                Dispatcher.Invoke(() =>
                {
                    if (_currentCameraInfo != null)
                    {
                        CameraInfoLabel.Text = $"{_currentCameraInfo.GetDisplayName()} - Connection failed";
                        CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f85149")); // AccentRed
                    }
                    else
                    {
                        CameraInfoLabel.Text = "Connection failed";
                        CameraInfoLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f85149")); // AccentRed
                    }
                });
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

        private bool _isFullscreen = false;
        private WindowState _previousWindowState;
        private WindowStyle _previousWindowStyle;
        private bool _previousTopmost;

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                // Enter fullscreen
                _previousWindowState = WindowState;
                _previousWindowStyle = WindowStyle;
                _previousTopmost = Topmost;
                
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                Topmost = true;
                _isFullscreen = true;
                FullscreenButton.Content = "⛶ EXIT FULLSCREEN";
            }
            else
            {
                // Exit fullscreen
                WindowStyle = _previousWindowStyle;
                WindowState = _previousWindowState;
                Topmost = _previousTopmost;
                _isFullscreen = false;
                FullscreenButton.Content = "⛶ FULLSCREEN";
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
            if (!_ptzService.IsConnected)
            {
                MessageBox.Show("❌ Cannot go to home position\n\nNot connected to camera. Please connect first.", 
                    "Go Home Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            await _ptzService.GoToHomeAsync();
        }

        private async void SetHomeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_ptzService.IsConnected)
            {
                UpdateStatus("❌ Cannot set home - not connected to camera", true);
                MessageBox.Show("❌ Cannot set home position\n\nNot connected to camera. Please connect first.", 
                    "Set Home Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            await _ptzService.SetHomeAsync();
        }

        private void PtzService_StatusChanged(object? sender, string message)
        {
            // Status messages from PTZ service should be displayed prominently
            Dispatcher.Invoke(() =>
            {
                // Ensure diagnostic window is open for status messages
                EnsureDiagnosticWindow();
                _diagnosticWindow?.AppendDiagnosticMessage($"[{DateTime.Now:HH:mm:ss}] {message}");
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            });
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
                StreamUrlTextBox.Text = streamUrl;
                // Auto-start video if URL is discovered
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500); // Small delay to ensure UI is ready
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await StartVideo(streamUrl);
                    });
                });
            });
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
                    _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                    VideoView.MediaPlayer = _mediaPlayer;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to initialize LibVLC: {ex.Message}", true);
            }
        }
        
        private async void StartVideoButton_Click(object sender, RoutedEventArgs e)
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
                StartVideoButton.IsEnabled = false;
                StopVideoButton.IsEnabled = true;
                VideoPlaceholderText.Visibility = Visibility.Collapsed;
                
                var media = new Media(_libVLC, streamUrl, FromType.FromLocation);
                _mediaPlayer.Play(media);
                
                UpdateStatus($"Starting video stream: {streamUrl}");
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to start video: {ex.Message}", true);
                StartVideoButton.IsEnabled = true;
                StopVideoButton.IsEnabled = false;
                VideoPlaceholderText.Visibility = Visibility.Visible;
                return false;
            }
        }
        
        private void StopVideoButton_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }
        
        private void StopVideo()
        {
            try
            {
                _mediaPlayer?.Stop();
                StartVideoButton.IsEnabled = true;
                StopVideoButton.IsEnabled = false;
                VideoPlaceholderText.Visibility = Visibility.Visible;
                UpdateStatus("Video stream stopped");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error stopping video: {ex.Message}", true);
            }
        }
        
        private async void AutoDetectStreamButton_Click(object sender, RoutedEventArgs e)
        {
            await TryAutoDetectStream();
        }
        
        private async Task TryAutoDetectStream()
        {
            var host = HostTextBox.Text.Trim();
            var username = UsernameTextBox.Text.Trim();
            var password = PasswordBox.Password;
            var encodedPassword = Uri.EscapeDataString(password);
            var creds = !string.IsNullOrEmpty(username) ? $"{username}:{encodedPassword}@" : "";

            var rtspPort = 554;
            var urls = new[]
            {
                $"rtsp://{creds}{host}:{rtspPort}/11",  // Working format
                $"rtsp://{creds}{host}:{rtspPort}/12",
                $"rtsp://{creds}{host}:{rtspPort}/h264/ch1/main/av_stream",
                $"rtsp://{creds}{host}:{rtspPort}/h264/ch1/sub/av_stream",
                $"rtsp://{creds}{host}:{rtspPort}/live/main_stream",
                $"rtsp://{creds}{host}:{rtspPort}/live/sub_stream",
                $"rtsp://{creds}{host}:{rtspPort}/stream1",
                $"rtsp://{creds}{host}:{rtspPort}/stream2",
            };

            _autoDetectCts?.Cancel();
            _autoDetectCts = new CancellationTokenSource();
            var token = _autoDetectCts.Token;

            AutoDetectStreamButton.IsEnabled = false;
            StartVideoButton.IsEnabled = false;

            bool found = false;
            for (int i = 0; i < urls.Length && !token.IsCancellationRequested; i++)
            {
                StreamUrlTextBox.Text = urls[i];
                UpdateStatus($"Testing RTSP URL {i + 1}/{urls.Length}: {urls[i]}");
                
                if (await StartVideo(urls[i]))
                {
                    await Task.Delay(2000, token); // Wait 2 seconds to see if stream works
                    if (_mediaPlayer?.IsPlaying == true)
                    {
                        found = true;
                        UpdateStatus($"✓ Working RTSP URL found: {urls[i]}");
                        break;
                    }
                    else
                    {
                        StopVideo();
                    }
                }
                
                if (!token.IsCancellationRequested && i < urls.Length - 1)
                {
                    await Task.Delay(500, token);
                }
            }

            AutoDetectStreamButton.IsEnabled = true;
            StartVideoButton.IsEnabled = true;

            if (!found && !token.IsCancellationRequested)
            {
                MessageBox.Show("Auto-detect failed. Please enter stream URL manually.", 
                    "Auto-Detect Failed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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

        private async void DiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticButton.IsEnabled = false;
            
            // Open diagnostic window if not already open
            if (_diagnosticWindow == null || !_diagnosticWindow.IsLoaded)
            {
                _diagnosticWindow = new DiagnosticWindow();
                _diagnosticWindow.Closed += (s, args) => _diagnosticWindow = null;
                _diagnosticWindow.Show();
            }
            
            _diagnosticWindow.ClearDiagnostic();
            _diagnosticWindow.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _diagnosticWindow.AppendDiagnosticMessage("Starting comprehensive camera diagnostic...");
            _diagnosticWindow.AppendDiagnosticMessage("This will test all known camera APIs and connection methods");
            _diagnosticWindow.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            var host = HostTextBox.Text;
            if (!int.TryParse(PortTextBox.Text, out int port))
            {
                _diagnosticWindow.AppendDiagnosticMessage("❌ Invalid port number");
                DiagnosticButton.IsEnabled = true;
                return;
            }

            var username = UsernameTextBox.Text;
            var password = PasswordBox.Password;

            try
            {
                var results = await _diagnosticService.RunFullDiagnostic(host, port, username, password);
                
                _diagnosticWindow.AppendDiagnosticMessage("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                _diagnosticWindow.AppendDiagnosticMessage("DIAGNOSTIC RESULTS SUMMARY:");
                _diagnosticWindow.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

                foreach (var result in results)
                {
                    var statusIcon = result.Value.Contains("SUCCESS") || result.Value.Contains("200 OK") ? "✓" : "✗";
                    _diagnosticWindow?.AppendDiagnosticMessage($"{statusIcon} {result.Key}: {result.Value}");
                }

                // Check if we found any working endpoints
                var workingEndpoints = results.Where(r => r.Value.Contains("SUCCESS") || r.Value.Contains("200 OK")).ToList();
                if (workingEndpoints.Any())
                {
                    _diagnosticWindow?.AppendDiagnosticMessage("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _diagnosticWindow?.AppendDiagnosticMessage("✓ WORKING ENDPOINTS FOUND:");
                    _diagnosticWindow?.AppendDiagnosticMessage("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    foreach (var endpoint in workingEndpoints)
                    {
                        _diagnosticWindow?.AppendDiagnosticMessage($"  ✓ {endpoint.Key}");
                    }
                    _diagnosticWindow?.AppendDiagnosticMessage("\nThese endpoints can be used for camera control!");
                }
                else
                {
                    _diagnosticWindow?.AppendDiagnosticMessage("\n⚠️ No working endpoints found. Check credentials and camera settings.");
                }
            }
            catch (Exception ex)
            {
                _diagnosticWindow?.AppendDiagnosticMessage($"❌ Diagnostic error: {ex.Message}");
            }
            finally
            {
                DiagnosticButton.IsEnabled = true;
            }
        }

        private void DiagnosticService_DiagnosticMessage(object? sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                // Send diagnostic messages to the diagnostic window
                if (_diagnosticWindow != null && _diagnosticWindow.IsLoaded)
                {
                    _diagnosticWindow.AppendDiagnosticMessage(message);
                }
                else
                {
                    // If diagnostic window not open, open it
                    if (_diagnosticWindow == null || !_diagnosticWindow.IsLoaded)
                    {
                        _diagnosticWindow = new DiagnosticWindow();
                        _diagnosticWindow.Closed += (s, args) => _diagnosticWindow = null;
                        _diagnosticWindow.Show();
                    }
                    _diagnosticWindow.AppendDiagnosticMessage(message);
                }
            });
        }


        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopVideo();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
            _ptzService?.Dispose();
            _discoveryService?.Dispose();
            _diagnosticService?.Dispose();
            _identificationService?.Dispose();
            _diagnosticWindow?.Close();
            _autoDetectCts?.Cancel();
            SaveSettings();
        }
    }
}
