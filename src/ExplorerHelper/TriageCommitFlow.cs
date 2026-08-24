using System.Windows;
using System.Windows.Threading;
using ExplorerHelper.ViewModels;

namespace ExplorerHelper;

/// <summary>
/// The single path from pending triage marks to disk. Both entry points use it: the review
/// screen's Commit button and the toolbar's pending-marks pill.
/// <para>
/// Marks live in the session and are made from the list as often as from the deck, so committing
/// can't be something only the deck knows how to do — that left the list able to discard marks
/// but never commit them. <see cref="CommitDialog"/> was already independent of the deck (it takes
/// a per-folder summary and a totals callback, nothing else); only the call site was trapped there.
/// </para>
/// </summary>
public static class TriageCommitFlow
{
    /// <summary>
    /// Confirms the commit and, if the user goes ahead, applies it. Returns false when the dialog
    /// was cancelled. <paramref name="clearPreview"/> releases the caller's preview handles and
    /// <paramref name="committed"/> runs after the commit — both on the UI thread.
    /// </summary>
    public static bool Run(
        MainViewModel vm,
        Window owner,
        Action clearPreview,
        Action? committed = null)
    {
        // Marks can span folders (issue #43), so the dialog gets the per-folder breakdown and a
        // way to recompute its totals when the user narrows the scope.
        var dialog = new CommitDialog(vm.SummarizeMarksByFolder(), vm.FolderPath, vm.TotalsFor)
        {
            Owner = owner,
        };
        if (dialog.ShowDialog() != true)
            return false;

        var destination = dialog.KeepDestination;
        var copyKeepers = dialog.CopyKeepers;
        var deleteRejects = dialog.DeleteRejects;
        var currentFolderOnly = dialog.CurrentFolderOnly;

        // Release every preview handle, then let the dispatcher pump the media teardown before
        // files start moving - same discipline as delete/rename (issue #1).
        clearPreview();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var error = vm.CommitTriage(destination, copyKeepers, deleteRejects, currentFolderOnly);
            if (error is not null)
                MessageBox.Show(owner, error, "Commit finished with errors",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            committed?.Invoke();
        }));
        return true;
    }
}
