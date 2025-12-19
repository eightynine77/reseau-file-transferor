using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Principal;

namespace reseau.Services;

public class FileTransferService : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly HttpClient _httpClient;
    private bool _isServerRunning = false;
    private CancellationTokenSource _serverCts;
    private readonly IFolderPicker _folderPicker;

    public event Action OnFileReceived;

    public string CustomSavePath { get; set; }

    // Use a property for the path
    public string ReceivedFilesPath => !string.IsNullOrEmpty(CustomSavePath) 
        ? CustomSavePath 
        : Path.Combine(FileSystem.AppDataDirectory, "ReceivedFiles");

    public FileTransferService(HttpClient httpClient, IFolderPicker folderPicker)
    {
        _httpClient = httpClient;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://*:8080/");
        _folderPicker = folderPicker;
    }

    public bool IsServerRunning => _isServerRunning;

    public void EnsureDirectoryExists()
    {
        if (!Directory.Exists(ReceivedFilesPath))
        {
            Directory.CreateDirectory(ReceivedFilesPath);
        }
    }

    public async Task<string> PickSaveLocationAsync()
    {
        string path = await _folderPicker.PickFolderAsync();
        if (!string.IsNullOrEmpty(path))
        {
            CustomSavePath = path;
            // Ensure permissions or directory existence if possible
            if (!Directory.Exists(CustomSavePath))
            {
                try { Directory.CreateDirectory(CustomSavePath); } catch { }
            }
        }
        return CustomSavePath;
    }

    public string GetLocalIPAddress()
    {
        var candidates = GetAllPossibleIPsWithMetadata();

        // If no candidates, fallback to localhost
        if (!candidates.Any()) return "127.0.0.1";

        // Sort by score (descending) and take the best one
        var bestMatch = candidates.OrderByDescending(c => c.Score).First();
        return bestMatch.Ip;
    }

    public List<string> GetAllPossibleIPs()
    {
        return GetAllPossibleIPsWithMetadata()
            .OrderByDescending(x => x.Score)
            .Select(x => x.Ip)
            .ToList();
    }

    private class IpCandidate
    {
        public string Ip { get; set; }
        public int Score { get; set; }
        public string Name { get; set; }
    }

    private List<IpCandidate> GetAllPossibleIPsWithMetadata()
    {
        var candidates = new List<IpCandidate>();
        var virtualKeywords = new[] { "virtual", "wsl", "v-ethernet", "vmware", "pseudo", "loopback" };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();

                // Calculate Score
                int score = 0;

                // 1. Greatly prefer interfaces with a Gateway (means internet/router access)
                if (ipProps.GatewayAddresses.Count > 0) score += 10;

                // 2. Prefer Wi-Fi or Ethernet over others
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 5;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 5;

                // 3. Penalize Virtual Adapters
                string nameLower = ni.Name.ToLowerInvariant();
                string descLower = ni.Description.ToLowerInvariant();
                if (virtualKeywords.Any(k => nameLower.Contains(k) || descLower.Contains(k))) score -= 10;

                // 4. Slight boost for standard local subnets (192.168.x.x)
                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        var ipStr = ip.Address.ToString();
                        int localScore = score;

                        if (ipStr.StartsWith("192.168.")) localScore += 2;
                        if (ipStr.StartsWith("10.")) localScore += 1; // Class A private

                        candidates.Add(new IpCandidate { Ip = ipStr, Score = localScore, Name = ni.Name });
                    }
                }
            }
        }
        catch { /* Ignore enumeration errors */ }

        return candidates;
    }

    // --- END SMART IP LOGIC ---

    public async Task StartServerAsync()
    {
        if (_isServerRunning) return;

        try
        {
            await RequestFilePermissions();
            ConfigureWindowsFirewallAndPorts();

            _listener.Start();
            _isServerRunning = true;
            _serverCts = new CancellationTokenSource();

            Console.WriteLine("Server started.");

            // --- THE FIX IS HERE ---
            // We start the loop in a background thread and let it run independently.
            // We do NOT 'await' this Task.Run, so the method returns immediately to the UI.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        // This waits for a connection, but now it's on a background thread
                        var context = await _listener.GetContextAsync();
                        _ = Task.Run(() => HandleRequestAsync(context));
                    }
                }
                catch (HttpListenerException)
                {
                    // This happens normally when we stop the server (listener is closed)
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Server loop error: {ex.Message}");
                }
            });
            // -----------------------
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
            StopServer();
            throw;
        }
    }

    public void StopServer()
    {
        if (!_isServerRunning) return;
        try
        {
            _serverCts?.Cancel();
            _listener.Stop();
        }
        catch { }
        _isServerRunning = false;
    }

    // 1. UPDATED: Sends pure file bytes (Fixes corrupted/empty files)
    public async Task<string> SendFileAsync(string targetIp, string filePath)
    {
        if (string.IsNullOrWhiteSpace(targetIp)) return "Target IP is empty.";
        if (!File.Exists(filePath)) return "File not found.";

        try
        {
            string url = $"http://{targetIp.Trim()}:8080/upload";

            using var fileStream = File.OpenRead(filePath);

            // USE STREAM CONTENT DIRECTLY (No Multipart Wrapper)
            using var content = new StreamContent(fileStream);

            // We still send the filename in the header so the server knows what to name it
            _httpClient.DefaultRequestHeaders.Remove("X-File-Name");
            _httpClient.DefaultRequestHeaders.Add("X-File-Name", Path.GetFileName(filePath));

            var response = await _httpClient.PostAsync(url, content);

            return response.IsSuccessStatusCode ? "Success" : $"Error: {response.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return $"Connection error: {ex.Message}";
        }
    }

    // 2. UPDATED: Handles the request (Double checks stream safety)
    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/upload")
            {
                string fileName = request.Headers["X-File-Name"];
                // Fallback if header is missing
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"received_{Guid.NewGuid()}.dat";

                // Clean filename
                fileName = Path.GetFileName(fileName);
                string savePath = Path.Combine(ReceivedFilesPath, fileName);

                // Save the raw stream directly to disk
                using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                {
                    await request.InputStream.CopyToAsync(fileStream);
                }

                // IMPORTANT: Notify UI
                OnFileReceived?.Invoke();

                response.StatusCode = (int)HttpStatusCode.OK;
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task RequestFilePermissions()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.StorageWrite>();
        if (status != PermissionStatus.Granted)
        {
            await Permissions.RequestAsync<Permissions.StorageWrite>();
        }
    }

    public void ConfigureWindowsFirewallAndPorts()
    {
#if WINDOWS
        if (!IsAdministrator()) return;
        try 
        {
            RunCommand("netsh", "advfirewall firewall add rule name=\"ReseauApp\" dir=in action=allow protocol=TCP localport=8080");
            RunCommand("netsh", "http add urlacl url=http://*:8080/ user=Everyone");
        }
        catch { }
#endif
    }

    private bool IsAdministrator()
    {
#if WINDOWS
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
#else
        return true;
#endif
    }

    private void RunCommand(string command, string args)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = command, Arguments = args, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process.Start(info)?.WaitForExit();
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        StopServer();
    }
}