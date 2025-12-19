namespace reseau.Services;

public interface IFolderPicker
{
    Task<string> PickFolderAsync();
}