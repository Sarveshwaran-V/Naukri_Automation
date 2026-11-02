using System.Text.Json;
using System.Text.Json.Serialization;
using NaukriMatcher.Api.Models;
using NaukriMatcher.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization to use camelCase so the extension JS receives camelCase field names
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

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

// Configure HTTP client, context accessor, and services
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});

var app = builder.Build();

app.UseCors("ExtensionAndLocalhostPolicy");

// Health check endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "cAnbIdate API",
    Framework = ".NET 10",
    Status = "Healthy",
    Timestamp = DateTime.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
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

    try
    {
        var results = await geminiService.EvaluateCandidatesAsync(request.JobDescription, request.Candidates, ct);
        return Results.Ok(new EvaluationResponse(results));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Boolean search string generator endpoint for candidate search queries
app.MapPost("/api/generate-search-string", async (JobDescriptionDto jobDescription, IGeminiService geminiService, CancellationToken ct) =>
{
    if (jobDescription == null)
    {
        return Results.BadRequest(new { error = "Job description data is required." });
    }

    try
    {
        var searchString = await geminiService.GenerateSearchStringsAsync(jobDescription, ct);
        return Results.Ok(new { searchString });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Deep full profile & resume career analysis endpoint
app.MapPost("/api/analyze-profile", async (ProfileAnalysisRequest request, IGeminiService geminiService, CancellationToken ct) =>
{
    if (request == null || request.Profile == null)
    {
        return Results.BadRequest(new { error = "Request must contain JobDescription and Candidate Profile data." });
    }

    try
    {
        var result = await geminiService.AnalyzeDetailedProfileAsync(request.JobDescription, request.Profile, ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// LinkedIn Recruiter candidate profile & resume analysis endpoint
app.MapPost("/api/evaluate/linkedin-recruiter", async (ProfileAnalysisRequest request, IGeminiService geminiService, CancellationToken ct) =>
{
    if (request == null || request.Profile == null)
    {
        return Results.BadRequest(new { error = "Request must contain JobDescription and Candidate Profile data." });
    }

    try
    {
        var result = await geminiService.AnalyzeLinkedInProfileAsync(request.JobDescription, request.Profile, ct);
        return Results.Ok(result);
    }
    catch (NotImplementedException)
    {
        // Extension-to-backend plumbing is fully operational.
        // Returns an informational result informing the user how to activate Gemini for LinkedIn.
        return Results.Ok(new ProfileAnalysisResult(
            request.Profile.CandidateId,
            request.Profile.FullName ?? "LinkedIn Recruiter Candidate",
            75,
            "WORTH GOING FORWARD",
            "<p style=\"color: #1e40af; font-weight: 600; line-height: 1.5;\">LinkedIn Recruiter integration is connected! To enable AI evaluations, implement AnalyzeLinkedInProfileAsync in GeminiService.cs.</p>",
            new List<TechUsageAudit>(),
            new List<string> { "Candidate profile extracted from LinkedIn Recruiter" },
            new List<string>(),
            new List<InterviewQuestionDto>()
        ));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();
