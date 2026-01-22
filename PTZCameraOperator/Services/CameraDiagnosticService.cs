using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PTZCameraOperator.Services
{
    /// <summary>
    /// Comprehensive camera diagnostic service
    /// Tests multiple manufacturer-specific APIs and connection methods to find what works
    /// </summary>
    public class CameraDiagnosticService : IDisposable
    {
        private readonly HttpClient _httpClient;

        public CameraDiagnosticService()
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public event EventHandler<string>? DiagnosticMessage;

        /// <summary>
        /// Comprehensive diagnostic test - tries all known camera APIs
        /// Tests BOTH port 80 (web interface) and specified port (ONVIF) for cameras with separate services
        /// </summary>
        public async Task<Dictionary<string, string>> RunFullDiagnostic(string host, int port, string username, string password)
        {
            var results = new Dictionary<string, string>();
            
            DiagnosticMessage?.Invoke(this, $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            DiagnosticMessage?.Invoke(this, $"Running comprehensive diagnostic on {host}");
            
            // Always test port 80 (HTTP web interface) and the specified port
            var portsToTest = new List<int> { 80 }; // Always test port 80
            if (port != 80 && port != 443)
            {
                portsToTest.Add(port); // Add specified port if it's not 80 or 443
            }
            // For HTTPS ports, test both HTTP (80) and HTTPS (443/8443)
            if (port == 443 || port == 8443)
            {
                if (!portsToTest.Contains(port))
                {
                    portsToTest.Add(port); // Add HTTPS port
                }
                DiagnosticMessage?.Invoke(this, $"Testing ports: 80 (HTTP web interface) AND {port} (HTTPS service port)");
            }
            else
            {
                DiagnosticMessage?.Invoke(this, $"Testing ports: 80 (HTTP web interface) AND {port} (service port)");
            }
            DiagnosticMessage?.Invoke(this, $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            // Test 0: Web Interface (Port 80) - ALWAYS test port 80 regardless of specified port
            DiagnosticMessage?.Invoke(this, $"\n[0] Testing Web Interface on Port 80 (HTTP)...");
            await TestWebInterface(host, 80, username, password, results);
            
            // If service port is HTTPS (443/8443), also test HTTPS web interface
            if (port == 443 || port == 8443)
            {
                DiagnosticMessage?.Invoke(this, $"\n[0b] Testing Web Interface on Port {port} (HTTPS)...");
                await TestWebInterface(host, port, username, password, results);
            }

            // Test 1: Basic HTTP connectivity on specified port
            DiagnosticMessage?.Invoke(this, $"\n[1] Testing Service Port {port}...");
            await TestBasicConnectivity(host, port, results);

            // Test 2: Manufacturer detection
            await DetectManufacturer(host, port, username, password, results);
            
            // Also check port 80 for manufacturer info
            if (port != 80)
            {
                await DetectManufacturer(host, 80, username, password, results);
            }

            // Test 3: Hikvision ISAPI (test on BOTH ports)
            await TestHikvisionISAPI(host, port, username, password, results);
            if (port != 80)
            {
                await TestHikvisionISAPI(host, 80, username, password, results);
            }

            // Test 4: Dahua CGI (test on BOTH ports)
            await TestDahuaCGI(host, port, username, password, results);
            if (port != 80)
            {
                await TestDahuaCGI(host, 80, username, password, results);
            }

            // Test 5: Generic CGI paths
            await TestGenericCGI(host, port, username, password, results);
            if (port != 80)
            {
                await TestGenericCGI(host, 80, username, password, results);
            }

            // Test 6: ONVIF variations (ONVIF typically on service port, not 80)
            // Test ONVIF on the specified service port
            await TestONVIFVariations(host, port, username, password, results);
            
            // Also try ONVIF on port 80 and 443 if service port is different
            if (port != 80)
            {
                await TestONVIFVariations(host, 80, username, password, results);
            }
            if (port == 443 || port == 8443)
            {
                // Already tested above, but ensure we have it
            }
            else if (port != 443 && port != 8443)
            {
                // Try HTTPS ONVIF even if service port is not HTTPS (some cameras support both)
                DiagnosticMessage?.Invoke(this, "\n[6b] Testing ONVIF on Port 443 (HTTPS fallback)...");
                await TestONVIFVariations(host, 443, username, password, results);
            }

            // Test 7: Try different authentication methods (with better Digest detection)
            // Test on service port
            await TestAuthenticationMethods(host, port, username, password, results);
            // Also test on port 80
            if (port != 80)
            {
                await TestAuthenticationMethods(host, 80, username, password, results);
            }
            // If service port is HTTPS, we already tested it above

            // Test 8: Test actual PTZ endpoints if we found a working API
            if (results.Any(r => r.Value.Contains("SUCCESS") || r.Value.Contains("200 OK")))
            {
                await TestPTZEndpoints(host, port, username, password, results);
                if (port != 80)
                {
                    await TestPTZEndpoints(host, 80, username, password, results);
                }
            }

            // Summary
            DiagnosticMessage?.Invoke(this, $"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            DiagnosticMessage?.Invoke(this, $"DIAGNOSTIC COMPLETE");
            DiagnosticMessage?.Invoke(this, $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            return results;
        }

        private async Task TestWebInterface(string host, int webPort, string username, string password, Dictionary<string, string> results)
        {
            try
            {
                // Use HTTPS for ports 443 and 8443, HTTP for others
                var protocol = (webPort == 443 || webPort == 8443) ? "https" : "http";
                var url = $"{protocol}://{host}:{webPort}/";
                DiagnosticMessage?.Invoke(this, $"  Testing: {url}");
                
                // Try without auth first
                var response = await _httpClient.GetAsync(url);
                var statusCode = response.StatusCode;
                var serverHeader = response.Headers.Server?.FirstOrDefault()?.ToString() ?? "Unknown";
                
                // Check WWW-Authenticate header
                var authMethods = new List<string>();
                if (response.Headers.WwwAuthenticate.Count > 0)
                {
                    authMethods.AddRange(response.Headers.WwwAuthenticate.Select(h => h.Scheme));
                }
                
                results[$"Web Interface (Port {webPort})"] = $"Status: {statusCode}, Server: {serverHeader}, Auth Required: {(authMethods.Any() ? string.Join(", ", authMethods) : "None")}";
                
                if (statusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    DiagnosticMessage?.Invoke(this, $"  → Requires authentication: {string.Join(", ", authMethods)}");
                    
                    // Try with Basic Auth to see if credentials work
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    
                    var authResponse = await _httpClient.SendAsync(request);
                    if (authResponse.IsSuccessStatusCode)
                    {
                        results[$"Web Interface Auth ({webPort})"] = $"✓ CREDENTIALS WORK! Status: {authResponse.StatusCode}";
                        DiagnosticMessage?.Invoke(this, $"  ✓✓✓ CREDENTIALS VERIFIED ON WEB INTERFACE! ✓✓✓");
                    }
                    else
                    {
                        results[$"Web Interface Auth ({webPort})"] = $"✗ Credentials failed: {authResponse.StatusCode}";
                        DiagnosticMessage?.Invoke(this, $"  ✗ Credentials rejected: {authResponse.StatusCode}");
                    }
                }
                else if (statusCode == System.Net.HttpStatusCode.OK)
                {
                    DiagnosticMessage?.Invoke(this, $"  ✓ Web interface accessible without auth");
                }
                
                DiagnosticMessage?.Invoke(this, $"    Server: {serverHeader}");
            }
            catch (Exception ex)
            {
                results[$"Web Interface ({webPort})"] = $"FAILED: {ex.Message}";
                DiagnosticMessage?.Invoke(this, $"  ✗ Failed: {ex.Message}");
            }
        }

        private async Task TestBasicConnectivity(string host, int port, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "  Testing basic HTTP connectivity...");
            
            try
            {
                var url = port == 443 || port == 8443 ? $"https://{host}:{port}/" : $"http://{host}:{port}/";
                var response = await _httpClient.GetAsync(url);
                var serverHeader = response.Headers.Server?.FirstOrDefault()?.ToString() ?? "Unknown";
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "Unknown";
                
                // Check for auth requirements
                var authMethods = new List<string>();
                if (response.Headers.WwwAuthenticate.Count > 0)
                {
                    authMethods.AddRange(response.Headers.WwwAuthenticate.Select(h => h.Scheme));
                }
                
                results[$"Basic HTTP ({port})"] = $"Status: {response.StatusCode}, Server: {serverHeader}, Content-Type: {contentType}, Auth: {(authMethods.Any() ? string.Join(", ", authMethods) : "None")}";
                DiagnosticMessage?.Invoke(this, $"  ✓ HTTP {response.StatusCode} - Server: {serverHeader}");
                if (authMethods.Any())
                {
                    DiagnosticMessage?.Invoke(this, $"    → Requires: {string.Join(", ", authMethods)}");
                }
            }
            catch (Exception ex)
            {
                results[$"Basic HTTP ({port})"] = $"FAILED: {ex.Message}";
                DiagnosticMessage?.Invoke(this, $"  ✗ Failed: {ex.Message}");
            }
        }

        private async Task DetectManufacturer(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[2] Detecting camera manufacturer...");
            
            var url = port == 443 || port == 8443 ? $"https://{host}:{port}/" : $"http://{host}:{port}/";
            
            try
            {
                var response = await _httpClient.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();
                
                if (content.Contains("hikvision", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("海康威视", StringComparison.OrdinalIgnoreCase))
                {
                    results["Manufacturer"] = "HIKVISION";
                    DiagnosticMessage?.Invoke(this, "  ✓ Detected: HIKVISION camera");
                }
                else if (content.Contains("dahua", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("大华", StringComparison.OrdinalIgnoreCase))
                {
                    results["Manufacturer"] = "DAHUA";
                    DiagnosticMessage?.Invoke(this, "  ✓ Detected: DAHUA camera");
                }
                else if (content.Contains("axis", StringComparison.OrdinalIgnoreCase))
                {
                    results["Manufacturer"] = "AXIS";
                    DiagnosticMessage?.Invoke(this, "  ✓ Detected: AXIS camera");
                }
                else if (content.Contains("onvif", StringComparison.OrdinalIgnoreCase))
                {
                    results["Manufacturer"] = "ONVIF_COMPLIANT";
                    DiagnosticMessage?.Invoke(this, "  ✓ Detected: ONVIF-compliant camera");
                }
                else
                {
                    results["Manufacturer"] = "UNKNOWN";
                    DiagnosticMessage?.Invoke(this, "  ? Manufacturer unknown - checking response headers");
                    
                    // Check response headers for clues
                    var serverHeader = response.Headers.Server?.FirstOrDefault()?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(serverHeader))
                    {
                        DiagnosticMessage?.Invoke(this, $"    Server header: {serverHeader}");
                        results["Server Header"] = serverHeader;
                    }
                }
            }
            catch (Exception ex)
            {
                results["Manufacturer"] = $"FAILED: {ex.Message}";
                DiagnosticMessage?.Invoke(this, $"  ✗ Detection failed: {ex.Message}");
            }
        }

        private async Task TestHikvisionISAPI(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[3] Testing Hikvision ISAPI...");
            
            var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
            var endpoints = new[]
            {
                "/ISAPI/System/deviceInfo",
                "/ISAPI/PTZCtrl/channels/1/absolute",
                "/ISAPI/PTZCtrl/channels/1/continuous",
                "/ISAPI/Security/sessionLogin?timeout=60"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var url = baseUrl + endpoint;
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    
                    var response = await _httpClient.SendAsync(request);
                    var key = $"Hikvision ISAPI{endpoint}";
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        results[key] = $"SUCCESS (200 OK) - Response length: {content.Length} bytes";
                        DiagnosticMessage?.Invoke(this, $"  ✓ {endpoint} - SUCCESS");
                        
                        if (endpoint.Contains("deviceInfo"))
                        {
                            // Parse device info
                            var deviceMatch = System.Text.RegularExpressions.Regex.Match(content, @"<deviceName>(.*?)</deviceName>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (deviceMatch.Success)
                            {
                                results["Hikvision Device Name"] = deviceMatch.Groups[1].Value;
                                DiagnosticMessage?.Invoke(this, $"    Device: {deviceMatch.Groups[1].Value}");
                            }
                        }
                    }
                    else
                    {
                        results[key] = $"FAILED ({response.StatusCode})";
                        DiagnosticMessage?.Invoke(this, $"  ✗ {endpoint} - {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    var key = $"Hikvision ISAPI{endpoint}";
                    results[key] = $"ERROR: {ex.Message}";
                }
            }
        }

        private async Task TestDahuaCGI(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[4] Testing Dahua CGI...");
            
            var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
            var endpoints = new[]
            {
                "/cgi-bin/magicBox.cgi?action=getDeviceType",
                "/cgi-bin/configManager.cgi?action=getConfig&name=General",
                "/cgi-bin/ptz.cgi?action=getStatus&channel=0",
                "/cgi-bin/ptz.cgi?action=start&channel=0&code=Left&arg1=0&arg2=1&arg3=0"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var url = baseUrl + endpoint;
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    
                    var response = await _httpClient.SendAsync(request);
                    var key = $"Dahua CGI{endpoint.Split('?')[0]}";
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        results[key] = $"SUCCESS (200 OK) - Response: {content.Substring(0, Math.Min(100, content.Length))}";
                        DiagnosticMessage?.Invoke(this, $"  ✓ {endpoint.Split('?')[0]} - SUCCESS");
                    }
                    else
                    {
                        results[key] = $"FAILED ({response.StatusCode})";
                        DiagnosticMessage?.Invoke(this, $"  ✗ {endpoint.Split('?')[0]} - {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    var key = $"Dahua CGI{endpoint.Split('?')[0]}";
                    results[key] = $"ERROR: {ex.Message}";
                }
            }
        }

        private async Task TestGenericCGI(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[5] Testing generic CGI paths...");
            
            var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
            var endpoints = new[]
            {
                "/cgi-bin/main-cgi",
                "/cgi-bin/viewer/video.jpg",
                "/cgi-bin/currenttime.cgi",
                "/web/cgi-bin/hi3510/ptzctrl.cgi"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var url = baseUrl + endpoint;
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    
                    var response = await _httpClient.SendAsync(request);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        results[$"Generic CGI{endpoint}"] = $"SUCCESS (200 OK)";
                        DiagnosticMessage?.Invoke(this, $"  ✓ {endpoint} - SUCCESS");
                    }
                }
                catch { }
            }
        }

        private async Task TestONVIFVariations(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[6] Testing ONVIF endpoint variations...");
            
            var protocol = port == 443 || port == 8443 ? "https" : "http";
            var endpoints = new[]
            {
                $"{protocol}://{host}:{port}/onvif/device_service",
                $"{protocol}://{host}:{port}/onvif/device_service.wsdl",
                $"{protocol}://{host}:{port}/onvif/wsdl/device_service",
                $"{protocol}://{host}:{port}/onvif/device_service.asmx",
                $"{protocol}://{host}:{port}/wsdl/onvif_device_service",
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    var soapBody = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
  </s:Body>
</s:Envelope>";
                    request.Content = new StringContent(soapBody, Encoding.UTF8, "application/soap+xml");
                    
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    
                    var response = await _httpClient.SendAsync(request);
                    var key = $"ONVIF {endpoint.Split('/').Last()}";
                    
                    if (response.IsSuccessStatusCode)
                    {
                        results[key] = $"SUCCESS (200 OK)";
                        DiagnosticMessage?.Invoke(this, $"  ✓ {endpoint.Split('/').Last()} - SUCCESS");
                    }
                    else
                    {
                        results[key] = $"{response.StatusCode}";
                    }
                }
                catch { }
            }
        }

        private async Task TestAuthenticationMethods(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "  Testing authentication methods...");
            
            var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
            var testEndpoint = $"{baseUrl}/onvif/device_service";
            
            // Test Basic Auth
            try
            {
                var soapBody = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
  </s:Body>
</s:Envelope>";
                
                var request = new HttpRequestMessage(HttpMethod.Post, testEndpoint);
                request.Content = new StringContent(soapBody, Encoding.UTF8, "application/soap+xml");
                
                var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                
                var response = await _httpClient.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();
                results[$"Auth: Basic (Port {port})"] = $"{response.StatusCode}";
                
                // Check for Digest challenge with DETAILED parsing
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    response.Headers.WwwAuthenticate.Any())
                {
                    var authHeaders = response.Headers.WwwAuthenticate.ToList();
                    var authSchemes = authHeaders.Select(h => h.Scheme).ToList();
                    DiagnosticMessage?.Invoke(this, $"  ℹ️ Camera requires: {string.Join(", ", authSchemes)}");
                    results[$"Required Auth (Port {port})"] = string.Join(", ", authSchemes);
                    
                    // Check for Digest challenge with detailed info
                    var digestHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                    if (digestHeader != null && !string.IsNullOrEmpty(digestHeader.Parameter))
                    {
                        var param = digestHeader.Parameter;
                        var realmMatch = System.Text.RegularExpressions.Regex.Match(param, @"realm=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var nonceMatch = System.Text.RegularExpressions.Regex.Match(param, @"nonce=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        if (realmMatch.Success || nonceMatch.Success)
                        {
                            var realm = realmMatch.Success ? realmMatch.Groups[1].Value : "unknown";
                            var nonce = nonceMatch.Success ? nonceMatch.Groups[1].Value.Substring(0, Math.Min(20, nonceMatch.Groups[1].Value.Length)) + "..." : "unknown";
                            DiagnosticMessage?.Invoke(this, $"    → Digest Realm: {realm}");
                            DiagnosticMessage?.Invoke(this, $"    → Digest Nonce: {nonce}");
                            results[$"Digest Challenge (Port {port})"] = $"Realm: {realm}, Nonce: {nonce.Substring(0, Math.Min(20, nonce.Length))}...";
                        }
                    }
                    
                    // Also check Basic auth requirement
                    if (authHeaders.Any(h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)))
                    {
                        DiagnosticMessage?.Invoke(this, $"    → Basic Auth supported (but credentials may be wrong)");
                    }
                }
                else if (response.IsSuccessStatusCode)
                {
                    DiagnosticMessage?.Invoke(this, $"  ✓✓✓ BASIC AUTH SUCCESS ON PORT {port}! ✓✓✓");
                    results[$"Auth Success (Port {port})"] = "BASIC AUTH WORKS!";
                }
            }
            catch (Exception ex)
            {
                results[$"Auth: Basic (Port {port})"] = $"ERROR: {ex.Message}";
                DiagnosticMessage?.Invoke(this, $"  ✗ Auth test failed: {ex.Message}");
            }
        }

        private async Task TestPTZEndpoints(string host, int port, string username, string password, Dictionary<string, string> results)
        {
            DiagnosticMessage?.Invoke(this, "\n[8] Testing PTZ control endpoints...");
            
            var baseUrl = port == 443 || port == 8443 ? $"https://{host}:{port}" : $"http://{host}:{port}";
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            
            // Test Hikvision PTZ
            try
            {
                var url = $"{baseUrl}/ISAPI/PTZCtrl/channels/1/continuous";
                var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                request.Content = new StringContent(@"<?xml version=""1.0"" encoding=""UTF-8""?>
<PTZData>
    <pan>0</pan>
    <tilt>0</tilt>
    <zoom>0</zoom>
</PTZData>", Encoding.UTF8, "application/xml");
                
                var response = await _httpClient.SendAsync(request);
                results["PTZ: Hikvision Continuous"] = response.StatusCode.ToString();
                if (response.IsSuccessStatusCode)
                {
                    DiagnosticMessage?.Invoke(this, "  ✓ Hikvision PTZ endpoint works!");
                }
            }
            catch { }
            
            // Test Dahua PTZ
            try
            {
                var url = $"{baseUrl}/cgi-bin/ptz.cgi?action=getStatus&channel=0";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                
                var response = await _httpClient.SendAsync(request);
                results["PTZ: Dahua Status"] = response.StatusCode.ToString();
                if (response.IsSuccessStatusCode)
                {
                    DiagnosticMessage?.Invoke(this, "  ✓ Dahua PTZ endpoint works!");
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
