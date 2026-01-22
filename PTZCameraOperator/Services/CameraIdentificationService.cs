using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using PTZCameraOperator.Models;

namespace PTZCameraOperator.Services
{
    /// <summary>
    /// Camera Identification Service
    /// Discovers camera manufacturer, model, capabilities, and determines the best control interface
    /// </summary>
    public class CameraIdentificationService : IDisposable
    {
        private readonly HttpClient _httpClient;
        
        public event EventHandler<string>? StatusChanged;
        
        public CameraIdentificationService()
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }
        
        /// <summary>
        /// Comprehensive camera identification
        /// Queries the camera using multiple methods to discover all available information
        /// </summary>
        public async Task<CameraInfo> IdentifyCameraAsync(string host, int port, string username, string password)
        {
            var cameraInfo = new CameraInfo
            {
                IPAddress = host,
                Port = port,
                DiscoveryTime = DateTime.Now
            };
            
            StatusChanged?.Invoke(this, $"🔍 Starting camera identification for {host}:{port}...");
            
            // Try multiple identification methods in parallel
            var tasks = new List<Task>
            {
                Task.Run(async () => await IdentifyViaONVIF(cameraInfo, host, port, username, password)),
                Task.Run(async () => await IdentifyViaHikvisionISAPI(cameraInfo, host, port, username, password)),
                Task.Run(async () => await IdentifyViaHiSilicon(cameraInfo, host, port, username, password)),
                Task.Run(async () => await IdentifyViaDahua(cameraInfo, host, port, username, password)),
                Task.Run(async () => await IdentifyViaHTTPHeaders(cameraInfo, host, port, username, password))
            };
            
            await Task.WhenAll(tasks);
            
            // Determine best control interface based on discovered information
            DetermineBestInterface(cameraInfo);
            
            StatusChanged?.Invoke(this, $"✅ Camera identification complete: {cameraInfo.GetDisplayName()}");
            StatusChanged?.Invoke(this, $"   Recommended interface: {cameraInfo.RecommendedInterface}");
            
            return cameraInfo;
        }
        
