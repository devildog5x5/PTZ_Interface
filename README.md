# PTZ Camera Control

A professional ONVIF Profile S PTZ Camera Controller with live video streaming.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Windows](https://img.shields.io/badge/Platform-Windows-0078D6)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- **Full PTZ Control** - Pan, Tilt, and Zoom with 8-directional movement pad
- **ONVIF Profile S** - Industry-standard camera protocol support
- **Live Video Streaming** - Real-time RTSP video via LibVLC
- **Auto Stream Detection** - Automatically discovers camera stream URLs
- **Absolute Positioning** - Precise pan/tilt/zoom slider controls
- **Home Position** - Set and return to home position
- **Vibrant UI** - Modern dark theme with Electric Blue, Competition Orange, and Racing Red accents

## Screenshots

The application features a striking dark interface with:
- Electric Blue (`#00BFFF`) primary controls
- Competition Orange (`#FF6600`) action buttons
- Racing Red (`#FF1744`) stop/danger buttons
- Neon glow effects on interactive elements

## Requirements

- Windows 10/11 (64-bit)
- .NET 8.0 Runtime
- ONVIF Profile S compatible PTZ camera
- Network connection to camera

## Installation

### Using Installer
Download and run `PTZCameraControlSetup-1.0.0.exe` from the Releases page.

### Building from Source
```bash
git clone https://github.com/yourusername/PTZ_InterFace.git
cd PTZ_InterFace
dotnet restore
dotnet build -c Release
```

## Usage

1. Enter your camera's IP address and ONVIF port (default: 80)
2. Enter camera credentials (username/password)
3. Click **CONNECT** to establish ONVIF connection
4. Click **AUTO DETECT** or manually enter the RTSP URL
5. Click **START** to begin video streaming
6. Use the PTZ control pad to move the camera

## Project Structure

```
PTZ_InterFace/
├── App.xaml                 # Application resources & theme
├── App.xaml.cs
├── MainWindow.xaml          # Main UI layout
├── MainWindow.xaml.cs       # UI logic & event handlers
├── PTZCameraControl.csproj  # Project file
├── PTZCameraControl.sln     # Solution file
├── Models/
│   └── CameraSettings.cs    # Settings persistence
├── Services/
│   └── OnvifPtzService.cs   # ONVIF communication
└── Installer/
    └── PTZCameraControlInstaller.iss  # Inno Setup script
```

## Dependencies

- [LibVLCSharp](https://github.com/videolan/libvlcsharp) - VLC media player for .NET
- [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/) - LibVLC native binaries

## License

MIT License - See [LICENSE.txt](LICENSE.txt) for details.

## Author

Robert Foster - 2025

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

