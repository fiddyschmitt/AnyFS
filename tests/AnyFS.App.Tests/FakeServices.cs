using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using AnyFS.Sdk;

namespace AnyFS.App.Tests;

/// <summary>Dialogs as scripts: each handler fills the view model and says whether to press OK.</summary>
public sealed class FakeDialogs : IDialogs
{
    public Func<CollectionDialogViewModel, Task<bool>>? OnCollection { get; set; }

    public Func<SourceDialogViewModel, Task<bool>>? OnSource { get; set; }

    public Func<ExistingSourceDialogViewModel, Task<bool>>? OnExisting { get; set; }

    public Func<MountDialogViewModel, Task<bool>>? OnMount { get; set; }

    public Func<ConfirmDialogViewModel, ConfirmAnswer>? OnConfirm { get; set; }

    public Func<ExportSourcesDialogViewModel, Task<bool>>? OnExport { get; set; }

    /// <summary>Answers ChooseAsync with an option index (null dismisses); the questions are recorded in Choices.</summary>
    public Func<string, string, IReadOnlyList<string>, int?>? OnChoose { get; set; }

    public List<(string Title, string Message, IReadOnlyList<string> Options)> Choices { get; } = [];

    public string? SaveFileToPick { get; set; }

    public List<(string Title, string Message)> Messages { get; } = [];

    public List<(string Title, string Text)> Texts { get; } = [];

    public List<ConfirmDialogViewModel> Confirms { get; } = [];

    public string? FileToPick { get; set; }

    public string? FolderToPick { get; set; }

    public Task<bool> ShowAsync(CollectionDialogViewModel dialog) => RunAsync(dialog, OnCollection);

    public Task<bool> ShowAsync(SourceDialogViewModel dialog) => RunAsync(dialog, OnSource);

    public Task<bool> ShowAsync(ExistingSourceDialogViewModel dialog) => RunAsync(dialog, OnExisting);

    public Task<bool> ShowAsync(MountDialogViewModel dialog) => RunAsync(dialog, OnMount);

    public Task<bool> ShowAsync(ExportSourcesDialogViewModel dialog) => RunAsync(dialog, OnExport);

    public Task<ConfirmAnswer> ConfirmAsync(ConfirmDialogViewModel dialog)
    {
        Confirms.Add(dialog);
        return Task.FromResult(OnConfirm?.Invoke(dialog) ?? new ConfirmAnswer(ConfirmButton.Cancel, false));
    }

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
    {
        Choices.Add((title, message, options));
        return Task.FromResult(OnChoose?.Invoke(title, message, options));
    }

    public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult(SaveFileToPick);

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task ShowTextAsync(string title, string text)
    {
        Texts.Add((title, text));
        return Task.CompletedTask;
    }

    public Task<string?> PickFileAsync(string? current) => Task.FromResult(FileToPick);

    public Task<string?> PickFolderAsync(string? current) => Task.FromResult(FolderToPick);

    /// <summary>What a real dialog's OK button does: the script fills the fields, then TryAcceptAsync runs the engine call; a false leaves Error on the view model.</summary>
    private static async Task<bool> RunAsync<T>(T dialog, Func<T, Task<bool>>? script)
        where T : DialogViewModelBase
    {
        if (script is null || !await script(dialog))
        {
            return false;
        }

        return await dialog.TryAcceptAsync();
    }
}

public sealed class FakeOpener : IOpener
{
    public List<(MountOpenAction Action, string? SubPath)> Opened { get; } = [];

    public List<string> Files { get; } = [];

    public void Open(MountOpenAction action, string? subPath) => Opened.Add((action, subPath));

    public void OpenFile(string path) => Files.Add(path);
}

public sealed class FakeClipboard : ITextClipboard
{
    public List<string> Texts { get; } = [];

    public Task SetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(10);
        }
    }
}
