using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class NavigationTestApi
{
    public static void MapNavigationTestApi(this WebApplication app)
    {
        app.MapGet("/api/navigation-test",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestStatus>("GetNavigationTest",new{},ct));
        app.MapPost("/api/navigation-test/preview",async(NavigationTestQuery q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestPreview>("PreviewNavigationTest",q,ct));
        app.MapPost("/api/navigation-test/submit",async(NavigationTestSubmit q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("SubmitNavigationTest",q,ct));
        app.MapPost("/api/navigation-test/cancel",async(NavigationTestCancel q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<NavigationTestRecord>("CancelNavigationTest",q,ct));
    }
}
