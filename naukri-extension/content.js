/**
 * Naukri AI Candidate Matcher - Content Script
 * 1. Search Cards: In-place AI match badges (No DOM reordering/ranking).
 * 2. Detailed Profile: Robust Deep Career Technology & Resume Audit.
 */

(function () {
  console.log('[Naukri AI Matcher] Content script initialized.');

  // Selectors covering modern Naukri Resdex, SRP, and test mock pages
  const CARD_SELECTORS = [
    '.tuple[data-tuple-id]',
    '.tuple',
    '[data-tuple-id]',
    '.srp-tuple',
    '.resdex-tuple',
    '.tuple-box',
    '.candidate-card',
    'article.jobTuple',
    'div[class*="tuple"]',
    'div[class*="candidate-card"]'
  ];

  function cleanText(text) {
    return text ? text.replace(/\s+/g, ' ').trim() : '';
  }

  function getCandidateCardElements() {
    let elements = [];
    for (const selector of CARD_SELECTORS) {
      const found = Array.from(document.querySelectorAll(selector));
      if (found.length > 0) {
        elements = found.filter(el => {
          return !found.some(parent => parent !== el && parent.contains(el));
        });
        if (elements.length > 0) break;
      }
    }
    return elements;
  }

  function isCandidateProfilePage() {
    return !!(
      document.querySelector('.profile-container, .cand-details, #candidate-details, .resdex-profile, .resume-container, .full-profile-view, [data-page-type="candidate-profile"]') ||
      (document.querySelector('.work-experience, .experience-sec, [class*="experience"]') && !document.querySelector('.tuple'))
    );
  }

  // ==========================================
  // SECTION 1: SEARCH PAGE CANDIDATE CARDS (IN-PLACE BADGES)
  // ==========================================

  function extractCandidateCardData(card, index) {
    const candidateId =
      card.getAttribute('data-tuple-id') ||
      card.getAttribute('data-cid') ||
      card.getAttribute('data-candidate-id') ||
      card.getAttribute('id') ||
      `cand_${index}_${Date.now()}`;

    card.setAttribute('data-naukri-ai-id', candidateId);

    const titleEl = card.querySelector('.title, .designation, [class*="designation"], .subTitle, [class*="title"], h2, h3, a.subTitle');
    const designation = cleanText(titleEl ? titleEl.innerText : 'Candidate');

    const expEl = card.querySelector('.exp, [class*="exp"], [title*="Yrs"], li[class*="experience"], span[class*="experience"]');
    let totalExperience = cleanText(expEl ? expEl.innerText : '');
    if (!totalExperience) {
      const expMatch = card.innerText.match(/\b\d+(\.\d+)?\s*(?:to\s*\d+(\.\d+)?)?\s*(?:Yrs|Years|yr|yrs)\b/i);
      totalExperience = expMatch ? expMatch[0] : 'Experience not specified';
    }

    const skillElements = card.querySelectorAll('.tags-gt, .tags, [class*="skill"], [class*="tag"], ul.tags li, span.tag, .skill-badge');
    const skillsSet = new Set();
    skillElements.forEach(el => {
      const skill = cleanText(el.innerText);
      if (skill && skill.length < 50) {
        skill.split(/[,|•]/).forEach(s => {
          const trimmed = cleanText(s);
          if (trimmed) skillsSet.add(trimmed);
        });
      }
    });

    let noticePeriod = '';
    const noticeEl = card.querySelector('[class*="notice"], .notice-period, [title*="Notice"]');
    if (noticeEl) {
      noticePeriod = cleanText(noticeEl.innerText);
    } else {
      const noticeMatch = card.innerText.match(/(?:notice\s*period|notice)\s*[:\-]?\s*([0-9]+\s*(?:days|months|day|month)|immediate|serving\s*notice)/i);
      if (noticeMatch) {
        noticePeriod = cleanText(noticeMatch[1]);
      } else {
        const directMatch = card.innerText.match(/\b(immediate|15\s*days|30\s*days|1\s*month|2\s*months|3\s*months|serving\s*notice)\b/i);
        noticePeriod = directMatch ? cleanText(directMatch[0]) : 'Not specified';
      }
    }

    const locEl = card.querySelector('.loc, [class*="loc"], [title*="Location"], li[class*="location"], span[class*="location"]');
    let location = cleanText(locEl ? locEl.innerText : '');
    if (!location) {
      const locMatch = card.innerText.match(/(?:Bengaluru|Bangalore|Hyderabad|Pune|Mumbai|Delhi|Noida|Gurgaon|Chennai|Remote|Hybrid)/i);
      location = locMatch ? locMatch[0] : 'Location not specified';
    }

    const salaryEl = card.querySelector('.salary, [class*="salary"], [title*="PA"], [class*="ctc"], span[class*="salary"]');
    let annualSalary = cleanText(salaryEl ? salaryEl.innerText : '');
    if (!annualSalary) {
      const salaryMatch = card.innerText.match(/\b\d+(\.\d+)?\s*(?:-\s*\d+(\.\d+)?)?\s*(?:Lakhs?|Lacs?|Cr|PA|INR|₹)\b/i);
      annualSalary = salaryMatch ? salaryMatch[0] : 'Not disclosed';
    }

    const snippetEl = card.querySelector('.snippet, [class*="summary"], [class*="desc"], .job-desc, p');
    const snippet = cleanText(snippetEl ? snippetEl.innerText : card.innerText.slice(0, 300));

    return {
      candidateId,
      designation,
      totalExperience,
      keySkills: Array.from(skillsSet),
      noticePeriod,
      location,
      annualSalary,
      snippet
    };
  }

  function scrapeAllCandidateCards() {
    const cardElements = getCandidateCardElements();
    const candidates = cardElements.map((card, idx) => extractCandidateCardData(card, idx));
    return { count: candidates.length, candidates };
  }

  function injectCardBadge(card, evaluation) {
    const existing = card.querySelector('.naukri-ai-badge-container');
    if (existing) existing.remove();

    const recommendation = (evaluation.recommendation || 'MAYBE').toUpperCase();
    const score = parseInt(evaluation.matchPercentage, 10) || 0;

    let themeClass = 'naukri-ai-maybe';
    let icon = '⚡';

    if (recommendation.includes('WORTH') || recommendation.includes('OPEN')) {
      themeClass = 'naukri-ai-worth-opening';
      icon = '✓';
    } else if (recommendation.includes('SKIP') || recommendation.includes('REJECT')) {
      themeClass = 'naukri-ai-skip';
      icon = '✕';
    }

    card.classList.add('naukri-ai-card-highlight');
    card.setAttribute('data-ai-rec', recommendation);

    const badge = document.createElement('div');
    badge.className = `naukri-ai-badge-container ${themeClass}`;

    badge.innerHTML = `
      <div class="naukri-ai-header">
        <div class="naukri-ai-title-wrap">
          <span class="naukri-ai-tag">${icon} ${recommendation}</span>
          <span class="naukri-ai-score">${score}% Match</span>
        </div>
        <div class="naukri-ai-branding">🤖 Gemini AI Matcher</div>
      </div>
      <div class="naukri-ai-reasoning">
        ${evaluation.reasoning || 'Evaluated against job requirements.'}
      </div>
    `;

    const targetHeader = card.querySelector('.title, .designation, h2, h3, header') || card.firstChild;
    if (targetHeader && targetHeader.nextSibling) {
      targetHeader.parentNode.insertBefore(badge, targetHeader.nextSibling);
    } else {
      card.prepend(badge);
    }
  }

  function renderCardEvaluations(evaluations) {
    if (!evaluations || !Array.isArray(evaluations)) return 0;

    let injectedCount = 0;
    evaluations.forEach(ev => {
      if (!ev || !ev.candidateId) return;
      const card = document.querySelector(`[data-naukri-ai-id="${CSS.escape(ev.candidateId)}"]`);
      if (card) {
        injectCardBadge(card, ev);
        injectedCount++;
      }
    });

    return injectedCount;
  }

  // ==========================================
  // SECTION 2: ROBUST DEEP PROFILE & RESUME AUDITOR
  // ==========================================

  function scrapeDetailedCandidateProfile() {
    const candidateId = document.querySelector('[data-candidate-id], [data-cid]')?.getAttribute('data-candidate-id') ||
      document.querySelector('[id*="candidate"]')?.id ||
      `cand_profile_${Date.now()}`;

    // Candidate Name
    const nameEl = document.querySelector('.cand-name, .user-name, .name, [class*="candidate-name"], [class*="candName"], h1, .profile-name');
    const fullName = cleanText(nameEl ? nameEl.innerText : 'Candidate');

    // Current Designation
    const desigEl = document.querySelector('.designation, [class*="designation"], .current-role, .subTitle, h2, h3');
    const currentDesignation = cleanText(desigEl ? desigEl.innerText : 'Current Role');

    // Total Experience
    const expEl = document.querySelector('.exp, [class*="exp"], [class*="experience"], [title*="Yrs"]');
    let totalExperience = cleanText(expEl ? expEl.innerText : '');
    if (!totalExperience) {
      const expMatch = document.body.innerText.match(/\b\d+(\.\d+)?\s*(?:to\s*\d+(\.\d+)?)?\s*(?:Yrs|Years|yr|yrs)\b/i);
      totalExperience = expMatch ? expMatch[0] : 'Experience not specified';
    }

    // Notice Period
    let noticePeriod = 'Not specified';
    const noticeMatch = document.body.innerText.match(/(?:notice\s*period|notice)\s*[:\-]?\s*([0-9]+\s*(?:days|months|day|month)|immediate|serving\s*notice)/i);
    if (noticeMatch) {
      noticePeriod = cleanText(noticeMatch[1]);
    } else {
      const directMatch = document.body.innerText.match(/\b(immediate|15\s*days|30\s*days|1\s*month|2\s*months|3\s*months|serving\s*notice)\b/i);
      if (directMatch) noticePeriod = cleanText(directMatch[0]);
    }

    // Location & Salary
    const locEl = document.querySelector('.loc, [class*="location"]');
    const currentLocation = cleanText(locEl ? locEl.innerText : 'Location not specified');

    const salEl = document.querySelector('.salary, [class*="salary"]');
    const annualSalary = cleanText(salEl ? salEl.innerText : 'Not disclosed');

    // Skills
    const skillElements = document.querySelectorAll('.tags-gt, .tags, [class*="skill"], .skill-tag, .chip, [class*="tag"]');
    const keySkills = [];
    skillElements.forEach(el => {
      const s = cleanText(el.innerText);
      if (s && s.length < 50 && !keySkills.includes(s)) keySkills.push(s);
    });

    // Work Experiences & Projects
    const expContainers = Array.from(document.querySelectorAll(
      '.experience-sec, .work-history-item, .exp-item, .job-detail, .experience-card, [class*="experience-item"], [class*="work-history"], .timeline-item'
    ));

    const experiences = [];
    if (expContainers.length > 0) {
      expContainers.forEach(container => {
        const compEl = container.querySelector('.company, .org, [class*="company"], h3, h4');
        const roleEl = container.querySelector('.designation, .role, [class*="designation"]');
        const durEl = container.querySelector('.duration, .dates, [class*="duration"], [class*="date"]');
        const descEl = container.querySelector('.desc, .description, [class*="description"], ul, p');

        experiences.push({
          company: cleanText(compEl ? compEl.innerText : 'Company'),
          designation: cleanText(roleEl ? roleEl.innerText : currentDesignation),
          duration: cleanText(durEl ? durEl.innerText : ''),
          description: cleanText(descEl ? descEl.innerText : container.innerText)
        });
      });
    }

    // Capture main container or full body text so Gemini always has full resume context
    const profileContainer = document.querySelector('.profile-container, .cand-details, .resume-container, main, article') || document.body;
    const fullProfileRawText = cleanText(profileContainer.innerText.slice(0, 5000));

    if (experiences.length === 0) {
      experiences.push({
        company: 'Career History',
        designation: currentDesignation,
        duration: totalExperience,
        description: fullProfileRawText.slice(0, 3000)
      });
    }

    const headlineEl = document.querySelector('.resume-headline, .summary, [class*="headline"]');
    const resumeHeadline = cleanText(headlineEl ? headlineEl.innerText : '');

    return {
      candidateId,
      fullName,
      currentDesignation,
      totalExperience,
      noticePeriod,
      currentLocation,
      annualSalary,
      keySkills,
      experiences,
      resumeHeadline,
      fullProfileRawText
    };
  }

  /**
   * Runs the Deep Profile Audit, managing UI and API directly on the page
   */
  async function runDeepProfileAudit(apiUrl, jobDescription) {
    // 1. Immediately create and display the Loading Modal on the page
    let modal = document.getElementById('naukriAiAuditModal');
    if (!modal) {
      modal = document.createElement('div');
      modal.id = 'naukriAiAuditModal';
      modal.className = 'naukri-ai-modal-overlay';
      document.body.appendChild(modal);
    }

    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score">...</span>
            <div>
              <div class="naukri-ai-modal-name">🔍 Deep Career Technology Audit</div>
              <span class="naukri-ai-modal-rec rec-caution">Analyzing with Gemini AI...</span>
            </div>
          </div>
          <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
        </div>

        <div class="naukri-ai-modal-body" style="text-align: center; padding: 40px 20px;">
          <div class="naukri-ai-audit-spinner"></div>
          <h3 style="margin-top: 16px; font-size: 16px; color: #0f172a;">Auditing Candidate Project History & Technology Depth...</h3>
          <p style="font-size: 13px; color: #64748b; margin-top: 6px;">
            Checking whether required technologies were actually used in production vs merely keyword-stuffed.
          </p>
          <div id="auditStepStatus" style="font-size: 12px; color: #0284c7; margin-top: 14px; font-weight: 600;">
            Scraping profile and work history...
          </div>
        </div>
      </div>
    `;

    modal.querySelector('#closeAuditModalBtn').addEventListener('click', () => modal.remove());

    try {
      const stepStatus = modal.querySelector('#auditStepStatus');
      const profile = scrapeDetailedCandidateProfile();

      if (stepStatus) {
        stepStatus.textContent = `Connecting to .NET 10 API (${profile.fullName || 'Candidate'})...`;
      }

      const requestPayload = {
        jobDescription: jobDescription,
        profile: profile
      };

      const auditEndpoint = `${apiUrl.replace(/\/+$/, '')}/api/analyze-profile`;
      const auditResult = await callApiViaBackground(auditEndpoint, requestPayload);
      renderAuditSuccess(modal, auditResult);

    } catch (err) {
      console.error('[Deep Audit Failure]', err);
      renderAuditError(modal, err.message || 'Error occurred while analyzing profile.', apiUrl, jobDescription);
    }
  }

  function callApiViaBackground(url, payload) {
    return new Promise((resolve, reject) => {
      try {
        chrome.runtime.sendMessage({
          action: 'CALL_API',
          url: url,
          method: 'POST',
          body: payload
        }, (response) => {
          if (chrome.runtime.lastError || !response) {
            directFetch(url, payload).then(resolve).catch(reject);
            return;
          }

          if (response.success) {
            resolve(response.data);
          } else {
            reject(new Error(response.error || 'Failed to connect to local API'));
          }
        });
      } catch (err) {
        directFetch(url, payload).then(resolve).catch(reject);
      }
    });
  }

  async function directFetch(url, payload) {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json'
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`API error HTTP ${res.status}: ${text || res.statusText}`);
    }
    return await res.json();
  }

  function renderAuditSuccess(modal, audit) {
    const score = audit.overallSuitabilityScore || 0;
    const rec = (audit.recommendation || 'EVALUATED').toUpperCase();

    let recClass = 'rec-caution';
    if (rec.includes('WORTH') || rec.includes('PROCEED')) recClass = 'rec-proceed';
    else if (rec.includes('NOT') || rec.includes('REJECT')) recClass = 'rec-reject';

    // Technology Career Authenticity items
    let techRowsHtml = '';
    (audit.techAudits || []).forEach(tech => {
      const isVerified = tech.actuallyUsedInCareer;
      const badgeClass = isVerified ? 'tech-verified' : 'tech-stuffed';
      const badgeText = isVerified ? '✓ Evidenced in Projects' : '⚠️ Keyword Only / Not Evident';

      techRowsHtml += `
        <div class="naukri-ai-tech-item">
          <div class="naukri-ai-tech-header">
            <span class="naukri-ai-tech-name">${tech.technology}</span>
            <span class="naukri-ai-tech-badge ${badgeClass}">${badgeText} (${tech.depthLevel || 'Assessed'})</span>
          </div>
          <div class="naukri-ai-tech-evidence">${tech.careerEvidence || 'No career evidence provided.'}</div>
        </div>
      `;
    });

    let strengthsHtml = (audit.strengths || []).map(s => `<li>${s}</li>`).join('') || '<li>Standard technical profile.</li>';
    let redFlagsHtml = (audit.redFlags || []).map(r => `<li>${r}</li>`).join('') || '<li>No major red flags detected.</li>';
    let questionsHtml = (audit.interviewQuestions || []).map((q, i) => `
      <div class="naukri-ai-q-item"><strong>Q${i + 1}:</strong> ${q}</div>
    `).join('') || '<div class="naukri-ai-q-item">Verify recent project architecture.</div>';

    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score">${score}%</span>
            <div>
              <div class="naukri-ai-modal-name">${audit.candidateName || 'Candidate Profile'}</div>
              <span class="naukri-ai-modal-rec ${recClass}">Verdict: ${rec}</span>
            </div>
          </div>
          <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
        </div>

        <div class="naukri-ai-modal-body">
          <!-- Executive Summary -->
          <div>
            <div class="naukri-ai-section-title">📌 Executive Assessment</div>
            <div class="naukri-ai-exec-summary">${audit.executiveSummary || 'Profile evaluated against target job.'}</div>
          </div>

          <!-- Technology Career Authenticity -->
          <div>
            <div class="naukri-ai-section-title">🔍 Technology Career Authenticity Audit</div>
            <div class="naukri-ai-tech-grid">${techRowsHtml}</div>
          </div>

          <!-- Strengths & Red Flags -->
          <div class="naukri-ai-dual-grid">
            <div class="naukri-ai-pill-box box-strengths">
              <div class="naukri-ai-section-title" style="color: #166534;">✓ Key Strengths</div>
              <ul class="naukri-ai-list">${strengthsHtml}</ul>
            </div>
            <div class="naukri-ai-pill-box box-redflags">
              <div class="naukri-ai-section-title" style="color: #991b1b;">⚠️ Red Flags & Gaps</div>
              <ul class="naukri-ai-list">${redFlagsHtml}</ul>
            </div>
          </div>

          <!-- Probing Questions -->
          <div>
            <div class="naukri-ai-section-title">🎯 Targeted Interview Screening Questions</div>
            <div class="naukri-ai-questions-box">${questionsHtml}</div>
          </div>
        </div>

        <div class="naukri-ai-modal-footer">
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-copy" id="copyAuditReportBtn">
            📋 Copy Audit Summary
          </button>
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-close" id="dismissAuditModalBtn">
            Close
          </button>
        </div>
      </div>
    `;

    modal.querySelector('#closeAuditModalBtn').addEventListener('click', () => modal.remove());
    modal.querySelector('#dismissAuditModalBtn').addEventListener('click', () => modal.remove());
    modal.addEventListener('click', (e) => {
      if (e.target === modal) modal.remove();
    });

    modal.querySelector('#copyAuditReportBtn').addEventListener('click', () => {
      const summaryText = `Candidate: ${audit.candidateName}\nScore: ${score}%\nVerdict: ${rec}\n\nSummary:\n${audit.executiveSummary}\n\nKey Strengths:\n${(audit.strengths || []).map(s => `- ${s}`).join('\n')}\n\nRed Flags:\n${(audit.redFlags || []).map(r => `- ${r}`).join('\n')}`;
      navigator.clipboard.writeText(summaryText);
      const btn = modal.querySelector('#copyAuditReportBtn');
      btn.textContent = '✓ Copied to Clipboard!';
      setTimeout(() => btn.textContent = '📋 Copy Audit Summary', 2500);
    });
  }

  function renderAuditError(modal, errorMsg, apiUrl, jobDescription) {
    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score" style="background: #dc2626;">!</span>
            <div>
              <div class="naukri-ai-modal-name">Deep Audit Notice</div>
              <span class="naukri-ai-modal-rec rec-reject">Audit Incomplete</span>
            </div>
          </div>
          <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
        </div>

        <div class="naukri-ai-modal-body" style="padding: 24px;">
          <div style="background: #fef2f2; border: 1px solid #fecaca; border-radius: 8px; padding: 14px; color: #b91c1c; font-size: 13px; line-height: 1.5;">
            <strong>Unable to complete career audit:</strong><br/>
            ${errorMsg}
          </div>
          <p style="margin-top: 14px; font-size: 12px; color: #64748b;">
            Ensure that your .NET 10 API is running at <code>${apiUrl}</code> and that an opened candidate profile or resume is active on this tab.
          </p>
        </div>

        <div class="naukri-ai-modal-footer">
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-copy" id="retryAuditBtn">
            ↻ Try Again
          </button>
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-close" id="dismissAuditModalBtn">
            Close
          </button>
        </div>
      </div>
    `;

    modal.querySelector('#closeAuditModalBtn').addEventListener('click', () => modal.remove());
    modal.querySelector('#dismissAuditModalBtn').addEventListener('click', () => modal.remove());
    modal.querySelector('#retryAuditBtn').addEventListener('click', () => {
      runDeepProfileAudit(apiUrl, jobDescription);
    });
  }

  // Floating Action Button on Candidate Profile Pages
  function injectProfilePageFloatingButton() {
    if (!isCandidateProfilePage() || document.getElementById('naukriAiFloatAuditBtn')) return;

    const floatBtn = document.createElement('button');
    floatBtn.id = 'naukriAiFloatAuditBtn';
    floatBtn.className = 'naukri-ai-profile-float-btn';
    floatBtn.innerHTML = `<span>⚡</span> AI Deep Audit Profile`;
    floatBtn.title = 'Click to deeply audit this candidate\'s career technology usage with Gemini';

    floatBtn.addEventListener('click', () => {
      chrome.storage.local.get([
        'naukri_api_url',
        'naukri_job_title',
        'naukri_mandatory_skills',
        'naukri_max_notice',
        'naukri_job_desc'
      ], (res) => {
        const apiUrl = res.naukri_api_url || 'http://localhost:5000';
        const jobDescription = {
          title: res.naukri_job_title || 'Target Role',
          rawText: res.naukri_job_desc || 'Software Engineer',
          mandatorySkills: (res.naukri_mandatory_skills || '').split(',').map(s => s.trim()).filter(Boolean),
          maxNoticePeriodDays: parseInt(res.naukri_max_notice, 10) || 30
        };
        runDeepProfileAudit(apiUrl, jobDescription);
      });
    });

    document.body.appendChild(floatBtn);
  }

  if (isCandidateProfilePage()) {
    injectProfilePageFloatingButton();
  }

  // ==========================================
  // SECTION 3: EXTENSION MESSAGE LISTENER
  // ==========================================
  chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    if (request.action === 'PING') {
      const cards = getCandidateCardElements();
      const isProf = isCandidateProfilePage();
      sendResponse({ status: 'ready', cardCount: cards.length, isProfilePage: isProf });
      return;
    }

    if (request.action === 'SCRAPE_CANDIDATES') {
      const result = scrapeAllCandidateCards();
      sendResponse({ success: true, count: result.count, candidates: result.candidates });
      return;
    }

    if (request.action === 'INJECT_EVALUATIONS') {
      const count = renderCardEvaluations(request.evaluations);
      sendResponse({ success: true, injectedCount: count });
      return;
    }

    if (request.action === 'START_DEEP_AUDIT') {
      runDeepProfileAudit(request.apiUrl || 'http://localhost:5000', request.jobDescription);
      sendResponse({ started: true });
      return;
    }
  });
})();
