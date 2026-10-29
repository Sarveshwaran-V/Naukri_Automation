using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using NaukriMatcher.Api.Models;

namespace NaukriMatcher.Api.Services;

public class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GeminiService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    private string ResolveApiKey()
    {
        // 1. Check client request header X-Gemini-Api-Key
        var context = _httpContextAccessor?.HttpContext;
        if (context != null && context.Request.Headers.TryGetValue("X-Gemini-Api-Key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey))
        {
            var keyStr = headerKey.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(keyStr))
            {
                return keyStr;
            }
        }

        // 2. Check configuration or environment variable
        string configKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(configKey) && !configKey.Contains("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            return configKey.Trim();
        }

        return string.Empty;
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

        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini API key is required. Please enter your Google Gemini API Key in the extension Settings (⚙) or set GEMINI_API_KEY in the backend appsettings.json.");
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        var systemInstructionText = """
            You are an expert Technical Recruitment Assistant evaluating candidate search cards from Naukri.com against a Job Description.
            Analyze each candidate profile strictly against the job requirements.

            Skills Prioritization & Knockout Rules:
            1. Mandatory Skills (HIGHEST PRIORITY - 80% Weight):
               - Candidates MUST demonstrate the required mandatory skills. Mandatory skills are hard requirements.
               - Missing core mandatory skills drastically drops the match score (must not exceed 50%) and should classify the candidate as "SKIP" or "MAYBE".
            2. Preferred Skills (SECONDARY PRIORITY - 20% Weight):
               - Preferred skills act as value-add bonus points.
               - Missing preferred skills must NEVER cause a candidate to be rejected or heavily penalized if they possess all mandatory skills.
            3. Notice Period Knockout & Anti-Hallucination Rules:
               - If candidate's noticePeriod is "Not specified", empty, or unknown:
                 * You MUST NOT assume, guess, or invent a notice period under ANY circumstances.
                 * In your reasoning, you MUST explicitly state: "Notice period: Not specified".
                 * NEVER penalize, knock out, or reward a candidate based on notice period when it is "Not specified". Evaluate them purely on skills, role seniority, and experience.
               - If candidate's noticePeriod is explicitly provided (e.g. 15 days, 30 days, 90 days, immediate):
                 * If MaxNoticePeriodDays is specified (> 0), check if candidate's notice period clearly exceeds MaxNoticePeriodDays (e.g. 90 days when max allowed is 30 or 15 days). If so, classify as SKIP or heavily penalize. Immediate or <= 15 days is preferred.
            4. Total Experience: Assess if total experience aligns with seniority implied by the job title and raw text.
            5. Recommendation Categories (Must be exactly one of these):
               - "WORTH OPENING": Strong match (score >= 75%), possesses all mandatory skills, acceptable notice period (or not specified).
               - "MAYBE": Moderate match (score 50%-74%), satisfies partial mandatory skills or slightly longer notice period.
               - "SKIP": Poor match (score < 50%), missing core mandatory skills, or knocked out by excessive notice period.
            6. Output Requirement:
               Return a strictly valid JSON object matching this schema:
               {
                 "evaluations": [
                   {
                     "candidateId": "string",
                     "matchPercentage": 85,
                     "recommendation": "WORTH OPENING",
                     "reasoning": "Concise 1-2 sentence justification highlighting mandatory skills and notice period (state 'Notice period: Not specified' if not provided)."
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
                preferredSkills = jobDescription.PreferredSkills ?? new List<string>(),
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

            string userFriendlyMessage = $"Gemini API error ({response.StatusCode})";
            try
            {
                using var errDoc = JsonDocument.Parse(errorBody);
                if (errDoc.RootElement.TryGetProperty("error", out var errObj) &&
                    errObj.TryGetProperty("message", out var msgProp))
                {
                    userFriendlyMessage = $"Gemini API: {msgProp.GetString()}";
                }
            }
            catch {}

            throw new InvalidOperationException(userFriendlyMessage);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseGeminiResponse(responseBody, candidates);
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
            _logger.LogError(ex, "Failed to parse Gemini output JSON structure. Raw: {RawJson}", rawJson);
            throw new InvalidOperationException("Failed to parse Gemini candidate evaluations response. Please try again.");
        }

        throw new InvalidOperationException("Gemini returned a response without valid evaluations.");
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

    private static List<EvaluationResult> AlignWithCandidates(List<EvaluationResult> evaluatedList, List<CandidateCardDto> originalCandidates)
    {
        var resultMap = evaluatedList.ToDictionary(r => r.CandidateId, r => r);
        var finalResults = new List<EvaluationResult>();

        foreach (var c in originalCandidates)
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

    public async Task<ProfileAnalysisResult> AnalyzeDetailedProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile), "Candidate profile data cannot be empty.");
        }

        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini API key is required. Please enter your Google Gemini API Key in the extension Settings (⚙) or set GEMINI_API_KEY in the backend appsettings.json.");
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        var systemInstructionText = """
            You are a Principal Technical Recruiter and Engineering Architect conducting a rigorous Deep Career Audit on a candidate's full profile and resume against a Job Description.

            YOUR PRIMARY OBJECTIVE:
            Determine whether the candidate has ACTUALLY USED the required technologies in real production projects throughout their career, or whether they simply listed them as superficial keywords.

            Auditing Rules:
            1. Mandatory Skills Justification (CRITICAL MANDATORY REQUIREMENT):
               - YOU MUST EVALUATE EVERY SINGLE MANDATORY SKILL LISTED IN targetJob.mandatorySkills.
               - For EACH mandatory skill, provide an explicit, short, and concise explanation justifying:
                 * WHY the candidate is good at it (e.g. hands-on production use, specific companies/projects, duration, concrete deliverables from work history or attached CV).
                 * OR WHY the candidate is NOT fit in that skill (e.g. only listed in skill tags without project context, no evidence of practical usage in employment history/resume, or only academic/basic). Keep the non-fit explanation concise without going overly in-depth.
               - This mandatory skill justification MUST appear in TWO places:
                 a) AT THE VERY BEGINNING OF executiveSummary: Start executiveSummary with a structured breakdown for all mandatory skills:
                    "Mandatory Skills Evaluation:
                    • [Skill 1]: [Concise justification why candidate is good or not fit]
                    • [Skill 2]: [Concise justification why candidate is good or not fit]
                    ...
                    Overall Assessment: [1-2 sentences on overall role fit against job description]."
                 b) IN techAudits ARRAY: Every mandatory skill MUST be the first items in the techAudits array with technology, actuallyUsedInCareer (true if good/fit, false if not fit), depthLevel, and careerEvidence containing the concise justification.
               - Weighting: If ANY mandatory skill lacks authentic project evidence or is not fit, heavily penalize the score (cap at 55% or lower) and recommend "BORDERLINE / INTERVIEW WITH CAUTION" or "DO NOT PROCEED".

            2. Preferred Skills (Secondary Priority - 20% Weight):
               - Check for preferred/secondary skills. Evidence acts as a positive booster.
               - Do not heavily penalize or reject if preferred skills are missing, as long as all mandatory skills are satisfied.

            3. Career Continuity & Seniority:
               - Evaluate total tenure, career progression, stability, and whether their seniority matches the role title.

            4. Knockout Parameters & Anti-Hallucination:
               - If the candidate's notice period is "Not specified", empty, or unknown, explicitly state "Notice period: Not specified" and do NOT invent or assume any notice period days.
               - If an explicit notice period is given, evaluate against MaxNoticePeriodDays.

            5. Final Verdict Recommendation (Must be one of):
               - "WORTH GOING FORWARD": Strong evidence of authentic hands-on usage of all mandatory skills, within acceptable notice period, solid impact.
               - "BORDERLINE / INTERVIEW WITH CAUTION": Partial hands-on depth in mandatory skills, or border notice period, or missing 1 mandatory skill in actual projects.
               - "DO NOT PROCEED": Core mandatory tech is keyword stuffed without real project evidence, or knockout violation.

            6. Targeted HR Interview Questions & Answers (EXACTLY 5 Questions):
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
              "executiveSummary": "Mandatory Skills Evaluation:\n• C#: Good fit — 5+ years building production REST APIs and enterprise services at Company A and B.\n• .NET Core: Good fit — architected microservices using .NET 8/9 with Docker.\n• SQL: Good fit — hands-on database optimization and complex queries.\n\nOverall Assessment: Candidate demonstrates strong hands-on seniority matching the Senior .NET Engineer role requirements.",
              "techAudits": [
                {
                  "technology": "C#",
                  "actuallyUsedInCareer": true,
                  "careerEvidence": "Hands-on production use for 5+ years across 2 companies building REST microservices.",
                  "depthLevel": "Primary / Production"
                },
                {
                  "technology": "Docker",
                  "actuallyUsedInCareer": false,
                  "careerEvidence": "Listed in skills tags only; no direct containerization tasks or deliverables evident in project history.",
                  "depthLevel": "Keyword Stuffed / Not Evident"
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
                preferredSkills = jobDescription.PreferredSkills ?? new List<string>(),
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
                fullRawProfileText = profile.FullProfileRawText?.Length > 50000 ? profile.FullProfileRawText[..50000] : profile.FullProfileRawText
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

            string userFriendlyMessage = $"Gemini API error ({response.StatusCode})";
            try
            {
                using var errDoc = JsonDocument.Parse(errorBody);
                if (errDoc.RootElement.TryGetProperty("error", out var errObj) &&
                    errObj.TryGetProperty("message", out var msgProp))
                {
                    userFriendlyMessage = $"Gemini API: {msgProp.GetString()}";
                }
            }
            catch {}

            throw new InvalidOperationException(userFriendlyMessage);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseProfileAnalysisResponse(responseBody, jobDescription, profile);
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
                                    CandidateName = string.IsNullOrWhiteSpace(result.CandidateName) ? profile.FullName : result.CandidateName,
                                    ExecutiveSummary = FormatExecutiveSummaryHtml(result.ExecutiveSummary)
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Direct deserialize of ProfileAnalysisResult failed; attempting fallback parser.");
                        }

                        return ParseProfileAnalysisFallback(cleaned, profile, jobDescription);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ProfileAnalysisResult. Raw: {RawJson}", rawJson);
            throw new InvalidOperationException("Failed to parse Gemini profile analysis response. Please try again.");
        }

        throw new InvalidOperationException("Gemini returned a profile analysis response that could not be parsed.");
    }

    private ProfileAnalysisResult ParseProfileAnalysisFallback(string cleanedJson, DetailedCandidateProfileDto profile, JobDescriptionDto jobDescription)
    {
        using var doc = JsonDocument.Parse(cleanedJson);
        var root = doc.RootElement;

        string candidateId = root.TryGetProperty("candidateId", out var idProp) ? (idProp.GetString() ?? profile.CandidateId) : profile.CandidateId;
        string candidateName = root.TryGetProperty("candidateName", out var nameProp) ? (nameProp.GetString() ?? profile.FullName) : profile.FullName;
        int score = root.TryGetProperty("overallSuitabilityScore", out var sProp) ? (sProp.GetInt32()) : 65;
        string rec = root.TryGetProperty("recommendation", out var recProp) ? (recProp.GetString() ?? "BORDERLINE / INTERVIEW WITH CAUTION") : "BORDERLINE / INTERVIEW WITH CAUTION";
        string summary = root.TryGetProperty("executiveSummary", out var sumProp) ? (sumProp.GetString() ?? "Candidate profile analyzed against target role.") : "Candidate profile analyzed against target role.";
        summary = FormatExecutiveSummaryHtml(summary);

        var techAudits = new List<TechUsageAudit>();
        if (root.TryGetProperty("techAudits", out var techArray) && techArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in techArray.EnumerateArray())
            {
                string tech = t.TryGetProperty("technology", out var tp) ? (tp.GetString() ?? "") : "";
                bool used = t.TryGetProperty("actuallyUsedInCareer", out var up) && up.GetBoolean();
                string ev = t.TryGetProperty("careerEvidence", out var ep) ? (ep.GetString() ?? "") : "";
                string depth = t.TryGetProperty("depthLevel", out var dp) ? (dp.GetString() ?? "Assessed") : "Assessed";
                if (!string.IsNullOrWhiteSpace(tech))
                {
                    techAudits.Add(new TechUsageAudit(tech, used, ev, depth));
                }
            }
        }

        var strengths = new List<string>();
        if (root.TryGetProperty("strengths", out var strArray) && strArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in strArray.EnumerateArray())
            {
                if (s.GetString() is string sVal && !string.IsNullOrWhiteSpace(sVal)) strengths.Add(sVal);
            }
        }

        var redFlags = new List<string>();
        if (root.TryGetProperty("redFlags", out var rfArray) && rfArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rfArray.EnumerateArray())
            {
                if (r.GetString() is string rVal && !string.IsNullOrWhiteSpace(rVal)) redFlags.Add(rVal);
            }
        }

        var questions = new List<InterviewQuestionDto>();
        if (root.TryGetProperty("interviewQuestions", out var qArray) && qArray.ValueKind == JsonValueKind.Array)
        {
            int qIndex = 0;
            foreach (var q in qArray.EnumerateArray())
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

    public async Task<ProfileAnalysisResult> AnalyzeLinkedInProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        // =========================================================================
        // LINKEDIN RECRUITER AI EVALUATION PIPELINE
        // =========================================================================
        // When you are ready to activate full Gemini evaluations for LinkedIn Recruiter,
        // you can simply uncomment the line below to use the deep career technology auditor:
        //
        // return await AnalyzeDetailedProfileAsync(jobDescription, profile, cancellationToken);
        //
        // Or write custom prompt logic tailored to LinkedIn Recruiter.
        // The extension is already completely wired to this endpoint!
        // =========================================================================

        throw new NotImplementedException("LinkedIn Recruiter backend pipeline is pending deployment. Implement prompt logic in AnalyzeLinkedInProfileAsync in GeminiService.cs.");
    }

    private static string FormatExecutiveSummaryHtml(string rawSummary)
    {
        if (string.IsNullOrWhiteSpace(rawSummary))
            return "<p>Profile evaluated against target job description.</p>";

        var trimmed = rawSummary.Trim();
        // If it already has clean HTML tags like <div>, <p>, or <ul>, return trimmed
        if (trimmed.Contains("<div") || trimmed.Contains("<p") || trimmed.Contains("<ul") || trimmed.Contains("<li"))
        {
            return trimmed;
        }

        var lines = trimmed.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Check if line is a section header like "Mandatory Skills Evaluation:"
            if (line.StartsWith("Mandatory Skills", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Skills Evaluation", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Skills Breakdown", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<div style=\"font-weight: 700; margin-bottom: 6px; color: #0f172a; font-size: 13px;\">")
                  .Append(line)
                  .Append("</div>");
            }
            // Check if line is a bullet item like "• C#: Good fit..." or "- C#: ..."
            else if (line.StartsWith("•") || line.StartsWith("-") || line.StartsWith("*"))
            {
                var content = line.TrimStart('•', '-', '*', ' ').Trim();
                bool isGoodFit = content.Contains("Good fit", StringComparison.OrdinalIgnoreCase) ||
                                 content.Contains("Strong fit", StringComparison.OrdinalIgnoreCase) ||
                                 content.Contains("Fit —", StringComparison.OrdinalIgnoreCase) ||
                                 content.Contains("Verified", StringComparison.OrdinalIgnoreCase);

                string accentColor = isGoodFit ? "#10b981" : "#ef4444";

                var colonIndex = content.IndexOf(':');
                string formattedContent = content;
                if (colonIndex > 0)
                {
                    string skillName = content.Substring(0, colonIndex).Trim();
                    string rest = content.Substring(colonIndex + 1).Trim();
                    formattedContent = $"<strong>{skillName}:</strong> {rest}";
                }

                sb.Append("<div style=\"padding: 5px 8px 5px 10px; border-left: 3px solid ")
                  .Append(accentColor)
                  .Append("; background: #ffffff; border: 1px solid #e2e8f0; border-left-width: 3px; border-radius: 4px; margin-bottom: 5px; font-size: 12px; line-height: 1.4;\">")
                  .Append(formattedContent)
                  .Append("</div>");
            }
            // Check if line is overall assessment or summary
            else if (line.StartsWith("Overall", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Assessment", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Summary", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Verdict", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<div style=\"margin-top: 8px; padding-top: 6px; border-top: 1px solid #e2e8f0; color: #334155; font-size: 12px; line-height: 1.45;\">")
                  .Append("<strong>").Append(line).Append("</strong>")
                  .Append("</div>");
            }
            else
            {
                sb.Append("<p style=\"margin: 4px 0; line-height: 1.45; font-size: 12px;\">").Append(line).Append("</p>");
            }
        }

        return sb.Length > 0 ? sb.ToString() : rawSummary;
    }
}
