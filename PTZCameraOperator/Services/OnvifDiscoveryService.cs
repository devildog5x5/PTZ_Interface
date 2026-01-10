using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PTZCameraOperator.Services
{
    /// <summary>
    /// Represents a camera discovered via ONVIF WS-Discovery
    /// Contains all information extracted from the discovery probe response
    /// </summary>
    public class DiscoveredCamera
    {
        public string Endpoint { get; set; } = "";           // ONVIF endpoint reference
        public string IPAddress { get; set; } = "";          // Camera IP address
        public int Port { get; set; } = 80;                   // ONVIF HTTP port
        public string Manufacturer { get; set; } = "";        // Camera manufacturer (e.g., "Hikvision", "Axis")
        public string Model { get; set; } = "";               // Camera model number
        public string SerialNumber { get; set; } = "";        // Camera serial number
        public string HardwareId { get; set; } = "";             // Hardware identifier
        public string Name { get; set; } = "";                 // Camera name/description
        public string OnvifAddress { get; set; } = "";        // Full ONVIF service URL

        /// <summary>
        /// Returns a user-friendly string representation for display in UI
        /// </summary>
        public override string ToString()
        {
            var name = !string.IsNullOrEmpty(Name) ? Name : !string.IsNullOrEmpty(Manufacturer) ? $"{Manufacturer} {Model}" : "Unknown Camera";
            return $"{name} ({IPAddress}:{Port})";
        }
    }

    /// <summary>
    /// ONVIF Device Discovery Service
    /// 
    /// Implements WS-Discovery protocol (UDP multicast) to find ONVIF cameras on the local network.
    /// Sends probe messages to multicast address 239.255.255.250:3702 and listens for responses.
    /// 
    /// Discovery process:
    /// 1. Sends WS-Discovery Probe message via UDP multicast
    /// 2. Listens for ProbeMatch responses from cameras
    /// 3. Parses XML responses to extract camera information
    /// 4. Fires CameraDiscovered event for each camera found
    /// 
    /// Typical discovery time: 1-5 seconds depending on network
    /// </summary>
    public class OnvifDiscoveryService : IDisposable
    {
        private const string MULTICAST_ADDRESS = "239.255.255.250";
        private const int MULTICAST_PORT = 3702;
        private UdpClient? _udpClient;
        private bool _isDiscovering;
        private CancellationTokenSource? _cts;

        public event EventHandler<DiscoveredCamera>? CameraDiscovered;

        public async Task<List<DiscoveredCamera>> DiscoverAsync(int timeoutSeconds = 5, CancellationToken cancellationToken = default)
        {
            var discoveredCameras = new List<DiscoveredCamera>();
            var cameraSet = new HashSet<string>(); // Track by endpoint to avoid duplicates

            if (_isDiscovering)
            {
                return discoveredCameras;
            }

            _isDiscovering = true;
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                _udpClient?.Close();
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                _udpClient.JoinMulticastGroup(IPAddress.Parse(MULTICAST_ADDRESS));

                // Send probe message
                var probeMessage = CreateProbeMessage();
                var probeBytes = Encoding.UTF8.GetBytes(probeMessage);
                var multicastEndpoint = new IPEndPoint(IPAddress.Parse(MULTICAST_ADDRESS), MULTICAST_PORT);
                await _udpClient.SendAsync(probeBytes, probeBytes.Length, multicastEndpoint);

                // Listen for responses
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), _cts.Token);
                
                while (!_cts.Token.IsCancellationRequested)
                {
                    var receiveTask = _udpClient.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        break;
                    }

                    try
                    {
                        var result = await receiveTask;
                        var responseXml = Encoding.UTF8.GetString(result.Buffer);
                        var camera = ParseProbeMatch(responseXml, result.RemoteEndPoint);

                        if (camera != null && !string.IsNullOrEmpty(camera.Endpoint) && !cameraSet.Contains(camera.Endpoint))
                        {
                            cameraSet.Add(camera.Endpoint);
                            discoveredCameras.Add(camera);
                            CameraDiscovered?.Invoke(this, camera);
                        }
                    }
                    catch
                    {
                        // Ignore parse errors, continue listening
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Discovery error: {ex.Message}");
            }
            finally
            {
                _isDiscovering = false;
            }

            return discoveredCameras;
        }

        private string CreateProbeMessage()
        {
            var messageId = Guid.NewGuid().ToString();
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery"">
  <s:Header>
    <a:Action s:mustUnderstand=""1"">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
    <a:MessageID>urn:uuid:{messageId}</a:MessageID>
    <a:To s:mustUnderstand=""1"">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
  </s:Header>
  <s:Body>
    <d:Probe>
      <d:Types>dn:NetworkVideoTransmitter</d:Types>
    </d:Probe>
  </s:Body>
</s:Envelope>";
        }

        private DiscoveredCamera? ParseProbeMatch(string xml, IPEndPoint remoteEndPoint)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var ns = XNamespace.Get("http://schemas.xmlsoap.org/ws/2005/04/discovery");
                var addr = XNamespace.Get("http://schemas.xmlsoap.org/ws/2004/08/addressing");

                var probeMatch = doc.Descendants(ns + "ProbeMatches").FirstOrDefault()?.Element(ns + "ProbeMatch");
                if (probeMatch == null)
                    return null;

                var endpoint = probeMatch.Element(addr + "EndpointReference")?.Element(addr + "Address")?.Value;
                var types = probeMatch.Element(ns + "Types")?.Value ?? "";
                var scopes = probeMatch.Element(ns + "Scopes")?.Value ?? "";
                var xaddrs = probeMatch.Element(ns + "XAddrs")?.Value ?? "";

                if (string.IsNullOrEmpty(xaddrs))
                    return null;

                // Extract ONVIF service address
                var onvifAddresses = xaddrs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var onvifAddress = onvifAddresses.FirstOrDefault(a => a.Contains("/onvif/device_service")) 
                    ?? onvifAddresses.FirstOrDefault() 
                    ?? "";

                // Parse IP and port from XAddrs or use remote endpoint
                string ip = remoteEndPoint.Address.ToString();
                int port = 80;

                if (!string.IsNullOrEmpty(onvifAddress))
                {
                    try
                    {
                        var uri = new Uri(onvifAddress);
                        if (!string.IsNullOrEmpty(uri.Host))
                            ip = uri.Host;
                        if (uri.Port > 0)
                            port = uri.Port;
                    }
                    catch { }
                }

                // Extract device info from scopes
                var manufacturer = ExtractFromScopes(scopes, "onvif://www.onvif.org/name/", "Manufacturer");
                var model = ExtractFromScopes(scopes, "onvif://www.onvif.org/model/", "Model");
                var hardwareId = ExtractFromScopes(scopes, "onvif://www.onvif.org/hardware/", "HardwareId");
                var serialNumber = ExtractFromScopes(scopes, "onvif://www.onvif.org/serial_number/", "SerialNumber");
                var name = ExtractFromScopes(scopes, "onvif://www.onvif.org/name/", "Name");

                return new DiscoveredCamera
                {
                    Endpoint = endpoint ?? "",
                    IPAddress = ip,
                    Port = port,
                    Manufacturer = manufacturer,
                    Model = model,
                    SerialNumber = serialNumber,
                    HardwareId = hardwareId,
                    Name = name,
                    OnvifAddress = onvifAddress
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Parse error: {ex.Message}");
                return null;
            }
        }

        private string ExtractFromScopes(string scopes, string prefix, string fallback)
        {
            if (string.IsNullOrEmpty(scopes))
                return fallback;

            var scopeParts = scopes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var scope in scopeParts)
            {
                if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = scope.Substring(prefix.Length);
                    if (!string.IsNullOrEmpty(value))
                        return Uri.UnescapeDataString(value);
                }
            }

            return fallback;
        }

        public void StopDiscovery()
        {
            _cts?.Cancel();
            _isDiscovering = false;
        }

        public void Dispose()
        {
            StopDiscovery();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _cts?.Dispose();
        }
    }
}

