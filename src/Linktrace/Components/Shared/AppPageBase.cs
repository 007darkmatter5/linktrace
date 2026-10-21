using Linktrace.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Linktrace.Components.Shared;

/// <summary>Common services for app pages, plus whether the current user may edit.</summary>
public abstract class AppPageBase : ComponentBase
{
    [Inject] protected ICurrentUser CurrentUser { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected IDialogService Dialogs { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected bool CanEdit { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        CanEdit = await CurrentUser.CanEditAsync();
    }
}
