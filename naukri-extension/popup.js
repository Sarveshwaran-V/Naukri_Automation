/**
 * Naukri AI Candidate Matcher - Popup Script
 */

document.addEventListener('DOMContentLoaded', async () => {
  const apiUrlInput = document.getElementById('apiUrl');
  const toggleHttpBtn = document.getElementById('toggleHttpBtn');
  const toggleSettingsBtn = document.getElementById('toggleSettingsBtn');
  const closeSettingsBtn = document.getElementById('closeSettingsBtn');
  const settingsPanel = document.getElementById('settingsPanel');
  const jobTitleInput = document.getElementById('jobTitle');
  const mandatorySkillsInput = document.getElementById('mandatorySkills');
  const maxNoticePeriodInput = document.getElementById('maxNoticePeriod');
  const jobDescriptionInput = document.getElementById('jobDescription');

  const deepAuditBtn = document.getElementById('deepAuditBtn');
  const evaluateBtn = document.getElementById('evaluateBtn');

  const statusAlert = document.getElementById('statusAlert');
  const pageStatus = document.getElementById('pageStatus') || document.getElementById('page-status');
  const resultsPanel = document.getElementById('resultsPanel');
  const countWorth = document.getElementById('countWorth');
  const countMaybe = document.getElementById('countMaybe');
  const countSkip = document.getElementById('countSkip');

  // 1. Settings Drawer Toggle
  toggleSettingsBtn.addEventListener('click', () => {
    settingsPanel.classList.toggle('hidden');
  });

  closeSettingsBtn.addEventListener('click', () => {
    settingsPanel.classList.add('hidden');
  });

  // 2. Restore stored inputs
  chrome.storage.local.get([
    'naukri_api_url',
    'naukri_job_title',
    'naukri_mandatory_skills',
    'naukri_max_notice',
    'naukri_job_desc'
  ], (res) => {
    if (res.naukri_api_url) apiUrlInput.value = res.naukri_api_url;
    if (res.naukri_job_title) jobTitleInput.value = res.naukri_job_title;
    if (res.naukri_mandatory_skills) mandatorySkillsInput.value = res.naukri_mandatory_skills;
    if (res.naukri_max_notice !== undefined) maxNoticePeriodInput.value = res.naukri_max_notice;
    if (res.naukri_job_desc) jobDescriptionInput.value = res.naukri_job_desc;
  });

  function saveForm() {
    chrome.storage.local.set({
      naukri_api_url: apiUrlInput.value.trim(),
      naukri_job_title: jobTitleInput.value.trim(),
      naukri_mandatory_skills: mandatorySkillsInput.value.trim(),
      naukri_max_notice: maxNoticePeriodInput.value.trim(),
      naukri_job_desc: jobDescriptionInput.value.trim()
    });
  }

  [apiUrlInput, jobTitleInput, mandatorySkillsInput, maxNoticePeriodInput, jobDescriptionInput].forEach(el => {
    if (el) {
      el.addEventListener('input', saveForm);
      el.addEventListener('change', saveForm);
    }
  });

  // 3. Toggle HTTP / HTTPS quick switch
  toggleHttpBtn.addEventListener('click', () => {
    const current = apiUrlInput.value.trim();
    if (current.includes('5001')) {
      apiUrlInput.value = current.replace('https://localhost:5001', 'http://localhost:5000');
      toggleHttpBtn.textContent = 'Switch to HTTPS (5001)';
    } else {
      apiUrlInput.value = current.replace('http://localhost:5000', 'https://localhost:5001');
      toggleHttpBtn.textContent = 'Switch to HTTP (5000)';
    }
    saveForm();
  });

  function getBaseApiUrl() {
    const full = apiUrlInput.value.trim() || 'http://localhost:5000';
    return full.replace(/\/api\/(evaluate|analyze-profile).*/i, '').replace(/\/+$/, '');
  }

  function showAlert(message, type = 'info') {
    statusAlert.className = `alert alert-${type}`;
    statusAlert.textContent = message;
    statusAlert.classList.remove('hidden');
  }

  function hideAlert() {
    statusAlert.classList.add('hidden');
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

  // 4. Check active tab status
  const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (activeTab && activeTab.id) {
    try {
      await ensureContentScript(activeTab.id);
      const response = await chrome.tabs.sendMessage(activeTab.id, { action: 'PING' });
      if (response) {
        if (response.isProfilePage) {
          pageStatus.textContent = 'Candidate Profile Opened';
          pageStatus.className = 'status-pill status-ready';
        } else if (response.cardCount > 0) {
          pageStatus.textContent = `${response.cardCount} Cards Detected`;
          pageStatus.className = 'status-pill status-ready';
        } else {
          pageStatus.textContent = 'Ready on page';
          pageStatus.className = 'status-pill status-ready';
        }
      }
    } catch (e) {
      pageStatus.textContent = 'Ready on page';
      pageStatus.className = 'status-pill status-warn';
    }
  }

  // 5. Button: Deep Analyze Opened Profile & Resume
  deepAuditBtn.addEventListener('click', async () => {
    hideAlert();
    saveForm();

    const baseApi = getBaseApiUrl();
    const jobTitle = jobTitleInput.value.trim() || 'Target Role';
    const rawJobDescription = jobDescriptionInput.value.trim();
    const mandatorySkills = mandatorySkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean);
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
    deepAuditBtn.querySelector('.btn-text').textContent = 'Starting Audit on Page...';

    try {
      await ensureContentScript(currentTab.id);

      // Trigger the on-page deep audit modal
      const res = await chrome.tabs.sendMessage(currentTab.id, {
        action: 'START_DEEP_AUDIT',
        apiUrl: baseApi,
        jobDescription: {
          title: jobTitle,
          rawText: rawJobDescription,
          mandatorySkills: mandatorySkills,
          maxNoticePeriodDays: maxNoticeDays
        }
      });

      if (res && res.started) {
        showAlert('🔍 Deep Audit Report opened directly on the candidate page!', 'success');
      } else {
        showAlert('Could not start audit. Make sure an opened candidate profile or resume is visible on the page.', 'error');
      }
    } catch (error) {
      console.error('[Profile Audit Error]', error);
      showAlert(`Error: ${error.message || 'Could not communicate with page.'}`, 'error');
    } finally {
      deepAuditBtn.disabled = false;
      deepAuditBtn.querySelector('.btn-spinner').classList.add('hidden');
      deepAuditBtn.querySelector('.btn-text').textContent = 'Deep Analyze Opened Profile & Resume';
    }
  });

  // 6. Button: Evaluate Search Page Cards (In-Place Badges, No Reordering)
  evaluateBtn.addEventListener('click', async () => {
    hideAlert();
    saveForm();
    resultsPanel.classList.add('hidden');

    const baseApi = getBaseApiUrl();
    const evaluateUrl = `${baseApi}/api/evaluate`;
    const jobTitle = jobTitleInput.value.trim() || 'Role';
    const rawJobDescription = jobDescriptionInput.value.trim();
    const mandatorySkills = mandatorySkillsInput.value
      .split(',')
      .map(s => s.trim())
      .filter(Boolean);
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
        showAlert('No candidate search cards found on this page. If you opened a candidate profile, click "Deep Analyze Opened Profile & Resume" above!', 'error');
        return;
      }

      showAlert(`Scraped ${scrapeRes.candidates.length} cards. Contacting .NET 10 API...`, 'info');

      const requestPayload = {
        jobDescription: {
          title: jobTitle,
          rawText: rawJobDescription,
          mandatorySkills: mandatorySkills,
          maxNoticePeriodDays: maxNoticeDays
        },
        candidates: scrapeRes.candidates
      };

      const responseData = await callApiViaBackground(evaluateUrl, requestPayload);
      const evaluations = responseData.evaluations || responseData;

      if (!Array.isArray(evaluations)) {
        throw new Error('API returned invalid evaluations format.');
      }

      // Paint in-place badges onto cards
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

      const injectedCount = injectRes ? injectRes.injectedCount : evaluations.length;
      showAlert(`✓ Evaluated & badged ${injectedCount} candidate cards in place!`, 'success');

    } catch (error) {
      console.error('[Naukri AI Error]', error);
      let errorMsg = error.message || 'Error evaluating candidates.';
      if (errorMsg.includes('Failed to fetch')) {
        errorMsg += ' - Ensure your .NET 10 backend is running at ' + baseApi;
      }
      showAlert(errorMsg, 'error');
    } finally {
      evaluateBtn.disabled = false;
      evaluateBtn.querySelector('.btn-spinner').classList.add('hidden');
      evaluateBtn.querySelector('.btn-text').textContent = 'Evaluate Search Page Cards';
    }
  });

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
});
