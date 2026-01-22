using System;
using System.IO;
using System.Text.Json;

namespace PTZCameraOperator.Models
{
    public class CameraSettings
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 0;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
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
                    if (settings != null)
                    {
                        // Clear old default values if they match the complete old default set
                        // Old defaults were: Host=192.168.1.11/192.168.1.12, Port=443/8080, Username=root/admin, Password=XTL.a1.1000!
                        bool isOldDefaultSet = 
                            (settings.Host == "192.168.1.11" || settings.Host == "192.168.1.12") &&
                            (settings.Port == 443 || settings.Port == 8080 || settings.Port == 80) &&
                            (settings.Username == "root" || settings.Username == "admin" || settings.Username == "onvif1") &&
                            settings.Password == "XTL.a1.1000!";
                        
                        if (isOldDefaultSet)
                        {
                            // Clear all connection fields to remove old defaults
                            settings.Host = "";
                            settings.Port = 0;
                            settings.Username = "";
                            settings.Password = "";
                            // Save the cleared settings
                            settings.Save();
                        }
                        
                        return settings;
                    }
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
