namespace NurseShift.App.Features.Shift;

public partial class ShiftPage : ContentPage
{
    private readonly ShiftViewModel _viewModel;
    private IDispatcherTimer? _timer;

    public ShiftPage(ShiftViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void OnTick(object? sender, EventArgs e) => _ = _viewModel.LoadAsync();
}
