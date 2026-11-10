using NurseShift.App.Features.History;
using NurseShift.App.Features.Import;
using NurseShift.App.Features.Shift;
using NurseShift.App.Services;

namespace NurseShift.App;

public partial class AppShell : Shell
{
    public AppShell(DeepLinkService deepLinks)
    {
        InitializeComponent();
        Routing.RegisterRoute("new-shift", typeof(NewShiftPage));
        Routing.RegisterRoute("assignment", typeof(AssignmentPage));
        Routing.RegisterRoute("import", typeof(ImportPage));
        Routing.RegisterRoute("review", typeof(ReviewPage));
        Routing.RegisterRoute("photo", typeof(PhotoPage));
        Routing.RegisterRoute("history-detail", typeof(HistoryDetailPage));
        deepLinks.AssignmentRequested += async (_, id) => await Dispatcher.DispatchAsync(() => GoToAsync("assignment", new Dictionary<string, object> { ["AssignmentId"] = id }));
    }
}
