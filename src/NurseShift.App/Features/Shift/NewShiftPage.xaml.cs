namespace NurseShift.App.Features.Shift;

public partial class NewShiftPage : ContentPage
{
    public NewShiftPage(NewShiftViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
