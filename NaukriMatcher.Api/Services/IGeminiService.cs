using NaukriMatcher.Api.Models;

namespace NaukriMatcher.Api.Services;

public interface IGeminiService
{
    Task<List<EvaluationResult>> EvaluateCandidatesAsync(JobDescriptionDto jobDescription, List<CandidateCardDto> candidates, CancellationToken cancellationToken = default);
    Task<ProfileAnalysisResult> AnalyzeDetailedProfileAsync(JobDescriptionDto jobDescription, DetailedCandidateProfileDto profile, CancellationToken cancellationToken = default);
    Task<ProfileAnalysisResult> AnalyzeLinkedInProfileAsync(JobDescriptionDto jobDescription, DetailedCandidateProfileDto profile, CancellationToken cancellationToken = default);
}
