using Microsoft.Extensions.Logging;
using reseau.Services; 

namespace reseau
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton<FileTransferService>();
            builder.Services.AddSingleton<HttpClient>();

#if WINDOWS
    builder.Services.AddSingleton<IFolderPicker, reseau.Services.FolderPickerImplementation>();
#elif ANDROID
            builder.Services.AddSingleton<IFolderPicker, reseau.Services.FolderPickerImplementation>();
#endif

            return builder.Build();
        }
    }
}