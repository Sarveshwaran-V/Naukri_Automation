using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NaukriMatcher.Api.Models;

namespace NaukriMatcher.Api.Services;

public class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<EvaluationResult>> EvaluateCandidatesAsync(
        JobDescriptionDto jobDescription,
        List<CandidateCardDto> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new List<EvaluationResult>();
        }

        string apiKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        // If API key is not configured or is placeholder, provide intelligent local fallback evaluation
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Gemini API key is not configured. Running fallback rule-based evaluation.");
            return EvaluateLocally(jobDescription, candidates);
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        try
        {
            var systemInstructionText = """
                You are an expert Technical Recruitment Assistant evaluating candidate search cards from Naukri.com against a Job Description.
                Analyze each candidate profile strictly against the job requirements.

                Knockout & Evaluation Rules:
                1. Mandatory Skills: Check if candidate's key skills, designation, or snippet demonstrate the required mandatory skills. Missing core skills drastically drops match.
                2. Notice Period Knockout & Anti-Hallucination Rules:
                   - If candidate's noticePeriod is "Not specified", empty, or unknown:
                     * You MUST NOT assume, guess, or invent a notice period under ANY circumstances.
                     * In your reasoning, you MUST explicitly state: "Notice period: Not specified".
                     * NEVER penalize, knock out, or reward a candidate based on notice period when it is "Not specified". Evaluate them purely on skills, role seniority, and experience.
                   - If candidate's noticePeriod is explicitly provided (e.g. 15 days, 30 days, 90 days, immediate):
                     * If MaxNoticePeriodDays is specified (> 0), check if candidate's notice period clearly exceeds MaxNoticePeriodDays (e.g. 90 days when max allowed is 30 or 15 days). If so, classify as SKIP or heavily penalize. Immediate or <= 15 days is preferred.
                3. Total Experience: Assess if total experience aligns with seniority implied by the job title and raw text.
                4. Recommendation Categories (Must be exactly one of these):
                   - "WORTH OPENING": Strong match (score >= 75%), possesses mandatory skills, acceptable notice period (or not specified).
                   - "MAYBE": Moderate match (score 50%-74%), partial skill overlap or slightly longer notice period.
                   - "SKIP": Poor match (score < 50%), missing primary mandatory skills, or knocked out by excessive notice period.
                5. Output Requirement:
                   Return a strictly valid JSON object matching this schema:
                   {
                     "evaluations": [
                       {
                         "candidateId": "string",
                         "matchPercentage": 85,
                         "recommendation": "WORTH OPENING",
                         "reasoning": "Concise 1-2 sentence justification highlighting skills and notice period (state 'Notice period: Not specified' if not provided)."
                       }
                     ]
                   }
                Do not include markdown fences outside the JSON. Return pure JSON only.
                """;

            var promptData = new
            {
                job = new
                {
                    title = jobDescription.Title,
                    rawDescription = jobDescription.RawText,
                    mandatorySkills = jobDescription.MandatorySkills,
                    maxNoticePeriodDays = jobDescription.MaxNoticePeriodDays
                },
                candidates = candidates.Select(c => new
                {
                    candidateId = c.CandidateId,
                    designation = c.Designation,
                    totalExperience = c.TotalExperience,
                    keySkills = c.KeySkills,
                    noticePeriod = c.NoticePeriod,
                    location = c.Location,
                    annualSalary = c.AnnualSalary,
                    snippet = c.Snippet
                }).ToList()
            };

            var userPromptText = $"Please evaluate the following candidates against the job description:\n\n{JsonSerializer.Serialize(promptData, JsonOptions)}";

            var geminiRequest = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = systemInstructionText } }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = userPromptText } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2,
                    responseMimeType = "application/json"
                }
            };

            var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var jsonContent = new StringContent(JsonSerializer.Serialize(geminiRequest, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(requestUrl, jsonContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini API call failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);

                // Fallback to local evaluation on API errors so user workflow isn't blocked
                return EvaluateLocally(jobDescription, candidates, $"[Gemini API Error {response.StatusCode} - Fallback]");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseGeminiResponse(responseBody, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while contacting Gemini API. Reverting to fallback evaluation.");
            return EvaluateLocally(jobDescription, candidates, "[Gemini Connection Failed - Fallback]");
        }
    }

    private List<EvaluationResult> ParseGeminiResponse(string rawJson, List<CandidateCardDto> originalCandidates)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidateArray) && candidateArray.GetArrayLength() > 0)
            {
                var firstCandidate = candidateArray[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var partText = parts[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(partText))
                    {
                        var cleaned = CleanJsonString(partText);

                        using var parsedContent = JsonDocument.Parse(cleaned);
                        if (parsedContent.RootElement.TryGetProperty("evaluations", out var evaluationsElement))
                        {
                            var results = JsonSerializer.Deserialize<List<EvaluationResult>>(evaluationsElement.GetRawText(), JsonOptions);
                            if (results != null && results.Count > 0)
                            {
                                return AlignWithCandidates(results, originalCandidates);
                            }
                        }
                        else if (parsedContent.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var results = JsonSerializer.Deserialize<List<EvaluationResult>>(cleaned, JsonOptions);
                            if (results != null && results.Count > 0)
                            {
                                return AlignWithCandidates(results, originalCandidates);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini output JSON structure. Raw: {RawJson}", rawJson);
        }

        return EvaluateLocally(new JobDescriptionDto("", "", new List<string>(), 0), originalCandidates, "[Parsing Fallback]");
    }

    private static string CleanJsonString(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }
        else if (text.StartsWith("```"))
        {
            text = text[3..];
        }

        if (text.EndsWith("```"))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    private static List<EvaluationResult> AlignWithCandidates(List<EvaluationResult> results, List<CandidateCardDto> candidates)
    {
        var resultMap = results.ToDictionary(r => r.CandidateId, r => r, StringComparer.OrdinalIgnoreCase);
        var finalResults = new List<EvaluationResult>();

        foreach (var c in candidates)
        {
            if (resultMap.TryGetValue(c.CandidateId, out var evaluated))
            {
                finalResults.Add(evaluated);
            }
            else
            {
                finalResults.Add(new EvaluationResult(
                    c.CandidateId,
                    50,
                    "MAYBE",
                    "Evaluated with partial profile match."
                ));
            }
        }

        return finalResults;
    }

    private static List<EvaluationResult> EvaluateLocally(JobDescriptionDto job, List<CandidateCardDto> candidates, string prefix = "")
    {
        var results = new List<EvaluationResult>();
        var mandatory = job.MandatorySkills ?? new List<string>();
        int maxNoticeDays = job.MaxNoticePeriodDays > 0 ? job.MaxNoticePeriodDays : 60;

        foreach (var candidate in candidates)
        {
            int score = 50;
            var reasons = new List<string>();

            // 1. Mandatory skills check
            int matchedSkills = 0;
            var candidateAllText = $"{candidate.Designation} {string.Join(" ", candidate.KeySkills ?? new())} {candidate.Snippet}".ToLowerInvariant();

            foreach (var skill in mandatory.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                if (candidateAllText.Contains(skill.Trim().ToLowerInvariant()))
                {
                    matchedSkills++;
                }
            }

            if (mandatory.Count > 0)
            {
                double skillRatio = (double)matchedSkills / mandatory.Count;
                score += (int)(skillRatio * 35);
                reasons.Add($"Matched {matchedSkills}/{mandatory.Count} mandatory skills");
            }
            else
            {
                score += 25;
            }

            // 2. Notice period knockout check
            int candidateNoticeDays = ExtractNoticePeriodDays(candidate.NoticePeriod);
            if (candidateNoticeDays >= 0)
            {
                if (candidateNoticeDays <= 15)
                {
                    score += 15;
                    reasons.Add(candidateNoticeDays == 0 ? "Immediate availability" : $"Quick availability ({candidateNoticeDays}d notice)");
                }
                else if (candidateNoticeDays > maxNoticeDays)
                {
                    score -= 35;
                    reasons.Add($"Notice period ({candidateNoticeDays}d) exceeds target max of {maxNoticeDays}d (Knockout)");
                }
                else
                {
                    reasons.Add($"Notice period {candidateNoticeDays}d within acceptable range");
                }
            }
            else
            {
                reasons.Add("Notice period: Not specified");
            }

            // Clamp score between 10 and 98
            score = Math.Clamp(score, 10, 98);

            string recommendation;
            if (score >= 75)
            {
                recommendation = "WORTH OPENING";
            }
            else if (score >= 50)
            {
                recommendation = "MAYBE";
            }
            else
            {
                recommendation = "SKIP";
            }

            string combinedReasoning = string.Join("; ", reasons);
            if (string.IsNullOrWhiteSpace(combinedReasoning))
            {
                combinedReasoning = $"Candidate profile evaluated against {job.Title ?? "role"}.";
            }

            if (!string.IsNullOrEmpty(prefix))
            {
                combinedReasoning = $"{prefix} {combinedReasoning}";
            }
            else
            {
                combinedReasoning = $"{combinedReasoning} (Local Demo Mode - set GEMINI_API_KEY for live AI)";
            }

            results.Add(new EvaluationResult(
                candidate.CandidateId,
                score,
                recommendation,
                combinedReasoning
            ));
        }

        return results;
    }

    public async Task<ProfileAnalysisResult> AnalyzeDetailedProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            return new ProfileAnalysisResult(
                "", "Unknown", 0, "DO NOT PROCEED", "No profile data provided.",
                new List<TechUsageAudit>(), new List<string>(), new List<string> { "Empty profile data." }, new List<InterviewQuestionDto>()
            );
        }

        string apiKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Gemini API key is not configured. Running fallback detailed profile audit.");
            return AnalyzeProfileLocally(jobDescription, profile);
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        try
        {
            var systemInstructionText = """
                You are a Principal Technical Recruiter and Engineering Architect conducting a rigorous Deep Career Audit on a candidate's full profile and resume against a Job Description.

                YOUR PRIMARY OBJECTIVE:
                Determine whether the candidate has ACTUALLY USED the required technologies in real production projects throughout their career, or whether they simply listed them as superficial keywords.

                Auditing Rules:
                1. Technology Career Authenticity:
                   - For each required technology (e.g. .NET Core, C#, Microservices, SQL, Entity Framework, Docker, etc.):
                   - Search their employment history (companies, roles, project descriptions, responsibilities).
                   - Determine: Did they actually build, maintain, or architect systems with this technology? Or is it just in a "Skills" tag list with zero project evidence?
                   - Assign DepthLevel: "Primary / Production" (core day-to-day tool), "Secondary / Ancillary" (used occasionally), or "Keyword Stuffed / Not Evident".
                2. Career Continuity & Seniority:
                   - Evaluate total tenure, career progression, stability, and whether their seniority matches the role title.
                3. Knockout Parameters & Anti-Hallucination:
                   - If the candidate's notice period is "Not specified", empty, or unknown, explicitly state "Notice period: Not specified" and do NOT invent or assume any notice period days.
                   - If an explicit notice period is given, evaluate against MaxNoticePeriodDays.
                4. Final Verdict Recommendation (Must be one of):
                   - "WORTH GOING FORWARD": Strong evidence of authentic hands-on usage of mandatory tech, within acceptable notice period, solid impact.
                   - "BORDERLINE / INTERVIEW WITH CAUTION": Partial hands-on depth, or border notice period, or missing 1 mandatory skill in actual projects.
                   - "DO NOT PROCEED": Core required tech is keyword stuffed without real project evidence, or severe knockout violation.
                5. Targeted HR Interview Questions & Answers (EXACTLY 5 Questions):
                   Generate exactly 5 tailored interview questions based directly on the candidate's actual projects, claims, and technologies from their resume.
                   IMPORTANT: The HR interviewer who will ask these questions is NOT a technical person. Therefore:
                   - For each question, provide an "expectedAnswer" written in clear, plain English (2-3 sentences) so the non-technical HR recruiter can understand what a solid, knowledgeable response sounds like.
                   - Provide "expectedKeywords": an array of 3-5 specific technical buzzwords or industry terms that the candidate should naturally say during their answer. The HR recruiter will listen for these keywords during the call.
                   - Question breakdown (strictly 2 Simple and 3 Advanced):
                     * Question 1: difficulty = "Simple" (Tests core day-to-day fundamentals and practical usage of their primary mandatory technology).
                     * Question 2: difficulty = "Simple" (Tests practical API design, tooling, or project workflow from their recent employment).
                     * Question 3: difficulty = "Advanced" (Tests system architecture, microservices, scalability, or distributed systems from their stated projects).
                     * Question 4: difficulty = "Advanced" (Tests database optimization, query tuning, concurrency, or caching from their work history).
                     * Question 5: difficulty = "Advanced" (Tests diagnosing a difficult production bug, handling outages, or architectural trade-offs).

                Output Format (Strict JSON only, no markdown wrappers):
                {
                  "candidateId": "string",
                  "candidateName": "string",
                  "overallSuitabilityScore": 85,
                  "recommendation": "WORTH GOING FORWARD",
                  "executiveSummary": "2-3 sentences summarizing whether they truly possess the required skills and if they are worth speaking to.",
                  "techAudits": [
                    {
                      "technology": "string",
                      "actuallyUsedInCareer": true,
                      "careerEvidence": "Used across 2 companies (3.5 years) developing REST microservices.",
                      "depthLevel": "Primary / Production"
                    }
                  ],
                  "strengths": ["string", "string"],
                  "redFlags": ["string", "string"],
                  "interviewQuestions": [
                    {
                      "question": "Clear question for HR to ask",
                      "difficulty": "Simple",
                      "expectedAnswer": "Plain-English explanation of what a good candidate should say so non-technical HR can verify.",
                      "expectedKeywords": ["Keyword1", "Keyword2", "Keyword3"]
                    }
                  ]
                }
                """;

            var promptData = new
            {
                targetJob = new
                {
                    title = jobDescription.Title,
                    rawRequirements = jobDescription.RawText,
                    mandatorySkills = jobDescription.MandatorySkills,
                    maxNoticePeriodDays = jobDescription.MaxNoticePeriodDays
                },
                candidateProfile = new
                {
                    candidateId = profile.CandidateId,
                    fullName = profile.FullName,
                    currentDesignation = profile.CurrentDesignation,
                    totalExperience = profile.TotalExperience,
                    noticePeriod = profile.NoticePeriod,
                    currentLocation = profile.CurrentLocation,
                    annualSalary = profile.AnnualSalary,
                    keySkills = profile.KeySkills,
                    workHistory = profile.Experiences?.Select(e => new
                    {
                        company = e.Company,
                        designation = e.Designation,
                        duration = e.Duration,
                        projectResponsibilities = e.Description
                    }).ToList(),
                    resumeHeadline = profile.ResumeHeadline,
                    fullRawProfileText = profile.FullProfileRawText?.Length > 4000 ? profile.FullProfileRawText[..4000] : profile.FullProfileRawText
                }
            };

            var userPromptText = $"Please conduct a deep career audit on this candidate's profile:\n\n{JsonSerializer.Serialize(promptData, JsonOptions)}";

            var geminiRequest = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = systemInstructionText } }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = userPromptText } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2,
                    responseMimeType = "application/json"
                }
            };

            var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var jsonContent = new StringContent(JsonSerializer.Serialize(geminiRequest, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(requestUrl, jsonContent, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini profile analysis failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                return AnalyzeProfileLocally(jobDescription, profile, $"[Gemini API Error {response.StatusCode} - Fallback]");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseProfileAnalysisResponse(responseBody, jobDescription, profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception contacting Gemini API for profile analysis. Reverting to fallback audit.");
            return AnalyzeProfileLocally(jobDescription, profile, "[Gemini Connection Failed - Fallback]");
        }
    }

    private ProfileAnalysisResult ParseProfileAnalysisResponse(string rawJson, JobDescriptionDto jobDescription, DetailedCandidateProfileDto profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidateArray) && candidateArray.GetArrayLength() > 0)
            {
                var firstCandidate = candidateArray[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var partText = parts[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(partText))
                    {
                        var cleaned = CleanJsonString(partText);
                        try
                        {
                            var result = JsonSerializer.Deserialize<ProfileAnalysisResult>(cleaned, JsonOptions);
                            if (result != null && result.InterviewQuestions != null && result.InterviewQuestions.Count > 0)
                            {
                                return result with
                                {
                                    CandidateId = string.IsNullOrWhiteSpace(result.CandidateId) ? profile.CandidateId : result.CandidateId,
                                    CandidateName = string.IsNullOrWhiteSpace(result.CandidateName) ? profile.FullName : result.CandidateName
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Direct deserialize of ProfileAnalysisResult failed; attempting fallback parser.");
                        }

                        // Robust fallback parser in case Gemini returned string array for interviewQuestions or partial JSON
                        return ParseProfileAnalysisFallback(cleaned, profile, jobDescription);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini profile analysis output. Raw: {RawJson}", rawJson);
        }

        return AnalyzeProfileLocally(jobDescription, profile, "[Parsing Fallback]");
    }

    private ProfileAnalysisResult ParseProfileAnalysisFallback(string cleanedJson, DetailedCandidateProfileDto profile, JobDescriptionDto jobDescription)
    {
        try
        {
            using var doc = JsonDocument.Parse(cleanedJson);
            var root = doc.RootElement;

            string candidateId = root.TryGetProperty("candidateId", out var cidEl) && cidEl.GetString() is string cid && !string.IsNullOrWhiteSpace(cid) ? cid : profile.CandidateId;
            string candidateName = root.TryGetProperty("candidateName", out var cnameEl) && cnameEl.GetString() is string cname && !string.IsNullOrWhiteSpace(cname) ? cname : profile.FullName;
            int score = root.TryGetProperty("overallSuitabilityScore", out var sEl) && sEl.TryGetInt32(out var sc) ? sc : 50;
            string rec = root.TryGetProperty("recommendation", out var recEl) && recEl.GetString() is string r ? r : "BORDERLINE / INTERVIEW WITH CAUTION";
            string summary = root.TryGetProperty("executiveSummary", out var sumEl) && sumEl.GetString() is string sm ? sm : "Profile evaluated against requirements.";

            var techAudits = new List<TechUsageAudit>();
            if (root.TryGetProperty("techAudits", out var taEl) && taEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in taEl.EnumerateArray())
                {
                    string tech = item.TryGetProperty("technology", out var tEl) ? (tEl.GetString() ?? "") : "";
                    bool used = item.TryGetProperty("actuallyUsedInCareer", out var uEl) && uEl.GetBoolean();
                    string ev = item.TryGetProperty("careerEvidence", out var eEl) ? (eEl.GetString() ?? "") : "";
                    string depth = item.TryGetProperty("depthLevel", out var dEl) ? (dEl.GetString() ?? "") : "Secondary / Ancillary";
                    if (!string.IsNullOrWhiteSpace(tech))
                    {
                        techAudits.Add(new TechUsageAudit(tech, used, ev, depth));
                    }
                }
            }

            var strengths = new List<string>();
            if (root.TryGetProperty("strengths", out var strEl) && strEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in strEl.EnumerateArray())
                {
                    if (s.GetString() is string strVal && !string.IsNullOrWhiteSpace(strVal)) strengths.Add(strVal);
                }
            }

            var redFlags = new List<string>();
            if (root.TryGetProperty("redFlags", out var rfEl) && rfEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var rfItem in rfEl.EnumerateArray())
                {
                    if (rfItem.GetString() is string rfVal && !string.IsNullOrWhiteSpace(rfVal)) redFlags.Add(rfVal);
                }
            }

            var questions = new List<InterviewQuestionDto>();
            if (root.TryGetProperty("interviewQuestions", out var iqEl) && iqEl.ValueKind == JsonValueKind.Array)
            {
                int qIndex = 0;
                foreach (var q in iqEl.EnumerateArray())
                {
                    if (q.ValueKind == JsonValueKind.Object)
                    {
                        string qText = q.TryGetProperty("question", out var qt) ? (qt.GetString() ?? "") : "";
                        string diff = q.TryGetProperty("difficulty", out var qd) ? (qd.GetString() ?? "") : (qIndex < 2 ? "Simple" : "Advanced");
                        string ans = q.TryGetProperty("expectedAnswer", out var qa) ? (qa.GetString() ?? "") : "Candidate should explain practical hands-on experience and implementation.";
                        var keywords = new List<string>();
                        if (q.TryGetProperty("expectedKeywords", out var qk) && qk.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var kw in qk.EnumerateArray())
                            {
                                if (kw.GetString() is string kwStr && !string.IsNullOrWhiteSpace(kwStr)) keywords.Add(kwStr);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(qText))
                        {
                            questions.Add(new InterviewQuestionDto(qText, diff, ans, keywords));
                            qIndex++;
                        }
                    }
                    else if (q.ValueKind == JsonValueKind.String && q.GetString() is string strQ && !string.IsNullOrWhiteSpace(strQ))
                    {
                        questions.Add(new InterviewQuestionDto(
                            strQ,
                            qIndex < 2 ? "Simple" : "Advanced",
                            "Candidate should explain hands-on technical architecture and production decisions.",
                            new List<string> { "Practical Implementation", "Clean Architecture", "Production Reliability" }
                        ));
                        qIndex++;
                    }
                }
            }

            if (questions.Count == 0)
            {
                questions = GenerateDefaultInterviewQuestions(jobDescription.MandatorySkills);
            }

            return new ProfileAnalysisResult(
                candidateId,
                candidateName,
                score,
                rec,
                summary,
                techAudits,
                strengths,
                redFlags,
                questions
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ParseProfileAnalysisFallback manual parse failed. Reverting to local audit.");
            return AnalyzeProfileLocally(jobDescription, profile, "[Parsing Fallback]");
        }
    }

    private static ProfileAnalysisResult AnalyzeProfileLocally(JobDescriptionDto job, DetailedCandidateProfileDto profile, string prefix = "")
    {
        var mandatory = job.MandatorySkills ?? new List<string>();
        var experiences = profile.Experiences ?? new List<WorkExperienceDto>();
        var fullHistoryText = string.Join(" ", experiences.Select(e => $"{e.Company} {e.Designation} {e.Description}")).ToLowerInvariant();
        var rawText = (profile.FullProfileRawText ?? "").ToLowerInvariant();
        var allText = $"{fullHistoryText} {rawText}";

        var techAudits = new List<TechUsageAudit>();
        int authenticTechCount = 0;
        var strengths = new List<string>();
        var redFlags = new List<string>();

        foreach (var skill in mandatory.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var skillLower = skill.Trim().ToLowerInvariant();
            bool inJobHistory = fullHistoryText.Contains(skillLower);
            bool inSkillsList = (profile.KeySkills ?? new List<string>()).Any(k => k.ToLowerInvariant().Contains(skillLower));

            if (inJobHistory)
            {
                authenticTechCount++;
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    true,
                    $"Explicitly evidenced in work history across role descriptions.",
                    "Primary / Production"
                ));
            }
            else if (inSkillsList)
            {
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    false,
                    "Listed in Key Skills section, but lacks explicit project descriptions in employment history.",
                    "Keyword Stuffed / Not Evident"
                ));
                redFlags.Add($"Skill '{skill.Trim()}' is listed in tags but not elaborated in project bullet points.");
            }
            else
            {
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    false,
                    "Not found in project history or key skills.",
                    "Keyword Stuffed / Not Evident"
                ));
            }
        }

        // Notice period check
        int noticeDays = ExtractNoticePeriodDays(profile.NoticePeriod);
        int maxNotice = job.MaxNoticePeriodDays > 0 ? job.MaxNoticePeriodDays : 30;

        if (noticeDays >= 0)
        {
            if (noticeDays <= 15)
            {
                strengths.Add(noticeDays == 0 ? "Immediate availability (0 days notice)." : $"Quick availability ({noticeDays} days notice period).");
            }
            else if (noticeDays > maxNotice)
            {
                redFlags.Add($"Notice period ({noticeDays} days) exceeds maximum requirement of {maxNotice} days.");
            }
        }
        else
        {
            strengths.Add("Notice period: Not specified on profile (verify with candidate during screening).");
        }

        // Score calculation
        int score = 50;
        if (mandatory.Count > 0)
        {
            score += (int)(((double)authenticTechCount / mandatory.Count) * 40);
        }
        else
        {
            score += 30;
        }

        if (noticeDays >= 0 && noticeDays > maxNotice) score -= 25;
        score = Math.Clamp(score, 15, 95);

        string recommendation = score >= 75 ? "WORTH GOING FORWARD"
            : score >= 50 ? "BORDERLINE / INTERVIEW WITH CAUTION"
            : "DO NOT PROCEED";

        string noticeDisplay = string.IsNullOrWhiteSpace(profile.NoticePeriod) || profile.NoticePeriod.Contains("not specified", StringComparison.OrdinalIgnoreCase) 
            ? "Not specified" 
            : profile.NoticePeriod;

        var summary = $"Candidate has {profile.TotalExperience} of experience. Audited {mandatory.Count} required technologies with {authenticTechCount} verified in direct project descriptions. Notice period: {noticeDisplay}.";
        if (!string.IsNullOrEmpty(prefix)) summary = $"{prefix} {summary}";

        var questions = GenerateDefaultInterviewQuestions(mandatory);

        return new ProfileAnalysisResult(
            profile.CandidateId,
            string.IsNullOrWhiteSpace(profile.FullName) ? "Candidate" : profile.FullName,
            score,
            recommendation,
            summary,
            techAudits,
            strengths,
            redFlags,
            questions
        );
    }

    private static List<InterviewQuestionDto> GenerateDefaultInterviewQuestions(List<string>? mandatorySkills)
    {
        var mandatory = mandatorySkills ?? new List<string>();
        var primaryTech = mandatory.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? ".NET Core / C#";
        var secondTech = mandatory.Skip(1).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "REST APIs";
        var dbTech = mandatory.FirstOrDefault(s => s.Contains("sql", StringComparison.OrdinalIgnoreCase) || s.Contains("data", StringComparison.OrdinalIgnoreCase) || s.Contains("mongo", StringComparison.OrdinalIgnoreCase) || s.Contains("postgres", StringComparison.OrdinalIgnoreCase)) ?? "Relational Database / SQL";

        return new List<InterviewQuestionDto>
        {
            new InterviewQuestionDto(
                $"Can you describe your day-to-day experience with {primaryTech} and how dependency injection / service lifetimes are managed in your code?",
                "Simple",
                $"The candidate should explain the core principles of {primaryTech} and describe how services are registered (Transient creates a new instance each time, Scoped creates one per request, Singleton creates one for the entire application).",
                new List<string> { "Dependency Injection", "Transient", "Scoped", "Singleton", "Constructor Injection" }
            ),
            new InterviewQuestionDto(
                $"How do you design, validate, and test secure RESTful APIs using {secondTech} in your projects?",
                "Simple",
                "The candidate should describe standard HTTP verbs (GET, POST, PUT, DELETE), proper HTTP status codes (200, 201, 400, 404, 500), model validation, and using tools like Postman, Swagger/OpenAPI, or automated tests.",
                new List<string> { "HTTP Verbs", "Status Codes (200, 400, 500)", "Model Validation", "Swagger / OpenAPI", "Unit Testing" }
            ),
            new InterviewQuestionDto(
                "In your work with microservices or distributed systems, how do you handle service-to-service communication, eventual consistency, and network failures?",
                "Advanced",
                "The candidate should explain whether they use synchronous REST/gRPC or asynchronous message brokers (RabbitMQ/Kafka). They should discuss handling distributed transactions with eventual consistency and resilience patterns like Circuit Breakers and retries.",
                new List<string> { "Message Broker (Kafka/RabbitMQ)", "Eventual Consistency", "Circuit Breaker", "gRPC / REST", "Resilience / Retries" }
            ),
            new InterviewQuestionDto(
                $"Walk me through how you diagnose and optimize database query bottlenecks using {dbTech}. How do you ensure high query performance?",
                "Advanced",
                $"The candidate should explain analyzing slow queries using execution plans, adding appropriate indexes (clustered/non-clustered), resolving N+1 query problems in ORMs, and utilizing caching (such as Redis) to reduce database load.",
                new List<string> { "Query Execution Plan", "Database Indexing", "N+1 Problem", "Connection Pooling", "Caching (Redis)" }
            ),
            new InterviewQuestionDto(
                "Describe a critical production bug, memory leak, or concurrency issue you investigated. How did you identify the root cause and ensure zero downtime?",
                "Advanced",
                "The candidate should walk through a real incident systematically: checking application logs and metrics, analyzing thread dumps or memory allocations, applying an immediate mitigation, and deploying a permanent fix with zero downtime.",
                new List<string> { "Root Cause Analysis", "Application Logs / APM", "Memory Leak / Deadlock", "Concurrency / Threading", "Zero-Downtime Deployment" }
            )
        };
    }

    private static int ExtractNoticePeriodDays(string? noticeText)
    {
        if (string.IsNullOrWhiteSpace(noticeText)) return -1;

        var lower = noticeText.ToLowerInvariant().Trim();
        if (lower.Contains("not specified") || lower.Contains("not disclosed") || lower.Contains("unknown") || lower.Contains("n/a"))
            return -1;

        if (lower.Contains("immediate") || lower.Contains("serving notice")) return 0;
        if (lower.Contains("15 days")) return 15;
        if (lower.Contains("1 month") || lower.Contains("30 days")) return 30;
        if (lower.Contains("2 month") || lower.Contains("60 days")) return 60;
        if (lower.Contains("3 month") || lower.Contains("90 days")) return 90;

        var match = Regex.Match(lower, @"(\d+)\s*(day|month|mth)");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int count))
            {
                if (match.Groups[2].Value.StartsWith("m"))
                {
                    return count * 30;
                }
                return count;
            }
        }

        return -1;
    }
}

