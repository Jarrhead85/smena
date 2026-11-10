namespace NurseShift.App.Features.History;

public partial class HistoryDetailPage : ContentPage
{
    private readonly HistoryDetailViewModel _viewModel;
    public HistoryDetailPage(HistoryDetailViewModel viewModel) { InitializeComponent(); BindingContext = _viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await _viewModel.LoadAsync(); }
}
