namespace NurseShift.App.Features.Shift;

public partial class AssignmentPage : ContentPage
{
    private readonly AssignmentViewModel _viewModel;
    private bool _initialized;

    public AssignmentPage(AssignmentViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_initialized)
        {
            _initialized = true;
            await _viewModel.InitializeAsync();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.CancelCommand.CanExecute(null))
            _viewModel.CancelCommand.Execute(null);
        return true;
    }
}
