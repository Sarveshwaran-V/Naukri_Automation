/**
 * Naukri Candidate Matcher - Popup Script
 */

document.addEventListener('DOMContentLoaded', async () => {
  const apiUrlInput = document.getElementById('apiUrl');
  const geminiApiKeyInput = document.getElementById('geminiApiKey');
  const toggleHttpBtn = document.getElementById('toggleHttpBtn');
  const toggleSettingsBtn = document.getElementById('toggleSettingsBtn');
  const closeSettingsBtn = document.getElementById('closeSettingsBtn');
  const settingsPanel = document.getElementById('settingsPanel');
  const jobTitleInput = document.getElementById('jobTitle');
  const mandatorySkillsInput = document.getElementById('mandatorySkills');
  const preferredSkillsInput = document.getElementById('preferredSkills');
  const maxNoticePeriodInput = document.getElementById('maxNoticePeriod');
  const jobDescriptionInput = document.getElementById('jobDescription');

  const deepAuditBtn = document.getElementById('deepAuditBtn');
  const evaluateBtn = document.getElementById('evaluateBtn');

  const statusAlert = document.getElementById('statusAlert');
  const pageStatus = document.getElementById('page-status') || document.getElementById('pageStatus');
  const resumeTick = document.getElementById('resumeTick');
  const resultsPanel = document.getElementById('resultsPanel');
  const countWorth = document.getElementById('countWorth');
  const countMaybe = document.getElementById('countMaybe');
  const countSkip = document.getElementById('countSkip');

  // 1. Settings Drawer Toggle
  if (toggleSettingsBtn && settingsPanel) {
    toggleSettingsBtn.addEventListener('click', () => {
      settingsPanel.classList.toggle('hidden');
    });
  }

  if (closeSettingsBtn && settingsPanel) {
    closeSettingsBtn.addEventListener('click', () => {
      settingsPanel.classList.add('hidden');
    });
  }

  // 2. Restore stored inputs
  chrome.storage.local.get([
    'naukri_api_url',
    'naukri_gemini_api_key',
    'naukri_job_title',
    'naukri_mandatory_skills',
    'naukri_preferred_skills',
    'naukri_max_notice',
    'naukri_job_desc'
  ], (res) => {
    if (res.naukri_api_url) apiUrlInput.value = res.naukri_api_url;
    if (res.naukri_gemini_api_key && geminiApiKeyInput) geminiApiKeyInput.value = res.naukri_gemini_api_key;
    if (res.naukri_job_title) jobTitleInput.value = res.naukri_job_title;
    if (res.naukri_mandatory_skills) mandatorySkillsInput.value = res.naukri_mandatory_skills;
    if (res.naukri_preferred_skills && preferredSkillsInput) preferredSkillsInput.value = res.naukri_preferred_skills;
    if (res.naukri_max_notice !== undefined) maxNoticePeriodInput.value = res.naukri_max_notice;
    if (res.naukri_job_desc) jobDescriptionInput.value = res.naukri_job_desc;
  });

  function saveForm() {
    chrome.storage.local.set({
      naukri_api_url: apiUrlInput.value.trim(),
      naukri_gemini_api_key: geminiApiKeyInput ? geminiApiKeyInput.value.trim() : '',
      naukri_job_title: jobTitleInput.value.trim(),
      naukri_mandatory_skills: mandatorySkillsInput.value.trim(),
      naukri_preferred_skills: preferredSkillsInput ? preferredSkillsInput.value.trim() : '',
      naukri_max_notice: maxNoticePeriodInput.value.trim(),
      naukri_job_desc: jobDescriptionInput.value.trim()
    });
  }

  [apiUrlInput, geminiApiKeyInput, jobTitleInput, mandatorySkillsInput, preferredSkillsInput, maxNoticePeriodInput, jobDescriptionInput].forEach(el => {
    if (el) {
      el.addEventListener('input', saveForm);
      el.addEventListener('change', saveForm);
    }
  });

  // 3. Toggle HTTP / HTTPS quick switch
  if (toggleHttpBtn) {
    toggleHttpBtn.addEventListener('click', () => {
      const current = apiUrlInput.value.trim();
      if (current.includes('5001')) {
        apiUrlInput.value = current.replace('https://localhost:5001', 'http://localhost:5000');
        toggleHttpBtn.textContent = 'Use HTTPS (5001)';
      } else {
        apiUrlInput.value = current.replace('http://localhost:5000', 'https://localhost:5001');
        toggleHttpBtn.textContent = 'Use HTTP (5000)';
      }
      saveForm();
    });
  }

  function getBaseApiUrl() {
    const full = apiUrlInput.value.trim() || 'http://localhost:5000';
    return full.replace(/\/api\/(evaluate|analyze-profile).*/i, '').replace(/\/+$/, '');
  }

  function getGeminiApiKey() {
    return geminiApiKeyInput ? geminiApiKeyInput.value.trim() : '';
  }

  function showAlert(message, type = 'info') {
    statusAlert.className = `alert alert-${type}`;
    statusAlert.textContent = message;
    statusAlert.classList.remove('hidden');
  }

  function hideAlert() {
    statusAlert.classList.add('hidden');
  }

  function promptForApiKey() {
    showAlert('Gemini API key is required. Please paste your key in Settings below.', 'error');
    if (settingsPanel) {
      settingsPanel.classList.remove('hidden');
    }
    if (geminiApiKeyInput) {
      geminiApiKeyInput.focus();
    }
  }

  async function ensureContentScript(tabId) {
    try {
      await chrome.tabs.sendMessage(tabId, { action: 'PING' });
    } catch (err) {
      await chrome.scripting.executeScript({
        target: { tabId: tabId },
        files: ['content.js']
      });
      await chrome.scripting.insertCSS({
        target: { tabId: tabId },
        files: ['content.css']
      });
    }
  }

  // 4. Check active tab status & detect attached resume
  const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (activeTab && activeTab.id) {
    try {
      await ensureContentScript(activeTab.id);
      const response = await chrome.tabs.sendMessage(activeTab.id, { action: 'PING' });
      if (response) {
        if (response.hasResume && resumeTick) {
          resumeTick.classList.remove('hidden');
        } else if (resumeTick) {
          resumeTick.classList.add('hidden');
        }

        if (response.isProfilePage) {
          pageStatus.textContent = 'Profile Opened';
          pageStatus.className = 'status-pill status-ready';
        } else if (response.cardCount > 0) {
          pageStatus.textContent = `${response.cardCount} Cards`;
          pageStatus.className = 'status-pill status-ready';
        } else {
          pageStatus.textContent = 'Ready';
          pageStatus.className = 'status-pill status-ready';
        }
      }
    } catch (e) {
      pageStatus.textContent = 'Ready';
      pageStatus.className = 'status-pill status-ready';
    }
  }

  // 5. Button: Deep Analyze Opened Profile & Resume
  deepAuditBtn.addEventListener('click', async () => {
    hideAlert();
    saveForm();

    const apiKey = getGeminiApiKey();
    const baseApi = getBaseApiUrl();
    const jobTitle = jobTitleInput.value.trim() || 'Target Role';
    const rawJobDescription = jobDescriptionInput.value.trim();
    const mandatorySkills = mandatorySkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean);
    const preferredSkills = preferredSkillsInput ? preferredSkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean) : [];
    const maxNoticeDays = parseInt(maxNoticePeriodInput.value, 10) || 0;

    if (!rawJobDescription) {
      showAlert('Please enter the Job Description / Requirements first.', 'error');
      return;
    }

    const [currentTab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!currentTab || !currentTab.id) {
      showAlert('Unable to detect active browser tab.', 'error');
      return;
    }

    deepAuditBtn.disabled = true;
    deepAuditBtn.querySelector('.btn-spinner').classList.remove('hidden');
    deepAuditBtn.querySelector('.btn-text').textContent = 'Analyzing Profile & Resume...';

    try {
      await ensureContentScript(currentTab.id);

      const res = await chrome.tabs.sendMessage(currentTab.id, {
        action: 'START_DEEP_AUDIT',
        apiUrl: baseApi,
        apiKey: apiKey,
        jobDescription: {
          title: jobTitle,
          rawText: rawJobDescription,
          mandatorySkills: mandatorySkills,
          preferredSkills: preferredSkills,
          maxNoticePeriodDays: maxNoticeDays
        }
      });

      if (res && res.started) {
        showAlert('Deep Audit Report opened directly on the candidate page.', 'success');
      } else {
        showAlert('Could not start audit. Make sure an opened candidate profile or resume is active on this tab.', 'error');
      }
    } catch (error) {
      console.error('[Profile Audit Error]', error);
      if (error.message && error.message.toLowerCase().includes('api key')) {
        promptForApiKey();
      } else {
        showAlert(`Error: ${error.message || 'Could not communicate with page.'}`, 'error');
      }
    } finally {
      deepAuditBtn.disabled = false;
      deepAuditBtn.querySelector('.btn-spinner').classList.add('hidden');
      deepAuditBtn.querySelector('.btn-text').textContent = 'Deep Analyze Opened Profile & Resume';
    }
  });

  // 6. Button: Evaluate Search Page Cards
  evaluateBtn.addEventListener('click', async () => {
    hideAlert();
    saveForm();
    resultsPanel.classList.add('hidden');

    const apiKey = getGeminiApiKey();
    const baseApi = getBaseApiUrl();
    const evaluateUrl = `${baseApi}/api/evaluate`;
    const jobTitle = jobTitleInput.value.trim() || 'Role';
    const rawJobDescription = jobDescriptionInput.value.trim();
    const mandatorySkills = mandatorySkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean);
    const preferredSkills = preferredSkillsInput ? preferredSkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean) : [];
    const maxNoticeDays = parseInt(maxNoticePeriodInput.value, 10) || 0;

    if (!rawJobDescription) {
      showAlert('Please enter the Job Description / Requirements first.', 'error');
      return;
    }

    const [currentTab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!currentTab || !currentTab.id) {
      showAlert('Unable to detect active browser tab.', 'error');
      return;
    }

    evaluateBtn.disabled = true;
    evaluateBtn.querySelector('.btn-spinner').classList.remove('hidden');
    evaluateBtn.querySelector('.btn-text').textContent = 'Evaluating Cards...';

    try {
      await ensureContentScript(currentTab.id);
      const scrapeRes = await chrome.tabs.sendMessage(currentTab.id, { action: 'SCRAPE_CANDIDATES' });

      if (!scrapeRes || !scrapeRes.success || !scrapeRes.candidates || scrapeRes.candidates.length === 0) {
        showAlert('No candidate search cards found on this page. If you opened a candidate profile, click "Deep Analyze Opened Profile & Resume" above.', 'error');
        return;
      }

      showAlert(`Analyzing ${scrapeRes.candidates.length} candidate cards...`, 'info');

      const requestPayload = {
        jobDescription: {
          title: jobTitle,
          rawText: rawJobDescription,
          mandatorySkills: mandatorySkills,
          preferredSkills: preferredSkills,
          maxNoticePeriodDays: maxNoticeDays
        },
        candidates: scrapeRes.candidates
      };

      const headers = {};
      if (apiKey) {
        headers['X-Gemini-Api-Key'] = apiKey;
      }

      const responseData = await callApiViaBackground(evaluateUrl, requestPayload, headers);
      const evaluations = responseData.evaluations || responseData;

      if (!Array.isArray(evaluations)) {
        throw new Error('API returned invalid evaluations format.');
      }

      const injectRes = await chrome.tabs.sendMessage(currentTab.id, {
        action: 'INJECT_EVALUATIONS',
        evaluations: evaluations
      });

      let worthCount = 0;
      let maybeCount = 0;
      let skipCount = 0;

      evaluations.forEach(ev => {
        const rec = (ev.recommendation || '').toUpperCase();
        if (rec.includes('WORTH') || rec.includes('OPEN')) worthCount++;
        else if (rec.includes('SKIP') || rec.includes('REJECT')) skipCount++;
        else maybeCount++;
      });

      countWorth.textContent = worthCount;
      countMaybe.textContent = maybeCount;
      countSkip.textContent = skipCount;
      resultsPanel.classList.remove('hidden');

      const injected = injectRes?.injectedCount || evaluations.length;
      showAlert(`Evaluated ${injected} candidate cards.`, 'success');
    } catch (error) {
      console.error('[Evaluation Error]', error);
      if (error.message && error.message.toLowerCase().includes('api key')) {
        promptForApiKey();
      } else {
        showAlert(`Evaluation failed: ${error.message}`, 'error');
      }
    } finally {
      evaluateBtn.disabled = false;
      evaluateBtn.querySelector('.btn-spinner').classList.add('hidden');
      evaluateBtn.querySelector('.btn-text').textContent = 'Evaluate Search Page Cards';
    }
  });

  async function callApiViaBackground(url, payload, headers = {}) {
    return new Promise((resolve, reject) => {
      chrome.runtime.sendMessage({
        action: 'CALL_API',
        url: url,
        method: 'POST',
        headers: headers,
        body: payload
      }, (response) => {
        if (chrome.runtime.lastError) {
          directFetch(url, payload, headers).then(resolve).catch(reject);
          return;
        }

        if (response && response.success) {
          resolve(response.data);
        } else {
          const err = new Error(response?.error || 'Failed to communicate with API server');
          reject(err);
        }
      });
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
});
