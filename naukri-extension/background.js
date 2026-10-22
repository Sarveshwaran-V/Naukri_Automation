/**
 * Background Service Worker
 * Routes API calls to .NET 10 Minimal API on behalf of content scripts
 * to completely eliminate browser Mixed Content (HTTPS -> HTTP) and CORS blocks.
 */

chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  if (request.action === 'CALL_API') {
    const { url, method = 'POST', body } = request;

    fetch(url, {
      method: method,
      headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json'
      },
      body: body ? JSON.stringify(body) : undefined
    })
      .then(async (response) => {
        if (!response.ok) {
          const errText = await response.text();
          throw new Error(`API responded with HTTP ${response.status}: ${errText || response.statusText}`);
        }
        return response.json();
      })
      .then((data) => {
        sendResponse({ success: true, data: data });
      })
      .catch((error) => {
        console.error('[Background API Fetch Error]', error);
        sendResponse({ success: false, error: error.message || 'Failed to connect to API server' });
      });

    return true; // Keep channel open for async fetch response
  }
});
