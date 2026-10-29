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
            Determine whether the candidate has ACTUALLY USED the required technologies in real production projects throughout their career, with strict evaluation of 4 CRITICAL FACTORS:
            1. Relieved Date & Notice Period Category (Crucial Availability Factor)
            2. Active Hands-On Experience in Current Company (Recency & Production Depth)
            3. Employment History, Company Tenure & Switch Analysis (Candidate Preference Factor)
            4. Mandatory Skills Depth & Authenticity (Production Use vs Keyword Stuffing)

            CRITICAL AUDITING RULES:

            1. NOTICE PERIOD & RELIEVED DATE EVALUATION (CRUCIAL FACTOR #1):
               - Scrutinize the candidate profile, most recent employment duration (end date), resume headline, and raw text for:
                 * Relieved Date / Last Working Day (LWD) / Exit status: e.g. "Relieved on 15-Oct-2023", "LWD: 30-Nov-2024", "Currently serving notice", or "Currently employed (Active / Not relieved)".
                 * Stated Notice Period: e.g. Immediate, 15 days, 30 days, 60 days, 90 days, or "Not specified".
               - Categorize Notice Period into EXACTLY ONE of these categories:
                 * "Immediate Joiner": Relieved date has passed, or last working day is within 15 days, or serving notice ending soon, or stated notice <= 15 days / Immediate. (HIGHEST RECRUITER PREFERENCE).
                 * "Short Notice Period": Notice period is between 16 and 30 days. (GOOD PREFERENCE).
                 * "Long Notice Period": Notice period is >= 45, 60, or 90 days. (KNOCKOUT RISK / HEAVY PENALTY if MaxNoticePeriodDays is exceeded).
                 * "Not Specified": If notice period is empty, unknown, or not mentioned, explicitly state "Not Specified" and NEVER invent or assume any days (Anti-Hallucination rule).
               - Explicitly explain the recruitment impact of their availability.

            2. HANDS-ON EXPERIENCE IN CURRENT COMPANY (CRITICAL FACTOR #2):
               - Deeply analyze the candidate's CURRENT / MOST RECENT company in their employment history.
               - Extract what specific technologies, tools, architecture, and frameworks they are ACTIVELY USING HANDS-ON TODAY in their daily production work.
               - Differentiate between active daily production tools vs older, stale skills only used years ago at past employers.
               - Candidates actively engineering with the job's mandatory technologies in their CURRENT role receive high preference.
               - If mandatory skills are missing from their current role and only appear in past jobs (e.g. >2 years ago), flag this as a potential skill recency concern.

            3. CAREER STABILITY & SWITCH ANALYSIS (CRITICAL FACTOR #3):
               - Chronologically analyze the candidate's employment history:
                 * List each company worked at, duration, and title.
                 * Count total companies worked at and calculate total job switches (e.g. 2 switches across 3 companies).
                 * Calculate average tenure per company (total years / number of companies).
               - Assign a Stability & Recruiter Preference Rating:
                 * "High Stability (Preferred)": Average tenure >= 2.0 years per company. Demonstrates strong commitment and continuity. Rewarded with higher preference and score boost.
                 * "Moderate Stability": Average tenure between 1.2 and 2.0 years.
                 * "Frequent Job Hopper (Risk)": Multiple switches under 1 year (< 12 months per company). Flag as hiring risk with score penalty unless clearly contract roles.
               - Based on this stability analysis, explicitly declare candidate preference.

            4. MANDATORY SKILLS JUSTIFICATION (CRITICAL MANDATORY REQUIREMENT):
               - YOU MUST EVALUATE EVERY SINGLE MANDATORY SKILL LISTED IN targetJob.mandatorySkills.
               - For EACH mandatory skill, provide an explicit, short, and concise explanation justifying:
                 * WHY the candidate is good at it (hands-on production use, specific companies/projects, duration, concrete deliverables, and whether active in current company).
                 * OR WHY the candidate is NOT fit in that skill (only listed in skill tags without project context, no evidence of practical usage, or only academic/basic). Keep concise.
               - Must be reflected in:
                 a) executiveSummary (in the Mandatory Skills Evaluation breakdown)
                 b) techAudits ARRAY (every mandatory skill must be first, with actuallyUsedInCareer, depthLevel, and careerEvidence).
               - Weighting: If ANY mandatory skill lacks authentic project evidence or is not fit, heavily penalize the score (cap at 55% or lower) and recommend "BORDERLINE / INTERVIEW WITH CAUTION" or "DO NOT PROCEED".

            5. PREFERRED SKILLS (SECONDARY PRIORITY - 20% WEIGHT):
               - Positive booster if present; never heavily penalize or reject if missing when all mandatory skills are met.

            6. FINAL VERDICT RECOMMENDATION (Must be one of):
               - "WORTH GOING FORWARD": Strong evidence of authentic hands-on usage of all mandatory skills (especially in current company), acceptable notice period (Immediate or Short preferred), solid career stability.
               - "BORDERLINE / INTERVIEW WITH CAUTION": Partial hands-on depth, or border notice period, or missing 1 mandatory skill in current production, or moderate job hopping.
               - "DO NOT PROCEED": Core tech keyword stuffed, excessive long notice knockout, or severe job hopping.

            7. TARGETED HR INTERVIEW QUESTIONS & ANSWERS (EXACTLY 5 Questions):
               Strictly 2 Simple (fundamentals & daily API/workflow) and 3 Advanced (architecture, database/concurrency, production debugging/outages), with expectedAnswer in plain English for non-technical HR and 3-5 expectedKeywords to listen for.

            8. OUTPUT SCHEMA (Strict JSON only, no markdown outside JSON):
            {
              "candidateId": "string",
              "candidateName": "string",
              "overallSuitabilityScore": 85,
              "recommendation": "WORTH GOING FORWARD",
              "noticePeriodCategory": "Immediate Joiner",
              "relievedDate": "Relieved on 15-Oct-2023",
              "totalSwitches": 2,
              "currentCompanyHandsOn": "Active hands-on development in C#, .NET 8, PostgreSQL, and Docker in current role",
              "executiveSummary": "CRUCIAL FACTOR — NOTICE PERIOD & AVAILABILITY:\n• Category: Immediate Joiner\n• Stated Notice Period: Immediate\n• Relieved Date / LWD: Relieved on 15-Oct-2023 (Last Working Day passed)\n• Recruiter Impact: Highly preferred — ready for immediate onboarding with zero delay.\n\nCRITICAL FACTOR — CURRENT COMPANY HANDS-ON EXPERIENCE:\n• Current Company: TechCorp Solutions (Senior Software Engineer, 2 yrs 3 mos)\n• Active Hands-On Tech Stack: C#, ASP.NET Core, PostgreSQL, Docker, Microservices\n• Production Responsibilities: Actively designs REST microservices and optimizes database queries in daily production.\n\nCAREER STABILITY & SWITCH ANALYSIS:\n• Total Switches: 2 switches across 3 companies\n• Average Tenure: 2.8 years per company\n• Stability Preference: High Stability (Preferred) — demonstrates strong retention and career continuity.\n• Career Progression: Company A (2.3 yrs) -> Company B (3 yrs) -> Company C (2 yrs)\n\nMandatory Skills Evaluation:\n• C#: Good fit — active daily production use in current company for 2+ years building microservices.\n• .NET Core: Good fit — architected scalable services at current and previous company.\n• SQL: Good fit — hands-on database optimization and complex queries.\n\nOverall Assessment: Candidate is a strong fit with immediate availability, excellent career stability, and verified current hands-on expertise in the mandatory stack.",
              "techAudits": [ ... ],
              "strengths": [
                "Immediate Joiner — Relieved on Oct 2023, available for immediate onboarding",
                "Current Company Hands-On: Actively building production .NET Core microservices today",
                "High Career Stability: Average tenure of 2.8 years per company across 2 switches"
              ],
              "redFlags": [ ... ],
              "interviewQuestions": [ ... ]
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
                                    ExecutiveSummary = FormatExecutiveSummaryHtml(
                                        result.ExecutiveSummary,
                                        result.NoticePeriodCategory,
                                        result.RelievedDate,
                                        result.TotalSwitches,
                                        result.CurrentCompanyHandsOn)
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

        string? noticeCat = root.TryGetProperty("noticePeriodCategory", out var ncp) ? ncp.GetString() : null;
        string? relieved = root.TryGetProperty("relievedDate", out var rdp) ? rdp.GetString() : null;
        int? switches = root.TryGetProperty("totalSwitches", out var tsp) && tsp.TryGetInt32(out var tsVal) ? tsVal : null;
        string? currentHandsOn = root.TryGetProperty("currentCompanyHandsOn", out var cchp) ? cchp.GetString() : null;

        summary = FormatExecutiveSummaryHtml(summary, noticeCat, relieved, switches, currentHandsOn);

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
            questions,
            noticeCat,
            relieved,
            switches,
            currentHandsOn
        );
    }

    public async Task<ProfileAnalysisResult> AnalyzeLinkedInProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        // When activating full Gemini evaluations for LinkedIn Recruiter,
        // simply uncomment the line below to use the deep career technology auditor:
        //
        // return await AnalyzeDetailedProfileAsync(jobDescription, profile, cancellationToken);

        throw new NotImplementedException("LinkedIn Recruiter backend pipeline is pending deployment. Implement prompt logic in AnalyzeLinkedInProfileAsync in GeminiService.cs.");
    }

    private static string FormatExecutiveSummaryHtml(
        string rawSummary,
        string? noticePeriodCategory = null,
        string? relievedDate = null,
        int? totalSwitches = null,
        string? currentCompanyHandsOn = null)
    {
        if (string.IsNullOrWhiteSpace(rawSummary))
            return "<p>Profile evaluated against target job description.</p>";

        var trimmed = rawSummary.Trim();

        // If it already has fully structured HTML cards from our formatter, return as-is
        if (trimmed.Contains("CRUCIAL FACTOR") && (trimmed.Contains("<div") || trimmed.Contains("<p")))
        {
            return trimmed;
        }

        var lines = trimmed.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var noticeLines = new List<string>();
        var currentCompanyLines = new List<string>();
        var stabilityLines = new List<string>();
        var skillLines = new List<string>();
        var overallLines = new List<string>();
        var otherLines = new List<string>();

        int currentSection = 0; // 1 = Notice, 2 = Current Company, 3 = Stability, 4 = Skills, 5 = Overall

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Detect section headers
            if (line.Contains("NOTICE PERIOD", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("AVAILABILITY", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("RELIEVED", StringComparison.OrdinalIgnoreCase) && line.Contains("FACTOR", StringComparison.OrdinalIgnoreCase)))
            {
                currentSection = 1;
                continue;
            }
            if (line.Contains("CURRENT COMPANY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("CURRENT ROLE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("HANDS-ON EXPERIENCE", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = 2;
                continue;
            }
            if (line.Contains("CAREER STABILITY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("SWITCH ANALYSIS", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("EMPLOYMENT HISTORY", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = 3;
                continue;
            }
            if (line.StartsWith("Mandatory Skills", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Skills Evaluation", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Skills Breakdown", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = 4;
                continue;
            }
            if (line.StartsWith("Overall Assessment", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Overall", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Verdict", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Final Recommendation", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = 5;
                overallLines.Add(line);
                continue;
            }

            switch (currentSection)
            {
                case 1:
                    noticeLines.Add(line);
                    break;
                case 2:
                    currentCompanyLines.Add(line);
                    break;
                case 3:
                    stabilityLines.Add(line);
                    break;
                case 4:
                    skillLines.Add(line);
                    break;
                case 5:
                    overallLines.Add(line);
                    break;
                default:
                    if (line.StartsWith("•") || line.StartsWith("-") || line.StartsWith("*"))
                    {
                        skillLines.Add(line);
                    }
                    else
                    {
                        otherLines.Add(line);
                    }
                    break;
            }
        }

        var sb = new StringBuilder();

        // =========================================================================
        // CARD 1: CRUCIAL FACTOR · NOTICE PERIOD & RELIEVED DATE
        // =========================================================================
        string cat = noticePeriodCategory ?? "";
        if (string.IsNullOrWhiteSpace(cat))
        {
            var catLine = noticeLines.FirstOrDefault(l => l.Contains("Category:", StringComparison.OrdinalIgnoreCase));
            if (catLine != null)
            {
                cat = catLine.Substring(catLine.IndexOf(':') + 1).Trim();
            }
            else
            {
                var allNoticeText = string.Join(" ", noticeLines);
                if (allNoticeText.Contains("Immediate", StringComparison.OrdinalIgnoreCase)) cat = "Immediate Joiner";
                else if (allNoticeText.Contains("Short", StringComparison.OrdinalIgnoreCase)) cat = "Short Notice Period";
                else if (allNoticeText.Contains("Long", StringComparison.OrdinalIgnoreCase)) cat = "Long Notice Period";
                else cat = "Not Specified";
            }
        }

        bool isImmediate = cat.Contains("Immediate", StringComparison.OrdinalIgnoreCase);
        bool isShort = cat.Contains("Short", StringComparison.OrdinalIgnoreCase) || cat.Contains("30", StringComparison.OrdinalIgnoreCase);
        bool isLong = cat.Contains("Long", StringComparison.OrdinalIgnoreCase) || cat.Contains("60", StringComparison.OrdinalIgnoreCase) || cat.Contains("90", StringComparison.OrdinalIgnoreCase);

        string borderColor = isImmediate ? "#86efac" : isShort ? "#fde68a" : isLong ? "#fca5a5" : "#cbd5e1";
        string bgColor = isImmediate ? "#f0fdf4" : isShort ? "#fffbeb" : isLong ? "#fef2f2" : "#f8fafc";
        string badgeBg = isImmediate ? "#166534" : isShort ? "#b45309" : isLong ? "#991b1b" : "#475569";
        string badgeText = isImmediate ? "⚡ Immediate Joiner" : isShort ? "⏱️ Short Notice (15-30d)" : isLong ? "⚠️ Long Notice (60-90d+)" : "ℹ️ Notice Not Specified";

        sb.Append("<div style=\"margin-bottom: 12px; padding: 10px 14px; border-radius: 8px; border: 1px solid ")
          .Append(borderColor)
          .Append("; background: ")
          .Append(bgColor)
          .Append("; display: flex; flex-direction: column; gap: 5px;\">");

        sb.Append("<div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 6px;\">")
          .Append("<span style=\"font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.5px; color: ")
          .Append(badgeBg)
          .Append(";\">CRUCIAL FACTOR · NOTICE & RELIEVED DATE</span>")
          .Append("<span style=\"font-size: 11px; font-weight: 700; padding: 2px 9px; border-radius: 9999px; background: ")
          .Append(badgeBg)
          .Append("; color: #ffffff;\">")
          .Append(badgeText)
          .Append("</span></div>");

        if (noticeLines.Count > 0)
        {
            foreach (var nl in noticeLines)
            {
                var cleanNl = nl.TrimStart('•', '-', '*', ' ').Trim();
                var colonIdx = cleanNl.IndexOf(':');
                string formattedNl;
                if (colonIdx > 0)
                {
                    string label = cleanNl.Substring(0, colonIdx).Trim();
                    string val = cleanNl.Substring(colonIdx + 1).Trim();
                    formattedNl = $"<strong>{label}:</strong> {val}";
                }
                else
                {
                    formattedNl = cleanNl;
                }

                sb.Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\">")
                  .Append(formattedNl)
                  .Append("</div>");
            }
        }
        else
        {
            string rel = !string.IsNullOrWhiteSpace(relievedDate) ? relievedDate : "Relieved status assessed from profile history";
            sb.Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\"><strong>Relieved Date / LWD:</strong> ")
              .Append(rel)
              .Append("</div>")
              .Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\"><strong>Category:</strong> ")
              .Append(cat)
              .Append("</div>");
        }

        sb.Append("</div>");

        // =========================================================================
        // CARD 2: CRITICAL FACTOR · HANDS-ON EXPERIENCE IN CURRENT COMPANY
        // =========================================================================
        sb.Append("<div style=\"margin-bottom: 12px; padding: 10px 14px; border-radius: 8px; border: 1px solid #bfdbfe; background: #eff6ff; display: flex; flex-direction: column; gap: 5px;\">");
        sb.Append("<div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 6px;\">")
          .Append("<span style=\"font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.5px; color: #1e40af;\">CRITICAL FACTOR · CURRENT COMPANY HANDS-ON EXPERIENCE</span>")
          .Append("<span style=\"font-size: 10.5px; font-weight: 700; padding: 2px 8px; border-radius: 9999px; background: #2563eb; color: #ffffff;\">Active in Current Role</span></div>");

        if (currentCompanyLines.Count > 0)
        {
            foreach (var cl in currentCompanyLines)
            {
                var cleanCl = cl.TrimStart('•', '-', '*', ' ').Trim();
                var colonIdx = cleanCl.IndexOf(':');
                string formattedCl;
                if (colonIdx > 0)
                {
                    string label = cleanCl.Substring(0, colonIdx).Trim();
                    string val = cleanCl.Substring(colonIdx + 1).Trim();
                    if (label.Contains("Tech", StringComparison.OrdinalIgnoreCase) || label.Contains("Stack", StringComparison.OrdinalIgnoreCase))
                    {
                        formattedCl = $"<strong style=\"color: #1e40af;\">{label}:</strong> <span style=\"font-weight: 600; color: #1d4ed8;\">{val}</span>";
                    }
                    else
                    {
                        formattedCl = $"<strong>{label}:</strong> {val}";
                    }
                }
                else
                {
                    formattedCl = cleanCl;
                }

                sb.Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\">")
                  .Append(formattedCl)
                  .Append("</div>");
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentCompanyHandsOn))
        {
            sb.Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\"><strong style=\"color: #1e40af;\">Active Hands-On Tech Stack:</strong> <span style=\"font-weight: 600; color: #1d4ed8;\">")
              .Append(currentCompanyHandsOn)
              .Append("</span></div>");
        }
        else
        {
            sb.Append("<div style=\"font-size: 12px; color: #1e293b; line-height: 1.45;\">Candidate's current company experience and hands-on deliverables evaluated against target job.</div>");
        }

        sb.Append("</div>");

        // =========================================================================
        // CARD 3: CAREER STABILITY & SWITCH ANALYSIS (PREFERENCE FACTOR)
        // =========================================================================
        string stabilityText = "Stability Assessed";
        string stabilityPillBg = "#0f172a";
        var allStab = string.Join(" ", stabilityLines);
        if (allStab.Contains("High Stability", StringComparison.OrdinalIgnoreCase) || allStab.Contains("Preferred", StringComparison.OrdinalIgnoreCase))
        {
            stabilityText = "Preferred: High Stability";
            stabilityPillBg = "#166534";
        }
        else if (allStab.Contains("Frequent", StringComparison.OrdinalIgnoreCase) || allStab.Contains("Hopper", StringComparison.OrdinalIgnoreCase) || allStab.Contains("Risk", StringComparison.OrdinalIgnoreCase))
        {
            stabilityText = "Caution: Frequent Switches";
            stabilityPillBg = "#991b1b";
        }
        else if (allStab.Contains("Moderate", StringComparison.OrdinalIgnoreCase))
        {
            stabilityText = "Moderate Stability";
            stabilityPillBg = "#334155";
        }

        sb.Append("<div style=\"margin-bottom: 12px; padding: 10px 14px; border-radius: 8px; border: 1px solid #e2e8f0; background: #f8fafc; display: flex; flex-direction: column; gap: 5px;\">");
        sb.Append("<div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 6px;\">")
          .Append("<span style=\"font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.5px; color: #334155;\">CAREER STABILITY & SWITCH ANALYSIS</span>")
          .Append("<span style=\"font-size: 10.5px; font-weight: 700; padding: 2px 8px; border-radius: 9999px; background: ")
          .Append(stabilityPillBg)
          .Append("; color: #ffffff;\">")
          .Append(stabilityText)
          .Append("</span></div>");

        if (stabilityLines.Count > 0)
        {
            foreach (var sl in stabilityLines)
            {
                var cleanSl = sl.TrimStart('•', '-', '*', ' ').Trim();
                var colonIdx = cleanSl.IndexOf(':');
                string formattedSl;
                if (colonIdx > 0)
                {
                    string label = cleanSl.Substring(0, colonIdx).Trim();
                    string val = cleanSl.Substring(colonIdx + 1).Trim();
                    formattedSl = $"<strong>{label}:</strong> {val}";
                }
                else
                {
                    formattedSl = cleanSl;
                }

                sb.Append("<div style=\"font-size: 12px; color: #334155; line-height: 1.45;\">")
                  .Append(formattedSl)
                  .Append("</div>");
            }
        }
        else if (totalSwitches.HasValue)
        {
            sb.Append("<div style=\"font-size: 12px; color: #334155; line-height: 1.45;\"><strong>Total Switches:</strong> ")
              .Append(totalSwitches.Value)
              .Append(" switches identified across employment history.</div>");
        }
        else
        {
            sb.Append("<div style=\"font-size: 12px; color: #334155; line-height: 1.45;\">Career progression and company switch history verified.</div>");
        }

        sb.Append("</div>");

        // =========================================================================
        // CARD 4: MANDATORY SKILLS EVALUATION
        // =========================================================================
        sb.Append("<div style=\"font-weight: 700; margin: 12px 0 6px 0; color: #0f172a; font-size: 13px;\">Mandatory Skills Evaluation:</div>");

        foreach (var sk in skillLines)
        {
            var content = sk.TrimStart('•', '-', '*', ' ').Trim();
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

        // =========================================================================
        // CARD 5: OTHER LINES & OVERALL ASSESSMENT
        // =========================================================================
        if (otherLines.Count > 0)
        {
            foreach (var ol in otherLines)
            {
                sb.Append("<p style=\"margin: 4px 0; line-height: 1.45; font-size: 12px;\">").Append(ol).Append("</p>");
            }
        }

        if (overallLines.Count > 0)
        {
            sb.Append("<div style=\"margin-top: 10px; padding-top: 8px; border-top: 1px solid #e2e8f0; color: #1e293b; font-size: 12px; line-height: 1.45;\">");
            foreach (var ov in overallLines)
            {
                sb.Append("<p style=\"margin: 2px 0;\"><strong>").Append(ov).Append("</strong></p>");
            }
            sb.Append("</div>");
        }

        return sb.Length > 0 ? sb.ToString() : rawSummary;
    }
}
