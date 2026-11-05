namespace AgvDispatch.WebHost.Engineering;

public static class EngineeringCenterApi
{
    public static void MapEngineeringCenterApi(this WebApplication app)
    {
        app.MapGet("/api/engineering/draft", (EngineeringStore s, CancellationToken ct) => s.Draft(ct));
        app.MapPost("/api/engineering/draft", (EngineeringSave q, EngineeringStore s, CancellationToken ct) => s.Save(q, ct));
        app.MapPost("/api/engineering/validate", (EngineeringPlan q) => EngineeringValidator.Validate(q));
        app.MapPost("/api/engineering/publish", (EngineeringPublish q, EngineeringStore s, CancellationToken ct) => s.Publish(q, ct));
        app.MapGet("/api/engineering/versions", (EngineeringStore s, CancellationToken ct) => s.Versions(ct));
        app.MapGet("/api/engineering/versions/{id}", (string id, EngineeringStore s, CancellationToken ct) => s.Version(id, ct));
        app.MapGet("/api/engineering/sample", (int? stations, int? vehicles) => EngineeringSample.Create(stations ?? 300, vehicles ?? 5));
        app.MapGet("/api/engineering/template-library", (EngineeringStore s, CancellationToken ct) => s.TemplateLibrary(ct));
        app.MapPost("/api/engineering/template-library/save", (EngineeringTemplate q, EngineeringStore s, CancellationToken ct) => s.SaveTemplate(q, ct));
        app.MapPost("/api/engineering/template-library/import", (EngineeringTemplateTransfer q, EngineeringStore s, CancellationToken ct) => s.ImportTemplate(q, ct));
        app.MapPost("/api/engineering/task-preview", (EngineeringTaskPreviewRequest q) => EngineeringTaskTemplates.Preview(q.Plan, q.StationId, q.TemplateId));
        app.MapPost("/api/engineering/simulations", (EngineeringSimulationRequest q, EngineeringStore s, CancellationToken ct) => s.Simulate(q, ct));
        app.MapGet("/api/engineering/reports/{id}", (string id, EngineeringStore s, CancellationToken ct) => s.Report(id, ct));
    }
}
