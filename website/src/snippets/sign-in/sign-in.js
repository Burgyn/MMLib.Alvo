// Sign in with Google, then call Alvo as that person. The page loads https://accounts.google.com/gsi/client first.
const ALVO = 'http://localhost:8080';
let access = null, expiresAt = 0; // the access token lives in memory only

async function token(form) {
  const response = await fetch(`${ALVO}/auth/v1/token`, { method: 'POST', body: new URLSearchParams(form) });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail); // a problem document: type, error, error_description, detail
  access = body.access_token;
  expiresAt = Date.now() + body.expires_in * 1000;
  localStorage.setItem('alvo_refresh', body.refresh_token); // every answer rotates it: keep only the newest
}

google.accounts.id.initialize({
  client_id: 'YOUR-CLIENT-ID.apps.googleusercontent.com',
  callback: ({ credential }) => token({ // credential is Google's ID token: exchange it within five minutes
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    subject_token: credential,
    subject_token_type: 'urn:ietf:params:oauth:token-type:id_token',
  }),
});
google.accounts.id.renderButton(document.getElementById('sign-in'), { theme: 'outline' });

async function api(path, init = {}) {
  if (Date.now() > expiresAt - 60_000) { // a minute early; read storage now, another tab may have rotated it
    await token({ grant_type: 'refresh_token', refresh_token: localStorage.getItem('alvo_refresh') });
  }
  const headers = { ...init.headers, Authorization: `Bearer ${access}`, 'Content-Type': 'application/json' };
  return fetch(`${ALVO}${path}`, { ...init, headers });
}

// After sign-in: await api('/api/time_entries', { method: 'POST', body: JSON.stringify({ minutes: 30 }) });
