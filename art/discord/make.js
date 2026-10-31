const { chromium } = require('playwright');
const fs = require('fs');
const font = fs.readFileSync('Cinzel-Bold.ttf').toString('base64');
const css = `@font-face{font-family:Cinzel;src:url(data:font/ttf;base64,${font});font-weight:700}
html,body{margin:0;padding:0;background:transparent}`;

// The spiral and sword of the launcher icon.
function spiral(cx, cy, turns, r0, r1, stroke, color) {
  let d = '', n = 400;
  for (let i = 0; i <= n; i++) {
    const t = i / n, a = t * turns * 2 * Math.PI + Math.PI * 0.75, r = r0 + (r1 - r0) * t;
    d += (i ? 'L' : 'M') + (cx + r * Math.cos(a)).toFixed(1) + ' ' + (cy + r * Math.sin(a)).toFixed(1);
  }
  return `<path d="${d}" fill="none" stroke="${color}" stroke-width="${stroke}" stroke-linecap="round"/>`;
}
const defs = `<defs>
  <radialGradient id="bg" cx="50%" cy="45%" r="70%"><stop offset="0" stop-color="#7a3416"/><stop offset=".55" stop-color="#3d1a0e"/><stop offset="1" stop-color="#160b07"/></radialGradient>
  <linearGradient id="gold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f7d98a"/><stop offset=".5" stop-color="#e2b65a"/><stop offset="1" stop-color="#b88a33"/></linearGradient>
  <linearGradient id="blade" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#cfd3d6"/><stop offset=".5" stop-color="#ffffff"/><stop offset="1" stop-color="#b7bcc0"/></linearGradient>
  <filter id="glow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="14" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
</defs>`;
function sword(cx, top, len) {
  const w = len * 0.055;
  return `<g filter="url(#glow)">
    <rect x="${cx - w * 0.35}" y="${top}" width="${w * 0.7}" height="${len * 0.11}" rx="${w * 0.2}" fill="url(#gold)"/>
    <circle cx="${cx}" cy="${top - w * 0.2}" r="${w * 0.55}" fill="url(#gold)"/>
    <rect x="${cx - len * 0.17}" y="${top + len * 0.11}" width="${len * 0.34}" height="${w * 0.75}" rx="${w * 0.3}" fill="url(#gold)"/>
    <rect x="${cx - len * 0.14}" y="${top + len * 0.11 + w * 0.15}" width="${len * 0.28}" height="${w * 0.45}" fill="#b0301d"/>
    <path d="M${cx - w / 2} ${top + len * 0.11 + w * 0.75} L${cx + w / 2} ${top + len * 0.11 + w * 0.75} L${cx + w / 2} ${top + len * 0.93} L${cx} ${top + len} L${cx - w / 2} ${top + len * 0.93} Z" fill="url(#blade)" stroke="#8a6a2a" stroke-width="3"/>
  </g>`;
}
const logo = `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">${defs}
  <rect width="1024" height="1024" fill="url(#bg)"/>
  <rect x="34" y="34" width="956" height="956" rx="120" fill="none" stroke="url(#gold)" stroke-width="22"/>
  <g filter="url(#glow)">${spiral(512, 560, 2.6, 40, 330, 34, 'url(#gold)')}</g>
  ${sword(512, 150, 720)}
</svg>`;

const arcs = [
  { n: 'I', name: 'arc1', glow: '#e9783c', inner: '#5a1d14', ring: '#c75a2b' },   // embers: Wizard City to Dragonspyre
  { n: 'II', name: 'arc2', glow: '#4fb6d8', inner: '#123447', ring: '#3a8fb0' },  // frost and stars: Celestia to Wintertusk
  { n: 'III', name: 'arc3', glow: '#a473e8', inner: '#2a1747', ring: '#7d52c4' }, // the far worlds: Polaris to Empyrea
];
const arcSvg = a => `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">${defs}
  <defs><radialGradient id="g${a.name}" cx="50%" cy="45%" r="60%"><stop offset="0" stop-color="${a.ring}"/><stop offset=".6" stop-color="${a.inner}"/><stop offset="1" stop-color="#0d0806"/></radialGradient>
  <filter id="f${a.name}" x="-40%" y="-40%" width="180%" height="180%"><feGaussianBlur stdDeviation="22" result="b"/><feFlood flood-color="${a.glow}" flood-opacity=".75"/><feComposite in2="b" operator="in" result="c"/><feMerge><feMergeNode in="c"/><feMergeNode in="SourceGraphic"/></feMerge></filter></defs>
  <circle cx="512" cy="512" r="500" fill="#120a07"/>
  <circle cx="512" cy="512" r="470" fill="url(#g${a.name})"/>
  <circle cx="512" cy="512" r="470" fill="none" stroke="url(#gold)" stroke-width="26"/>
  <circle cx="512" cy="512" r="420" fill="none" stroke="url(#gold)" stroke-width="5" opacity=".55"/>
  <g transform="rotate(45 512 512)" opacity=".9"><rect x="${512 - 300}" y="${512 - 300}" width="600" height="600" fill="none" stroke="${a.glow}" stroke-width="6" opacity=".45"/></g>
  <text x="512" y="${a.n.length > 2 ? 640 : 660}" text-anchor="middle" font-family="Cinzel" font-weight="700"
        font-size="${a.n.length > 2 ? 400 : a.n.length > 1 ? 470 : 520}" fill="url(#gold)" filter="url(#f${a.name})" letter-spacing="-10">${a.n}</text>
</svg>`;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1024, height: 1024 } });
  const shots = [['logo', logo], ...arcs.map(a => [a.name, arcSvg(a)])];
  for (const [name, svg] of shots) {
    await page.setContent(`<!doctype html><html><head><style>${css}</style></head><body>${svg}</body></html>`);
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: `${name}.png`, omitBackground: true, clip: { x: 0, y: 0, width: 1024, height: 1024 } });
  }
  await browser.close();
})();
