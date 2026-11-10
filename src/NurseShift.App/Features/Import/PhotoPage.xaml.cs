namespace NurseShift.App.Features.Import;

public partial class PhotoPage : ContentPage
{
    private readonly PhotoViewModel _viewModel;
    private double _startScale = 1;
    public PhotoPage(PhotoViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinchUpdated;
        PhotoContainer.GestureRecognizers.Add(pinch);
    }
    protected override async void OnAppearing() { base.OnAppearing(); await _viewModel.LoadAsync(); }
    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started) _startScale = Photo.Scale;
        else if (e.Status == GestureStatus.Running) Photo.Scale = Math.Clamp(_startScale * e.Scale, 1, 5);
    }
    private async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
