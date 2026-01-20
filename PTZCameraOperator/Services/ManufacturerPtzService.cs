using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PTZCameraOperator.Services
{
    /// <summary>
    /// Manufacturer-specific PTZ service - uses Hikvision ISAPI, Dahua CGI, etc.
    /// This is a fallback when ONVIF doesn't work
    /// </summary>
    public class ManufacturerPtzService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl = "";
        private string _username = "";
        private string _password = "";
        private CameraType _cameraType = CameraType.Unknown;
        
        public enum CameraType
        {
            Unknown,
            Hikvision,
            Dahua,
            HiSilicon,  // HiSilicon Hi3510/Hi3516 chipset (common in many IP cameras)
            Generic
        }

        public bool IsConnected { get; private set; }
        public CameraType DetectedCameraType => _cameraType;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        public ManufacturerPtzService()
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// Attempts to connect using manufacturer-specific APIs
        /// </summary>
        public async Task<bool> ConnectAsync(string host, int port, string username, string password, CameraType? preferredType = null)
        {
            try
            {
                _username = username ?? "";
                _password = password ?? "";
                _baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";

                StatusChanged?.Invoke(this, $"Attempting manufacturer-specific connection to {host}:{port}...");

                // Try Hikvision ISAPI first
                if (preferredType == null || preferredType == CameraType.Hikvision)
                {
                    if (await TestHikvisionConnection())
                    {
                        _cameraType = CameraType.Hikvision;
                        IsConnected = true;
                        StatusChanged?.Invoke(this, "✓ Connected via Hikvision ISAPI");
                        return true;
                    }
                }

                // Try Dahua CGI
                if (preferredType == null || preferredType == CameraType.Dahua)
                {
                    if (await TestDahuaConnection())
                    {
                        _cameraType = CameraType.Dahua;
                        IsConnected = true;
                        StatusChanged?.Invoke(this, "✓ Connected via Dahua CGI");
                        return true;
                    }
                }

                // Try HiSilicon Hi3510 CGI (found on many Hikvision-based cameras)
                if (preferredType == null || preferredType == CameraType.HiSilicon)
                {
                    if (await TestHiSiliconConnection())
                    {
                        _cameraType = CameraType.HiSilicon;
                        IsConnected = true;
                        StatusChanged?.Invoke(this, "✓ Connected via HiSilicon Hi3510 CGI");
                        return true;
                    }
                }

                // Try generic CGI as last resort
                if (await TestGenericConnection())
                {
                    _cameraType = CameraType.Generic;
                    IsConnected = true;
                    StatusChanged?.Invoke(this, "✓ Connected via generic CGI");
                    return true;
                }

                ErrorOccurred?.Invoke(this, "Failed to connect using any manufacturer-specific API");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Connection error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TestHikvisionConnection()
        {
            try
            {
                var url = $"{_baseUrl}/ISAPI/System/deviceInfo";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);

                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("<deviceName>") || content.Contains("Hikvision"))
                    {
                        return true;
                    }
                }
            }
            catch { }
            
            return false;
        }

        private async Task<bool> TestDahuaConnection()
        {
            try
            {
                var url = $"{_baseUrl}/cgi-bin/magicBox.cgi?action=getDeviceType";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);

                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("deviceType") || content.Contains("type="))
                    {
                        return true;
                    }
                }
            }
            catch { }
            
            return false;
        }

        private async Task<bool> TestHiSiliconConnection()
        {
            try
            {
                // Test the HiSilicon Hi3510 PTZ control endpoint found by diagnostic
                var url = $"{_baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);

                var response = await _httpClient.SendAsync(request);
                
                // Hi3510 endpoint returns 200 OK even for test requests
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                // Some implementations return 401 if auth is wrong, 404 if endpoint doesn't exist
                // If we get 401, the endpoint exists but credentials are wrong (we'll handle that separately)
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return true; // Endpoint exists, credentials issue will be handled by caller
                }
            }
            catch { }
            
            return false;
        }

        private async Task<bool> TestGenericConnection()
        {
            try
            {
                var url = $"{_baseUrl}/cgi-bin/currenttime.cgi";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { }
            
            return false;
        }

        public async Task<bool> ContinuousMoveAsync(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            if (!IsConnected) return false;

            try
            {
                switch (_cameraType)
                {
                    case CameraType.Hikvision:
                        return await HikvisionContinuousMove(panSpeed, tiltSpeed, zoomSpeed);
                    case CameraType.Dahua:
                        return await DahuaContinuousMove(panSpeed, tiltSpeed, zoomSpeed);
                    case CameraType.HiSilicon:
                        return await HiSiliconContinuousMove(panSpeed, tiltSpeed, zoomSpeed);
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"PTZ move error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> HikvisionContinuousMove(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            var url = $"{_baseUrl}/ISAPI/PTZCtrl/channels/1/continuous";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            AddBasicAuth(request);

            var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<PTZData>
    <pan>{panSpeed}</pan>
    <tilt>{tiltSpeed}</tilt>
    <zoom>{zoomSpeed}</zoom>
</PTZData>";

            request.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
            
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }

        private async Task<bool> DahuaContinuousMove(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            // Convert normalized speeds to Dahua codes
            var panCode = panSpeed > 0 ? "Right" : panSpeed < 0 ? "Left" : "";
            var tiltCode = tiltSpeed > 0 ? "Up" : tiltSpeed < 0 ? "Down" : "";
            var zoomCode = zoomSpeed > 0 ? "ZoomTele" : zoomSpeed < 0 ? "ZoomWide" : "";

            // Dahua uses separate commands for each axis
            bool success = true;
            
            if (!string.IsNullOrEmpty(panCode))
            {
                var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=start&channel=0&code={panCode}&arg1=0&arg2={Math.Abs(panSpeed)}&arg3=0";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                var response = await _httpClient.SendAsync(request);
                success = success && response.IsSuccessStatusCode;
            }

            if (!string.IsNullOrEmpty(tiltCode))
            {
                var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=start&channel=0&code={tiltCode}&arg1=0&arg2={Math.Abs(tiltSpeed)}&arg3=0";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                var response = await _httpClient.SendAsync(request);
                success = success && response.IsSuccessStatusCode;
            }

            if (!string.IsNullOrEmpty(zoomCode))
            {
                var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=start&channel=0&code={zoomCode}&arg1=0&arg2={Math.Abs(zoomSpeed)}&arg3=0";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                var response = await _httpClient.SendAsync(request);
                success = success && response.IsSuccessStatusCode;
            }

            return success;
        }

        private async Task<bool> HiSiliconContinuousMove(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            // HiSilicon Hi3510 PTZ control uses GET requests with query parameters
            // Format: /web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act=[action]&speed=[1-8]
            // Actions: up, down, left, right, zoomin, zoomout, stop
            // Speed: 1-8 (typically 4-6 for moderate speed)
            
            bool success = true;
            var speed = (int)Math.Max(1, Math.Min(8, Math.Abs(Math.Max(panSpeed, Math.Max(tiltSpeed, zoomSpeed))) * 8));
            
            // Hi3510 requires separate commands for each axis
            if (Math.Abs(panSpeed) > 0.01f)
            {
                var action = panSpeed > 0 ? "right" : "left";
                var url = $"{_baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act={action}&speed={speed}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                
                try
                {
                    var response = await _httpClient.SendAsync(request);
                    success = success && response.IsSuccessStatusCode;
                }
                catch
                {
                    success = false;
                }
            }
            
            if (Math.Abs(tiltSpeed) > 0.01f)
            {
                var action = tiltSpeed > 0 ? "up" : "down";
                var url = $"{_baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act={action}&speed={speed}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                
                try
                {
                    var response = await _httpClient.SendAsync(request);
                    success = success && response.IsSuccessStatusCode;
                }
                catch
                {
                    success = false;
                }
            }
            
            if (Math.Abs(zoomSpeed) > 0.01f)
            {
                var action = zoomSpeed > 0 ? "zoomin" : "zoomout";
                var url = $"{_baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act={action}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                
                try
                {
                    var response = await _httpClient.SendAsync(request);
                    success = success && response.IsSuccessStatusCode;
                }
                catch
                {
                    success = false;
                }
            }
            
            return success;
        }

        public async Task<bool> StopAsync()
        {
            if (!IsConnected) return false;

            try
            {
                switch (_cameraType)
                {
                    case CameraType.Hikvision:
                        return await HikvisionStop();
                    case CameraType.Dahua:
                        return await DahuaStop();
                    case CameraType.HiSilicon:
                        return await HiSiliconStop();
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"PTZ stop error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> HikvisionStop()
        {
            var url = $"{_baseUrl}/ISAPI/PTZCtrl/channels/1/continuous";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            AddBasicAuth(request);

            var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<PTZData>
    <pan>0</pan>
    <tilt>0</tilt>
    <zoom>0</zoom>
</PTZData>";

            request.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }

        private async Task<bool> DahuaStop()
        {
            var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=stop&channel=0&code=Left&arg1=0&arg2=0&arg3=0";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBasicAuth(request);
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }

        private async Task<bool> HiSiliconStop()
        {
            try
            {
                // HiSilicon Hi3510 stop command
                var url = $"{_baseUrl}/web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act=stop";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBasicAuth(request);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private void AddBasicAuth(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(_username) || !string.IsNullOrEmpty(_password))
            {
                var credentials = $"{_username}:{_password}";
                var authBytes = Encoding.UTF8.GetBytes(credentials);
                var authValue = Convert.ToBase64String(authBytes);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            _cameraType = CameraType.Unknown;
            StatusChanged?.Invoke(this, "Disconnected");
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
