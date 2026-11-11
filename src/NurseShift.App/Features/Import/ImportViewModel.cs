using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Images;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.Import;

public sealed class ImportPageRow(SourceImage page)
{
    public Guid Id { get; } = page.Id;
    public string Title => UiStrings.Format("PageFormat", page.SortOrder + 1);
    public string State => page.ProcessingState switch
    {
        ImageProcessingState.Ready => UiStrings.Get("ReadyForReview"),
        ImageProcessingState.Failed => UiStrings.Format("PageErrorFormat", UiStrings.Error(page.SafeErrorCode ?? SafeErrorCode.Unknown)),
        ImageProcessingState.Uploading => UiStrings.Get("Uploading"),
        ImageProcessingState.Recognizing => UiStrings.Get("Recognizing"),
        ImageProcessingState.Cancelled => UiStrings.Get("CancelledState"),
        ImageProcessingState.Excluded => UiStrings.Get("ExcludedState"),
        _ => UiStrings.Get("WaitingUpload")
    };
}

[QueryProperty(nameof(ShiftId), "ShiftId")]
public sealed partial class ImportViewModel(IImportService importService, IImagePicker imagePicker) : ViewModelBase
{
    private CancellationTokenSource? _recognitionCancellation;
    [ObservableProperty] private Guid shiftId;
    [ObservableProperty] private Guid sessionId;
    [ObservableProperty] private bool hasPages;
    [ObservableProperty] private string progressText = string.Empty;
    [ObservableProperty] private string recognizeText = UiStrings.Recognize;
    [ObservableProperty] private bool isRecognizing;
    public ObservableCollection<ImportPageRow> Pages { get; } = [];

    public async Task InitializeAsync()
    {
        var session = await importService.CreateAsync(ShiftId, CancellationToken.None);
        SessionId = session.Id;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var details = await importService.GetAsync(SessionId, CancellationToken.None);
        Pages.Clear();
        if (details is not null)
            foreach (var page in details.Pages)
                Pages.Add(new ImportPageRow(page));
        HasPages = Pages.Count > 0;
        RecognizeText = UiStrings.Format("RecognizeCountFormat", Pages.Count);
    }

    [RelayCommand] private async Task CaptureAsync() => await AddImageAsync(true);
    [RelayCommand] private async Task PickAsync() => await AddImageAsync(false);

    private async Task AddImageAsync(bool capture)
    {
        try
        {
            var image = capture ? await imagePicker.CaptureAsync(CancellationToken.None) : await imagePicker.PickAsync(CancellationToken.None);
            if (image is null)
                return;
            var result = await importService.AddPageAsync(SessionId, image, CancellationToken.None);
            Show(result);
            await LoadAsync();
        }
        catch (PermissionException)
        {
            Show(SafeErrorCode.PermissionDenied);
        }
        catch (RecognitionUnavailableException exception)
        {
            Show(exception.Code);
        }
    }

    [RelayCommand]
    private async Task ReplaceAsync(ImportPageRow row)
    {
        try
        {
            var image = await imagePicker.CaptureAsync(CancellationToken.None);
            if (image is null)
                return;
            Show(await importService.ReplacePageAsync(row.Id, image, CancellationToken.None));
            await LoadAsync();
        }
        catch (PermissionException)
        {
            Show(SafeErrorCode.PermissionDenied);
        }
        catch (RecognitionUnavailableException exception)
        {
            Show(exception.Code);
        }
    }

    [RelayCommand] private async Task RotateAsync(ImportPageRow row) { Show(await importService.RotatePageAsync(row.Id, CancellationToken.None)); await LoadAsync(); }
    [RelayCommand] private async Task MoveUpAsync(ImportPageRow row) { Show(await importService.MovePageAsync(row.Id, -1, CancellationToken.None)); await LoadAsync(); }
    [RelayCommand] private async Task MoveDownAsync(ImportPageRow row) { Show(await importService.MovePageAsync(row.Id, 1, CancellationToken.None)); await LoadAsync(); }

    [RelayCommand]
    private async Task DeleteAsync(ImportPageRow row)
    {
        if (!await Shell.Current.DisplayAlert(UiStrings.Get("DeletePageTitle"), UiStrings.Get("DeletePageText"), UiStrings.Delete, UiStrings.Cancel))
            return;
        Show(await importService.DeletePageAsync(row.Id, CancellationToken.None));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RecognizeAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        IsRecognizing = true;
        _recognitionCancellation = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(value => ProgressText = UiStrings.Format("PageProgressFormat", value.CurrentPage, value.TotalPages,
            UiStrings.Get(value.State == ImageProcessingState.Uploading ? "Uploading" : "Recognizing")));
        try
        {
            var result = await importService.RecognizeAsync(SessionId, false, progress, _recognitionCancellation.Token);
            if (result.ErrorCode == SafeErrorCode.ConfirmationRequired)
            {
                var consent = await Shell.Current.DisplayAlert(UiStrings.Get("TransferTitle"), UiStrings.ExternalTransfer, UiStrings.Get("TransferConfirm"), UiStrings.Cancel);
                if (consent)
                    result = await importService.RecognizeAsync(SessionId, true, progress, _recognitionCancellation.Token);
            }
            Show(result);
            if (result.IsSuccess)
                await Shell.Current.GoToAsync("review", new Dictionary<string, object> { ["SessionId"] = SessionId });
        }
        catch (OperationCanceledException)
        {
            ProgressText = UiStrings.Get("ImportStopped");
        }
        finally
        {
            IsRecognizing = false;
            IsBusy = false;
            _recognitionCancellation.Dispose();
            _recognitionCancellation = null;
            await LoadAsync();
        }
    }

    [RelayCommand] private void CancelProcessing() => _recognitionCancellation?.Cancel();

    [RelayCommand]
    private async Task CloseAsync()
    {
        var choice = await Shell.Current.DisplayActionSheet(UiStrings.Get("SaveDraftPrompt"), UiStrings.Cancel, UiStrings.Get("DeleteDraft"), UiStrings.Get("SaveDraft"));
        if (choice == UiStrings.Get("DeleteDraft"))
        {
            await importService.CancelAsync(SessionId, CancellationToken.None);
            await Shell.Current.GoToAsync("..");
        }
        else if (choice == UiStrings.Get("SaveDraft"))
            await Shell.Current.GoToAsync("..");
    }
}
