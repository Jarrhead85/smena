namespace NurseShift.App.Features.Import;

public partial class ReviewPage : ContentPage
{
    private readonly ReviewViewModel _viewModel;
    public ReviewPage(ReviewViewModel viewModel) { InitializeComponent(); BindingContext = _viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await _viewModel.LoadAsync(); }
    protected override bool OnBackButtonPressed() { if (_viewModel.CancelCommand.CanExecute(null)) _viewModel.CancelCommand.Execute(null); return true; }
}
