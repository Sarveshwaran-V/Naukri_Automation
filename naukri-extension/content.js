/**
 * Naukri AI Candidate Matcher - Content Script
 * 1. Search Cards: In-place assessment badges with clean SaaS styling.
 * 2. Detailed Profile: Deep Career Technology & Resume Audit.
 */

(function () {
  // If running inside a child frame (e.g. cv-iframe on Naukri)
  if (window !== window.top) {
    window.addEventListener('message', (event) => {
      if (event.data && event.data.type === 'NAUKRI_AI_REQUEST_CV_TEXT') {
        const text = document.body ? (document.body.innerText || document.body.textContent || '') : '';
        if (event.source && event.source.postMessage) {
          event.source.postMessage({
            type: 'NAUKRI_AI_CV_TEXT_RESPONSE',
            text: text ? text.replace(/\s+/g, ' ').trim() : ''
          }, '*');
        }
      }
    });
    return; // Do not execute top-level UI or message listener in child iframes
  }

  console.log('[Naukri AI Matcher] Content script initialized in top frame.');

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

  function isLinkedInPlatform() {
    return window.location.hostname.includes('linkedin.com');
  }

  function isCandidateProfilePage() {
    if (isLinkedInPlatform()) {
      return !!(
        window.location.pathname.includes('/talent/') ||
        window.location.pathname.includes('/recruiter/') ||
        window.location.pathname.includes('/in/') ||
        document.querySelector('.profile-topcard, .profile-body, [data-live-test-component*="topcard"], [data-live-test-component*="profile"], .artdeco-entity-lockup, #profile-content, .talent-profile-view, .candidate-drawer, .core-rail')
      );
    }
    return !!(
      document.querySelector('.profile-container, .cand-details, #candidate-details, .resdex-profile, .resume-container, .full-profile-view, [data-page-type="candidate-profile"]') ||
      (document.querySelector('.work-experience, .experience-sec, [class*="experience"]') && !document.querySelector('.tuple'))
    );
  }

  function hasAttachedResumeOnPage() {
    const iframe = document.getElementById('cv-iframe') ||
                   document.querySelector('iframe[title*="Attached CV" i], iframe[id*="cv" i], iframe[src*="resume" i], iframe[src*="preview" i], iframe[title*="resume" i]');
    if (iframe) return true;
    const direct = document.querySelector('.textLayer, .pdfViewer, #viewerContainer, .resume-container, [class*="resume-preview"], [class*="cv-viewer"], [data-live-test-component="resume-viewer"], .resume-viewer');
    if (direct) return true;
    return false;
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

    let noticePeriod = 'Not specified';
    const noticeEl = card.querySelector(
      '[class*="notice"], [class*="Notice"], .notice-period, [title*="Notice"], [title*="notice"], [data-notice], [class*="availability"], [title*="Availability"]'
    );
    if (noticeEl) {
      const rawNoticeText = cleanText(noticeEl.innerText);
      const parsedMatch = rawNoticeText.match(/(?:notice\s*period|notice|availability)\s*[:\-–]?\s*(\b\d+\s*(?:days?|months?|mths?)\b|immediate(?:ly)?|serving\s*notice)/i);
      noticePeriod = parsedMatch ? cleanText(parsedMatch[1]) : (rawNoticeText || 'Not specified');
    } else {
      const strictNoticeMatch = card.innerText.match(/(?:notice\s*period|notice|available\s*(?:to\s*join\s*)?(?:in|within)?|availability)\s*[:\-–]?\s*(\b\d+\s*(?:days?|months?|mths?)\b|immediate(?:ly)?|serving\s*notice)/i);
      if (strictNoticeMatch) {
        noticePeriod = cleanText(strictNoticeMatch[1]);
      } else {
        const explicitServingMatch = card.innerText.match(/\b(serving\s*notice|immediate\s*joiner|immediate\s*availability)\b/i);
        if (explicitServingMatch) {
          noticePeriod = cleanText(explicitServingMatch[0]);
        } else {
          noticePeriod = 'Not specified';
        }
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

    let themeClass = 'badge-review';
    let statusLabel = 'Review';

    if (recommendation.includes('WORTH') || recommendation.includes('OPEN')) {
      themeClass = 'badge-fit';
      statusLabel = 'Strong Fit';
    } else if (recommendation.includes('SKIP') || recommendation.includes('REJECT')) {
      themeClass = 'badge-pass';
      statusLabel = 'Not a Fit';
    }

    card.classList.add('naukri-ai-card-highlight');
    card.setAttribute('data-ai-rec', recommendation);

    const badge = document.createElement('div');
    badge.className = `naukri-ai-badge-container ${themeClass}`;

    badge.innerHTML = `
      <div class="naukri-ai-header">
        <div class="naukri-ai-title-wrap">
          <span class="naukri-ai-score-pill">${score}%</span>
          <span class="naukri-ai-tag">${statusLabel}</span>
        </div>
        <span class="naukri-ai-branding" style="font-family:-apple-system,BlinkMacSystemFont,'Inter','Segoe UI',sans-serif;letter-spacing:-0.02em;font-size:11px;font-weight:600;color:#64748b;">c<span style="font-weight:850;color:#2563eb;background:linear-gradient(135deg,#2563eb,#7c3aed);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">A</span>nb<span style="font-weight:850;color:#2563eb;background:linear-gradient(135deg,#2563eb,#7c3aed);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">I</span>date</span>
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

  const EXCLUDED_SIDEBAR_SELECTORS = [
    'aside',
    '.sidebar',
    '[class*="sidebar"]',
    '[class*="Sidebar"]',
    '.right-panel',
    '.right-section',
    '.right-rail',
    '.right-container',
    '.right-col',
    '.right-column',
    '[class*="rightPanel"]',
    '[class*="rightSection"]',
    '[class*="rightRail"]',
    '[class*="rightContainer"]',
    '[class*="right-panel"]',
    '[class*="right-section"]',
    '[class*="right-rail"]',
    '[class*="right-container"]',
    '[class*="similar"]',
    '[class*="Similar"]',
    '[id*="similar"]',
    '[id*="Similar"]',
    '[class*="recommend"]',
    '[class*="Recommend"]',
    '[id*="recommend"]',
    '[class*="suggest"]',
    '[class*="Suggest"]',
    '[class*="other-candidate"]',
    '[class*="otherCandidate"]',
    '[class*="other-cand"]',
    '[class*="related-candidate"]',
    '[class*="related-cand"]',
    '.srp-tuple',
    '.resdex-tuple',
    '.tuple-box',
    '#naukriAiAuditModal',
    '.naukri-ai-modal-overlay'
  ];

  function isInsideExcludedSidebar(el) {
    if (!el || el === document.body || el === document.documentElement) return false;
    for (const sel of EXCLUDED_SIDEBAR_SELECTORS) {
      try {
        if (el.matches && el.matches(sel)) return true;
        if (el.closest && el.closest(sel)) return true;
      } catch (e) {}
    }
    return false;
  }

  function getPrimaryCandidateContainer() {
    const candidateContainerSelectors = [
      // LinkedIn Recruiter & Profile Containers
      '[data-live-test-component="profile-body"]',
      '[data-live-test-component="profile-container"]',
      '.profile-body',
      '.profile-topcard',
      '.artdeco-card',
      '#profile-content',
      '.talent-profile-view',
      '.candidate-drawer',
      '.core-rail',
      // Naukri Containers
      '#candidate-details',
      '.cand-details',
      '.resdex-profile',
      '.full-profile-view',
      '.resume-container',
      '[data-page-type="candidate-profile"]',
      '.left-container',
      '.profile-left',
      '[class*="profile-left"]',
      '[class*="profileLeft"]',
      '[class*="cand-details-main"]',
      '[class*="profile-main"]',
      '[class*="profileMain"]',
      '.profile-container',
      'main',
      'article'
    ];

    for (const sel of candidateContainerSelectors) {
      const candidates = Array.from(document.querySelectorAll(sel));
      for (const el of candidates) {
        if (!isInsideExcludedSidebar(el)) {
          return el;
        }
      }
    }
    return document.body;
  }

  /**
   * Extracts text directly from the attached CV viewer or iframe (e.g. cv-iframe on Naukri)
   */
  function extractAttachedCvText() {
    const cvIframe = document.getElementById('cv-iframe') ||
                     document.querySelector('iframe[title*="Attached CV" i], iframe[id*="cv" i], iframe[src*="resume" i], iframe[src*="preview" i]');

    if (cvIframe) {
      try {
        const iframeDoc = cvIframe.contentDocument || cvIframe.contentWindow?.document;
        if (iframeDoc && iframeDoc.body) {
          const spans = Array.from(iframeDoc.querySelectorAll('span, p, div, li'));
          let text = '';
          if (spans.length > 5) {
            text = cleanText(iframeDoc.body.innerText || iframeDoc.body.textContent || '');
          } else {
            text = cleanText(iframeDoc.body.textContent || iframeDoc.body.innerText || '');
          }
          if (text && text.length > 40) {
            console.log(`[Naukri AI Matcher] Extracted ${text.length} chars from attached CV iframe (${cvIframe.id || cvIframe.title}).`);
            return text;
          }
        }
      } catch (err) {
        console.warn('[Naukri AI Matcher] Direct access to cv-iframe was blocked by cross-origin policy:', err);
      }
    }

    const directViewer = document.querySelector('.textLayer, .pdfViewer, #viewerContainer, .resume-container, [class*="resume-preview"], [class*="cv-viewer"], [data-live-test-component="resume-viewer"], .resume-viewer');
    if (directViewer) {
      const text = cleanText(directViewer.innerText || directViewer.textContent || '');
      if (text && text.length > 50) {
        console.log(`[cAnbIdate] Extracted ${text.length} chars from direct resume viewer container.`);
        return text;
      }
    }

    return '';
  }

  function scrapeDetailedCandidateProfile() {
    const primaryContainer = getPrimaryCandidateContainer();

    const candidateId = primaryContainer.querySelector('[data-candidate-id], [data-cid]')?.getAttribute('data-candidate-id') ||
      document.querySelector('[data-candidate-id], [data-cid]')?.getAttribute('data-candidate-id') ||
      primaryContainer.id ||
      `cand_profile_${Date.now()}`;

    // Candidate Name
    const nameCandidateEls = Array.from(primaryContainer.querySelectorAll(
      '.cand-name, .user-name, .name, [class*="candidate-name"], [class*="candName"], .topcard-condensed__title, .artdeco-entity-lockup__title, .profile-topcard-person-details__name, h1.text-heading-xlarge, [data-control-name="topcard_name"], h1, .profile-name'
    )).filter(el => !isInsideExcludedSidebar(el));
    const fullName = cleanText(nameCandidateEls.length > 0 ? nameCandidateEls[0].innerText : 'Candidate');

    // Current Designation
    const desigCandidateEls = Array.from(primaryContainer.querySelectorAll(
      '.designation, [class*="designation"], .current-role, .subTitle, .topcard__headline, .profile-topcard-person-details__headline, .artdeco-entity-lockup__subtitle, .text-body-medium, h2, h3'
    )).filter(el => !isInsideExcludedSidebar(el));
    const currentDesignation = cleanText(desigCandidateEls.length > 0 ? desigCandidateEls[0].innerText : 'Current Role');

    // Total Experience
    const expCandidateEls = Array.from(primaryContainer.querySelectorAll('.exp, [class*="exp"], [class*="experience"], [title*="Yrs"]'))
      .filter(el => !isInsideExcludedSidebar(el));
    let totalExperience = cleanText(expCandidateEls.length > 0 ? expCandidateEls[0].innerText : '');
    if (!totalExperience) {
      const expMatch = primaryContainer.innerText.match(/\b\d+(\.\d+)?\s*(?:to\s*\d+(\.\d+)?)?\s*(?:Yrs|Years|yr|yrs)\b/i);
      totalExperience = expMatch ? expMatch[0] : 'Experience not specified';
    }

    // Clean cloned text to ensure no right-side candidate data leaks into analysis
    const cleanClone = primaryContainer.cloneNode(true);
    EXCLUDED_SIDEBAR_SELECTORS.forEach(sel => {
      try {
        cleanClone.querySelectorAll(sel).forEach(el => el.remove());
      } catch (e) {}
    });
    cleanClone.querySelectorAll('script, style, button, svg, noscript, nav, header, #naukriAiAuditModal').forEach(el => el.remove());

    const attachedCvText = extractAttachedCvText();
    let fullProfileRawText = cleanText(cleanClone.innerText);
    if (attachedCvText) {
      fullProfileRawText += '\n\n=== ATTACHED RESUME / CV TEXT (FROM CV-IFRAME) ===\n' + attachedCvText;
    }
    if (fullProfileRawText.length > 50000) {
      fullProfileRawText = fullProfileRawText.slice(0, 50000);
    }

    // Notice Period
    let noticePeriod = 'Not specified';
    const noticeCandidateEls = Array.from(primaryContainer.querySelectorAll(
      '[class*="notice"], [class*="Notice"], .notice-period, [title*="Notice"], [title*="notice"], [data-notice], [class*="availability"], [title*="Availability"]'
    )).filter(el => !isInsideExcludedSidebar(el));

    if (noticeCandidateEls.length > 0) {
      const rawText = cleanText(noticeCandidateEls[0].innerText);
      const parsedMatch = rawText.match(/(?:notice\s*period|notice|availability)\s*[:\-–]?\s*(\b\d+\s*(?:days?|months?|mths?)\b|immediate(?:ly)?|serving\s*notice)/i);
      noticePeriod = parsedMatch ? cleanText(parsedMatch[1]) : (rawText || 'Not specified');
    } else {
      const strictNoticeMatch = fullProfileRawText.match(/(?:notice\s*period|notice|available\s*(?:to\s*join)?\s*(?:in|within)?|availability)\s*[:\-–]?\s*(\b\d+\s*(?:days?|months?|mths?)\b|immediate(?:ly)?|serving\s*notice)/i);
      if (strictNoticeMatch) {
        noticePeriod = cleanText(strictNoticeMatch[1]);
      } else {
        const servingMatch = fullProfileRawText.match(/\b(serving\s*notice|immediate\s*joiner|immediate\s*availability)\b/i);
        if (servingMatch) {
          noticePeriod = cleanText(servingMatch[0]);
        } else {
          noticePeriod = 'Not specified';
        }
      }
    }

    // Location & Salary
    const locEls = Array.from(primaryContainer.querySelectorAll('.loc, [class*="location"]')).filter(el => !isInsideExcludedSidebar(el));
    const currentLocation = cleanText(locEls.length > 0 ? locEls[0].innerText : 'Location not specified');

    const salEls = Array.from(primaryContainer.querySelectorAll('.salary, [class*="salary"]')).filter(el => !isInsideExcludedSidebar(el));
    const annualSalary = cleanText(salEls.length > 0 ? salEls[0].innerText : 'Not disclosed');

    // Skills
    const skillElements = Array.from(primaryContainer.querySelectorAll('.tags-gt, .tags, [class*="skill"], .skill-tag, .chip, [class*="tag"]'))
      .filter(el => !isInsideExcludedSidebar(el));
    const keySkills = [];
    skillElements.forEach(el => {
      const s = cleanText(el.innerText);
      if (s && s.length < 50 && !keySkills.includes(s)) keySkills.push(s);
    });

    // Work Experiences & Projects
    const expContainers = Array.from(primaryContainer.querySelectorAll(
      '.experience-sec, .work-history-item, .exp-item, .job-detail, .experience-card, [class*="experience-item"], [class*="work-history"], .timeline-item, .project-details, .projects-sec, [class*="project-item"]'
    )).filter(el => !isInsideExcludedSidebar(el));

    const experiences = [];
    if (expContainers.length > 0) {
      expContainers.forEach(container => {
        const compEl = container.querySelector('.company, .org, [class*="company"], h3, h4');
        const roleEl = container.querySelector('.designation, .role, [class*="designation"]');
        const durEl = container.querySelector('.duration, .dates, [class*="duration"], [class*="date"]');
        const descEls = Array.from(container.querySelectorAll('.desc, .description, [class*="description"], ul, p'));
        const desc = descEls.length > 0 ? cleanText(descEls.map(d => d.innerText).join(' ')) : cleanText(container.innerText);

        experiences.push({
          company: cleanText(compEl ? compEl.innerText : 'Company'),
          designation: cleanText(roleEl ? roleEl.innerText : currentDesignation),
          duration: cleanText(durEl ? durEl.innerText : ''),
          description: desc
        });
      });
    }

    if (experiences.length === 0) {
      experiences.push({
        company: 'Career History',
        designation: currentDesignation,
        duration: totalExperience,
        description: fullProfileRawText.slice(0, 10000)
      });
    }

    const headlineCandidateEls = Array.from(primaryContainer.querySelectorAll('.resume-headline, .summary, [class*="headline"]'))
      .filter(el => !isInsideExcludedSidebar(el));
    const resumeHeadline = cleanText(headlineCandidateEls.length > 0 ? headlineCandidateEls[0].innerText : '');

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
   * Runs the Deep Profile Audit (Supports both Naukri & LinkedIn Recruiter)
   */
  async function runDeepProfileAudit(apiUrl, jobDescription, explicitApiKey = null, platform = 'auto') {
    const isLinkedIn = platform === 'linkedin_recruiter' || (platform === 'auto' && isLinkedInPlatform());

    let modal = document.getElementById('naukriAiAuditModal');
    if (!modal) {
      modal = document.createElement('div');
      modal.id = 'naukriAiAuditModal';
      modal.className = 'naukri-ai-modal-overlay';
      document.body.appendChild(modal);
    }

    const modalTitle = isLinkedIn ? 'LinkedIn Recruiter Evaluation' : 'Candidate Evaluation';
    const auditSubtitle = isLinkedIn ? 'Auditing LinkedIn Recruiter Profile & Resume' : 'Auditing Career Technology Usage & Resume';

    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score">...</span>
            <div>
              <div class="naukri-ai-modal-name">${modalTitle}</div>
              <span class="naukri-ai-modal-rec rec-caution">Analyzing with AI...</span>
            </div>
          </div>
          <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
        </div>
        <div class="naukri-ai-modal-body" style="padding: 32px; text-align: center; color: #475569;">
          <div style="font-size: 14px; font-weight: 600; color: #0f172a; margin-bottom: 6px;">
            ${auditSubtitle}
          </div>
          <div style="font-size: 12.5px; color: #64748b;">
            Cross-verifying mandatory skills against production experience and attached resume...
          </div>
        </div>
      </div>
    `;

    modal.querySelector('#closeAuditModalBtn').addEventListener('click', () => modal.remove());

    try {
      let apiKey = explicitApiKey;
      if (!apiKey) {
        const stored = await chrome.storage.local.get(['naukri_gemini_api_key']);
        apiKey = stored.naukri_gemini_api_key || '';
      }

      const candidateProfile = scrapeDetailedCandidateProfile();
      const cleanBase = apiUrl.replace(/\/+$/, '');
      const endpoint = isLinkedIn ?
        `${cleanBase}/api/evaluate/linkedin-recruiter` :
        `${cleanBase}/api/analyze-profile`;

      const requestPayload = {
        jobDescription: {
          title: jobDescription.title || 'Role',
          rawText: jobDescription.rawText || '',
          mandatorySkills: jobDescription.mandatorySkills || [],
          preferredSkills: jobDescription.preferredSkills || [],
          maxNoticePeriodDays: jobDescription.maxNoticePeriodDays || 0
        },
        profile: candidateProfile
      };

      const headers = {};
      if (apiKey) {
        headers['X-Gemini-Api-Key'] = apiKey;
      }
      if (isLinkedIn) {
        headers['X-Platform'] = 'linkedin_recruiter';
      }

      let auditResult;
      try {
        auditResult = await callApiThroughExtension(endpoint, requestPayload, headers);
      } catch (endpointErr) {
        // If the dedicated endpoint is not found (404) or not implemented, seamlessly fall back to analyze-profile
        if (isLinkedIn && endpointErr.message && (endpointErr.message.includes('404') || endpointErr.message.includes('501'))) {
          console.warn('[cAnbIdate] Falling back from /api/evaluate/linkedin-recruiter to /api/analyze-profile');
          const fallbackEndpoint = `${cleanBase}/api/analyze-profile`;
          auditResult = await callApiThroughExtension(fallbackEndpoint, requestPayload, headers);
        } else {
          throw endpointErr;
        }
      }

      renderAuditSuccess(modal, auditResult);
    } catch (err) {
      console.error('[Deep Profile Audit Error]', err);
      renderAuditError(modal, err.message, apiUrl, jobDescription, isLinkedIn ? 'linkedin_recruiter' : 'naukri');
    }
  }

  async function callApiThroughExtension(url, payload, headers = {}) {
    return new Promise((resolve, reject) => {
      try {
        chrome.runtime.sendMessage({
          action: 'CALL_API',
          url: url,
          method: 'POST',
          headers: headers,
          body: payload
        }, (response) => {
          if (chrome.runtime.lastError || !response) {
            directFetch(url, payload, headers).then(resolve).catch(reject);
            return;
          }

          if (response.success) {
            resolve(response.data);
          } else {
            reject(new Error(response.error || 'Failed to connect to local API'));
          }
        });
      } catch (err) {
        directFetch(url, payload, headers).then(resolve).catch(reject);
      }
    });
  }

  async function directFetch(url, payload, headers = {}) {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json',
        ...headers
      },
      body: JSON.stringify(payload)
    });
    const text = await res.text();
    let json;
    try { json = JSON.parse(text); } catch { json = null; }

    if (!res.ok) {
      throw new Error(json?.error || `API error HTTP ${res.status}: ${text || res.statusText}`);
    }
    return json;
  }

  function renderAuditSuccess(modal, audit) {
    const score = audit.overallSuitabilityScore || 0;
    const rec = (audit.recommendation || 'EVALUATED').toUpperCase();

    let recClass = 'rec-caution';
    let recLabel = 'Review with Caution';
    if (rec.includes('WORTH') || rec.includes('PROCEED')) {
      recClass = 'rec-proceed';
      recLabel = 'Strong Fit';
    } else if (rec.includes('NOT') || rec.includes('REJECT') || rec.includes('DO NOT')) {
      recClass = 'rec-reject';
      recLabel = 'Not a Fit';
    }

    const hasResume = hasAttachedResumeOnPage();
    const resumeBadgeHtml = hasResume 
      ? `<span class="resume-verified-badge"><span class="tick">✓</span> Attached CV Verified</span>`
      : '';

    // Technology Career Authenticity items
    let techRowsHtml = '';
    (audit.techAudits || []).forEach(tech => {
      const isVerified = tech.actuallyUsedInCareer;
      const badgeClass = isVerified ? 'tech-verified' : 'tech-stuffed';
      const badgeText = isVerified ? '✓ Verified in Projects' : 'Mentioned in Tags Only';

      techRowsHtml += `
        <div class="naukri-ai-tech-item">
          <div class="naukri-ai-tech-header">
            <span class="naukri-ai-tech-name">${tech.technology}</span>
            <span class="naukri-ai-tech-badge ${badgeClass}">${badgeText} (${tech.depthLevel || 'Assessed'})</span>
          </div>
          <div class="naukri-ai-tech-evidence">${tech.careerEvidence || 'No direct project evidence provided.'}</div>
        </div>
      `;
    });

    let strengthsHtml = (audit.strengths || []).map(s => `<li>${s}</li>`).join('') || '<li>Standard technical profile.</li>';
    let redFlagsHtml = (audit.redFlags || []).map(r => `<li>${r}</li>`).join('') || '<li>No major concerns identified.</li>';

    // Screening Questions
    let questionsHtml = '';
    const qList = audit.interviewQuestions || [];
    if (qList.length > 0) {
      qList.forEach((item, i) => {
        const isObj = typeof item === 'object' && item !== null;
        const qText = isObj ? (item.question || 'Interview question') : item;
        const diff = (isObj && item.difficulty) ? item.difficulty : (i < 2 ? 'Simple' : 'Advanced');
        const expectedAns = isObj && item.expectedAnswer ? item.expectedAnswer : 'Candidate should explain practical production experience and implementation.';
        const keywords = isObj && Array.isArray(item.expectedKeywords) ? item.expectedKeywords : [];

        const isSimple = diff.toLowerCase() === 'simple';
        const diffBadgeClass = isSimple ? 'diff-badge-simple' : 'diff-badge-advanced';

        let keywordsHtml = '';
        if (keywords.length > 0) {
          keywordsHtml = `
            <div class="naukri-ai-q-keywords-row">
              <span class="naukri-ai-keywords-label">Keywords to listen for:</span>
              <div class="naukri-ai-keywords-chips">
                ${keywords.map(kw => `<span class="naukri-ai-kw-chip">${kw}</span>`).join('')}
              </div>
            </div>
          `;
        }

        questionsHtml += `
          <div class="naukri-ai-hr-q-card">
            <div class="naukri-ai-hr-q-top">
              <div class="naukri-ai-hr-q-title-wrap">
                <span class="naukri-ai-hr-q-num">Q${i + 1}</span>
                <span class="naukri-ai-hr-q-text">${qText}</span>
              </div>
              <span class="naukri-ai-diff-badge ${diffBadgeClass}">${diff}</span>
            </div>
            <div class="naukri-ai-hr-ans-box">
              <div class="naukri-ai-hr-ans-label">Expected Candidate Answer:</div>
              <div class="naukri-ai-hr-ans-text">${expectedAns}</div>
              ${keywordsHtml}
            </div>
          </div>
        `;
      });
    } else {
      questionsHtml = '<div class="naukri-ai-tech-evidence">Ask candidate about recent production architecture and technical decisions.</div>';
    }

    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score">${score}%</span>
            <div>
              <div class="naukri-ai-modal-name">
                ${audit.candidateName || 'Candidate Profile'}
                ${resumeBadgeHtml}
              </div>
              <span class="naukri-ai-modal-rec ${recClass}">${recLabel}</span>
            </div>
          </div>
          <div style="display:flex;align-items:center;gap:12px;">
            <span style="font-size:12px;font-weight:700;color:#0f172a;letter-spacing:-0.02em;">c<span style="font-weight:850;color:#2563eb;background:linear-gradient(135deg,#2563eb,#7c3aed);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">A</span>nb<span style="font-weight:850;color:#2563eb;background:linear-gradient(135deg,#2563eb,#7c3aed);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">I</span>date</span>
            <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
          </div>
        </div>

        <div class="naukri-ai-modal-body">
          <!-- Executive Assessment -->
          <div>
            <div class="naukri-ai-section-title">Executive Assessment</div>
            <div class="naukri-ai-exec-summary">${audit.executiveSummary || 'Profile evaluated against target job.'}</div>
          </div>

          <!-- Technology Verification -->
          <div>
            <div class="naukri-ai-section-title">Technology & Skill Verification</div>
            <div class="naukri-ai-tech-grid">${techRowsHtml}</div>
          </div>

          <!-- Strengths & Concerns -->
          <div class="naukri-ai-dual-grid">
            <div class="naukri-ai-pill-box box-strengths">
              <div class="naukri-ai-section-title" style="color: #166534;">Key Strengths</div>
              <ul class="naukri-ai-list">${strengthsHtml}</ul>
            </div>
            <div class="naukri-ai-pill-box box-redflags">
              <div class="naukri-ai-section-title" style="color: #991b1b;">Areas of Concern & Gaps</div>
              <ul class="naukri-ai-list">${redFlagsHtml}</ul>
            </div>
          </div>

          <!-- Interview Questions -->
          <div>
            <div class="naukri-ai-section-title" style="display: flex; justify-content: space-between; align-items: center;">
              <span>Recommended Screening Questions</span>
              <span style="font-size: 11px; font-weight: normal; color: #64748b; text-transform: none;">2 Fundamental · 3 Advanced</span>
            </div>
            <div class="naukri-ai-questions-stack">${questionsHtml}</div>
          </div>
        </div>

        <div class="naukri-ai-modal-footer">
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-copy" id="copyAuditReportBtn">
            Copy Assessment Report
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
      let questionsCopyText = '';
      if (qList.length > 0) {
        questionsCopyText = '\n\nRECOMMENDED SCREENING QUESTIONS & KEYWORDS:\n' + qList.map((item, idx) => {
          const isObj = typeof item === 'object' && item !== null;
          const qText = isObj ? item.question : item;
          const diff = isObj && item.difficulty ? item.difficulty : (idx < 2 ? 'Simple' : 'Advanced');
          const ans = isObj && item.expectedAnswer ? item.expectedAnswer : '';
          const kws = isObj && Array.isArray(item.expectedKeywords) ? item.expectedKeywords.join(', ') : '';
          return `Q${idx + 1} [${diff}]: ${qText}\n  Expected Answer: ${ans}\n  Keywords to Listen For: ${kws}`;
        }).join('\n\n');
      }

      const summaryText = `Candidate: ${audit.candidateName}\nScore: ${score}%\nVerdict: ${recLabel}\n\nSummary:\n${audit.executiveSummary}\n\nKey Strengths:\n${(audit.strengths || []).map(s => `- ${s}`).join('\n')}\n\nAreas of Concern:\n${(audit.redFlags || []).map(r => `- ${r}`).join('\n')}${questionsCopyText}`;
      navigator.clipboard.writeText(summaryText);
      const btn = modal.querySelector('#copyAuditReportBtn');
      btn.textContent = '✓ Copied to Clipboard';
      setTimeout(() => btn.textContent = 'Copy Assessment Report', 2500);
    });
  }

  function renderAuditError(modal, errorMsg, apiUrl, jobDescription) {
    const isApiKeyError = errorMsg && (
      errorMsg.toLowerCase().includes('api key') ||
      errorMsg.toLowerCase().includes('api_key') ||
      errorMsg.toLowerCase().includes('unauthorized') ||
      errorMsg.toLowerCase().includes('401') ||
      errorMsg.toLowerCase().includes('403')
    );

    const apiKeyInjectionHtml = isApiKeyError ? `
      <div style="background: #f8fafc; border: 1px solid #cbd5e1; border-radius: 6px; padding: 14px; margin-top: 14px; text-align: left;">
        <div style="font-weight: 700; color: #0f172a; margin-bottom: 4px; font-size: 13px;">
          Inject Google Gemini API Key:
        </div>
        <div style="font-size: 11.5px; color: #64748b; margin-bottom: 10px;">
          Please enter your Gemini API key below to enable live AI candidate evaluations.
        </div>
        <div style="display: flex; gap: 8px;">
          <input 
            type="password" 
            id="modalApiKeyInput" 
            placeholder="Paste your Gemini API key (AIzaSy...)" 
            style="flex: 1; padding: 7px 10px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 12px;"
          />
          <button 
            type="button" 
            id="modalSaveApiKeyBtn" 
            style="background: #0f172a; color: #ffffff; padding: 7px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 12px; font-weight: 600;"
          >
            Save & Retry
          </button>
        </div>
        <div style="margin-top: 8px; font-size: 11px;">
          Don't have a key? <a href="https://aistudio.google.com/app/apikey" target="_blank" style="color: #2563eb; text-decoration: underline;">Get a free API key at Google AI Studio ↗</a>
        </div>
      </div>
    ` : '';

    modal.innerHTML = `
      <div class="naukri-ai-modal-card">
        <div class="naukri-ai-modal-header">
          <div class="naukri-ai-modal-title-wrap">
            <span class="naukri-ai-modal-score" style="background: #991b1b;">!</span>
            <div>
              <div class="naukri-ai-modal-name">${isApiKeyError ? 'Gemini API Key Required' : 'Audit Notice'}</div>
              <span class="naukri-ai-modal-rec rec-reject">${isApiKeyError ? 'Configuration Required' : 'Incomplete'}</span>
            </div>
          </div>
          <button type="button" class="naukri-ai-modal-close" id="closeAuditModalBtn">&times;</button>
        </div>

        <div class="naukri-ai-modal-body" style="padding: 24px;">
          <div style="background: #fffafa; border: 1px solid #fecaca; border-radius: 6px; padding: 12px; color: #991b1b; font-size: 12.5px; line-height: 1.5;">
            <strong>${isApiKeyError ? 'Gemini API Key is missing or invalid' : 'Unable to complete evaluation'}:</strong><br/>
            ${errorMsg}
          </div>

          ${apiKeyInjectionHtml}

          <p style="margin-top: 14px; font-size: 11.5px; color: #64748b;">
            Ensure that your .NET 10 API is running at <code>${apiUrl}</code> and that an opened candidate profile or resume is active on this tab.
          </p>
        </div>

        <div class="naukri-ai-modal-footer">
          <button type="button" class="naukri-ai-modal-btn naukri-ai-modal-btn-copy" id="retryAuditBtn">
            Try Again
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

    const saveKeyBtn = modal.querySelector('#modalSaveApiKeyBtn');
    if (saveKeyBtn) {
      saveKeyBtn.addEventListener('click', async () => {
        const inputKey = modal.querySelector('#modalApiKeyInput')?.value.trim();
        if (!inputKey) {
          alert('Please enter a valid Gemini API key.');
          return;
        }
        await chrome.storage.local.set({ naukri_gemini_api_key: inputKey });
        runDeepProfileAudit(apiUrl, jobDescription, inputKey);
      });
    }
  }

  // Floating Action Button on Candidate Profile Pages
  function injectProfilePageFloatingButton() {
    if (!isCandidateProfilePage() || document.getElementById('naukriAiFloatAuditBtn')) return;

    const hasResume = hasAttachedResumeOnPage();
    const resumeBadgeHtml = hasResume
      ? `<span class="float-resume-pill"><span class="float-resume-tick">✓</span> Resume detected</span>`
      : '';

    const isLinkedIn = isLinkedInPlatform();
    const btnLabel = isLinkedIn
      ? 'c<span style="font-weight:850;color:#38bdf8;background:linear-gradient(135deg,#38bdf8,#818cf8);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">A</span>nb<span style="font-weight:850;color:#38bdf8;background:linear-gradient(135deg,#38bdf8,#818cf8);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">I</span>date Audit · LinkedIn'
      : 'c<span style="font-weight:850;color:#38bdf8;background:linear-gradient(135deg,#38bdf8,#818cf8);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">A</span>nb<span style="font-weight:850;color:#38bdf8;background:linear-gradient(135deg,#38bdf8,#818cf8);-webkit-background-clip:text;-webkit-text-fill-color:transparent;">I</span>date Audit';

    const brandIconSvg = `<svg width="15" height="15" viewBox="0 0 28 28" fill="none" style="flex-shrink:0;vertical-align:middle;margin-right:3px;">
      <rect width="28" height="28" rx="7" fill="#090d16"/>
      <rect x="0.75" y="0.75" width="26.5" height="26.5" rx="6.25" stroke="rgba(255,255,255,0.2)"/>
      <path d="M19.5 9.5C18.1 7.4 15.6 6.4 12.8 6.8C9.5 7.3 6.8 10.3 6.5 13.8C6.1 17.8 9 21.2 12.9 21.5C15.8 21.7 18.2 20.3 19.6 18.5" stroke="#38bdf8" stroke-width="2.6" stroke-linecap="round"/>
      <path d="M15 9L16.2 12.8L20 14L16.2 15.2L15 19L13.8 15.2L10 14L13.8 12.8L15 9Z" fill="#38bdf8"/>
      <circle cx="15" cy="14" r="1.3" fill="#ffffff"/>
    </svg>`;

    const floatBtn = document.createElement('button');
    floatBtn.id = 'naukriAiFloatAuditBtn';
    floatBtn.className = 'naukri-ai-profile-float-btn';
    floatBtn.innerHTML = `
      ${resumeBadgeHtml}
      ${brandIconSvg}
      <span class="float-btn-label">${btnLabel}</span>
    `;
    floatBtn.title = isLinkedIn
      ? 'Evaluate candidate experience and resume from LinkedIn Recruiter'
      : 'Deeply evaluate candidate experience and skills against requirements';

    floatBtn.addEventListener('click', () => {
      chrome.storage.local.get([
        'naukri_api_url',
        'naukri_gemini_api_key',
        'naukri_job_title',
        'naukri_mandatory_skills',
        'naukri_preferred_skills',
        'naukri_max_notice',
        'naukri_job_desc'
      ], (res) => {
        const apiUrl = res.naukri_api_url || 'http://localhost:5000';
        const apiKey = res.naukri_gemini_api_key || '';
        const jobDescription = {
          title: res.naukri_job_title || 'Target Role',
          rawText: res.naukri_job_desc || 'Software Engineer',
          mandatorySkills: (res.naukri_mandatory_skills || '').split(',').map(s => s.trim()).filter(Boolean),
          preferredSkills: (res.naukri_preferred_skills || '').split(',').map(s => s.trim()).filter(Boolean),
          maxNoticePeriodDays: parseInt(res.naukri_max_notice, 10) || 30
        };
        runDeepProfileAudit(apiUrl, jobDescription, apiKey, isLinkedIn ? 'linkedin_recruiter' : 'naukri');
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
      const hasResume = hasAttachedResumeOnPage();
      const isLinkedIn = isLinkedInPlatform();
      sendResponse({
        status: 'ready',
        cardCount: cards.length,
        isProfilePage: isProf,
        hasResume: hasResume,
        platform: isLinkedIn ? 'linkedin' : 'naukri'
      });
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

    if (request.action === 'START_DEEP_AUDIT' || request.action === 'START_LINKEDIN_AUDIT') {
      const isLinkedInAction = request.action === 'START_LINKEDIN_AUDIT' || isLinkedInPlatform();
      runDeepProfileAudit(
        request.apiUrl || 'http://localhost:5000',
        request.jobDescription,
        request.apiKey,
        isLinkedInAction ? 'linkedin_recruiter' : 'naukri'
      );
      sendResponse({ started: true, success: true });
      return;
    }
  });
})();

