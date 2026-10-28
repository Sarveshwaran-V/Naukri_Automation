/**
 * Background Service Worker
 * Routes API calls to .NET 10 API on behalf of content scripts and popups,
 * forwarding custom headers (such as X-Gemini-Api-Key) and eliminating CORS blocks.
 */

chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  if (request.action === 'CALL_API') {
    const { url, method = 'POST', body, headers = {} } = request;

    fetch(url, {
      method: method,
      headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json',
        ...headers
      },
      body: body ? JSON.stringify(body) : undefined
    })
      .then(async (response) => {
        const text = await response.text();
        let json;
        try {
          json = JSON.parse(text);
        } catch {
          json = null;
        }

        if (!response.ok) {
          const errMessage = json?.error || `API responded with HTTP ${response.status}: ${text || response.statusText}`;
          sendResponse({ success: false, error: errMessage, statusCode: response.status });
          return;
        }

        sendResponse({ success: true, data: json });
      })
      .catch((error) => {
        console.error('[Background API Fetch Error]', error);
        sendResponse({ success: false, error: error.message || 'Failed to connect to API server' });
      });

    return true; // Keep channel open for async fetch response
  }
});