        private async Task IdentifyViaONVIF(CameraInfo info, string host, int port, string username, string password)
        {
            try
            {
                var endpoints = new[]
                {
                    "/onvif/device_service",
                    "/onvif/device_service.wsdl",
                    "/onvif/device_service.asmx",
                    "/onvif",
                    "/device_service",
                    "/webservices/device_service",
                    "/onvif/device"
                };
                
                var protocols = port == 443 || port == 8443 ? new[] { "https", "http" } : new[] { "http", "https" };
                
                foreach (var protocol in protocols)
                {
                    foreach (var endpoint in endpoints)
                    {
                        var url = $"{protocol}://{host}:{port}{endpoint}";
                        
                        try
                        {
                            // Try SOAP 1.2 first (ONVIF standard)
                            var soapRequest = CreateGetDeviceInformationRequest();
                            var request = new HttpRequestMessage(HttpMethod.Post, url);
                            request.Content = new StringContent(soapRequest, Encoding.UTF8, "application/soap+xml");
                            
                            if (!string.IsNullOrEmpty(username))
                            {
                                var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                            }
                            
                            var response = await _httpClient.SendAsync(request);
                            var responseText = await response.Content.ReadAsStringAsync();
                            
                            if (response.IsSuccessStatusCode && responseText.Contains("GetDeviceInformationResponse"))
                            {
                                var doc = XDocument.Parse(responseText);
                                var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
                                
                                var manufacturer = doc.Descendants(ns + "Manufacturer").FirstOrDefault()?.Value ?? "";
                                var model = doc.Descendants(ns + "Model").FirstOrDefault()?.Value ?? "";
                                var firmware = doc.Descendants(ns + "FirmwareVersion").FirstOrDefault()?.Value ?? "";
                                var serial = doc.Descendants(ns + "SerialNumber").FirstOrDefault()?.Value ?? "";
                                var hardware = doc.Descendants(ns + "HardwareId").FirstOrDefault()?.Value ?? "";
                                
                                if (!string.IsNullOrEmpty(manufacturer))
                                {
                                    info.Manufacturer = manufacturer;
                                    info.Model = model;
                                    info.FirmwareVersion = firmware;
                                    info.SerialNumber = serial;
                                    info.HardwareId = hardware;
                                    info.SupportsONVIF = true;
                                    info.ONVIFVersion = "1.0"; // Could be enhanced to detect version
                                    info.DiscoveryMethod = "ONVIF GetDeviceInformation";
                                    info.AdditionalInfo["ONVIF Endpoint"] = url;
                                    info.SupportsPTZ = true;
                                    info.SupportsPresets = true;
                                    info.SupportsStreaming = true;
                                    
                                    StatusChanged?.Invoke(this, $"  ✓ ONVIF: {manufacturer} {model}");
                                    return;
                                }
                            }
                            
                            // Try SOAP 1.1 if SOAP 1.2 failed
                            if (!response.IsSuccessStatusCode)
                            {
                                var soap11Request = CreateGetDeviceInformationRequestSOAP11();
                                request = new HttpRequestMessage(HttpMethod.Post, url);
                                request.Content = new StringContent(soap11Request, Encoding.UTF8, "text/xml");
                                
                                if (!string.IsNullOrEmpty(username))
                                {
                                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                                }
                                
                                response = await _httpClient.SendAsync(request);
                                responseText = await response.Content.ReadAsStringAsync();
                                
                                if (response.IsSuccessStatusCode && responseText.Contains("GetDeviceInformationResponse"))
                                {
                                    var doc = XDocument.Parse(responseText);
                                    var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
                                    
                                    var manufacturer = doc.Descendants(ns + "Manufacturer").FirstOrDefault()?.Value ?? "";
                                    var model = doc.Descendants(ns + "Model").FirstOrDefault()?.Value ?? "";
                                    
                                    if (!string.IsNullOrEmpty(manufacturer))
                                    {
                                        info.Manufacturer = manufacturer;
                                        info.Model = model;
                                        info.SupportsONVIF = true;
                                        info.AdditionalInfo["ONVIF Endpoint"] = url;
                                        info.SupportsPTZ = true;
                                        info.SupportsPresets = true;
                                        
                                        StatusChanged?.Invoke(this, $"  ✓ ONVIF (SOAP 1.1): {manufacturer} {model}");
                                        return;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ONVIF identification error: {ex.Message}");
            }
        }
        
        private string CreateGetDeviceInformationRequestSOAP11()
        {
            return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
  </s:Body>
</s:Envelope>";
        }
        
        private async Task IdentifyViaHikvisionISAPI(CameraInfo info, string host, int port, string username, string password)
        {
            try
            {
                var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
                var endpoints = new[]
                {
                    "/ISAPI/System/deviceInfo",
                    "/ISAPI/System/DeviceInfo",
                    "/ISAPI/System/Deviceinfo",
                    "/ISAPI/System/deviceinfo"
                };
                
                foreach (var endpoint in endpoints)
                {
                    var url = $"{baseUrl}{endpoint}";
                    
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrEmpty(username))
                        {
                            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                        }
                        
                        var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            
                            // Try XML parsing first
                            try
                            {
                                var doc = XDocument.Parse(content);
                                
                                var deviceName = doc.Descendants("deviceName").FirstOrDefault()?.Value ?? 
                                                doc.Descendants("DeviceName").FirstOrDefault()?.Value ?? "";
                                var model = doc.Descendants("model").FirstOrDefault()?.Value ?? 
                                           doc.Descendants("Model").FirstOrDefault()?.Value ?? "";
                                var serial = doc.Descendants("serialNumber").FirstOrDefault()?.Value ?? 
                                            doc.Descendants("SerialNumber").FirstOrDefault()?.Value ?? "";
                                var firmware = doc.Descendants("firmwareVersion").FirstOrDefault()?.Value ?? 
                                              doc.Descendants("FirmwareVersion").FirstOrDefault()?.Value ?? "";
                                var mac = doc.Descendants("macAddress").FirstOrDefault()?.Value ?? 
                                         doc.Descendants("MacAddress").FirstOrDefault()?.Value ?? "";
                                
                                if (!string.IsNullOrEmpty(deviceName) || !string.IsNullOrEmpty(model))
                                {
                                    info.Manufacturer = "Hikvision";
                                    info.Model = model;
                                    info.DeviceName = deviceName;
                                    info.SerialNumber = serial;
                                    info.FirmwareVersion = firmware;
                                    info.MACAddress = mac;
                                    info.AdditionalInfo["Hikvision ISAPI Endpoint"] = url;
                                    info.SupportsPTZ = true;
                                    info.SupportsPresets = true;
                                    
                                    StatusChanged?.Invoke(this, $"  ✓ Hikvision ISAPI: {deviceName} ({model})");
                                    return;
                                }
                            }
                            catch
                            {
                                // If XML parsing fails, check if content contains Hikvision keywords
                                if (content.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) ||
                                    content.Contains("hikvision", StringComparison.OrdinalIgnoreCase))
                                {
                                    info.Manufacturer = "Hikvision";
                                    info.AdditionalInfo["Hikvision ISAPI Endpoint"] = url;
                                    info.SupportsPTZ = true;
                                    info.SupportsPresets = true;
                                    
                                    StatusChanged?.Invoke(this, $"  ✓ Hikvision ISAPI detected (non-XML response)");
                                    return;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Hikvision identification error: {ex.Message}");
            }
        }
        
        private async Task IdentifyViaHiSilicon(CameraInfo info, string host, int port, string username, string password)
        {
            try
            {
                var baseUrl = $"http://{host}:{port}";
                var url = $"{baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi?action=getstatus";
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(username))
                {
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                }
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    
                    if (content.Contains("hi3510", StringComparison.OrdinalIgnoreCase) || 
                        url.Contains("hi3510"))
                    {
                        info.Manufacturer = "HiSilicon";
                        info.Model = "Hi3510";
                        info.AdditionalInfo["HiSilicon Endpoint"] = url;
                        info.SupportsPTZ = true;
                        
                        StatusChanged?.Invoke(this, $"  ✓ HiSilicon Hi3510 detected");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HiSilicon identification error: {ex.Message}");
            }
        }
        
        private async Task IdentifyViaDahua(CameraInfo info, string host, int port, string username, string password)
        {
            try
            {
                var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
                var endpoints = new[]
                {
                    "/cgi-bin/magicBox.cgi?action=getDeviceType",
                    "/cgi-bin/magicBox.cgi?action=getDeviceInfo",
                    "/cgi-bin/configManager.cgi?action=getConfig&name=General",
                    "/cgi-bin/configManager.cgi?action=getConfig&name=System"
                };
                
                foreach (var endpoint in endpoints)
                {
                    var url = $"{baseUrl}{endpoint}";
                    
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrEmpty(username))
                        {
                            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                        }
                        
                        var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            
                            // Check for Dahua-specific keywords
                            if (content.Contains("Dahua", StringComparison.OrdinalIgnoreCase) ||
                                content.Contains("deviceType", StringComparison.OrdinalIgnoreCase) ||
                                content.Contains("deviceName", StringComparison.OrdinalIgnoreCase))
                            {
                                info.Manufacturer = "Dahua";
                                info.AdditionalInfo["Dahua Endpoint"] = url;
                                info.SupportsPTZ = true;
                                info.SupportsPresets = true;
                                
                                // Try to extract model from response
                                var modelMatch = System.Text.RegularExpressions.Regex.Match(content, @"deviceType[=:]([^\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (modelMatch.Success)
                                {
                                    info.Model = modelMatch.Groups[1].Value.Trim();
                                }
                                
                                StatusChanged?.Invoke(this, $"  ✓ Dahua camera detected: {info.Model}");
                                return;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dahua identification error: {ex.Message}");
            }
        }
        
        private async Task IdentifyViaHTTPHeaders(CameraInfo info, string host, int port, string username, string password)
        {
            try
            {
                var url = port == 443 || port == 8443 ? $"https://{host}:{port}/" : $"http://{host}:{port}/";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                var response = await _httpClient.SendAsync(request);
                var serverHeader = response.Headers.Server?.FirstOrDefault()?.ToString() ?? "";
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                
                if (!string.IsNullOrEmpty(serverHeader))
                {
                    info.AdditionalInfo["Server Header"] = serverHeader;
                    
                    // Detect manufacturer from server header
                    if (serverHeader.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.Manufacturer))
                    {
                        info.Manufacturer = "Hikvision";
                        StatusChanged?.Invoke(this, $"  ✓ Detected from headers: Hikvision");
                    }
                    else if (serverHeader.Contains("Dahua", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.Manufacturer))
                    {
                        info.Manufacturer = "Dahua";
                        StatusChanged?.Invoke(this, $"  ✓ Detected from headers: Dahua");
                    }
                    else if (serverHeader.Contains("Axis", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.Manufacturer))
                    {
                        info.Manufacturer = "Axis";
                        StatusChanged?.Invoke(this, $"  ✓ Detected from headers: Axis");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HTTP header identification error: {ex.Message}");
            }
        }
        
        private void DetermineBestInterface(CameraInfo info)
        {
            // Determine best control interface based on discovered information
            if (info.SupportsONVIF)
            {
                info.RecommendedInterface = "ONVIF";
                info.RecommendedEndpoint = "/onvif/device_service";
                info.RecommendedProtocol = info.Port == 443 || info.Port == 8443 ? "HTTPS" : "HTTP";
                info.RecommendedPort = info.Port;
            }
            else if (info.Manufacturer.Equals("Hikvision", StringComparison.OrdinalIgnoreCase))
            {
                info.RecommendedInterface = "Hikvision";
                info.RecommendedEndpoint = "/ISAPI/System/deviceInfo";
                info.RecommendedProtocol = info.Port == 443 || info.Port == 8443 ? "HTTPS" : "HTTP";
                info.RecommendedPort = info.Port;
            }
            else if (info.Manufacturer.Equals("HiSilicon", StringComparison.OrdinalIgnoreCase))
            {
                info.RecommendedInterface = "HiSilicon";
                info.RecommendedEndpoint = "/web/cgi-bin/hi3510/ptzctrl.cgi";
                info.RecommendedProtocol = "HTTP";
                info.RecommendedPort = 80; // HiSilicon typically uses HTTP on port 80
            }
            else if (info.Manufacturer.Equals("Dahua", StringComparison.OrdinalIgnoreCase))
            {
                info.RecommendedInterface = "Dahua";
                info.RecommendedEndpoint = "/cgi-bin/magicBox.cgi";
                info.RecommendedProtocol = info.Port == 443 || info.Port == 8443 ? "HTTPS" : "HTTP";
                info.RecommendedPort = info.Port;
            }
            else
            {
                // Default to ONVIF attempt
                info.RecommendedInterface = "ONVIF";
                info.RecommendedEndpoint = "/onvif/device_service";
                info.RecommendedProtocol = "HTTP";
                info.RecommendedPort = info.Port;
            }
            
            // Set PTZ and preset support based on manufacturer
            if (info.Manufacturer.Equals("HiSilicon", StringComparison.OrdinalIgnoreCase) ||
                info.Manufacturer.Equals("Hikvision", StringComparison.OrdinalIgnoreCase) ||
                info.Manufacturer.Equals("Dahua", StringComparison.OrdinalIgnoreCase))
            {
                info.SupportsPTZ = true;
                info.SupportsPresets = true;
            }
            
            if (info.SupportsONVIF)
            {
                info.SupportsPTZ = true;
                info.SupportsPresets = true;
                info.SupportsStreaming = true;
            }
        }
        
        private string CreateGetDeviceInformationRequest()
        {
            return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
  </s:Body>
</s:Envelope>";
        }
        
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
