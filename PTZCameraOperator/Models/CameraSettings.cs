using System;
using System.IO;
using System.Text.Json;

namespace PTZCameraOperator.Models
{
    public class CameraSettings
    {
        public string Host { get; set; } = "192.168.1.11";
        public int Port { get; set; } = 443;
        public string Username { get; set; } = "root";
        public string Password { get; set; } = "XTL.a1.1000!";
        public string StreamUrl { get; set; } = "";
        public float PanSpeed { get; set; } = 0.5f;
        public float ZoomSpeed { get; set; } = 0.3f;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PTZCameraControl",
            "settings.json"
        );

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public static CameraSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<CameraSettings>(json);
                    return settings ?? new CameraSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            }

            return new CameraSettings();
        }
    }
}
