window.ui = SwaggerUIBundle({
  url: '../openapi/v1.json',
  dom_id: '#swagger-ui',
  deepLinking: true,
  persistAuthorization: false,
  validatorUrl: null,
  withCredentials: true,
  presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
  layout: 'StandaloneLayout',
  requestInterceptor: async request => {
    const url = new URL(request.url, window.location.href);
    const bearer = Object.keys(request.headers || {}).some(name => name.toLowerCase() === 'authorization' && request.headers[name]);
    if (url.origin === window.location.origin && url.pathname.includes('/api/v1/') &&
        !['GET', 'HEAD', 'OPTIONS'].includes(request.method.toUpperCase()) && !bearer) {
      // Session cookies are HttpOnly. Get the CSRF token from the existing browser session.
      const response = await fetch('../api/v1/session', { credentials: 'same-origin' });
      if (response.ok) {
        const session = await response.json();
        request.headers['X-CSRF-Token'] = session.csrfToken;
      }
    }
    return request;
  }
});
