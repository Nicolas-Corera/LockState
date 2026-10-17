// Service worker mínimo: cachea el "shell" para que la app abra sin conexión.
// NO cachea /api ni /pc (siempre a la red).
const CACHE = 'ch-v2';

self.addEventListener('install', (e) => { self.skipWaiting(); });
self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((ks) => Promise.all(ks.map((k) => k !== CACHE && caches.delete(k)))).then(() => self.clients.claim()));
});
// Red primero (así siempre ves la última versión); si no hay internet, caché.
self.addEventListener('fetch', (e) => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET') return;
  if (u.pathname.startsWith('/api/') || u.pathname === '/pc' || u.pathname === '/phone') return; // nunca cachear
  e.respondWith(
    fetch(e.request).then((resp) => {
      if (resp.ok && u.origin === location.origin) { const c = resp.clone(); caches.open(CACHE).then((x) => x.put(e.request, c)); }
      return resp;
    }).catch(() => caches.match(e.request).then((r) => r || caches.match('/index.html')))
  );
});
