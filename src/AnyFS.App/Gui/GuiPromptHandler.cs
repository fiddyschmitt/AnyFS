using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Runtime;
using AnyFS.Sdk;

namespace AnyFS.App.Gui;

/// <summary>
/// Plugin prompts (a PIN, a 2FA code) as modal dialogs on the main window, one at a time (the engine
/// serialises them, spec 6.4, 11.4). Until a window exists the prompt waits; if none appears the source
/// fails with "Needs input" as it would headless.
/// </summary>
public sealed class GuiPromptHandler : IPromptHandler
{
    private readonly TaskCompletionSource<Func<PromptDialogViewModel, CancellationToken, Task<PromptResult>>> _presenter = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The window registers how to show a prompt (it owns the dialog); tests register a fake.</summary>
    public void SetPresenter(Func<PromptDialogViewModel, CancellationToken, Task<PromptResult>> presenter) => _presenter.TrySetResult(presenter);

    public async Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct)
    {
        Func<PromptDialogViewModel, CancellationToken, Task<PromptResult>> presenter;
        try
        {
            presenter = await _presenter.Task.WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SourceFailedException($"Needs input: {request.Title}", permanent: true);
        }

        var dialog = new PromptDialogViewModel(sourceName, request);
        return await presenter(dialog, ct).ConfigureAwait(false);
    }
}
