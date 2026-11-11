using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;

namespace NurseShift.App.Features.Import;

public sealed class ReviewRow(Assignment assignment, bool duplicate)
{
    public Assignment Assignment { get; } = assignment;
    public Guid Id => Assignment.Id;
    public string Time => Assignment.DueLocal?.ToString("HH:mm") ?? UiStrings.Get("MissingTime");
    public string Patient => Assignment.PatientName ?? UiStrings.Get("MissingPatient");
    public string Procedure => Assignment.Procedure ?? UiStrings.Get("MissingProcedure");
    public string? Warning => duplicate ? UiStrings.Get("DuplicateWarning") : Assignment.ReviewReason ??
        (Assignment.NeedsReview ? UiStrings.Get("NeedsAttention") : Assignment.DueAtUtc is null ? UiStrings.Get("MissingTime") : null);
    public bool HasWarning => duplicate || Assignment.NeedsReview || !string.IsNullOrWhiteSpace(Assignment.ReviewReason) || Assignment.DueAtUtc is null;
}

public sealed class UnparsedRow(Guid imageId, int index, UnparsedLine line)
{
    public Guid ImageId { get; } = imageId;
    public int Index { get; } = index;
    public string Text => line.Text ?? UiStrings.Get("MissingUnparsedText");
    public string Reason => line.Reason;
}

public sealed class ReviewPageRow(SourceImage page)
{
    public SourceImage Page { get; } = page;
    public int DisplayNumber => Page.SortOrder + 1;
    public string Status => Page.ProcessingState switch
    {
        ImageProcessingState.Ready => UiStrings.Get("ReadyForReview"),
        ImageProcessingState.Failed => UiStrings.Error(Page.SafeErrorCode ?? SafeErrorCode.Unknown),
        ImageProcessingState.Excluded => UiStrings.Get("ExcludedState"),
        ImageProcessingState.Pending => UiStrings.Get("WaitingUpload"),
        ImageProcessingState.Uploading => UiStrings.Get("Uploading"),
        ImageProcessingState.Recognizing => UiStrings.Get("Recognizing"),
        _ => UiStrings.Get("CancelledState")
    };
}

[QueryProperty(nameof(SessionId), "SessionId")]
public sealed partial class ReviewViewModel(IImportService importService, IPhotoAccess photoAccess) : ViewModelBase
{
    private ImportDetails? _details;
    [ObservableProperty] private Guid sessionId;
    [ObservableProperty] private SourceImage? selectedPage;
    [ObservableProperty] private ImageSource? pageImage;
    [ObservableProperty] private string summary = string.Empty;
    [ObservableProperty] private string pageSummary = string.Empty;
    [ObservableProperty] private string blockingPagesText = string.Empty;
    [ObservableProperty] private bool hasBlockingPages;
    [ObservableProperty] private bool canExcludeSelectedPage;
    [ObservableProperty] private bool hasUnparsed;
    [ObservableProperty] private bool hasFailedPages;
    public ObservableCollection<ReviewPageRow> Pages { get; } = [];
    public ObservableCollection<ReviewRow> Assignments { get; } = [];
    public ObservableCollection<UnparsedRow> Unparsed { get; } = [];

    public async Task LoadAsync()
    {
        _details = await importService.GetAsync(SessionId, CancellationToken.None);
        if (_details is null)
        {
            Show(SafeErrorCode.ImportNotFound);
            return;
        }
        HasFailedPages = _details.Pages.Any(item => item.ProcessingState == ImageProcessingState.Failed);
        UpdateSummary();
        var currentPageId = SelectedPage?.Id;
        Pages.Clear();
        foreach (var page in _details.Pages)
            Pages.Add(new ReviewPageRow(page));
        SelectedPage = Pages.FirstOrDefault(item => item.Page.Id == currentPageId)?.Page ?? Pages.FirstOrDefault()?.Page;
        await RefreshPageAsync();
    }

    partial void OnSelectedPageChanged(SourceImage? value)
    {
        CanExcludeSelectedPage = value is not null && value.ProcessingState is not ImageProcessingState.Excluded and
            not ImageProcessingState.Uploading and not ImageProcessingState.Recognizing;
        _ = RefreshPageAsync();
    }

    private async Task RefreshPageAsync()
    {
        PageImage = null;
        Assignments.Clear();
        Unparsed.Clear();
        HasUnparsed = false;
        if (_details is null || SelectedPage is null)
            return;
        try
        {
            var bytes = await photoAccess.ReadAsync(SelectedPage.Id, CancellationToken.None);
            PageImage = ImageSource.FromStream(() => new MemoryStream(bytes, false));
        }
        catch (IOException)
        {
            Show(SafeErrorCode.ImageUnreadable);
        }
        var duplicates = _details.Assignments.GroupBy(item => (item.PatientName?.Trim(), item.Procedure?.Trim(), item.DueLocal))
            .Where(group => group.Count() > 1).SelectMany(group => group).Select(item => item.Id).ToHashSet();
        foreach (var assignment in _details.Assignments.Where(item => item.SourceImageId == SelectedPage.Id))
            Assignments.Add(new ReviewRow(assignment, duplicates.Contains(assignment.Id)));
        var lines = ImportValidator.ReadUnparsedLines(SelectedPage);
        for (var index = 0; index < lines.Count; index++)
            if (!lines[index].Excluded)
                Unparsed.Add(new UnparsedRow(SelectedPage.Id, index, lines[index]));
        HasUnparsed = Unparsed.Count > 0;
    }

