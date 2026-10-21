using Linktrace.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Linktrace.Components.Shared;

public static class UiExtensions
{
    /// <summary>Runs a service call and shows user-facing errors as a snackbar. Returns true on success.</summary>
    public static async Task<bool> RunAsync(this ISnackbar snackbar, Func<Task> action, string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage is not null) snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (LinktraceException ex)
        {
            snackbar.Add(ex.Message, Severity.Error);
            return false;
        }
    }

    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message, string yesText = "Delete")
    {
        var result = await dialogs.ShowMessageBoxAsync(title, message, yesText: yesText, cancelText: "Cancel");
        return result == true;
    }

    /// <summary>Opens a dialog and returns its result data, or default if it was cancelled.</summary>
    public static async Task<TResult?> OpenAsync<TDialog, TResult>(this IDialogService dialogs, string title,
        DialogParameters<TDialog> parameters, MaxWidth maxWidth = MaxWidth.Small) where TDialog : IComponent
    {
        var options = new DialogOptions { MaxWidth = maxWidth, FullWidth = true };
        var dialog = await dialogs.ShowAsync<TDialog>(title, parameters, options);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: TResult data } ? data : default;
    }

    /// <summary>A detached copy for editing in a dialog, so cancelling leaves the original untouched.</summary>
    public static T EditCopy<T>(this T entity) where T : Data.Entities.EntityBase => (T)entity.ShallowClone();
}
