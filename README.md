# Naukri AI Candidate Matcher & Recruiter Assistant

An intelligent, AI-powered recruitment automation toolkit for Naukri.com, built with a **.NET 10 Minimal API** backend leveraging **Google Gemini AI** and a **Manifest V3 Chrome Extension** for real-time candidate screening and deep profile audits.

---

## 🌟 Key Features

1. **In-Page Candidate Screening (Knockout Evaluation)**
   - Evaluates candidate cards directly on Naukri recruiter search / applicant pages.
   - Applies strict or lenient knockout criteria against:
     - Mandatory skills & technologies
     - Minimum/Maximum years of experience
     - Location & relocation preferences
     - Notice period requirements
   - Injects visual badges directly onto candidate cards with match score, knockout verdict, and AI reasoning.

2. **Deep Profile & Career Audit**
   - Performs comprehensive analysis on opened candidate profiles and resumes.
   - Analyzes experience history chronologically to evaluate if claimed technologies were authentically utilized in production environments or merely listed as keywords.
   - Assesses career progression, role transitions, tenure stability, employment gaps, and red flags.
   - Provides clear verdict: **Advance to Interview**, **Conditional Review**, or **Reject**.

3. **Manifest V3 Architecture & Robust Proxying**
   - Chrome Manifest V3 extension featuring a dedicated background service worker.
   - Bridges communication between HTTPS web pages (Naukri) and the local or remote API backend (`http://localhost:5000`), avoiding Mixed Content blocks and CORS issues.

4. **Clean & Intuitive Recruiter UI**
   - Pop-up interface with a collapsible settings drawer (`⚙️ Settings`).
   - Quick toggles for knockout severity and customizable job description inputs.

---

## 📁 Repository Structure

```
├── NaukriMatcher.Api/           # .NET 10 Minimal Web API
│   ├── Models/
│   │   └── EvaluationDtos.cs    # DTOs for evaluations, requests, and career audits
│   ├── Services/
│   │   ├── IGeminiService.cs    # Gemini integration interface
│   │   └── GeminiService.cs     # Prompt engineering & JSON parsing logic
│   ├── Program.cs               # Minimal API setup, CORS, and endpoint routing
│   ├── appsettings.json         # API & Gemini configuration
│   └── NaukriMatcher.Api.csproj # .NET 10 project file
│
├── naukri-extension/            # Chrome Extension (Manifest V3)
│   ├── manifest.json            # Manifest V3 configuration & permissions
│   ├── background.js            # Background service worker (API proxy)
│   ├── content.js               # Content script (card scoring & profile scraping)
│   ├── content.css              # Badge & modal styling
│   ├── popup.html               # Popup UI layout
│   ├── popup.css                # Recruiter popup styling
│   ├── popup.js                 # Popup logic and settings drawer management
│   ├── icons/                   # Extension icons (16, 48, 128)
│   ├── test-candidate-profile.html # Local mock candidate profile for testing
│   └── test-naukri-page.html    # Local mock Naukri search page for testing
│
├── .gitignore
└── README.md
```

---

## 🚀 Getting Started

### 1. Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Google Gemini API Key](https://aistudio.google.com/)
- Google Chrome or Chromium-based browser (Edge, Brave, etc.)

---

### 2. Backend API Setup

1. Open a terminal in `NaukriMatcher.Api`:
   ```bash
   cd NaukriMatcher.Api
   ```

2. Configure your Gemini API Key in `appsettings.json` or as an environment variable:
   ```json
   {
     "Gemini": {
       "ApiKey": "YOUR_GEMINI_API_KEY",
       "Model": "gemini-2.5-flash"
     }
   }
   ```
   *Alternatively, export the environment variable:*
   ```bash
   # Windows PowerShell
   $env:GEMINI_API_KEY="YOUR_GEMINI_API_KEY"

   # Linux / macOS
   export GEMINI_API_KEY="YOUR_GEMINI_API_KEY"
   ```

3. Run the API:
   ```bash
   dotnet run
   ```
   The API will bind to `http://localhost:5000` (or `http://127.0.0.1:5000`).

---

### 3. Chrome Extension Setup

1. Open Google Chrome and navigate to `chrome://extensions/`.
2. Enable **Developer mode** (toggle switch in the top-right corner).
3. Click **Load unpacked**.
4. Select the `naukri-extension` directory from this repository.
5. Pin the extension to your toolbar.

---

## 🧪 Testing Locally

To verify functionality without an active Naukri recruiter session:

1. Open `naukri-extension/test-naukri-page.html` in Chrome to test candidate card batch evaluations.
2. Open `naukri-extension/test-candidate-profile.html` in Chrome to test the **Deep Analyze Opened Profile** career audit.
3. Click the extension icon, enter your job requirements, and run the evaluation or profile audit.

---

## 🔒 Security & Privacy

- Candidate data is evaluated strictly in-memory and proxied directly to the Gemini API endpoint.
- No candidate resumes or profile credentials are persisted on third-party servers.
- Remember to never commit your personal `GEMINI_API_KEY` to public repositories.

---

## 📄 License
MIT License. Feel free to customize and extend for your recruitment workflows!
