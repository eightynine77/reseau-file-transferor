using Windows.Storage.Pickers;
using WinRT.Interop; // Required for Window Handle (HWND)

namespace reseau.Services;

public class FolderPickerImplementation : IFolderPicker
{
    public async Task<string> PickFolderAsync()
    {
        try
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*"); // Required filter

            // Get the Window Handle (HWND) of the current MAUI window
            var window = Application.Current?.Windows[0].Handler.PlatformView as MauiWinUIWindow;
            var hwnd = WindowNative.GetWindowHandle(window);

            // Associate the picker with the window
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            var result = await folderPicker.PickSingleFolderAsync();

            return result?.Path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Windows Picker Error: {ex.Message}");
            return null;
        }
    }
}