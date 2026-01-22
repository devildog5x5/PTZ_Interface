using System;
using System.Collections.Generic;
using System.Linq;

namespace PTZCameraOperator.Models
{
    /// <summary>
    /// Comprehensive camera identification information
    /// Discovered through various APIs and used to determine the best control interface
    /// </summary>
    public class CameraInfo
    {
        // Basic Information
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string FirmwareVersion { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        
        // Network Information
        public string IPAddress { get; set; } = "";
        public int Port { get; set; } = 80;
        public string MACAddress { get; set; } = "";
        
        // Capabilities
        public bool SupportsONVIF { get; set; } = false;
        public string? ONVIFVersion { get; set; }
        public bool SupportsPTZ { get; set; } = false;
        public bool SupportsPresets { get; set; } = false;
        public bool SupportsStreaming { get; set; } = false;
        
        // Recommended Control Interface
        public string RecommendedInterface { get; set; } = "ONVIF"; // ONVIF, HiSilicon, Hikvision, Dahua, etc.
        public string RecommendedEndpoint { get; set; } = "";
        public string RecommendedProtocol { get; set; } = "HTTP"; // HTTP, HTTPS
        public int RecommendedPort { get; set; } = 80;
        public string? RecommendedUsername { get; set; } // e.g., "onvif1" for some cameras
        
        // Additional Details
        public Dictionary<string, string> AdditionalInfo { get; set; } = new Dictionary<string, string>();
        
        // Discovery Metadata
        public DateTime DiscoveryTime { get; set; } = DateTime.Now;
        public string DiscoveryMethod { get; set; } = ""; // "ONVIF", "WS-Discovery", "Manual", etc.
        
        public override string ToString()
        {
            var name = !string.IsNullOrEmpty(DeviceName) ? DeviceName 
                      : !string.IsNullOrEmpty(Manufacturer) ? $"{Manufacturer} {Model}".Trim()
                      : "Unknown Camera";
            return $"{name} ({IPAddress}:{Port})";
        }
        
        public string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(DeviceName))
                return DeviceName;
            
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Manufacturer))
                parts.Add(Manufacturer);
            if (!string.IsNullOrEmpty(Model))
                parts.Add(Model);
            
            return parts.Count > 0 ? string.Join(" ", parts) : "Unknown Camera";
        }
    }
}
