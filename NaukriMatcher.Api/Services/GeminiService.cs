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

            STRICT SCORING INDEPENDENCE MANDATE (CRITICAL RULE):
            1. The matchPercentage (0-100%) and recommendation ("WORTH OPENING", "MAYBE", "SKIP") MUST BE DETERMINED STRICTLY AND EXCLUSIVELY based on candidate's technical skills, experience alignment, and notice period knockout.
            2. Location and Target Company Relevance are PURELY INFORMATIONAL classification tags.
            3. UNDER NO CIRCUMSTANCES should candidate location or company relevance affect, lower, or increase matchPercentage!
               - Even if candidate is located in a different city or works at a non-peer company, if they satisfy the mandatory technical skills and notice period, their match score must remain 100% truthful to technical fit (e.g. 80%-95%).

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

            3b. Location Matching & Indication (Informational Only - Does NOT alter matchPercentage):
               - If target job specifies a target location (e.g. Bengaluru, Pune, Hyderabad, Remote):
                 * Compare candidate's location against the target location (accounting for common Indian metro aliases like Bangalore/Bengaluru, NCR/Delhi/Noida/Gurgaon, Mumbai/Thane/Navi Mumbai).
                 * If candidate is in the target city or Remote, set "locationMatch": "MATCH".
                 * If candidate is in a different city, set "locationMatch": "RELOCATION".
                 * If candidate location is not specified, set "locationMatch": "NOT_SPECIFIED".
               - If no target location was provided in the job description, set "locationMatch": "NOT_SPECIFIED".

            3c. Target Company & Industry Domain Relevance (Informational Only - Does NOT alter matchPercentage):
               - If target job specifies targetCompany (e.g. "KLA"):
                 * First, identify what industry domain targetCompany belongs to (e.g., KLA is Semiconductor Capital Equipment / Chip Manufacturing; Razorpay is FinTech; Swiggy is E-Commerce / Quick Commerce; etc.).
                 * Examine candidate's currentCompany, designation, and snippet to identify where they work (e.g. Tata Electronics, Micron, ASM Technologies, Applied Materials, Intel, Lam Research, ASML, etc.).
                 * If candidate's company operates in the same industry domain, semiconductor ecosystem, or is an industry peer/competitor, set:
                   "isRelevantCompany": true,
                   "companyRelevance": "Domain Peer",
                   "candidateCompany": "[Extracted company name]"
                 * If candidate's company is from an unrelated industry domain or not evident, set:
                   "isRelevantCompany": false,
                   "companyRelevance": null,
                   "candidateCompany": "[Extracted company name or 'Not disclosed']"
               - If targetCompany is not specified:
                 * Set "isRelevantCompany": false, "companyRelevance": null, "candidateCompany": null.

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
                     "locationMatch": "MATCH",
                     "isRelevantCompany": true,
                     "companyRelevance": "Semiconductor Peer",
                     "candidateCompany": "Tata Electronics",
                     "reasoning": "Concise 1-2 sentence justification highlighting mandatory skills and notice period."
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
                location = jobDescription.Location,
                targetCompany = jobDescription.TargetCompany,
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
                currentCompany = c.CurrentCompany,
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
                    "Evaluated with partial profile match.",
                    "NOT_SPECIFIED",
                    false,
                    null,
                    c.CurrentCompany
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
            1. Executive Summary & HR Fit Verdict (CONCISE NARRATIVE — CRITICAL FOR HR RECRUITERS):
               - Write a CONCISE, 2 TO 3-LINE overall narrative summary of the candidate's profile and fit verdict.
               - Keep it high-level, clear, and direct so a non-technical HR recruiter immediately knows whether the candidate is fit or not.
               - DO NOT list out each individual skill breakdown in executiveSummary (the detailed skill breakdown is displayed right below in the Technology & Skill Verification section).
               - SKILL HIGHLIGHTING REQUIREMENT:
                 Whenever you mention a specific skill or technology name in the executiveSummary:
                 * If the candidate is STRONG / verified in that skill, format it as: <span style="color: #166534; font-weight: 600; background: #dcfce7; padding: 1px 5px; border-radius: 3px;">SkillName</span>
                 * If the candidate is NOT STRONG / weak / missing in that skill, format it as: <span style="color: #991b1b; font-weight: 600; background: #fee2e2; padding: 1px 5px; border-radius: 3px;">SkillName</span>

            2. CRUCIAL FACTOR — Notice Period & Availability Analysis (Naukri Categories):
               - Scrutinize the candidate profile, full profile text, noticePeriod, and employment history for any mention of: notice period, availability, serving notice, immediate joiner, relieved date, or last working day (LWD).
               - Reference date for current time: {DateTime.UtcNow:dd-MMM-yyyy}.
               - Categorize the candidate into EXACTLY ONE of these categories (noticePeriodCategory):
                 * "Immediate Joiner"                → Ready to join right away without a notice period (e.g. states "immediate joiner", "ready to join immediately", already relieved, 0 days notice).
                 * "Serving Notice Period"          → Candidate has officially resigned and is currently serving their notice period (has an active Last Working Day / LWD).
                 * "15 Days Notice Period"          → Standard notice period of 15 days or less (1 to 15 days).
                 * "1 Month Notice Period"          → Standard notice period of 1 month (16 to 30 days).
                 * "2 Months Notice Period"         → Standard notice period of 2 months (31 to 60 days).
                 * "3 Months Notice Period"         → Standard notice period of 3 months (61 to 90 days).
                 * "3 Months and More Notice Period"→ Notice period of more than 3 months (> 90 days).
                 * "Notice Not Specified"           → ANTI-HALLUCINATION GUARD: use ONLY if no notice period or availability is found. NEVER invent days.
               - If candidate is "Serving Notice Period":
                 * servingNoticeDaysLeft: Specify the integer number of days left in their notice period (e.g. 15). Calculate this from their stated Last Working Day (LWD) compared to {DateTime.UtcNow:dd-MMM-yyyy}, or from explicit text like "serving notice (15 days left)".
                 * relievedDate: Extract their exact Last Working Day / LWD text (e.g. "Last Working Day: 25-Nov-2024").
               - If candidate is "Immediate Joiner":
                 * servingNoticeDaysLeft: 0
                 * relievedDate: If already relieved, extract the relieved date text (e.g. "Relieved on 15-Oct-2024"). If not, set to null.
               - In executiveSummary, always state the recruiter impact of their availability clearly.

            3. CRITICAL FACTOR — Current Company Hands-On Experience:
               - Deeply analyze the CURRENT / MOST RECENT company work history entry ONLY.
               - Extract specifically what technologies, frameworks, tools, and architecture patterns the candidate is ACTIVELY CODING WITH in their current role TODAY.
               - Differentiate active daily production tools from stale/legacy skills from companies 2+ years ago.
               - currentCompanyHandsOn: Write a concise summary (e.g. "Active daily production in C#, .NET 8, PostgreSQL, Docker, Azure Service Bus").

            4. COMPANY DOMAIN RELEVANCE ANALYSIS (PERCENTAGE RELEVANCE):
               - If targetJob.targetCompany is provided (e.g. "KLA"):
                 * First, identify what industry domain targetCompany belongs to (e.g. KLA belongs to "Semiconductor Capital Equipment & Chip Manufacturing"). Set targetCompanyDomain.
                 * Analyze candidate's current employer (and previous employers).
                 * Compute companyRelevancePercentage (0 to 100):
                   - 80% to 98%: Direct peer, supplier, or competitor in the same specialized domain (e.g. for KLA: Tata Electronics, Micron, ASM Technologies, Applied Materials, Lam Research, Intel, TSMC, Texas Instruments, ASML, Qualcomm, etc.).
                   - 55% to 75%: Adjacent hardware, embedded systems, or industrial tech domain.
                   - 10% to 40%: Unrelated industry domain (e.g. generic fintech, retail, or e-commerce vs semiconductor).
                 * Output companyRelevanceReason: 1-2 concise sentences explaining the domain alignment.
               - If targetJob.targetCompany is not provided:
                 * Set companyRelevancePercentage = null, companyRelevanceReason = null, targetCompanyDomain = null.

            5. PREFERENCE FACTOR — Career Stability & Switch Analysis:
               - List each company, duration, and title chronologically (most recent first).
               - Count total company switches (totalSwitches): every change of employer = 1 switch.
               - Calculate average tenure per company.
               - Assign a stability rating and include it in the executiveSummary narrative:
                 * "High Stability (Preferred)"  → average tenure >= 2.0 years per company → score boost
                 * "Moderate Stability"           → average tenure 1.2 to 2.0 years per company
                 * "Frequent Job Hopper (Risk)"  → multiple companies with < 1 year tenure → score penalty

            6. Technology & Skill Verification (techAudits ARRAY):
               - YOU MUST EVALUATE EVERY SINGLE MANDATORY SKILL LISTED IN targetJob.mandatorySkills as the first entries in techAudits.
               - For EACH mandatory skill, assign:
                 * actuallyUsedInCareer: true if they have authentic production/project experience; false if keyword stuffed, weak, or absent.
                 * depthLevel: "Primary / Production" (core day-to-day tool), "Secondary / Ancillary" (occasional usage), or "Keyword Stuffed / Not Evident".
                 * careerEvidence: A short, concise explanation justifying WHY the candidate is good at it OR WHY they are not fit.
               - Weighting: If ANY mandatory skill lacks authentic project evidence or is not fit, heavily penalize the score (cap at 55% or lower) and recommend "BORDERLINE / INTERVIEW WITH CAUTION" or "DO NOT PROCEED".

            7. Preferred Skills (Secondary Priority - 20% Weight):
               - Check for preferred/secondary skills. Evidence acts as a positive booster.
               - Do not heavily penalize or reject if preferred skills are missing, as long as all mandatory skills are satisfied.

            8. Career Continuity & Seniority:
               - Evaluate total tenure, career progression, stability, and whether their seniority matches the role title.

            9. Knockout Parameters & Anti-Hallucination:
               - If the candidate's notice period is "Not specified", empty, or unknown, explicitly state "Notice period: Not specified" and do NOT invent or assume any notice period days.
               - If an explicit notice period is given, evaluate against MaxNoticePeriodDays.

            10. Final Verdict Recommendation (Must be one of):
                - "WORTH GOING FORWARD": Strong evidence of authentic hands-on usage of all mandatory skills, within acceptable notice period, solid impact.
                - "BORDERLINE / INTERVIEW WITH CAUTION": Partial hands-on depth in mandatory skills, or border notice period, or missing 1 mandatory skill in actual projects.
                - "DO NOT PROCEED": Core mandatory tech is keyword stuffed without real project evidence, or knockout violation.

            11. Targeted HR Interview Questions & Answers (EXACTLY 5 Questions):
                Generate exactly 5 tailored interview questions based directly on the candidate's actual projects, claims, and technologies from their resume.
                IMPORTANT: The HR interviewer who will ask these questions is NOT a technical person.
                - For each question, provide an "expectedAnswer" written in clear, plain English (2-3 sentences).
                - Provide "expectedKeywords": an array of 3-5 specific technical buzzwords.
                - Breakdown (strictly 2 Simple and 3 Advanced).

            Output Format (Strict JSON only, no markdown wrappers):
            {
              "candidateId": "string",
              "candidateName": "string",
              "overallSuitabilityScore": 85,
              "recommendation": "WORTH GOING FORWARD",
              "companyRelevancePercentage": 90,
              "companyRelevanceReason": "Tata Electronics operates directly in semiconductor and electronics manufacturing, creating direct domain alignment with KLA.",
              "targetCompanyDomain": "Semiconductor & Capital Equipment",
              "noticePeriodCategory": "Immediate Joiner",
              "servingNoticeDaysLeft": 0,
              "relievedDate": "Relieved on 15-Oct-2023",
              "totalSwitches": 2,
              "currentCompanyHandsOn": "Active daily production in C#, .NET 8, PostgreSQL, Docker",
              "executiveSummary": "Candidate brings 6+ years of backend development experience with strong verified hands-on depth in <span style=\"color: #166534; font-weight: 600; background: #dcfce7; padding: 1px 5px; border-radius: 3px;\">C#</span> and <span style=\"color: #166534; font-weight: 600; background: #dcfce7; padding: 1px 5px; border-radius: 3px;\">.NET Core</span> microservices.",
              "techAudits": [
                {
                  "technology": "C#",
                  "actuallyUsedInCareer": true,
                  "careerEvidence": "Hands-on production use for 5+ years building REST microservices.",
                  "depthLevel": "Primary / Production"
                }
              ],
              "strengths": ["string", "string"],
              "redFlags": ["string", "string"],
              "interviewQuestions": [
                {
                  "question": "Clear question for HR to ask",
                  "difficulty": "Simple",
                  "expectedAnswer": "Plain-English explanation.",
                  "expectedKeywords": ["Keyword1", "Keyword2"]
                }
              ]
            }
            """;

        var promptData = new
        {
            targetJob = new
            {
                title = jobDescription.Title,
                location = jobDescription.Location,
                targetCompany = jobDescription.TargetCompany,
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
                                var noticeInfo = ResolveNoticePeriodInfo(
                                    result.NoticePeriodCategory,
                                    result.ServingNoticeDaysLeft,
                                    result.RelievedDate,
                                    profile.NoticePeriod,
                                    profile.FullProfileRawText);

                                return result with
                                {
                                    CandidateId = string.IsNullOrWhiteSpace(result.CandidateId) ? profile.CandidateId : result.CandidateId,
                                    CandidateName = string.IsNullOrWhiteSpace(result.CandidateName) ? profile.FullName : result.CandidateName,
                                    NoticePeriodCategory = noticeInfo.Category,
                                    ServingNoticeDaysLeft = noticeInfo.DaysLeft,
                                    RelievedDate = noticeInfo.RelievedDate,
                                    ExecutiveSummary = FormatExecutiveSummaryHtml(
                                        result.ExecutiveSummary,
                                        result.TechAudits,
                                        noticeInfo.Category,
                                        noticeInfo.RelievedDate,
                                        result.TotalSwitches,
                                        result.CurrentCompanyHandsOn,
                                        jobDescription.MandatorySkills,
                                        jobDescription.TargetCompany,
                                        result.CompanyRelevancePercentage,
                                        result.CompanyRelevanceReason,
                                        result.TargetCompanyDomain,
                                        noticeInfo.DaysLeft)
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

        // Extract critical factor fields
        string? noticeCat = root.TryGetProperty("noticePeriodCategory", out var ncProp) ? ncProp.GetString() : null;
        string? relieved  = root.TryGetProperty("relievedDate", out var rdProp) ? rdProp.GetString() : null;
        int? servingDays  = null;
        if (root.TryGetProperty("servingNoticeDaysLeft", out var sndlProp) && sndlProp.ValueKind == JsonValueKind.Number)
        {
            servingDays = sndlProp.GetInt32();
        }

        var noticeInfo = ResolveNoticePeriodInfo(
            noticeCat,
            servingDays,
            relieved,
            profile.NoticePeriod,
            profile.FullProfileRawText);

        int? switches     = null;
        if (root.TryGetProperty("totalSwitches", out var swProp) && swProp.ValueKind == JsonValueKind.Number)
        {
            switches = swProp.GetInt32();
        }
        string? handsOn   = root.TryGetProperty("currentCompanyHandsOn", out var hoProp) ? hoProp.GetString() : null;

        // Company relevance fields
        int? compRelPct = null;
        if (root.TryGetProperty("companyRelevancePercentage", out var crp) && crp.ValueKind == JsonValueKind.Number)
        {
            compRelPct = crp.GetInt32();
        }
        string? compRelReason = root.TryGetProperty("companyRelevanceReason", out var crr) ? crr.GetString() : null;
        string? targetDomain  = root.TryGetProperty("targetCompanyDomain", out var tcd) ? tcd.GetString() : null;

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

        string summary = root.TryGetProperty("executiveSummary", out var sumProp) ? (sumProp.GetString() ?? "Candidate profile analyzed against target role.") : "Candidate profile analyzed against target role.";
        summary = FormatExecutiveSummaryHtml(
            summary,
            techAudits,
            noticeInfo.Category,
            noticeInfo.RelievedDate,
            switches,
            handsOn,
            jobDescription.MandatorySkills,
            jobDescription.TargetCompany,
            compRelPct,
            compRelReason,
            targetDomain,
            noticeInfo.DaysLeft);

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
            noticeInfo.Category,
            noticeInfo.RelievedDate,
            switches,
            handsOn,
            compRelPct,
            compRelReason,
            targetDomain,
            noticeInfo.DaysLeft
        );
    }

    public async Task<ProfileAnalysisResult> AnalyzeLinkedInProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("LinkedIn Recruiter backend pipeline is pending deployment. Implement prompt logic in AnalyzeLinkedInProfileAsync in GeminiService.cs.");
    }

    public async Task<string> GenerateSearchStringsAsync(
        JobDescriptionDto jobDescription,
        CancellationToken cancellationToken = default)
    {
        if (jobDescription == null)
        {
            return string.Empty;
        }

        string apiKey = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

                var systemInstructionText = """
                    You are a Master Technical Recruitment Sourcer and Boolean Search Architect.
                    Generate the single most optimal, high-precision Boolean Search string for finding relevant candidates on Naukri Resdex, LinkedIn Recruiter, and talent search engines.

                    Construction Rules:
                    1. Mandatory Skills: Group synonymous terms and aliases together with OR inside parentheses.
                       Examples:
                       - If C# / .NET: (".NET" OR "dot net" OR "dotnet" OR "C#")
                       - If Dapper: "Dapper"
                       - If React: ("React" OR "React.js" OR "Reactjs")
                       - If Angular: ("Angular" OR "AngularJS" OR "Angular 2+")
                       - If Java: ("Java" OR "J2EE" OR "Spring Boot")
                       - If Python: "Python"
                       - If SQL / database: ("SQL" OR "MSSQL" OR "SQL Server")
                    2. Combine each core skill group using AND.
                    3. Role Variations: Include standard role synonyms in parentheses with OR:
                       Example: ("developer" OR "engineer" OR "programmer")
                    4. Output format:
                       Return strictly a valid JSON object matching:
                       {
                         "searchString": "(\".NET\" OR \"dot net\" OR \"C#\") AND \"Dapper\" AND (\"developer\" OR \"engineer\" OR \"programmer\")"
                       }
                    Return pure JSON only. Do not include markdown fences outside the JSON.
                    """;

                var promptPayload = new
                {
                    title = jobDescription.Title,
                    mandatorySkills = jobDescription.MandatorySkills,
                    preferredSkills = jobDescription.PreferredSkills ?? new List<string>(),
                    location = jobDescription.Location,
                    targetCompany = jobDescription.TargetCompany,
                    jobDescriptionSummary = string.IsNullOrWhiteSpace(jobDescription.RawText) ? "" : (jobDescription.RawText.Length > 2000 ? jobDescription.RawText[..2000] : jobDescription.RawText)
                };

                var userPromptText = $"Generate the optimal Boolean Search String for this role:\n\n{JsonSerializer.Serialize(promptPayload, JsonOptions)}";

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
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("candidates", out var candidatesArr) && candidatesArr.GetArrayLength() > 0)
                    {
                        var firstCand = candidatesArr[0];
                        if (firstCand.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("parts", out var parts) &&
                            parts.GetArrayLength() > 0)
                        {
                            var partText = parts[0].GetProperty("text").GetString();
                            if (!string.IsNullOrWhiteSpace(partText))
                            {
                                var cleaned = CleanJsonString(partText);
                                using var parsed = JsonDocument.Parse(cleaned);
                                if (parsed.RootElement.TryGetProperty("searchString", out var strProp))
                                {
                                    var generated = strProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(generated))
                                    {
                                        return generated.Trim();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gemini search string generation failed. Falling back to deterministic builder.");
            }
        }

        return BuildDeterministicSearchString(jobDescription);
    }

    public static string BuildDeterministicSearchString(JobDescriptionDto jobDescription)
    {
        var andClauses = new List<string>();

        // 1. Mandatory Skills
        var skills = jobDescription.MandatorySkills ?? new List<string>();
        if (skills.Count == 0 && jobDescription.PreferredSkills != null && jobDescription.PreferredSkills.Count > 0)
        {
            skills = jobDescription.PreferredSkills;
        }

        bool hasDotNetGroup = false;

        foreach (var rawSkill in skills)
        {
            var skill = rawSkill.Trim();
            if (string.IsNullOrWhiteSpace(skill)) continue;

            var upper = skill.ToUpperInvariant();

            if (upper.Contains(".NET") || upper.Contains("DOTNET") || upper.Contains("C#") || upper.Contains("CSHARP") || upper.Contains("ASP.NET"))
            {
                if (!hasDotNetGroup)
                {
                    andClauses.Add("(\".NET\" OR \"dot net\" OR \"C#\")");
                    hasDotNetGroup = true;
                }
                continue;
            }

            if (upper == "DAPPER")
            {
                andClauses.Add("\"Dapper\"");
                continue;
            }

            if (upper.Contains("ENTITY FRAMEWORK") || upper == "EF" || upper == "EF CORE")
            {
                andClauses.Add("(\"Entity Framework\" OR \"EF Core\")");
                continue;
            }

            if (upper.Contains("REACT"))
            {
                andClauses.Add("(\"React\" OR \"React.js\" OR \"Reactjs\")");
                continue;
            }

            if (upper.Contains("ANGULAR"))
            {
                andClauses.Add("(\"Angular\" OR \"AngularJS\")");
                continue;
            }

            if (upper.Contains("NODE"))
            {
                andClauses.Add("(\"Node\" OR \"Node.js\" OR \"Nodejs\")");
                continue;
            }

            if (upper.Contains("PYTHON"))
            {
                andClauses.Add("\"Python\"");
                continue;
            }

            if (upper.Contains("JAVA") && !upper.Contains("JAVASCRIPT"))
            {
                andClauses.Add("(\"Java\" OR \"Core Java\" OR \"Spring Boot\")");
                continue;
            }

            if (upper.Contains("SQL") || upper.Contains("POSTGRESQL") || upper.Contains("MYSQL"))
            {
                andClauses.Add("(\"SQL\" OR \"PostgreSQL\" OR \"SQL Server\")");
                continue;
            }

            if (upper.Contains("DOCKER"))
            {
                andClauses.Add("(\"Docker\" OR \"Containers\")");
                continue;
            }

            if (upper.Contains("KUBERNETES") || upper == "K8S")
            {
                andClauses.Add("(\"Kubernetes\" OR \"K8s\")");
                continue;
            }

            if (upper.Contains("AZURE"))
            {
                andClauses.Add("(\"Azure\" OR \"Microsoft Azure\")");
                continue;
            }

            if (upper.Contains("AWS"))
            {
                andClauses.Add("(\"AWS\" OR \"Amazon Web Services\")");
                continue;
            }

            // Default quoted skill
            andClauses.Add($"\"{skill}\"");
        }

        // 2. Role Title Variations
        var title = (jobDescription.Title ?? string.Empty).ToLowerInvariant();
        if (title.Contains("developer") || title.Contains("engineer") || title.Contains("programmer"))
        {
            andClauses.Add("(\"developer\" OR \"engineer\" OR \"programmer\")");
        }
        else if (title.Contains("architect"))
        {
            andClauses.Add("(\"architect\" OR \"technical lead\" OR \"tech lead\")");
        }
        else if (title.Contains("tester") || title.Contains("qa") || title.Contains("quality"))
        {
            andClauses.Add("(\"QA\" OR \"tester\" OR \"SDET\" OR \"test engineer\")");
        }
        else if (!string.IsNullOrWhiteSpace(jobDescription.Title))
        {
            andClauses.Add($"\"{jobDescription.Title.Trim()}\"");
        }

        if (andClauses.Count == 0)
        {
            return "(\".NET\" OR \"dot net\" OR \"C#\") AND \"Dapper\" AND (\"developer\" OR \"engineer\" OR \"programmer\")";
        }

        return string.Join(" AND ", andClauses);
    }

    private record NoticePeriodInfo(string Category, int? DaysLeft, string? RelievedDate);

    private static NoticePeriodInfo ResolveNoticePeriodInfo(
        string? geminiCategory,
        int? geminiDaysLeft,
        string? geminiRelievedDate,
        string? profileNoticeText,
        string? profileFullRawText)
    {
        string rawCombined = $"{profileNoticeText} {geminiRelievedDate} {profileFullRawText}".ToLowerInvariant();
        string profileNotice = (profileNoticeText ?? "").Trim().ToLowerInvariant();

        // 1. Check for Immediate Joiner
        bool isImmediate = false;
        if (!string.IsNullOrWhiteSpace(geminiCategory) &&
            geminiCategory.Contains("Immediate", StringComparison.OrdinalIgnoreCase))
        {
            isImmediate = true;
        }
        else if (profileNotice.Contains("immediate") || Regex.IsMatch(profileNotice, @"\b0\s*days?\b") || profileNotice == "0" ||
                 profileNotice.Contains("join immediately") || profileNotice.Contains("ready to join") ||
                 profileNotice.Contains("already relieved"))
        {
            isImmediate = true;
        }
        else if (Regex.IsMatch(rawCombined, @"\b(immediate\s*joiner|immediately\s*available|ready\s*to\s*join\s*(?:immediately|right\s*away)|available\s*to\s*join\s*immediately)\b"))
        {
            isImmediate = true;
        }

        // 2. Check for Serving Notice Period
        bool isServing = false;
        if (!isImmediate)
        {
            if (!string.IsNullOrWhiteSpace(geminiCategory) &&
                geminiCategory.Contains("Serving", StringComparison.OrdinalIgnoreCase))
            {
                isServing = true;
            }
            else if (profileNotice.Contains("serving") || Regex.IsMatch(rawCombined, @"\b(serving\s*notice|currently\s*serving|on\s*notice\s*period)\b"))
            {
                isServing = true;
            }
        }

        int? daysLeft = geminiDaysLeft;
        string? relievedDate = geminiRelievedDate;

        // If relieving / LWD date is not provided, try to extract from text
        if (string.IsNullOrWhiteSpace(relievedDate))
        {
            var lwdMatch = Regex.Match(rawCombined, @"(?:last\s*working\s*day|lwd|relieved\s*on|relieving\s*date|exit\s*date)\s*[:\-–]?\s*(\d{1,2}[\/\-\s][A-Za-z]{3,9}[\/\-\s]\d{2,4}|\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4})", RegexOptions.IgnoreCase);
            if (lwdMatch.Success)
            {
                relievedDate = lwdMatch.Value.Trim();
            }
        }

        // If serving notice, calculate or extract days left if not already provided
        if (isServing)
        {
            if (!daysLeft.HasValue || daysLeft <= 0)
            {
                // Try to find explicit "X days left / remaining"
                var daysMatch = Regex.Match(rawCombined, @"(\d+)\s*(?:days?|d)\s*(?:left|remaining|to\s*serve)", RegexOptions.IgnoreCase);
                if (daysMatch.Success && int.TryParse(daysMatch.Groups[1].Value, out int extractedDays))
                {
                    daysLeft = extractedDays;
                }
                else if (!string.IsNullOrWhiteSpace(relievedDate))
                {
                    // Try to parse LWD date and calculate difference from today
                    var dateStrMatch = Regex.Match(relievedDate, @"\d{1,2}[\/\-\s][A-Za-z]{3,9}[\/\-\s]\d{2,4}|\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4}");
                    if (dateStrMatch.Success && DateTime.TryParse(dateStrMatch.Value, out var lwdDate))
                    {
                        var diff = (lwdDate.Date - DateTime.UtcNow.Date).Days;
                        if (diff >= 0)
                        {
                            daysLeft = diff;
                        }
                        else
                        {
                            // LWD was in the past -> candidate is already relieved!
                            isImmediate = true;
                            isServing = false;
                            daysLeft = 0;
                        }
                    }
                }
            }

            if (isServing)
            {
                return new NoticePeriodInfo("Serving Notice Period", daysLeft, relievedDate);
            }
        }

        if (isImmediate)
        {
            return new NoticePeriodInfo("Immediate Joiner", 0, relievedDate);
        }

        // 3. Otherwise, categorize into standard Naukri categories:
        // - 15 Days Notice Period (<= 15 days)
        // - 1 Month Notice Period (16 to 30 days)
        // - 2 Months Notice Period (31 to 60 days)
        // - 3 Months Notice Period (61 to 90 days)
        // - 3 Months and More Notice Period (> 90 days)
        // - Notice Not Specified

        string gemCat = (geminiCategory ?? "").Trim();
        if (gemCat.Contains("15 Day", StringComparison.OrdinalIgnoreCase))
            return new NoticePeriodInfo("15 Days Notice Period", null, relievedDate);
        if (gemCat.Contains("1 Month", StringComparison.OrdinalIgnoreCase))
            return new NoticePeriodInfo("1 Month Notice Period", null, relievedDate);
        if (gemCat.Contains("2 Month", StringComparison.OrdinalIgnoreCase))
            return new NoticePeriodInfo("2 Months Notice Period", null, relievedDate);
        if (gemCat.Contains("3 Month", StringComparison.OrdinalIgnoreCase) && (gemCat.Contains("More", StringComparison.OrdinalIgnoreCase) || gemCat.Contains("+")))
            return new NoticePeriodInfo("3 Months and More Notice Period", null, relievedDate);
        if (gemCat.Contains("3 Month", StringComparison.OrdinalIgnoreCase))
            return new NoticePeriodInfo("3 Months Notice Period", null, relievedDate);

        // Check explicit words in profileNotice for > 3 months first
        if (profileNotice.Contains("more than 3") || profileNotice.Contains("3 months and more") ||
            profileNotice.Contains("3 month and more") || profileNotice.Contains("3 months or more") ||
            profileNotice.Contains("3+ month") || profileNotice.Contains("> 3 month") || profileNotice.Contains(">3 month"))
        {
            return new NoticePeriodInfo("3 Months and More Notice Period", null, relievedDate);
        }

        // Fallback: parse numeric days from profile notice text
        int parsedDays = ParseNumericNoticeDays(profileNoticeText);
        if (parsedDays > 0)
        {
            if (parsedDays <= 15)
                return new NoticePeriodInfo("15 Days Notice Period", null, relievedDate);
            if (parsedDays <= 30)
                return new NoticePeriodInfo("1 Month Notice Period", null, relievedDate);
            if (parsedDays <= 60)
                return new NoticePeriodInfo("2 Months Notice Period", null, relievedDate);
            if (parsedDays <= 90)
                return new NoticePeriodInfo("3 Months Notice Period", null, relievedDate);
            return new NoticePeriodInfo("3 Months and More Notice Period", null, relievedDate);
        }

        if (profileNotice.Contains("1 month") || profileNotice.Contains("30 day"))
            return new NoticePeriodInfo("1 Month Notice Period", null, relievedDate);
        if (profileNotice.Contains("2 month") || profileNotice.Contains("60 day"))
            return new NoticePeriodInfo("2 Months Notice Period", null, relievedDate);
        if (profileNotice.Contains("3 month") || profileNotice.Contains("90 day"))
            return new NoticePeriodInfo("3 Months Notice Period", null, relievedDate);
        if (profileNotice.Contains("15 day"))
            return new NoticePeriodInfo("15 Days Notice Period", null, relievedDate);

        return new NoticePeriodInfo("Notice Not Specified", null, relievedDate);
    }

    private static int ParseNumericNoticeDays(string? noticeText)
    {
        if (string.IsNullOrWhiteSpace(noticeText)) return -1;
        var lower = noticeText.Trim().ToLowerInvariant();

        if (lower.Contains("immediate") || Regex.IsMatch(lower, @"\b0\s*days?\b") || lower == "0") return 0;
        if (lower.Contains("15 day")) return 15;
        if (lower.Contains("1 month") || lower.Contains("30 day")) return 30;
        if (lower.Contains("2 month") || lower.Contains("60 day")) return 60;
        if (lower.Contains("3 month") || lower.Contains("90 day")) return 90;

        var match = Regex.Match(lower, @"(\d+)\s*(day|month|mth)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
        {
            if (match.Groups[2].Value.StartsWith("m"))
            {
                return count * 30;
            }
            return count;
        }

        return -1;
    }

    private static string FormatExecutiveSummaryHtml(
        string rawSummary,
        IReadOnlyList<TechUsageAudit>? techAudits = null,
        string? noticePeriodCategory = null,
        string? relievedDate = null,
        int? totalSwitches = null,
        string? currentCompanyHandsOn = null,
        IReadOnlyList<string>? mandatorySkills = null,
        string? targetCompany = null,
        int? companyRelevancePercentage = null,
        string? companyRelevanceReason = null,
        string? targetCompanyDomain = null,
        int? servingNoticeDaysLeft = null)
    {
        var sb = new StringBuilder();

        // ── Card 1: Crucial Factor — Notice Period & Relieved Date ──────────────────
        {
            string cat = noticePeriodCategory ?? "Notice Not Specified";
            bool isImmediate = cat.Equals("Immediate Joiner", StringComparison.OrdinalIgnoreCase);
            bool isServing   = cat.Equals("Serving Notice Period", StringComparison.OrdinalIgnoreCase);
            bool is15Days    = cat.StartsWith("15 Day", StringComparison.OrdinalIgnoreCase);
            bool is1Month    = cat.StartsWith("1 Month", StringComparison.OrdinalIgnoreCase);
            bool is2Months   = cat.StartsWith("2 Month", StringComparison.OrdinalIgnoreCase);
            bool is3MonthsMore = cat.Contains("3 Month", StringComparison.OrdinalIgnoreCase) && (cat.Contains("More", StringComparison.OrdinalIgnoreCase) || cat.Contains("+"));
            bool is3Months   = !is3MonthsMore && cat.StartsWith("3 Month", StringComparison.OrdinalIgnoreCase);

            string cardBg, cardBorder, pillBg, pillColor, pillText, recruiterImpact;

            if (isImmediate)
            {
                cardBg = "#f0fdf4"; cardBorder = "#86efac";
                pillBg = "#16a34a"; pillColor = "#ffffff";
                pillText = "Immediate Joiner (0 Days)";
                recruiterImpact = "Immediate availability — candidate is ready to join right away without serving any notice period. Strong preference for urgent requirements.";
            }
            else if (isServing)
            {
                cardBg = "#f0f9ff"; cardBorder = "#7dd3fc";
                pillBg = "#0284c7"; pillColor = "#ffffff";
                pillText = servingNoticeDaysLeft.HasValue && servingNoticeDaysLeft.Value >= 0
                    ? $"Serving Notice ({servingNoticeDaysLeft.Value} Days Left)"
                    : "Serving Notice Period";
                recruiterImpact = servingNoticeDaysLeft.HasValue && servingNoticeDaysLeft.Value >= 0
                    ? $"Candidate has officially resigned and is currently serving notice with {servingNoticeDaysLeft.Value} days remaining. Fast hiring turnaround."
                    : "Candidate has officially resigned and is currently serving notice period. Fast hiring turnaround — confirm exact last working day.";
            }
            else if (is15Days)
            {
                cardBg = "#f0fdf4"; cardBorder = "#86efac";
                pillBg = "#16a34a"; pillColor = "#ffffff";
                pillText = "15 Days Notice Period";
                recruiterImpact = "Short notice — candidate requires 15 days notice period. High availability for standard and priority hiring timelines.";
            }
            else if (is1Month)
            {
                cardBg = "#fffbeb"; cardBorder = "#fcd34d";
                pillBg = "#d97706"; pillColor = "#ffffff";
                pillText = "1 Month Notice Period";
                recruiterImpact = "Standard notice — candidate requires 1 month (30 days) notice period. Suitable for standard corporate hiring schedules.";
            }
            else if (is2Months)
            {
                cardBg = "#fff7ed"; cardBorder = "#fdba74";
                pillBg = "#ea580c"; pillColor = "#ffffff";
                pillText = "2 Months Notice Period";
                recruiterImpact = "Moderate notice — candidate requires 2 months (60 days) notice period. Suitable for advance planning; evaluate buyout if urgent.";
            }
            else if (is3Months)
            {
                cardBg = "#fff1f2"; cardBorder = "#fca5a5";
                pillBg = "#dc2626"; pillColor = "#ffffff";
                pillText = "3 Months Notice Period";
                recruiterImpact = "Standard corporate 3-month (90 days) notice period. Evaluate buyout, early release negotiations, or offer retention risk.";
            }
            else if (is3MonthsMore)
            {
                cardBg = "#fff1f2"; cardBorder = "#f87171";
                pillBg = "#991b1b"; pillColor = "#ffffff";
                pillText = "3 Months or More Notice Period";
                recruiterImpact = "Extended notice — candidate requires more than 3 months notice period. Significant delay risk; explore buyout options immediately.";
            }
            else
            {
                cardBg = "#f8fafc"; cardBorder = "#cbd5e1";
                pillBg = "#64748b"; pillColor = "#ffffff";
                pillText = "Notice Not Specified";
                recruiterImpact = "Notice period not found in profile. Confirm availability directly during screening call.";
            }

            var extraRows = new StringBuilder();
            if (isServing && servingNoticeDaysLeft.HasValue && servingNoticeDaysLeft.Value >= 0)
            {
                extraRows.Append($"<div style=\"margin-top: 4px; font-size: 12px; color: #0369a1;\"><strong>Days Left in Notice Period:</strong> <span style=\"font-weight: 700; color: #0284c7; background: #e0f2fe; padding: 2px 7px; border-radius: 4px;\">{servingNoticeDaysLeft.Value} Days Left</span></div>");
            }
            else if (isImmediate)
            {
                extraRows.Append("<div style=\"margin-top: 4px; font-size: 12px; color: #15803d;\"><strong>Status:</strong> Ready to join right away without notice period</div>");
            }

            if (!string.IsNullOrWhiteSpace(relievedDate))
            {
                extraRows.Append($"<div style=\"margin-top: 4px; font-size: 12px; color: #374151;\"><strong>Relieved Date / LWD:</strong> {relievedDate}</div>");
            }

            sb.Append($"""
                <div style="background: {cardBg}; border: 1px solid {cardBorder}; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px;">
                  <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px;">
                    <span style="font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase;">NOTICE &amp; RELIEVED STATUS</span>
                    <span style="background: {pillBg}; color: {pillColor}; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 12px;">{pillText}</span>
                  </div>
                  <div style="font-size: 12px; color: #374151;"><strong>Category:</strong> {cat}</div>
                  {extraRows}
                  <div style="margin-top: 4px; font-size: 12px; color: #4b5563; font-style: italic;">{recruiterImpact}</div>
                </div>
                """);
        }

        // ── Card 2: Company Domain & Industry Relevance (Target Company Match) ──────
        if (!string.IsNullOrWhiteSpace(targetCompany))
        {
            int pct = companyRelevancePercentage ?? 50;
            string domain = !string.IsNullOrWhiteSpace(targetCompanyDomain) ? targetCompanyDomain : "Domain Peer";
            string reason = !string.IsNullOrWhiteSpace(companyRelevanceReason) ? companyRelevanceReason : $"Evaluated against target company {targetCompany}.";

            string badgeBg, badgeColor, badgeBorder, badgeText;
            if (pct >= 75)
            {
                badgeBg = "#ecfdf5"; badgeColor = "#065f46"; badgeBorder = "#a7f3d0";
                badgeText = $"{pct}% · High Domain Peer";
            }
            else if (pct >= 50)
            {
                badgeBg = "#eff6ff"; badgeColor = "#1e40af"; badgeBorder = "#bfdbfe";
                badgeText = $"{pct}% · Moderate Domain Relevance";
            }
            else
            {
                badgeBg = "#f8fafc"; badgeColor = "#475569"; badgeBorder = "#cbd5e1";
                badgeText = $"{pct}% · Different Domain";
            }

            sb.Append($"""
                <div style="background: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 11px 13px; margin-bottom: 8px;">
                  <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px;">
                    <span style="font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase;">COMPANY &amp; INDUSTRY DOMAIN RELEVANCE</span>
                    <span style="background: {badgeBg}; color: {badgeColor}; border: 1px solid {badgeBorder}; font-size: 11.5px; font-weight: 700; padding: 2px 8px; border-radius: 5px;">{badgeText}</span>
                  </div>
                  <div style="font-size: 12.5px; color: #1e293b; margin-bottom: 4px;">
                    <strong>Target Company:</strong> {targetCompany} <span style="font-size: 11.5px; color: #64748b;">({domain})</span>
                  </div>
                  <div style="font-size: 12px; color: #334155; line-height: 1.45;">
                    {reason}
                  </div>
                </div>
                """);
        }

        // ── Card 3: Current Company Hands-On Tech ────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(currentCompanyHandsOn))
        {
            sb.Append($"""
                <div style="background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px;">
                  <div style="font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase; margin-bottom: 6px;">CURRENT ROLE HANDS-ON TECH</div>
                  <div style="font-size: 12px; color: #1e40af;"><strong style="color: #1d4ed8;">Active Stack:</strong> {currentCompanyHandsOn}</div>
                </div>
                """);
        }

        // ── Card 4: Career Stability & Switch Analysis ───────────────────────────────
        if (totalSwitches.HasValue)
        {
            string stabilityPillBg, stabilityPillColor, stabilityLabel;

            if (totalSwitches.Value <= 2)
            {
                stabilityPillBg = "#14532d"; stabilityPillColor = "#ffffff";
                stabilityLabel = "Preferred: High Stability";
            }
            else if (totalSwitches.Value <= 4)
            {
                stabilityPillBg = "#475569"; stabilityPillColor = "#ffffff";
                stabilityLabel = "Moderate Stability";
            }
            else
            {
                stabilityPillBg = "#991b1b"; stabilityPillColor = "#ffffff";
                stabilityLabel = "Caution: Frequent Switches";
            }

            sb.Append($"""
                <div style="background: #f9fafb; border: 1px solid #e5e7eb; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px;">
                  <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px;">
                    <span style="font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase;">CAREER STABILITY &amp; SWITCHES</span>
                    <span style="background: {stabilityPillBg}; color: {stabilityPillColor}; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 12px;">{stabilityLabel}</span>
                  </div>
                  <div style="font-size: 12px; color: #374151;"><strong>Total Company Switches:</strong> {totalSwitches.Value}</div>
                </div>
                """);
        }

        // ── Card 5: Mandatory Skills Evaluation ─────────────────────────────────────
        if (techAudits != null && techAudits.Count > 0)
        {
            var mandatorySet = new HashSet<string>(
                mandatorySkills ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var skillsToShow = mandatorySet.Count > 0
                ? techAudits.Where(t => mandatorySet.Contains(t.Technology)).ToList()
                : techAudits.ToList();

            if (skillsToShow.Count > 0)
            {
                sb.Append("<div style=\"margin-bottom: 8px;\">");
                sb.Append("<div style=\"font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase; margin-bottom: 6px;\">MANDATORY SKILLS BREAKDOWN</div>");

                foreach (var audit in skillsToShow)
                {
                    string borderColor = audit.ActuallyUsedInCareer ? "#10b981" : "#ef4444";
                    string bgColor     = audit.ActuallyUsedInCareer ? "#f0fdf4" : "#fff1f2";
                    string verdict     = audit.ActuallyUsedInCareer ? "Verified" : "Missing / Stuffed";
                    string verdictColor= audit.ActuallyUsedInCareer ? "#15803d" : "#dc2626";

                    sb.Append($"""
                        <div style="border-left: 3px solid {borderColor}; background: {bgColor}; padding: 6px 10px; margin-bottom: 4px; border-radius: 0 4px 4px 0;">
                          <span style="font-weight: 600; font-size: 12px; color: #1e293b;">{audit.Technology}</span>
                          <span style="font-size: 11px; font-weight: 600; color: {verdictColor}; margin-left: 6px;">[{verdict}]</span>
                          <span style="font-size: 11px; color: #6b7280; margin-left: 4px;">— {audit.CareerEvidence}</span>
                        </div>
                        """);
                }

                sb.Append("</div>");
            }
        }

        // ── Card 6: Overall Assessment (executive narrative) ─────────────────────────
        if (!string.IsNullOrWhiteSpace(rawSummary))
        {
            var trimmed = rawSummary.Trim();

            if (trimmed.Contains("Mandatory Skills Evaluation:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Skills Breakdown:", StringComparison.OrdinalIgnoreCase))
            {
                var matchAssessment = Regex.Match(trimmed, @"Overall Assessment:\s*(.+)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (matchAssessment.Success && !string.IsNullOrWhiteSpace(matchAssessment.Groups[1].Value))
                {
                    trimmed = matchAssessment.Groups[1].Value.Trim();
                }
            }

            trimmed = Regex.Replace(trimmed, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            trimmed = Regex.Replace(trimmed, @"^[\s•\-\*]+", "", RegexOptions.Multiline);

            var lines = trimmed.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l));
            string narrative = string.Join(" ", lines);

            if (techAudits != null && techAudits.Count > 0)
            {
                var orderedSkills = techAudits
                    .Where(t => !string.IsNullOrWhiteSpace(t.Technology))
                    .OrderByDescending(t => t.Technology.Length)
                    .ToList();

                foreach (var audit in orderedSkills)
                {
                    string skill = audit.Technology.Trim();
                    if (skill.Length < 2) continue;

                    bool alreadyStyled = Regex.IsMatch(narrative, @"<span[^>]*>[^<]*" + Regex.Escape(skill) + @"[^<]*</span>", RegexOptions.IgnoreCase);
                    if (alreadyStyled) continue;

                    string color = audit.ActuallyUsedInCareer ? "#166534" : "#991b1b";
                    string bg    = audit.ActuallyUsedInCareer ? "#dcfce7" : "#fee2e2";
                    string border= audit.ActuallyUsedInCareer ? "#bbf7d0" : "#fecaca";

                    string pattern = @"(<[^>]+>)|((?<![\w#\+])" + Regex.Escape(skill) + @"(?![\w#\+]))";
                    narrative = Regex.Replace(narrative, pattern, m =>
                    {
                        if (m.Groups[1].Success) return m.Groups[1].Value;
                        return $"<span style=\"color: {color}; background: {bg}; border: 1px solid {border}; font-weight: 600; padding: 1px 5px; border-radius: 3px;\">{m.Groups[2].Value}</span>";
                    }, RegexOptions.IgnoreCase);
                }
            }

            sb.Append($"""
                <div style="border-top: 1px solid #e5e7eb; padding-top: 8px; margin-top: 4px;">
                  <div style="font-size: 11px; font-weight: 700; color: #475569; letter-spacing: 0.04em; text-transform: uppercase; margin-bottom: 4px;">OVERALL ASSESSMENT</div>
                  <p style="margin: 0; line-height: 1.55; font-size: 13px; color: #1e293b;">{narrative}</p>
                </div>
                """);
        }

        string result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            return "<p style=\"margin: 0; line-height: 1.55; font-size: 13px; color: #1e293b;\">Profile evaluated against target job description.</p>";
        }

        return result;
    }
}
