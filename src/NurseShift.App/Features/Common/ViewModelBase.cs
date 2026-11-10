using CommunityToolkit.Mvvm.ComponentModel;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.Common;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;

    protected void Show(OperationResult result) => ErrorMessage = result.IsSuccess ? null : UiStrings.Error(result.ErrorCode);
    protected void Show(SafeErrorCode code) => ErrorMessage = UiStrings.Error(code);
}
