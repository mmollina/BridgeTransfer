using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.ViewModels;

/// <summary>Dialogs used by view models. Implemented by the WPF views.</summary>
public interface IDialogService
{
    void ShowError(string title, Exception exception);

    void ShowError(string title, string message, string? details = null);

    void ShowInfo(string title, string message);

    bool Confirm(string title, string message, string confirmText, string cancelText = "Cancel", bool emphasizeCancel = false);

    string? PromptText(string title, string label, string initial, Func<string, string?> validate, string okText);

    string? PromptSecret(string title, string message, string label);

    bool ConfirmUnknownHostKey(PresentedHostKey key);

    void ShowHostKeyChanged(HostKeyChangedException ex);

    (ConflictChoice Choice, ConflictScope Scope)? AskConflict(ConflictRequest request, int waitingInTransfer, int waitingInQueue);

    /// <summary>Confirms a transfer and returns what to do with existing files, or null if cancelled.</summary>
    ConflictChoice? ConfirmTransfer(string title, string message, string startText, ConflictChoice preselected);
}
