using NaukriMatcher.Api.Models;
using NaukriMatcher.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure CORS for Chrome Extension origins and localhost
builder.Services.AddCors(options =>
{
    options.AddPolicy("ExtensionAndLocalhostPolicy", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Configure HTTP client and services
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});

var app = builder.Build();

app.UseCors("ExtensionAndLocalhostPolicy");

// Health check endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "Naukri AI Candidate Matcher API",
    Framework = ".NET 10",
    Status = "Healthy",
    Timestamp = DateTime.UtcNow
}));

// Candidate search cards evaluation endpoint
app.MapPost("/api/evaluate", async (EvaluationRequest request, IGeminiService geminiService, CancellationToken ct) =>
{
    if (request == null || request.Candidates == null || request.Candidates.Count == 0)
    {
        return Results.BadRequest(new { error = "Request must contain JobDescription and a non-empty list of Candidates." });
    }

    var results = await geminiService.EvaluateCandidatesAsync(request.JobDescription, request.Candidates, ct);
    return Results.Ok(new EvaluationResponse(results));
});

// Deep full profile & resume career analysis endpoint
app.MapPost("/api/analyze-profile", async (ProfileAnalysisRequest request, IGeminiService geminiService, CancellationToken ct) =>
{
    if (request == null || request.Profile == null)
    {
        return Results.BadRequest(new { error = "Request must contain JobDescription and Candidate Profile data." });
    }

    var result = await geminiService.AnalyzeDetailedProfileAsync(request.JobDescription, request.Profile, ct);
    return Results.Ok(result);
});

app.Run();
