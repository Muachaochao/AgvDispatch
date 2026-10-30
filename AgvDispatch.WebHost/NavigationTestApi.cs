using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class NavigationTestApi
{
    public static void MapNavigationTestApi(this WebApplication app)
    {
        app.MapPost("/api/navigation-test/detour/preview",async(NavigationTestDetourQuery q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestDetourPreview>("PreviewNavigationTestDetour",q,ct));
        app.MapPost("/api/navigation-test/detour/confirm",async(NavigationTestDetourConfirm q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("ConfirmNavigationTestDetour",q,ct));
        app.MapPost("/api/navigation-test/detour/continue",async(NavigationTestDetourContinue q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("ContinueNavigationTestDetour",q,ct));
        app.MapGet("/api/navigation-test/fork",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<ForkTestStatus>("GetForkTest",new{},ct));
        app.MapPost("/api/navigation-test/fork",async(ForkTestSubmit q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<ForkTestRecord>(q.Action=="Stop"?"StopForkTest":"SubmitForkTest",q,ct));
        app.MapGet("/api/navigation-test/jack",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<JackTestStatus>("GetJackTest",new{},ct));
        app.MapPost("/api/navigation-test/jack",async(JackTestSubmit q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<JackTestRecord>("SubmitJackTest",q,ct));
        app.MapGet("/api/navigation-test",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestStatus>("GetNavigationTest",new{},ct));
        app.MapGet("/api/navigation-test/stations",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestStations>("GetNavigationTestStations",new{},ct));
        app.MapPost("/api/navigation-test/preview",async(NavigationTestQuery q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestPreview>("PreviewNavigationTest",q,ct));
        app.MapPost("/api/navigation-test/submit",async(NavigationTestSubmit q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("SubmitNavigationTest",q,ct));
        app.MapPost("/api/navigation-test/cancel",async(NavigationTestCancel q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("CancelNavigationTest",q,ct));
        app.MapPost("/api/navigation-test/resume",async(NavigationTestResume q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("ResumeNavigationTest",q,ct));
    }
}
