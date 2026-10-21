using Linktrace.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Linktrace.Components.Dialogs;

/// <summary>Shared behaviour for edit dialogs: validate, save, show errors inline, close with the result.</summary>
public abstract class DialogBase : ComponentBase
{
    [CascadingParameter] protected IMudDialogInstance MudDialog { get; set; } = default!;

    protected MudForm? Form;
    protected string? Error;
    protected bool Busy;

    protected void Cancel() => MudDialog.Cancel();

    protected async Task SubmitAsync<T>(Func<Task<T>> save)
    {
        if (Form is not null)
        {
            await Form.ValidateAsync();
            if (!Form.IsValid) return;
        }

        Busy = true;
        Error = null;
        try
        {
            var result = await save();
            MudDialog.Close(DialogResult.Ok(result));
        }
        catch (LinktraceException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
