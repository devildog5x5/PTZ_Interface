using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PTZCameraOperator.Services
{
    /// <summary>
    /// ONVIF PTZ Service - Handles communication with ONVIF-compatible cameras
    /// Implements ONVIF Profile S for PTZ control and device information
    /// </summary>
    public class OnvifPtzService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl = "";      // ONVIF device service endpoint URL
        private string _username = "";     // Camera authentication username
        private string _password = "";     // Camera authentication password
        private string? _digestRealm;      // Digest authentication realm (from WWW-Authenticate header)
        private string? _digestNonce;      // Digest authentication nonce (from WWW-Authenticate header)
        private string? _digestQop;        // Digest authentication qop (from WWW-Authenticate header)
        private bool _useDigestAuth = false; // Whether to use Digest Authentication instead of Basic
        private string? _connectedEndpointType = null; // Track what type of endpoint we connected to: "ONVIF", "HiSilicon", "Hikvision", "Dahua", etc.
        
        public bool IsConnected { get; private set; }
        
        // Public properties for accessing connection info (used by VideoWindow for stream URL generation)
        public string Host { get; private set; } = "";
        public int Port { get; private set; } = 80;
        public string Username => _username;
        public string Password => _password;
        
        // Events for status updates and error reporting
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? StreamUrlDiscovered;

        /// <summary>
        /// Initialize ONVIF PTZ Service with HTTP client configured for slow networks
        /// Timeout set to 30 seconds to accommodate wireless/slow network connections
        /// SSL certificate validation is bypassed for IP cameras (they often use self-signed certificates)
        /// </summary>
        public OnvifPtzService()
        {
            // Create HttpClientHandler that bypasses SSL certificate validation
            // IP cameras often use self-signed certificates that would otherwise fail validation
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    // Accept all certificates for IP cameras (self-signed certificates are common)
                    // This is safe for local network IP cameras
                    return true;
                }
            };
            
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }; // Increased for slow networks
        }

        /// <summary>
        /// Connect to ONVIF camera by trying multiple endpoint paths, SOAP versions, and usernames
        /// Many cameras use different endpoint paths, so we try common variations:
        /// - Standard ONVIF paths (/onvif/device_service, etc.)
        /// - Manufacturer-specific paths (Hikvision ISAPI, etc.)
        /// Also tries both SOAP 1.1 and SOAP 1.2 as different cameras support different versions
        /// If connection fails with 401, will also try common ONVIF usernames (onvif, onvif1, etc.)
        /// </summary>
        /// <param name="host">Camera IP address</param>
        /// <param name="port">Camera HTTP port (usually 80, 443, 8080, or 8443)</param>
        /// <param name="username">Camera authentication username</param>
        /// <param name="password">Camera authentication password</param>
        /// <param name="onvifUsername">Optional ONVIF-specific username (e.g., "onvif1"). If null, will try common ONVIF usernames automatically</param>
        /// <returns>True if connection successful, false otherwise</returns>
        public async Task<bool> ConnectAsync(string host, int port, string username, string password, string? onvifUsername = null)
        {
            try
            {
                // Build list of ports to try based on camera IP
                // Camera 1 (192.168.1.12): try 8080 (ONVIF) first, then 80 (standard)
                // Camera 2 (192.168.1.11): try 443 (HTTPS) first, then 80
                var portsToTry = new List<int>();
                
                if (host == "192.168.1.12")
                {
                    // Camera 1: ONVIF is on port 8080, so prioritize that
                    // Always try 8080 first (ONVIF port), then 80 as fallback
                    if (!portsToTry.Contains(8080))
                    {
                        portsToTry.Add(8080);
                    }
                    if (port != 8080 && !portsToTry.Contains(port))
                    {
                        portsToTry.Add(port);
                    }
                    // Also try 80 if not already in list
                    if (!portsToTry.Contains(80))
                    {
                        portsToTry.Add(80);
                    }
                }
                else if (host == "192.168.1.11")
                {
                    // Camera 2: port 443 is correct, but also try 80 as fallback
                    if (port == 443 && !portsToTry.Contains(80))
                    {
                        portsToTry.Add(80);
                    }
                }
                
                // Try each port (8080 is prioritized for camera 192.168.1.12)
                foreach (var tryPort in portsToTry)
                {
                    StatusChanged?.Invoke(this, $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    StatusChanged?.Invoke(this, $"Attempting connection to {host}:{tryPort}...");
                    
                    var success = await TryConnectToPort(host, tryPort, username, password, onvifUsername);
                    if (success)
                    {
                        return true;
                    }
                    
                    if (portsToTry.Count > 1 && tryPort != portsToTry.Last())
                    {
                        StatusChanged?.Invoke(this, $"Port {tryPort} failed, trying next port...");
                    }
                }
                
                // All ports failed - provide summary
                if (portsToTry.Count > 1)
                {
                    var portsList = string.Join(", ", portsToTry);
                    ErrorOccurred?.Invoke(this, $"Connection failed after trying ports: {portsList}. See detailed errors above for each port attempt.");
                }
                
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Connection error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Attempts to connect to a specific port with given credentials
        /// Tries multiple usernames and endpoints automatically
        /// </summary>
        private async Task<bool> TryConnectToPort(string host, int port, string username, string password, string? onvifUsername)
        {
            try
            {
                // Store connection info for access by VideoWindow
                Host = host;
                Port = port;
                _username = username ?? "";
                _password = password ?? "";
                
                // Debug: Log credential setup (without showing full password for security)
                System.Diagnostics.Debug.WriteLine($"ONVIF Connect: Host={host}, Port={port}, Username='{_username}', Password length={_password.Length}");

                // First, do comprehensive connectivity scan to detect camera type and available services
                StatusChanged?.Invoke(this, $"Scanning camera {host} for available services and ports...");
                var connectivityResults = await TestCameraConnectivity(host);
                if (connectivityResults.Count > 0)
                {
                    foreach (var result in connectivityResults)
                    {
                        StatusChanged?.Invoke(this, result);
                        System.Diagnostics.Debug.WriteLine($"Camera scan: {result}");
                    }
                }
                
                // Test basic HTTP connectivity on the specified port
                StatusChanged?.Invoke(this, $"Testing HTTP connectivity to {host}:{port}...");
                var httpTest = await TestHttpConnectivity(host, port);
                if (!string.IsNullOrEmpty(httpTest))
                {
                    StatusChanged?.Invoke(this, $"HTTP test result: {httpTest}");
                }

                // Determine protocol based on port (HTTPS for 443/8443, HTTP otherwise)
                string protocol = (port == 443 || port == 8443) ? "https" : "http";
                
                // Try common endpoint paths - expanded list including non-ONVIF
                // Different camera manufacturers use different endpoint paths
                var possibleEndpoints = new List<string>
                {
                    // Standard ONVIF paths
                    $"{protocol}://{host}:{port}/onvif/device_service",
                    $"{protocol}://{host}:{port}/onvif/device_service.wsdl",
                    $"{protocol}://{host}:{port}/onvif/device_service.asmx",
                    $"{protocol}://{host}:{port}/onvif",
                    $"{protocol}://{host}:{port}/device_service",
                    $"{protocol}://{host}:{port}/webservices/device_service",
                    $"{protocol}://{host}:{port}/onvif/device",
                    // Manufacturer-specific paths (non-ONVIF)
                    $"http://{host}:{port}/ISAPI/System/deviceInfo",              // Hikvision ISAPI
                    $"http://{host}:{port}/cgi-bin/magicBox.cgi?action=getDeviceType", // Dahua CGI
                    $"http://{host}:{port}/web/cgi-bin/hi3510/ptzctrl.cgi",       // HiSilicon Hi3510 PTZ control (found in diagnostic!)
                    $"http://{host}:{port}/",                                     // Root path
                };
                
                // Also try HTTP endpoints even if using HTTPS port (some cameras support both)
                // Some cameras accept HTTP on HTTPS ports as a fallback
                if (protocol == "https")
                {
                    possibleEndpoints.InsertRange(0, new[]  // Insert at beginning to try HTTP first
                    {
                        $"http://{host}:{port}/onvif/device_service",
                        $"http://{host}:{port}/onvif",
                    });
                }

                var allErrors = new List<string>(); // Track errors for diagnostic summary
                var usernamesTried = new HashSet<string>(); // Track which usernames were tried (use HashSet to avoid duplicates)
                
                // Build list of usernames to try
                // Some cameras require separate ONVIF accounts (e.g., "onvif1" instead of "admin")
                var usernamesToTry = new List<string> { username ?? "" };
                
                // If ONVIF username provided, add it
                if (!string.IsNullOrEmpty(onvifUsername))
                {
                    usernamesToTry.Add(onvifUsername);
                }
                
                // Also try common ONVIF usernames automatically
                var commonOnvifUsernames = new[] { "onvif", "onvif1", "onvifuser", "onvifadmin" };
                foreach (var onvifUser in commonOnvifUsernames)
                {
                    if (!usernamesToTry.Contains(onvifUser, StringComparer.OrdinalIgnoreCase))
                    {
                        usernamesToTry.Add(onvifUser);
                    }
                }
                
                // Try variations based on common camera patterns
                // For camera 192.168.1.12: try admin, Administrator, Admin
                // For camera 192.168.1.11: try root, Root, admin, Administrator
                if (host == "192.168.1.12" || host == "192.168.1.11")
                {
                    var variations = new List<string>();
                    if (host == "192.168.1.12")
                    {
                        // Camera 1 variations
                        variations.AddRange(new[] { "admin", "Admin", "Administrator", "administrator" });
                    }
                    else if (host == "192.168.1.11")
                    {
                        // Camera 2 variations
                        variations.AddRange(new[] { "root", "Root", "admin", "Admin", "Administrator", "onvif1", "onvif" });
                    }
                    
                    foreach (var variant in variations)
                    {
                        if (!usernamesToTry.Contains(variant, StringComparer.OrdinalIgnoreCase))
                        {
                            usernamesToTry.Add(variant);
                        }
                    }
                }
                
                StatusChanged?.Invoke(this, $"Trying {usernamesToTry.Count} username(s): {string.Join(", ", usernamesToTry)}");
                StatusChanged?.Invoke(this, $"Testing {possibleEndpoints.Count} endpoint(s)...");
                
                // Try each username with each endpoint
                foreach (var tryUsername in usernamesToTry)
                {
                    // Set credentials for this attempt
                    _username = tryUsername ?? "";
                    _password = password ?? "";
                    
                    if (!string.IsNullOrEmpty(tryUsername) && tryUsername != username)
                    {
                        StatusChanged?.Invoke(this, $"Trying ONVIF username: {tryUsername}");
                    }

                    // Try each endpoint path
                    foreach (var endpoint in possibleEndpoints)
                    {
                        _baseUrl = endpoint;
                        StatusChanged?.Invoke(this, $"Trying endpoint: {endpoint}");

                        // Check if this is a CGI/ISAPI endpoint (not ONVIF)
                        // CGI endpoints use GET requests with query parameters, not SOAP POST
                        // HiSilicon Hi3510 endpoint: /web/cgi-bin/hi3510/ptzctrl.cgi
                        bool isCgiEndpoint = endpoint.Contains("/cgi-bin/") || endpoint.Contains("/ISAPI/") || endpoint.Contains("/web/cgi-bin/");
                        
                        if (isCgiEndpoint)
                        {
                            // Test CGI/ISAPI endpoint with GET request (not SOAP)
                            // For HiSilicon, try with action=getstatus query parameter if base URL doesn't have params
                            string testUrl = endpoint;
                            if (endpoint.Contains("hi3510", StringComparison.OrdinalIgnoreCase) && !endpoint.Contains("?"))
                            {
                                // Try with action=getstatus first
                                testUrl = endpoint.Contains("?") ? endpoint : $"{endpoint}?action=getstatus";
                            }
                            
                            StatusChanged?.Invoke(this, $"Testing {testUrl} with GET request (Basic Auth)");
                            var (cgiResponse, cgiStatusCode, cgiErrorMessage) = await TestCgiEndpointAsync(testUrl, tryUsername ?? "", password ?? "");
                            
                            // If getstatus fails, try without query params (some cameras accept bare endpoint)
                            if ((cgiResponse == null || !cgiStatusCode.HasValue || cgiStatusCode.Value != 200) && testUrl != endpoint)
                            {
                                StatusChanged?.Invoke(this, $"Trying {endpoint} without query parameters...");
                                var (cgiResponse2, cgiStatusCode2, _) = await TestCgiEndpointAsync(endpoint, tryUsername ?? "", password ?? "");
                                if (cgiResponse2 != null && cgiStatusCode2.HasValue && cgiStatusCode2.Value == 200)
                                {
                                    cgiResponse = cgiResponse2;
                                    cgiStatusCode = cgiStatusCode2;
                                }
                            }
                            
                            if (cgiResponse != null && cgiStatusCode.HasValue && cgiStatusCode.Value == 200)
                            {
                                // Success! CGI endpoint works with these credentials
                                IsConnected = true;
                                
                                // Detect endpoint type based on URL
                                if (endpoint.Contains("hi3510", StringComparison.OrdinalIgnoreCase))
                                {
                                    _connectedEndpointType = "HiSilicon";
                                    _baseUrl = $"http://{host}:{port}/web/cgi-bin/hi3510"; // Base URL without the script name
                                }
                                else if (endpoint.Contains("/ISAPI/", StringComparison.OrdinalIgnoreCase))
                                {
                                    _connectedEndpointType = "Hikvision";
                                    _baseUrl = $"http://{host}:{port}";
                                }
                                else if (endpoint.Contains("/cgi-bin/", StringComparison.OrdinalIgnoreCase))
                                {
                                    _connectedEndpointType = "Dahua";
                                    _baseUrl = $"http://{host}:{port}";
                                }
                                else
                                {
                                    _connectedEndpointType = "GenericCGI";
                                    _baseUrl = $"http://{host}:{port}";
                                }
                                
                                var successMsg = $"Connected to camera via {endpoint} ({_connectedEndpointType} API)";
                                if (tryUsername != username)
                                {
                                    successMsg += $" using username: {tryUsername}";
                                }
                                StatusChanged?.Invoke(this, successMsg);
                                System.Diagnostics.Debug.WriteLine($"✓ CGI endpoint works: {endpoint} with user '{tryUsername}', type: {_connectedEndpointType}");
                                
                                // Store connection details
                                Host = host;
                                Port = port;
                                _username = tryUsername ?? "";
                                _password = password ?? "";
                                
                                // For HiSilicon cameras, generate a default RTSP URL since they don't support ONVIF stream discovery
                                if (_connectedEndpointType == "HiSilicon")
                                {
                                    // Common HiSilicon RTSP URL formats
                                    var encodedPassword = Uri.EscapeDataString(_password);
                                    var creds = !string.IsNullOrEmpty(_username) ? $"{_username}:{encodedPassword}@" : "";
                                    
                                    // Try common HiSilicon RTSP formats
                                    var defaultRtspUrl = $"rtsp://{creds}{host}:554/Streaming/Channels/101";
                                    
                                    // Fire event in background to update UI
                                    _ = Task.Run(() =>
                                    {
                                        StreamUrlDiscovered?.Invoke(this, defaultRtspUrl);
                                    });
                                }
                                
                                return true;
                            }
                            else
                            {
                                // Track error for CGI endpoint
                                var cgiStatusMsg = cgiStatusCode.HasValue 
                                    ? $"HTTP {cgiStatusCode} - {cgiErrorMessage ?? "No response"}"
                                    : (cgiErrorMessage ?? "Network timeout or unreachable");
                                
                                if (!string.IsNullOrEmpty(tryUsername))
                                {
                                    cgiStatusMsg = $"[User: {tryUsername}] {cgiStatusMsg}";
                                }
                                
                                StatusChanged?.Invoke(this, $"Failed {endpoint} GET: {cgiStatusMsg}");
                                
                                // Extract authentication requirements from error message
                                if (!string.IsNullOrEmpty(cgiErrorMessage))
                                {
                                    var authMatch = System.Text.RegularExpressions.Regex.Match(cgiErrorMessage, @"Authentication method required:\s*([^()]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (authMatch.Success)
                                    {
                                        var authMethod = authMatch.Groups[1].Value.Trim();
                                        StatusChanged?.Invoke(this, $"  → CGI endpoint requires: {authMethod}");
                                    }
                                }
                                else if (cgiStatusCode == 401)
                                {
                                    StatusChanged?.Invoke(this, $"  → CGI endpoint returned HTTP 401 Unauthorized");
                                }
                                
                                allErrors.Add($"{endpoint} GET: {cgiStatusMsg}");
                                System.Diagnostics.Debug.WriteLine($"✗ CGI endpoint failed: {endpoint} with user '{tryUsername}': {cgiStatusMsg}");
                            }
                        }
                        else
                        {
                            // ONVIF endpoint - try SOAP 1.1 and 1.2
                            var soapFormats = new[] { "1.2", "1.1" };
                            
                            foreach (var soapVersion in soapFormats)
                            {
                                StatusChanged?.Invoke(this, $"Trying {endpoint} with SOAP {soapVersion}");
                                
                                // Test connection using GetDeviceInformation request
                                // This is a simple request that works on all ONVIF cameras
                                var request = CreateSoapRequest("GetDeviceInformation", "http://www.onvif.org/ver10/device/wsdl", soapVersion);
                                var (response, httpStatusCode, errorMessage) = await SendRequestAsync(request);
                                
                                if (response != null)
                                {
                                    // Success! We found a working endpoint, SOAP version, and username
                                    IsConnected = true;
                                    var successMsg = $"Connected to camera via {endpoint} (SOAP {soapVersion})";
                                    if (tryUsername != username)
                                    {
                                        successMsg += $" using ONVIF username: {tryUsername}";
                                    }
                                    StatusChanged?.Invoke(this, successMsg);
                                    
                                    // Store connection details
                                    Host = host;
                                    Port = port;
                                    _username = tryUsername ?? "";
                                    _password = password ?? "";
                                    _connectedEndpointType = "ONVIF"; // Standard ONVIF connection
                                    
                                    // Try to discover stream URL in background (non-blocking)
                                    // This helps auto-populate the stream URL field
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
                                else
                                {
                                    // Track which username was used for this attempt
                                    if (!string.IsNullOrEmpty(tryUsername) && !usernamesTried.Contains(tryUsername))
                                    {
                                        usernamesTried.Add(tryUsername);
                                    }
                                    
                                    // Always log the error, even if errorMessage is empty (might be timeout)
                                    var statusMsg = httpStatusCode.HasValue 
                                        ? $"HTTP {httpStatusCode} - {errorMessage ?? "No response"}"
                                        : (errorMessage ?? "Network timeout or unreachable");
                                    
                                    // Extract authentication method requirements from error message
                                    string? authMethod = null;
                                    string? digestRealm = null;
                                    if (!string.IsNullOrEmpty(errorMessage))
                                    {
                                        // Look for "Authentication method required:" in error message
                                        var authMatch = System.Text.RegularExpressions.Regex.Match(errorMessage, @"Authentication method required:\s*([^\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        if (authMatch.Success)
                                        {
                                            authMethod = authMatch.Groups[1].Value.Trim();
                                            
                                            // If Digest, extract realm and nonce
                                            if (authMethod.Contains("Digest", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var realmMatch = System.Text.RegularExpressions.Regex.Match(errorMessage, @"realm=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                if (realmMatch.Success)
                                                {
                                                    digestRealm = realmMatch.Groups[1].Value;
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Add username to error message for clarity
                                    if (!string.IsNullOrEmpty(tryUsername))
                                    {
                                        statusMsg = $"[User: {tryUsername}] {statusMsg}";
                                    }
                                    
                                    StatusChanged?.Invoke(this, $"Failed {endpoint} SOAP {soapVersion}: {statusMsg}");
                                    
                                    // Log authentication requirements prominently
                                    if (!string.IsNullOrEmpty(authMethod))
                                    {
                                        StatusChanged?.Invoke(this, $"  → Camera requires authentication: {authMethod}");
                                        if (!string.IsNullOrEmpty(digestRealm))
                                        {
                                            StatusChanged?.Invoke(this, $"    → Digest Realm: {digestRealm}");
                                        }
                                        if (authMethod.Contains("Digest", StringComparison.OrdinalIgnoreCase) && _useDigestAuth)
                                        {
                                            StatusChanged?.Invoke(this, $"    → Using Digest Auth (retrying automatically)");
                                        }
                                    }
                                    else if (httpStatusCode == 401)
                                    {
                                        // If 401 but no auth method specified, log that we couldn't detect it
                                        StatusChanged?.Invoke(this, $"  → HTTP 401 Unauthorized - authentication method not specified in response");
                                    }
                                    
                                    allErrors.Add($"{endpoint} SOAP {soapVersion}: {statusMsg}");
                                    System.Diagnostics.Debug.WriteLine($"Failed {endpoint} SOAP {soapVersion} with user '{tryUsername}': {statusMsg}");
                                }
                            }
                        }
                    }
                } // End of username loop
                
                // All attempts failed - provide diagnostic summary with actual errors
                var errorDetails = $"Connection failed after trying {possibleEndpoints.Count} endpoints (ONVIF and non-ONVIF).\n\n";
                
                if (allErrors.Count > 0)
                {
                    errorDetails += "Errors encountered (showing first 10):\n";
                    foreach (var error in allErrors.Take(10))
                    {
                        errorDetails += $"  • {error}\n";
                    }
                    errorDetails += "\n";
                }
                else
                {
                    errorDetails += "No specific error details available (likely network timeout or unreachable).\n\n";
                }
                
                errorDetails += $"Camera: {host}:{port}\n\n";
                errorDetails += "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n";
                errorDetails += "CONNECTION SUGGESTIONS:\n";
                errorDetails += "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n";
                errorDetails += "This camera may not use standard ONVIF. Try these:\n\n";
                errorDetails += "1. WEB INTERFACE TEST:\n";
                errorDetails += $"   → Open in browser: http://{host}:{port}/ or https://{host}:{port}/\n";
                errorDetails += $"   → Check what login page or API is available\n";
                errorDetails += $"   → Look for API documentation in camera settings\n\n";
                errorDetails += "2. MANUFACTURER-SPECIFIC APIs:\n";
                errorDetails += $"   → Hikvision: http://{host}:{port}/ISAPI/\n";
                errorDetails += $"   → Dahua: http://{host}:{port}/cgi-bin/\n";
                errorDetails += $"   → Axis: http://{host}:{port}/axis-cgi/\n";
                errorDetails += $"   → Generic: http://{host}:{port}/cgi-bin/\n\n";
                errorDetails += "3. ALTERNATIVE PORTS:\n";
                errorDetails += $"   → Try port 80 (standard HTTP)\n";
                errorDetails += $"   → Try port 443 (HTTPS)\n";
                errorDetails += $"   → Try port 8080 (alternative HTTP)\n";
                errorDetails += $"   → Try port 8443 (alternative HTTPS)\n\n";
                errorDetails += "4. CHECK CAMERA SETTINGS:\n";
                errorDetails += $"   → Enable ONVIF in camera settings if available\n";
                errorDetails += $"   → Check if camera uses proprietary API (not ONVIF)\n";
                errorDetails += $"   → Verify camera model and manufacturer\n";
                errorDetails += $"   → Check camera manual for API/PTZ control methods\n\n";
                
                // Check if all errors are 401 (authentication failure)
                bool allUnauthorized = allErrors.Count > 0 && allErrors.All(e => 
                    e.Contains("HTTP 401") || e.Contains("Unauthorized"));
                
                if (allUnauthorized)
                {
                    errorDetails += $"\n⚠️ AUTHENTICATION FAILED (HTTP 401 Unauthorized)\n\n";
                    errorDetails += $"All endpoints returned 401, which means:\n";
                    errorDetails += $"  ✓ Camera is reachable on port {port}\n";
                    errorDetails += $"  ✓ ONVIF endpoints are correct\n";
                    errorDetails += $"  ✗ Username/password are incorrect or camera requires different ONVIF credentials\n\n";
                    errorDetails += $"SOLUTION:\n";
                    errorDetails += $"1. Verify username '{username}' and password are EXACTLY correct (no typos/spaces)\n";
                    errorDetails += $"2. Some cameras require SEPARATE ONVIF user account (not the web admin account)\n";
                    errorDetails += $"   → Check camera's ONVIF settings for dedicated ONVIF username/password\n";
                    errorDetails += $"3. Ensure ONVIF user has proper permissions enabled in camera settings\n";
                    errorDetails += $"4. Try accessing http://{host}:{port}/onvif/device_service in a browser\n";
                    errorDetails += $"   → Browser will prompt for credentials - use same credentials there\n";
                    errorDetails += $"5. Check camera firmware is up to date (old firmware may have ONVIF bugs)\n";
                    errorDetails += $"6. Try creating a new ONVIF user in camera settings with simple password (no special chars)\n";
                }
                else
                {
                    errorDetails += $"\nTroubleshooting:\n";
                    errorDetails += $"1. Verify camera IP and port are correct (you specified port {port})\n";
                    errorDetails += $"2. Check username and password (username: {username})\n";
                    errorDetails += $"3. Ensure camera has ONVIF enabled\n";
                    errorDetails += $"4. Try accessing http://{host}:{port}/onvif/device_service in a browser\n";
                    errorDetails += $"5. Check firewall allows port {port}\n";
                    errorDetails += $"6. Ping test: Check if ping works (use PING button)\n";
                    errorDetails += $"7. Some cameras use HTTPS - try port 443";
                }
                
                ErrorOccurred?.Invoke(this, errorDetails);
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Connection error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Comprehensive camera connectivity test - tries multiple ports and protocols
        /// Tests HTTP/HTTPS on common ports and detects what services are available
        /// </summary>
        /// <param name="host">Camera IP address</param>
        /// <returns>List of available services and ports found</returns>
        private async Task<List<string>> TestCameraConnectivity(string host)
        {
            var results = new List<string>();
            var commonPorts = new[] { 80, 443, 8080, 8443, 554, 8000, 8001 };
            
            StatusChanged?.Invoke(this, $"Scanning camera {host} for available services...");
            
            foreach (var port in commonPorts)
            {
                try
                {
                    // Test HTTP - bypass SSL validation for IP cameras
                    var testHandler = new System.Net.Http.HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };
                    using var httpClient = new HttpClient(testHandler) { Timeout = TimeSpan.FromSeconds(3) };
                    try
                    {
                        var httpUrl = $"http://{host}:{port}/";
                        var httpResponse = await httpClient.GetAsync(httpUrl);
                        var responseHeaders = httpResponse.Headers.ToString();
                        var contentType = httpResponse.Content.Headers.ContentType?.MediaType ?? "unknown";
                        var serverHeader = httpResponse.Headers.Server?.FirstOrDefault()?.ToString() ?? "unknown";
                        
                        results.Add($"Port {port} (HTTP): Status {httpResponse.StatusCode}, Content-Type: {contentType}, Server: {serverHeader}");
                        
                        // Read first bit of response to detect camera type
                        if (httpResponse.IsSuccessStatusCode)
                        {
                            var contentPreview = await httpResponse.Content.ReadAsStringAsync();
                            var preview = contentPreview.Substring(0, Math.Min(500, contentPreview.Length));
                            
                            if (preview.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) || 
                                preview.Contains("hikvision", StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add($"  → Detected: Hikvision camera (check /ISAPI path)");
                            }
                            else if (preview.Contains("Dahua", StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add($"  → Detected: Dahua camera");
                            }
                            else if (preview.Contains("onvif", StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add($"  → Detected: ONVIF service available");
                            }
                            else if (preview.Contains("login", StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add($"  → Detected: Web login page available");
                            }
                        }
                    }
                    catch { }
                    
                    // Test HTTPS if port is commonly SSL
                    if (port == 443 || port == 8443)
                    {
                        try
                        {
                            // Note: This may fail if SSL cert is invalid, but that's OK - we're just detecting
                            var httpsUrl = $"https://{host}:{port}/";
                            var httpsResponse = await httpClient.GetAsync(httpsUrl);
                            results.Add($"Port {port} (HTTPS): Status {httpsResponse.StatusCode}, Secure connection available");
                        }
                        catch (Exception httpsEx)
                        {
                            if (httpsEx.Message.Contains("SSL") || httpsEx.Message.Contains("certificate"))
                            {
                                results.Add($"Port {port} (HTTPS): SSL connection possible but certificate issue (this is normal)");
                            }
                        }
                    }
                }
                catch
                {
                    // Port not responding or filtered - skip
                }
            }
            
            return results;
        }
        
        /// <summary>
        /// Tests basic HTTP connectivity to the camera
        /// 
        /// Performs a simple HTTP GET request to verify the camera is reachable
        /// and responding on the specified port. This helps diagnose network issues
        /// before attempting ONVIF-specific requests.
        /// </summary>
        /// <param name="host">Camera IP address</param>
        /// <param name="port">Camera port</param>
        /// <returns>Test result message, or null if test succeeded</returns>
        private async Task<string?> TestHttpConnectivity(string host, int port)
        {
            try
            {
                // Bypass SSL certificate validation for IP cameras (self-signed certificates are common)
                var testHandler = new System.Net.Http.HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                };
                using var testClient = new HttpClient(testHandler) { Timeout = TimeSpan.FromSeconds(5) };
                var testUrl = $"http://{host}:{port}/";
                var response = await testClient.GetAsync(testUrl);
                var serverHeader = response.Headers.Server?.FirstOrDefault()?.ToString() ?? "";
                return $"HTTP {response.StatusCode} - Camera is reachable" + (!string.IsNullOrEmpty(serverHeader) ? $" (Server: {serverHeader})" : "");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                return $"Network error: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                return "Timeout - Camera not responding on port";
            }
            catch (Exception ex)
            {
                return $"Connectivity test failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Tests a CGI/ISAPI endpoint with GET request and Basic Authentication
        /// CGI endpoints don't use SOAP - they use GET requests with query parameters
        /// </summary>
        /// <param name="endpointUrl">Full URL of the CGI endpoint (e.g., http://192.168.1.12:80/cgi-bin/magicBox.cgi?action=getDeviceType)</param>
        /// <param name="username">Username for Basic Auth</param>
        /// <param name="password">Password for Basic Auth</param>
        /// <returns>Tuple containing response (null if failed), HTTP status code, and error message</returns>
        private async Task<(string? response, int? statusCode, string? errorMessage)> TestCgiEndpointAsync(string endpointUrl, string username, string password)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);
                
                // Add Basic Authentication header
                if (!string.IsNullOrEmpty(username))
                {
                    var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                }
                
                var response = await _httpClient.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    // Success! Return the response text
                    return (responseText, (int)response.StatusCode, null);
                }
                else
                {
                    // Check for authentication requirement from WWW-Authenticate header
                    var authHeaders = response.Headers.WwwAuthenticate.ToList();
                    string? errorMsg = $"HTTP {response.StatusCode} - {response.ReasonPhrase}";
                    
                    if (authHeaders != null && authHeaders.Any())
                    {
                        var authSchemes = string.Join(", ", authHeaders.Select(h => h.Scheme));
                        errorMsg += $" | Authentication method required: {authSchemes}";
                        
                        // Check for Digest authentication
                        var digestHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                        if (digestHeader != null && !string.IsNullOrEmpty(digestHeader.Parameter))
                        {
                            // Parse Digest challenge parameters
                            var realmMatch = System.Text.RegularExpressions.Regex.Match(digestHeader.Parameter, @"realm=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (realmMatch.Success)
                            {
                                errorMsg += $" | Digest Realm: {realmMatch.Groups[1].Value}";
                            }
                        }
                        
                        // Check for Basic Auth requirement
                        var basicHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
                        if (basicHeader != null && digestHeader == null)
                        {
                            errorMsg += " | Basic Auth required (credentials may be incorrect)";
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        // 401 but no WWW-Authenticate header
                        errorMsg += " | Unauthorized (no authentication method specified)";
                    }
                    
                    // Truncate long error messages
                    if (responseText.Length > 200)
                    {
                        errorMsg += $": {responseText.Substring(0, 200)}...";
                    }
                    else if (!string.IsNullOrEmpty(responseText))
                    {
                        errorMsg += $": {responseText}";
                    }
                    
                    return (null, (int)response.StatusCode, errorMsg);
                }
            }
            catch (HttpRequestException httpEx)
            {
                return (null, null, $"Network error: {httpEx.Message}");
            }
            catch (TaskCanceledException)
            {
                return (null, null, "Request timeout");
            }
            catch (Exception ex)
            {
                return (null, null, $"Request failed: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            Host = "";
            Port = 0;
            _username = "";
            _password = "";
            _baseUrl = "";
            _connectedEndpointType = null;
            _useDigestAuth = false;
            StatusChanged?.Invoke(this, "Disconnected");
        }

        /// <summary>
        /// Gets the RTSP stream URI from the camera
        /// 
        /// Requests the main video stream URL from the camera's media service.
        /// This is typically called automatically after successful connection to auto-populate
        /// the stream URL field in the UI.
        /// 
        /// Returns RTSP URL in format: rtsp://[username]:[password]@[ip]:[port]/[path]
        /// </summary>
        /// <returns>RTSP stream URL if successful, null otherwise</returns>
        public async Task<string?> GetStreamUriAsync()
        {
            try
            {
                var request = CreateGetStreamUriRequest();
                var (response, _, _) = await SendRequestAsync(request);
                
                if (response != null)
                {
                    // Parse XML response to extract stream URI
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

        /// <summary>
        /// Starts continuous PTZ movement (pan, tilt, zoom)
        /// 
        /// Movement continues until StopAsync() is called.
        /// Speed values are typically in range -1.0 to 1.0:
        /// - Positive values: right/up/zoom in
        /// - Negative values: left/down/zoom out
        /// - Zero: no movement in that axis
        /// </summary>
        /// <param name="panSpeed">Pan speed (-1.0 to 1.0)</param>
        /// <param name="tiltSpeed">Tilt speed (-1.0 to 1.0)</param>
        /// <param name="zoomSpeed">Zoom speed (-1.0 to 1.0)</param>
        /// <returns>True if command sent successfully</returns>
        public async Task<bool> ContinuousMoveAsync(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            if (!IsConnected) return false;
            
            // Route to manufacturer-specific PTZ if connected via non-ONVIF endpoint
            if (_connectedEndpointType == "HiSilicon")
            {
                return await HiSiliconContinuousMove(panSpeed, tiltSpeed, zoomSpeed);
            }
            
            // Standard ONVIF PTZ control
            try
            {
                var request = CreateContinuousMoveRequest(panSpeed, tiltSpeed, zoomSpeed);
                var (response, _, _) = await SendRequestAsync(request);
                return response != null;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// HiSilicon Hi3510 PTZ control using CGI GET requests
        /// Format: /web/cgi-bin/hi3510/ptzctrl.cgi?-step=0&-act=[action]&speed=[1-8]
        /// </summary>
        private async Task<bool> HiSiliconContinuousMove(float panSpeed, float tiltSpeed, float zoomSpeed)
        {
            try
            {
                // HiSilicon Hi3510 requires separate commands for each axis
                bool success = true;
                var speed = (int)Math.Max(1, Math.Min(8, Math.Abs(Math.Max(panSpeed, Math.Max(tiltSpeed, zoomSpeed))) * 8));
                
                // Send pan command if needed
                if (Math.Abs(panSpeed) > 0.01f)
                {
                    var action = panSpeed > 0 ? "right" : "left";
                    var url = $"{_baseUrl}/ptzctrl.cgi?-step=0&-act={action}&speed={speed}";
                    var (response, statusCode, _) = await TestCgiEndpointAsync(url, _username, _password);
                    success = success && (response != null || (statusCode.HasValue && statusCode.Value == 200));
                }
                
                // Send tilt command if needed
                if (Math.Abs(tiltSpeed) > 0.01f)
                {
                    var action = tiltSpeed > 0 ? "up" : "down";
                    var url = $"{_baseUrl}/ptzctrl.cgi?-step=0&-act={action}&speed={speed}";
                    var (response, statusCode, _) = await TestCgiEndpointAsync(url, _username, _password);
                    success = success && (response != null || (statusCode.HasValue && statusCode.Value == 200));
                }
                
                // Send zoom command if needed
                if (Math.Abs(zoomSpeed) > 0.01f)
                {
                    var action = zoomSpeed > 0 ? "zoomin" : "zoomout";
                    var url = $"{_baseUrl}/ptzctrl.cgi?-step=0&-act={action}";
                    var (response, statusCode, _) = await TestCgiEndpointAsync(url, _username, _password);
                    success = success && (response != null || (statusCode.HasValue && statusCode.Value == 200));
                }
                
                return success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HiSilicon PTZ move error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops all PTZ movement (pan, tilt, and zoom)
        /// 
        /// Called when user releases a direction button or zoom button.
        /// Stops any ongoing continuous movement.
        /// </summary>
        /// <returns>True if stop command sent successfully</returns>
        public async Task<bool> StopAsync()
        {
            if (!IsConnected) return false;
            
            // Route to manufacturer-specific stop if connected via non-ONVIF endpoint
            if (_connectedEndpointType == "HiSilicon")
            {
                return await HiSiliconStop();
            }
            
            // Standard ONVIF stop
            try
            {
                var request = CreateStopRequest();
                var (response, _, _) = await SendRequestAsync(request);
                return response != null;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// HiSilicon Hi3510 stop command
        /// </summary>
        private async Task<bool> HiSiliconStop()
        {
            try
            {
                var url = $"{_baseUrl}/ptzctrl.cgi?-step=0&-act=stop";
                var (response, statusCode, _) = await TestCgiEndpointAsync(url, _username, _password);
                return response != null || (statusCode.HasValue && statusCode.Value == 200);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HiSilicon PTZ stop error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Moves camera to an absolute position
        /// 
        /// Position values are typically in range -1.0 to 1.0:
        /// - Pan: -1.0 (left) to 1.0 (right)
        /// - Tilt: -1.0 (down) to 1.0 (up)
        /// - Zoom: 0.0 (wide) to 1.0 (telephoto)
        /// 
        /// Used when user sets position via sliders and clicks "GO TO"
        /// </summary>
        /// <param name="pan">Absolute pan position (-1.0 to 1.0)</param>
        /// <param name="tilt">Absolute tilt position (-1.0 to 1.0)</param>
        /// <param name="zoom">Absolute zoom position (0.0 to 1.0)</param>
        /// <returns>True if command sent successfully</returns>
        public async Task<bool> AbsoluteMoveAsync(float pan, float tilt, float zoom)
        {
            if (!IsConnected) return false;
            
            try
            {
                var request = CreateAbsoluteMoveRequest(pan, tilt, zoom);
                var (response, _, _) = await SendRequestAsync(request);
                StatusChanged?.Invoke(this, $"Moving to position: P:{pan:F2} T:{tilt:F2} Z:{zoom:F2}");
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the current PTZ position from the camera
        /// 
        /// Queries the camera for its current pan, tilt, and zoom values.
        /// Used by the "GET POS" button to update the position sliders in the UI.
        /// 
        /// Returns tuple with normalized values:
        /// - Pan: -1.0 (left) to 1.0 (right)
        /// - Tilt: -1.0 (down) to 1.0 (up)
        /// - Zoom: 0.0 (wide) to 1.0 (telephoto)
        /// </summary>
        /// <returns>Tuple with (Pan, Tilt, Zoom) if successful, null otherwise</returns>
        public async Task<(float Pan, float Tilt, float Zoom)?> GetPositionAsync()
        {
            if (!IsConnected) return null;
            
            try
            {
                var request = CreateGetPositionRequest();
                var (response, _, _) = await SendRequestAsync(request);
                
                if (response != null)
                {
                    // Parse XML response to extract position values
                    var ns = XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl");
                    var position = response.Descendants(ns + "Position").FirstOrDefault();
                    
                    if (position != null)
                    {
                        var panTilt = position.Element(XName.Get("PanTilt", "http://www.onvif.org/ver10/schema"));
                        var zoomElem = position.Element(XName.Get("Zoom", "http://www.onvif.org/ver10/schema"));
                        
                        if (panTilt != null)
                        {
                            // Extract x/y attributes from XML (x=pan, y=tilt for PanTilt, x=zoom for Zoom)
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
                var (response, _, _) = await SendRequestAsync(request);
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
                var (response, _, _) = await SendRequestAsync(request);
                StatusChanged?.Invoke(this, "Home position set");
                return response != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Create SOAP request XML for ONVIF commands
        /// Supports both SOAP 1.1 and SOAP 1.2 formats as different cameras use different versions
        /// </summary>
        /// <param name="action">ONVIF action name (e.g., "GetDeviceInformation")</param>
        /// <param name="xmlns">ONVIF namespace for the action</param>
        /// <param name="soapVersion">SOAP version: "1.1" or "1.2" (default)</param>
        /// <returns>SOAP XML request string</returns>
        private string CreateSoapRequest(string action, string xmlns, string soapVersion = "1.2")
        {
            if (soapVersion == "1.1")
            {
                // SOAP 1.1 format - used by older cameras
                return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:tns=""{xmlns}"">
  <soap:Body>
    <tns:{action}/>
  </soap:Body>
</soap:Envelope>";
            }
            else
            {
                // SOAP 1.2 format (default) - modern ONVIF standard
                return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <{action} xmlns=""{xmlns}""/>
  </s:Body>
</s:Envelope>";
            }
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

        /// <summary>
        /// Send SOAP request to ONVIF camera with proper authentication and error handling
        /// Handles both SOAP 1.1 and SOAP 1.2 formats with appropriate content types
        /// Returns tuple with response document, HTTP status code, and error message
        /// </summary>
        /// <param name="soapRequest">SOAP XML request string</param>
        /// <returns>Tuple: (XDocument response, HTTP status code, error message)</returns>
        private async Task<(XDocument? response, int? statusCode, string? errorMessage)> SendRequestAsync(string soapRequest)
        {
            try
            {
                // Detect SOAP version from request content to set correct Content-Type header
                // SOAP 1.1 uses text/xml, SOAP 1.2 uses application/soap+xml
                // Note: StringContent mediaType parameter should NOT include charset - it's handled by Encoding parameter
                var mediaType = soapRequest.Contains("http://schemas.xmlsoap.org/soap/envelope/")
                    ? "text/xml"  // SOAP 1.1
                    : "application/soap+xml";  // SOAP 1.2

                // Create StringContent with UTF-8 encoding (charset is set automatically)
                var content = new StringContent(soapRequest, Encoding.UTF8, mediaType);
                
                // Create HTTP POST request to ONVIF endpoint
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl)
                {
                    Content = content
                };

                // Add Authentication header if credentials are provided
                // Try Digest first if we have digest parameters, otherwise use Basic
                if (!string.IsNullOrEmpty(_username) || !string.IsNullOrEmpty(_password))
                {
                    var username = _username ?? "";
                    var password = _password ?? "";
                    
                    if (_useDigestAuth && !string.IsNullOrEmpty(_digestNonce) && !string.IsNullOrEmpty(_digestRealm))
                    {
                        // Use Digest Authentication
                        var digestAuth = CalculateDigestAuth(username, password, request.Method.Method, _baseUrl);
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Digest", digestAuth);
                        System.Diagnostics.Debug.WriteLine($"Added Digest Auth header - Username: '{username}'");
                    }
                    else
                    {
                        // Use Basic Authentication
                        var credentials = $"{username}:{password}";
                        var authBytes = Encoding.UTF8.GetBytes(credentials);
                        var authValue = Convert.ToBase64String(authBytes);
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                        System.Diagnostics.Debug.WriteLine($"Added Basic Auth header - Username: '{username}'");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("WARNING: No credentials provided - sending request without authentication");
                }

                // Send request and get response
                var response = await _httpClient.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();
                
                // If we get 401 and camera requires Digest, retry with Digest Authentication
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && 
                    response.Headers.WwwAuthenticate.Count > 0 && 
                    !_useDigestAuth)
                {
                    var authHeaders = response.Headers.WwwAuthenticate.ToList();
                    var digestHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                    
                    if (digestHeader != null && !string.IsNullOrEmpty(digestHeader.Parameter))
                    {
                        // Parse Digest challenge and retry with Digest Auth
                        ParseDigestChallenge(digestHeader.Parameter);
                        _useDigestAuth = true;
                        System.Diagnostics.Debug.WriteLine($"Retrying with Digest Auth - Realm: {_digestRealm}, Nonce: {_digestNonce}");
                        StatusChanged?.Invoke(this, "Camera requires Digest Authentication, retrying...");
                        
                        // Retry the same request with Digest Authentication
                        return await SendRequestAsync(soapRequest);
                    }
                }
                
                if (response.IsSuccessStatusCode)
                {
                    // Success - parse XML response
                    try
                    {
                        var doc = XDocument.Parse(responseText);
                        return (doc, (int)response.StatusCode, null);
                    }
                    catch (Exception parseEx)
                    {
                        // Response was successful but not valid XML
                        System.Diagnostics.Debug.WriteLine($"Failed to parse response: {parseEx.Message}");
                        System.Diagnostics.Debug.WriteLine($"Response content: {responseText.Substring(0, Math.Min(500, responseText.Length))}");
                        return (null, (int)response.StatusCode, $"Invalid XML response: {parseEx.Message}");
                    }
                }
                else
                {
                    // HTTP error - extract error details from response
                    var errorMsg = $"HTTP {response.StatusCode} {response.ReasonPhrase}";
                    
                    // Check for WWW-Authenticate header to see what authentication method camera requires
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0)
                    {
                        var authHeaders = response.Headers.WwwAuthenticate.ToList();
                        var authSchemes = string.Join(", ", authHeaders.Select(h => h.Scheme));
                        errorMsg += $" | Authentication method required: {authSchemes}";
                        System.Diagnostics.Debug.WriteLine($"Camera requires authentication: {authSchemes}");
                        
                        // Check if camera requires Digest Authentication
                        var digestHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                        if (digestHeader != null && !string.IsNullOrEmpty(digestHeader.Parameter))
                        {
                            // Parse Digest challenge parameters
                            ParseDigestChallenge(digestHeader.Parameter);
                            _useDigestAuth = true;
                            System.Diagnostics.Debug.WriteLine($"Camera requires Digest Auth - Realm: {_digestRealm}, Nonce: {_digestNonce}");
                            
                            // Add Digest challenge details to error message
                            if (!string.IsNullOrEmpty(_digestRealm))
                            {
                                errorMsg += $" | Digest Realm: {_digestRealm}";
                            }
                            if (!string.IsNullOrEmpty(_digestNonce))
                            {
                                var noncePreview = _digestNonce.Length > 16 ? _digestNonce.Substring(0, 16) + "..." : _digestNonce;
                                errorMsg += $" | Digest Nonce: {noncePreview}";
                            }
                            errorMsg += " | Will retry with Digest Auth";
                        }
                        
                        // Also check for Basic Auth requirement
                        var basicHeader = authHeaders.FirstOrDefault(h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
                        if (basicHeader != null && string.IsNullOrEmpty(digestHeader?.Parameter))
                        {
                            errorMsg += " | Basic Auth required (but credentials may be incorrect)";
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        // 401 but no WWW-Authenticate header - unusual but possible
                        errorMsg += " | Unauthorized (no authentication method specified)";
                    }
                    
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        var preview = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText;
                        errorMsg += $": {preview}";
                    }
                    System.Diagnostics.Debug.WriteLine($"ONVIF HTTP Error: {errorMsg}");
                    System.Diagnostics.Debug.WriteLine($"Full response: {responseText}");
                    return (null, (int)response.StatusCode, errorMsg);
                }
            }
            catch (System.Net.Http.HttpRequestException httpEx)
            {
                // Network-level error (connection refused, DNS failure, SSL error, etc.)
                var errorMsg = $"Network error: {httpEx.Message}";
                if (httpEx.InnerException != null)
                {
                    errorMsg += $" (Inner: {httpEx.InnerException.Message})";
                }
                System.Diagnostics.Debug.WriteLine($"ONVIF request error: {httpEx}");
                System.Diagnostics.Debug.WriteLine($"Inner exception: {httpEx.InnerException}");
                return (null, null, errorMsg);
            }
            catch (TaskCanceledException timeoutEx)
            {
                // Check if this is actually a timeout or just cancellation
                if (timeoutEx.CancellationToken.IsCancellationRequested)
                {
                    return (null, null, "Request cancelled");
                }
                // Request timed out after 30 seconds (camera not responding or network too slow)
                var errorMsg = "Request timeout after 30 seconds - camera not responding or network too slow";
                System.Diagnostics.Debug.WriteLine($"ONVIF timeout: {timeoutEx}");
                return (null, null, errorMsg);
            }
            catch (Exception ex)
            {
                // Unexpected error - include full details for debugging
                var errorMsg = $"Request failed: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMsg += $" ({ex.InnerException.Message})";
                }
                System.Diagnostics.Debug.WriteLine($"ONVIF request error: {ex}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return (null, null, errorMsg);
            }
        }

        /// <summary>
        /// Parses Digest Authentication challenge from WWW-Authenticate header
        /// Extracts realm, nonce, qop, and other parameters needed for Digest Auth
        /// </summary>
        private void ParseDigestChallenge(string challenge)
        {
            // Parse parameters like: realm="...", nonce="...", qop="auth"
            var realmMatch = Regex.Match(challenge, @"realm=""([^""]+)""", RegexOptions.IgnoreCase);
            if (realmMatch.Success)
            {
                _digestRealm = realmMatch.Groups[1].Value;
            }
            
            var nonceMatch = Regex.Match(challenge, @"nonce=""([^""]+)""", RegexOptions.IgnoreCase);
            if (nonceMatch.Success)
            {
                _digestNonce = nonceMatch.Groups[1].Value;
            }
            
            var qopMatch = Regex.Match(challenge, @"qop=""([^""]+)""", RegexOptions.IgnoreCase);
            if (qopMatch.Success)
            {
                _digestQop = qopMatch.Groups[1].Value;
            }
        }
        
        /// <summary>
        /// Calculates Digest Authentication response
        /// Implements RFC 2617 Digest Authentication algorithm
        /// </summary>
        private string CalculateDigestAuth(string username, string password, string method, string uri)
        {
            if (string.IsNullOrEmpty(_digestRealm) || string.IsNullOrEmpty(_digestNonce))
            {
                return "";
            }
            
            // Extract URI path from full URL
            var uriObj = new Uri(uri);
            var uriPath = uriObj.PathAndQuery;
            
            // HA1 = MD5(username:realm:password)
            var ha1Input = $"{username}:{_digestRealm}:{password}";
            var ha1 = ComputeMD5Hash(ha1Input);
            
            // HA2 = MD5(method:uri)
            var ha2Input = $"{method}:{uriPath}";
            var ha2 = ComputeMD5Hash(ha2Input);
            
            // Response = MD5(HA1:nonce:HA2) or MD5(HA1:nonce:nc:cnonce:qop:HA2) if qop
            string response;
            if (!string.IsNullOrEmpty(_digestQop) && _digestQop.Contains("auth"))
            {
                // With qop, we need nc (nonce count) and cnonce (client nonce)
                var nc = "00000001"; // Nonce count (increment for each request)
                var cnonce = Guid.NewGuid().ToString("N").Substring(0, 16); // Client nonce
                var responseInput = $"{ha1}:{_digestNonce}:{nc}:{cnonce}:auth:{ha2}";
                response = ComputeMD5Hash(responseInput);
                
                // Build Digest header with qop
                return $"username=\"{username}\", realm=\"{_digestRealm}\", nonce=\"{_digestNonce}\", uri=\"{uriPath}\", response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
            }
            else
            {
                // Without qop
                var responseInput = $"{ha1}:{_digestNonce}:{ha2}";
                response = ComputeMD5Hash(responseInput);
                
                // Build Digest header without qop
                return $"username=\"{username}\", realm=\"{_digestRealm}\", nonce=\"{_digestNonce}\", uri=\"{uriPath}\", response=\"{response}\"";
            }
        }
        
        /// <summary>
        /// Computes MD5 hash of input string (used for Digest Authentication)
        /// </summary>
        private string ComputeMD5Hash(string input)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
