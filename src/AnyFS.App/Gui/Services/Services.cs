using AnyFS.App.Gui.ViewModels;
using AnyFS.Sdk;

namespace AnyFS.App.Gui.Services;

public enum ConfirmButton
{
    Yes,
    No,
    Cancel,
}

public readonly record struct ConfirmAnswer(ConfirmButton Button, bool Checked);

/// <summary>Everything the view models ask the window for, so they can be tested without one.</summary>
public interface IDialogs
{
    Task<bool> ShowAsync(CollectionDialogViewModel dialog);

    Task<bool> ShowAsync(SourceDialogViewModel dialog);

    Task<bool> ShowAsync(ExistingSourceDialogViewModel dialog);

    Task<bool> ShowAsync(MountDialogViewModel dialog);

    Task<ConfirmAnswer> ConfirmAsync(ConfirmDialogViewModel dialog);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Text with a copy button (a mount command the user must run as root, spec 8.7).</summary>
    Task ShowTextAsync(string title, string text);

    Task<string?> PickFileAsync(string? current);

    Task<string?> PickFolderAsync(string? current);
}

/// <summary>The Open containing folder button and the Open log button (spec 8.9, 11).</summary>
public interface IOpener
{
    /// <summary>Opens a mount's access path, or <paramref name="subPath"/> (a source path, '/'-separated) inside it.</summary>
    void Open(MountOpenAction action, string? subPath);

    void OpenFile(string path);
}

public interface ITextClipboard
{
    Task SetTextAsync(string text);
}

/// <summary>Runs a view-model operation, reports a failure as a message box, and remembers the last one (tests await it).</summary>
public sealed class Operations(IDialogs dialogs)
{
    public Task Last { get; private set; } = Task.CompletedTask;

    public Task Run(string what, Func<Task> action)
    {
        var task = Execute(what, action);
        Last = task;
        return task;
    }

    private async Task Execute(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync(what, ex is ArgumentException or InvalidOperationException ? ex.Message : ex.GetBaseException().Message);
        }
    }
}
