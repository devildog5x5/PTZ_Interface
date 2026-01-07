using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PTZCameraControl.Services
{
    public class OnvifPtzService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl = "";
        private string _username = "";
        private string _password = "";
        
        public bool IsConnected { get; private set; }
        
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? StreamUrlDiscovered;

        public OnvifPtzService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<bool> ConnectAsync(string host, int port, string username, string password)
        {
            try
            {
                _baseUrl = $"http://{host}:{port}/onvif/device_service";
                _username = username;
                _password = password;

                // Test connection
                var request = CreateSoapRequest("GetDeviceInformation", "http://www.onvif.org/ver10/device/wsdl");
                var response = await SendRequestAsync(request);
                
                if (response != null)
                {
                    IsConnected = true;
                    StatusChanged?.Invoke(this, "Connected to camera");
                    
                    // Try to discover stream URL
                    _ = Task.Run(async () =>
                    {
                        var streamUrl = await GetStreamUriAsync();
                        if (!string.IsNullOrEmpty(streamUrl))
                        {
                            StreamUrlDiscovered?.Invoke(this, streamUrl);
                        }
                    });
                    
                    return true;
                }
                
                ErrorOccurred?.Invoke(this, "Connection failed - no response from camera");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Connection error: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            StatusChanged?.Invoke(this, "Disconnected");
        }

        public async Task<string?> GetStreamUriAsync()
        {
            try
            {
                var request = CreateGetStreamUriRequest();
                var response = await SendRequestAsync(request);
                
                if (response != null)
                {
                    var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
                    var uri = response.Descendants(ns + "Uri").FirstOrDefault();
                    return uri?.Value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting stream URI: {ex.Message}");
            }

            return null;
        }

        public async Task<bool> ContinuousMoveAsync(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateContinuousMoveRequest(panSpeed, tiltSpeed, zoomSpeed);
                var response = await SendRequestAsync(request);
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> StopAsync()
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateStopRequest();
                var response = await SendRequestAsync(request);
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> AbsoluteMoveAsync(float pan, float tilt, float zoom)
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateAbsoluteMoveRequest(pan, tilt, zoom);
                var response = await SendRequestAsync(request);
                StatusChanged?.Invoke(this, $"Moving to position: P:{pan:F2} T:{tilt:F2} Z:{zoom:F2}");
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(float Pan, float Tilt, float Zoom)?> GetPositionAsync()
        {
            if (!IsConnected) return null;
            
            try
            {
                var request = CreateGetPositionRequest();
                var response = await SendRequestAsync(request);
                
                if (response != null)
                {
                    var ns = XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl");
                    var position = response.Descendants(ns + "Position").FirstOrDefault();
                    
                    if (position != null)
                    {
                        var panTilt = position.Element(XName.Get("PanTilt", "http://www.onvif.org/ver10/schema"));
                        var zoomElem = position.Element(XName.Get("Zoom", "http://www.onvif.org/ver10/schema"));
                        
                        if (panTilt != null)
                        {
                            float pan = float.Parse(panTilt.Attribute("x")?.Value ?? "0");
                            float tilt = float.Parse(panTilt.Attribute("y")?.Value ?? "0");
                            float zoom = float.Parse(zoomElem?.Attribute("x")?.Value ?? "0");
                            
                            return (pan, tilt, zoom);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting position: {ex.Message}");
            }

            return null;
        }

        public async Task<bool> GoToHomeAsync()
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateGoToHomeRequest();
                var response = await SendRequestAsync(request);
                StatusChanged?.Invoke(this, "Moving to home position");
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> SetHomeAsync()
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateSetHomeRequest();
                var response = await SendRequestAsync(request);
                StatusChanged?.Invoke(this, "Home position set");
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        private string CreateSoapRequest(string action, string xmlns)
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <{action} xmlns=""{xmlns}""/>
  </s:Body>
</s:Envelope>";
        }

        private string CreateGetStreamUriRequest()
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GetStreamUri xmlns=""http://www.onvif.org/ver10/media/wsdl"">
      <StreamSetup>
        <Stream xmlns=""http://www.onvif.org/ver10/schema"">RTP-Unicast</Stream>
        <Transport xmlns=""http://www.onvif.org/ver10/schema"">
          <Protocol>RTSP</Protocol>
        </Transport>
      </StreamSetup>
      <ProfileToken>MainStream</ProfileToken>
    </GetStreamUri>
  </s:Body>
</s:Envelope>";
        }

        private string CreateContinuousMoveRequest(float pan, float tilt, float zoom)
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <ContinuousMove xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
      <Velocity>
        <PanTilt x=""{pan}"" y=""{tilt}"" xmlns=""http://www.onvif.org/ver10/schema""/>
        <Zoom x=""{zoom}"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Velocity>
    </ContinuousMove>
  </s:Body>
</s:Envelope>";
        }

        private string CreateStopRequest()
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <Stop xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
      <PanTilt>true</PanTilt>
      <Zoom>true</Zoom>
    </Stop>
  </s:Body>
</s:Envelope>";
        }

        private string CreateAbsoluteMoveRequest(float pan, float tilt, float zoom)
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <AbsoluteMove xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
      <Position>
        <PanTilt x=""{pan}"" y=""{tilt}"" xmlns=""http://www.onvif.org/ver10/schema""/>
        <Zoom x=""{zoom}"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Position>
      <Speed>
        <PanTilt x=""0.5"" y=""0.5"" xmlns=""http://www.onvif.org/ver10/schema""/>
        <Zoom x=""0.5"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Speed>
    </AbsoluteMove>
  </s:Body>
</s:Envelope>";
        }

        private string CreateGetPositionRequest()
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GetStatus xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
    </GetStatus>
  </s:Body>
</s:Envelope>";
        }

        private string CreateGoToHomeRequest()
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GotoHomePosition xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
      <Speed>
        <PanTilt x=""0.5"" y=""0.5"" xmlns=""http://www.onvif.org/ver10/schema""/>
        <Zoom x=""0.5"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Speed>
    </GotoHomePosition>
  </s:Body>
</s:Envelope>";
        }

        private string CreateSetHomeRequest()
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <SetHomePosition xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>MainProfile</ProfileToken>
    </SetHomePosition>
  </s:Body>
</s:Envelope>";
        }

        private async Task<XDocument?> SendRequestAsync(string soapRequest)
        {
            try
            {
                var content = new StringContent(soapRequest, Encoding.UTF8, "application/soap+xml");
                var response = await _httpClient.PostAsync(_baseUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync();
                    return XDocument.Parse(responseText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ONVIF request error: {ex.Message}");
            }

            return null;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
