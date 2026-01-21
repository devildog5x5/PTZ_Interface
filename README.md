# PTZ Camera Operator

A professional PTZ Camera Controller with comprehensive support for ONVIF, HiSilicon, Hikvision, and Dahua cameras. Features live video streaming, multiple presets, and intelligent camera detection.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Windows](https://img.shields.io/badge/Platform-Windows-0078D6)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- **Full PTZ Control** - Pan, Tilt, and Zoom with 8-directional movement pad and speed control
- **Multi-Protocol Support** - ONVIF Profile S, HiSilicon Hi3510, Hikvision ISAPI, and Dahua CGI
- **Live Video Streaming** - Real-time RTSP video via LibVLC
- **Auto Stream Detection** - Automatically discovers camera stream URLs
- **Multiple Presets** - Save and recall up to 10 preset positions (0-9) with keyboard shortcuts
- **Coordinate-Based Recall** - Automatic fallback to coordinate-based positioning when preset recall fails
- **Intelligent Camera Detection** - Automatic manufacturer and model detection
- **Absolute Positioning** - Precise pan/tilt/zoom slider controls
- **Home Position** - Set and return to home position (Preset 0)
- **Diagnostic Tools** - Comprehensive diagnostic window for troubleshooting
- **Modern UI** - Dark theme with professional color scheme

## Screenshots

The application features a striking dark interface with:
- Electric Blue (`#00BFFF`) primary controls
- Competition Orange (`#FF6600`) action buttons
- Racing Red (`#FF1744`) stop/danger buttons
- Neon glow effects on interactive elements

## Requirements

- Windows 10/11 (64-bit)
- .NET 8.0 Runtime (included in self-contained builds)
- PTZ camera compatible with ONVIF, HiSilicon, Hikvision, or Dahua protocols
- Network connection to camera

## Download

### 📥 Quick Download Links

**Note:** Due to GitHub's 100MB file size limit, pre-built binaries are available via:
- **GitHub Releases** (recommended) - Check the [Releases page](https://github.com/devildog5x5/PTZ_Interface/releases)
- **Build from source** (see Option 3 below) - Takes ~2-3 minutes

#### Option 1: Installer Package (Recommended)
**Download the Windows installer from GitHub Releases:**

📦 **Download:** [Latest Release](https://github.com/devildog5x5/PTZ_Interface/releases) → Download `PTZCameraOperatorSetup-1.0.0.exe` (~117 MB)

**Features:**
- ✅ Professional Windows installer
- ✅ Automatic installation with desktop shortcut option
- ✅ Uninstaller included
- ✅ All dependencies bundled
- ✅ Easy installation process

**Installation:**
1. Download `PTZCameraOperatorSetup-1.0.0.exe`
2. Run the installer (admin rights may be required)
3. Follow the installation wizard
4. Launch from Start Menu or desktop shortcut

#### Option 2: Portable Build (No Installation)
**Download the ready-to-run portable application:**

🔗 **Direct Download:** [publish/release folder](https://github.com/devildog5x5/PTZ_Interface/tree/2026-01-09-6cls/publish/release) (~145 MB)

**To Download:**
1. Navigate to the [publish/release folder](https://github.com/devildog5x5/PTZ_Interface/tree/2026-01-09-6cls/publish/release) on GitHub
2. Click "Download" button (top right) or use the green "Code" button → "Download ZIP"
3. Extract the files from the `publish/release` folder
4. Run `PTZCameraOperator.exe` - no installation required!

**What's included:**
- ✅ Self-contained executable (includes .NET 8.0 runtime)
- ✅ All dependencies including LibVLC
- ✅ Ready to run - no installation needed
- ✅ Portable - run from any folder

#### Option 3: Build from Source
If you prefer to build it yourself:

```bash
git clone https://github.com/devildog5x5/PTZ_Interface.git
cd PTZ_Interface
cd PTZCameraOperator
dotnet publish -c Release -r win-x64 --self-contained true -o ../publish/release
```

**Create Installer (Optional):**
1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. Open `PTZCameraControlInstaller.iss` in Inno Setup Compiler
3. Click "Build" → "Compile" or press F9
4. The installer will be created in `Installer/Output/PTZCameraOperatorSetup-1.0.0.exe`

### Building from Source
```bash
git clone https://github.com/devildog5x5/PTZ_Interface.git
cd PTZ_Interface
cd PTZCameraOperator
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -o publish/release
```

## Usage

1. Enter your camera's IP address and port (default: 80 for HTTP, 443 for HTTPS)
2. Enter camera credentials (username/password)
3. Click **CONNECT** - the app will automatically detect your camera type and best connection method
4. Click **AUTO DETECT** or manually enter the RTSP URL for video streaming
5. Click **START** to begin video streaming
6. Use the PTZ control pad to move the camera:
   - **W/↑** - Tilt Up
   - **S/↓** - Tilt Down
   - **A/←** - Pan Left
   - **D/→** - Pan Right
   - **+/=** - Zoom In
   - **-/_** - Zoom Out
   - **H** - Go to Home position
   - **Ctrl+1-9** - Go to Preset 1-9

### Setting Presets
- **SET HOME** - Saves current position as home (Preset 0)
- **Preset Buttons 1-9** - Click to go to that preset, or use Ctrl+1-9 keyboard shortcuts
- **SET + Preset Selector** - Select a preset number (1-9) and click SET to save current position

### Advanced Features
- **Diagnostic Window** - Opens automatically to show connection details and camera information
- **Fullscreen Mode** - Press F11 or click FULLSCREEN button
- **Speed Control** - Adjust pan/tilt and zoom speeds with sliders

## Project Structure

```
PTZ_Interface/
├── PTZCameraOperator/
│   ├── Views/
│   │   ├── MainWindow.xaml         # Main UI layout
│   │   ├── MainWindow.xaml.cs      # UI logic & event handlers
│   │   └── DiagnosticWindow.xaml   # Diagnostic output window
│   ├── Models/
│   │   ├── CameraSettings.cs       # Settings persistence
│   │   ├── CameraInfo.cs           # Camera identification data
│   │   └── PresetPosition.cs       # Preset position storage
│   ├── Services/
│   │   ├── OnvifPtzService.cs      # ONVIF/PTZ communication
│   │   ├── CameraIdentificationService.cs  # Camera detection
│   │   └── CameraDiagnosticService.cs      # Diagnostic testing
│   └── PTZCameraOperator.csproj    # Project file
└── publish/
    └── release/                    # Self-contained release build
```

## Dependencies

- [LibVLCSharp](https://github.com/videolan/libvlcsharp) - VLC media player for .NET
- [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/) - LibVLC native binaries

## License

MIT License - See [LICENSE.txt](LICENSE.txt) for details.

## Supported Cameras

- **ONVIF Profile S** - Standard ONVIF-compatible cameras
- **HiSilicon Hi3510** - Cameras using HiSilicon chipset (common in many IP cameras)
- **Hikvision** - ISAPI protocol support
- **Dahua** - CGI protocol support

The application automatically detects your camera type and selects the best control interface.

## Author

**Robert Foster** - 2025

Professional PTZ Camera Controller with multi-protocol support and LibVLC video streaming.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

