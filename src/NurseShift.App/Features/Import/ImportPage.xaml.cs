namespace NurseShift.App.Features.Import;

public partial class ImportPage : ContentPage
{
    private readonly ImportViewModel _viewModel;
    private bool _initialized;
    public ImportPage(ImportViewModel viewModel) { InitializeComponent(); BindingContext = _viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); if (!_initialized) { _initialized = true; await _viewModel.InitializeAsync(); } }
}