    private void UpdateSummary()
    {
        if (_details is null)
            return;
        var blocking = _details.Assignments.Count(item => string.IsNullOrWhiteSpace(item.PatientName) || string.IsNullOrWhiteSpace(item.Procedure) || item.DueAtUtc is null);
        var allUnparsed = _details.Pages.Where(item => item.ProcessingState != ImageProcessingState.Excluded)
            .SelectMany(ImportValidator.ReadUnparsedLines).Count(item => !item.Excluded);
        Summary = UiStrings.Format("ReviewSummaryFormat", _details.Assignments.Count, blocking, allUnparsed);
        PageSummary = UiStrings.Format("PageSummaryFormat",
            _details.Pages.Count(item => item.ProcessingState == ImageProcessingState.Ready),
            _details.Pages.Count(item => item.ProcessingState == ImageProcessingState.Failed),
            _details.Pages.Count(item => item.ProcessingState == ImageProcessingState.Excluded));
        var blockingPages = _details.Pages.Where(item => item.ProcessingState is ImageProcessingState.Pending or
            ImageProcessingState.Uploading or ImageProcessingState.Recognizing or ImageProcessingState.Failed or ImageProcessingState.Cancelled)
            .Select(item => (item.SortOrder + 1).ToString()).ToArray();
        HasBlockingPages = blockingPages.Length > 0;
        BlockingPagesText = HasBlockingPages ? UiStrings.Format("BlockingPages", string.Join(", ", blockingPages)) : string.Empty;
    }

    [RelayCommand] private void SelectPage(SourceImage page) => SelectedPage = page;
    [RelayCommand] private async Task EditAsync(ReviewRow row) => await Shell.Current.GoToAsync("assignment", new Dictionary<string, object> { ["SessionId"] = SessionId, ["DraftId"] = row.Id });
    [RelayCommand] private async Task ViewPhotoAsync() => await Shell.Current.GoToAsync("photo", new Dictionary<string, object> { ["ImageId"] = SelectedPage!.Id });
    [RelayCommand] private async Task RetryAsync() => await Shell.Current.GoToAsync("import", new Dictionary<string, object> { ["ShiftId"] = _details!.Session.ShiftId });

    [RelayCommand]
    private async Task AddManualAsync()
    {
        var sourceImageId = SelectedPage?.ProcessingState == ImageProcessingState.Excluded ? null : SelectedPage?.Id;
        Show(await importService.AddDraftAsync(SessionId, sourceImageId, null, CancellationToken.None));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExcludeAsync(UnparsedRow row)
    {
        Show(await importService.ExcludeUnparsedLineAsync(row.ImageId, row.Index, CancellationToken.None));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddUnparsedAsync(UnparsedRow row)
    {
        var result = await importService.AddDraftAsync(SessionId, row.ImageId, row.Text, CancellationToken.None);
        if (result.IsSuccess)
            result = await importService.ExcludeUnparsedLineAsync(row.ImageId, row.Index, CancellationToken.None);
        Show(result);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExcludePageAsync()
    {
        if (SelectedPage is null || IsBusy)
            return;
        var approved = await Shell.Current.DisplayAlert(
            UiStrings.Format("ExcludePageTitle", SelectedPage.SortOrder + 1),
            UiStrings.Get("ExcludePageText"), UiStrings.Get("ExcludePage"), UiStrings.Cancel);
        if (!approved)
            return;
        IsBusy = true;
        try
        {
            Show(await importService.ExcludePageAsync(SelectedPage.Id, CancellationToken.None));
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_details is null || IsBusy)
            return;
        if (HasBlockingPages)
        {
            Show(SafeErrorCode.PagesNotReady);
            return;
        }
        var confirmedEmpty = false;
        if (_details.Assignments.Count == 0)
        {
            confirmedEmpty = await Shell.Current.DisplayAlert(UiStrings.Get("EmptyImportTitle"),
                UiStrings.Get("EmptyImportText"), UiStrings.Get("ConfirmEmptyImport"), UiStrings.Cancel);
            if (!confirmedEmpty)
                return;
        }
        IsBusy = true;
        try
        {
            var result = await importService.ConfirmAsync(SessionId, _details.Session.RowVersion, confirmedEmpty, CancellationToken.None);
            Show(result);
            if (result.IsSuccess)
                await Shell.Current.GoToAsync("//shift");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        var choice = await Shell.Current.DisplayActionSheet(UiStrings.Get("SaveDraftPrompt"), UiStrings.Cancel, UiStrings.Get("DeleteDraft"), UiStrings.Get("SaveDraft"));
        if (choice == UiStrings.Get("DeleteDraft"))
        {
            await importService.CancelAsync(SessionId, CancellationToken.None);
            await Shell.Current.GoToAsync("//shift");
        }
        else if (choice == UiStrings.Get("SaveDraft"))
            await Shell.Current.GoToAsync("//shift");
    }
}

[QueryProperty(nameof(ImageId), "ImageId")]
public sealed partial class PhotoViewModel(IPhotoAccess photoAccess) : ViewModelBase
{
    [ObservableProperty] private Guid imageId;
    [ObservableProperty] private ImageSource? image;

    public async Task LoadAsync()
    {
        try
        {
            var bytes = await photoAccess.ReadAsync(ImageId, CancellationToken.None);
            Image = ImageSource.FromStream(() => new MemoryStream(bytes, false));
        }
        catch (IOException)
        {
            Show(SafeErrorCode.ImageUnreadable);
        }
    }
}
