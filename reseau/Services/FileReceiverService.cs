using System.Net;
using System.Net.Sockets;
using System.Text;

namespace reseau.Services;

public class FileReceiverService
{
    private TcpListener _listener;
    private const int Port = 8080;
    public bool IsListening { get; private set; }

    // An event to notify the UI when a new file is received
    public event Action<string> OnFileReceived;

    public void StartListening()
    {
        if (IsListening) return;

        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            IsListening = true;

            // Start a new thread to accept clients
            Task.Run(AcceptClientsAsync);
        }
        catch (Exception ex)
        {
            // Handle exceptions (e.g., port in use)
            Console.WriteLine($"Error starting listener: {ex.Message}");
            IsListening = false;
        }
    }

    private async Task AcceptClientsAsync()
    {
        while (IsListening)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                // Handle each client in a new task to not block the listener
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (Exception ex)
            {
                // Handle exceptions (e.g., listener stopped)
                Console.WriteLine($"Error accepting client: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        string receivedFilePath = "";
        try
        {
            await using NetworkStream stream = client.GetStream();

            // 1. Read filename length (8 bytes for a long)
            byte[] fileNameLenBytes = new byte[8];
            await stream.ReadAsync(fileNameLenBytes, 0, 8);
            long fileNameLen = BitConverter.ToInt64(fileNameLenBytes, 0);

            // 2. Read filename
            byte[] fileNameBytes = new byte[fileNameLen];
            await stream.ReadAsync(fileNameBytes, 0, fileNameBytes.Length);
            string fileName = Encoding.UTF8.GetString(fileNameBytes);

            // 3. Read file data length (8 bytes for a long)
            byte[] fileDataLenBytes = new byte[8];
            await stream.ReadAsync(fileDataLenBytes, 0, 8);
            long fileDataLen = BitConverter.ToInt64(fileDataLenBytes, 0);

            // 4. Read file data
            // Use FileSystem.AppDataDirectory for safe cross-platform storage
            string targetDirectory = FileSystem.AppDataDirectory;
            receivedFilePath = Path.Combine(targetDirectory, "ReceivedFiles", fileName);

            // Ensure the directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(receivedFilePath));

            await using FileStream fileStream = new FileStream(receivedFilePath, FileMode.Create, FileAccess.Write);

            // Copy data from the network stream to the file stream
            await stream.CopyToAsync(fileStream, 81920); // 80KB buffer

            // Notify UI
            OnFileReceived?.Invoke(fileName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client: {ex.Message}");
            // Clean up partial file if error occurred
            if (!string.IsNullOrEmpty(receivedFilePath) && File.Exists(receivedFilePath))
            {
                File.Delete(receivedFilePath);
            }
        }
        finally
        {
            client.Close();
        }
    }

    public void StopListening()
    {
        IsListening = false;
        _listener?.Stop();
    }
}