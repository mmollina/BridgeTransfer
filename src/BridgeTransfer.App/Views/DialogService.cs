using BridgeTransfer.App.Services;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.Views;

public sealed class DialogService : IDialogService
{
    private readonly Action<string> _log;

    public DialogService(Action<string> log) => _log = log;

    public void ShowError(string title, Exception exception)
    {
        var (message, details) = ErrorText.Describe(exception);
        _log($"{title}: {details}");
        ShowError(title, message, details);
    }

    public void ShowError(string title, string message, string? details = null) =>
        MessageWindow.Show(title, message, MessageKind.Error, details, ("Close", true, true, true, true));

    public void ShowInfo(string title, string message) =>
        MessageWindow.Show(title, message, MessageKind.Info, null, ("OK", true, true, true, true));

    public bool Confirm(string title, string message, string confirmText, string cancelText = "Cancel", bool emphasizeCancel = false) =>
        MessageWindow.Show(title, message, emphasizeCancel ? MessageKind.Warning : MessageKind.Question, null,
            (cancelText, false, false, emphasizeCancel, true),
            (confirmText, true, !emphasizeCancel, !emphasizeCancel, false)) is true;

    public string? PromptText(string title, string label, string initial, Func<string, string?> validate, string okText) =>
        PromptWindow.AskText(title, label, initial, validate, okText);

    public string? PromptSecret(string title, string message, string label) => PromptWindow.AskSecret(title, message, label);

    public bool ConfirmUnknownHostKey(PresentedHostKey key) => HostKeyWindow.ConfirmUnknown(key);

    public void ShowHostKeyChanged(HostKeyChangedException ex)
    {
        _log("Blocked connection: host key changed for " + ex.Check.Presented.Host + ":" + ex.Check.Presented.Port);
        HostKeyWindow.ShowChanged(ex);
    }

    public (ConflictChoice Choice, ConflictScope Scope)? AskConflict(ConflictRequest request, int waitingInTransfer, int waitingInQueue) =>
        ConflictWindow.Ask(request, waitingInTransfer, waitingInQueue);

    public ConflictChoice? ConfirmTransfer(string title, string message, string startText, ConflictChoice preselected) =>
        TransferConfirmWindow.Ask(title, message, startText, preselected);
}
