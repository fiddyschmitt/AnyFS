using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace AnyFS.App.Gui.Views;

/// <summary>The dialogs as real windows, owned by the main window.</summary>
public sealed class AvaloniaDialogs(Window owner, ITextClipboard clipboard) : IDialogs
{
    public Task<bool> ShowAsync(CollectionDialogViewModel dialog) => ShowAsync(new CollectionDialog(dialog));

    public Task<bool> ShowAsync(SourceDialogViewModel dialog) => ShowAsync(new SourceDialog(dialog));

    public Task<bool> ShowAsync(ExistingSourceDialogViewModel dialog) => ShowAsync(new ExistingSourceDialog(dialog));

    public Task<bool> ShowAsync(MountDialogViewModel dialog) => ShowAsync(new MountDialog(dialog));

    public async Task<ConfirmAnswer> ConfirmAsync(ConfirmDialogViewModel dialog)
    {
        var button = await new ConfirmDialog(dialog).ShowDialog<ConfirmButton?>(owner) ?? ConfirmButton.Cancel;
        return new ConfirmAnswer(button, dialog.Checked);
    }

    public Task ShowMessageAsync(string title, string message)
        => ConfirmAsync(new ConfirmDialogViewModel(title, message, "OK", cancelText: null));

    public Task ShowTextAsync(string title, string text) => new TextDialog(new TextDialogViewModel(title, text, clipboard)).ShowDialog(owner);

    public async Task<string?> PickFileAsync(string? current)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a file",
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(current),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string? current)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder",
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(current),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<bool> ShowAsync(Window dialog) => await dialog.ShowDialog<bool?>(owner) ?? false;

    private async Task<IStorageFolder?> StartFolderAsync(string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return null;
        }

        try
        {
            var directory = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            return await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or IOException)
        {
            return null;
        }
    }
}

public sealed class AvaloniaClipboard(TopLevel owner) : ITextClipboard
{
    public Task SetTextAsync(string text) => owner.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}
