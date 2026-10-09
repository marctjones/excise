#!/usr/bin/env node
// Measures where pdf.js's XFA layer draws each field and exclusion group, and the option lists of
// its choice lists, for the independent-oracle test of the XFA field map (#2027). pdf.js lays the
// form out in its worker and the browser places flowed content with CSS, so a real browser is needed
// for the boxes: the page is rendered in headless Chrome and getBoundingClientRect is read back.
//
// Nothing is downloaded. Point the environment at an existing install:
//   PDFJS_DIST     a pdfjs-dist package directory (legacy/build/pdf.mjs and web/pdf_viewer.css)
//   PUPPETEER_CORE a puppeteer-core package directory
//   CHROME         a Chrome or chrome-headless-shell executable
// Usage: node scripts/xfa-pdfjs-field-boxes.mjs <form.pdf> <out.json>
//
// Output: pdf.js version, the input's SHA-256, and per page its size and every visible .xfaField /
// .xfaExclgroup box (CSS px at scale 1 = PDF points, top-left origin of the page) with its xfaName.
// A choice list also carries its options: their count and the SHA-256 of each option written as
// value U+001F label U+001E, concatenated in order (the lists hold whole country tables; the hash
// keeps the fixture small and still detects any difference). The hidden " " option pdf.js inserts
// when no item matches the field's value is viewer chrome, not an item, and is left out.

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { join } from "node:path";
import { createRequire } from "node:module";

const [, , pdfPath, outPath] = process.argv;
const { PDFJS_DIST, PUPPETEER_CORE, CHROME } = process.env;
if (!pdfPath || !outPath || !PDFJS_DIST || !PUPPETEER_CORE || !CHROME) {
  console.error("usage: PDFJS_DIST=... PUPPETEER_CORE=... CHROME=... node xfa-pdfjs-field-boxes.mjs <form.pdf> <out.json>");
  process.exit(2);
}

const pdfBytes = readFileSync(pdfPath);
const pdfjsVersion = JSON.parse(readFileSync(join(PDFJS_DIST, "package.json"), "utf8")).version;

const page = `<!doctype html><html><head><meta charset="utf-8">
<link rel="stylesheet" href="/pdfjs/web/pdf_viewer.css">
<style>body{margin:0} .pageHost{position:relative}</style></head><body>
<script type="module">
import * as pdfjs from "/pdfjs/legacy/build/pdf.mjs";
pdfjs.GlobalWorkerOptions.workerSrc = "/pdfjs/legacy/build/pdf.worker.mjs";
window.measure = async () => {
  const doc = await pdfjs.getDocument({ url: "/doc.pdf", enableXfa: true, password: "" }).promise;
  if (!doc.isPureXfa) return { pureXfa: false, pages: [] };
  const pages = [];
  for (let n = 1; n <= doc.numPages; n++) {
    const p = await doc.getPage(n);
    const xfaHtml = await p.getXfa();
    const host = document.createElement("div");
    host.className = "pageHost";
    document.body.append(host);
    const layer = document.createElement("div");
    host.append(layer);
    pdfjs.XfaLayer.render({ xfaHtml, div: layer, annotationStorage: doc.annotationStorage,
      linkService: { addLinkAttributes() {} }, intent: "display" });
    const root = layer.firstElementChild;
    const origin = root.getBoundingClientRect();
    const boxes = [];
    for (const el of layer.querySelectorAll(".xfaField, .xfaExclgroup")) {
      const style = getComputedStyle(el);
      if (style.display === "none" || style.visibility === "hidden") continue;
      const r = el.getBoundingClientRect();
      const box = { name: el.getAttribute("xfaname") || "", kind: el.classList.contains("xfaField") ? "field" : "exclGroup",
        x: +(r.left - origin.left).toFixed(2), y: +(r.top - origin.top).toFixed(2), w: +r.width.toFixed(2), h: +r.height.toFixed(2) };
      const select = el.classList.contains("xfaField") ? el.querySelector("select") : null;
      if (select) box.options = [...select.options].filter(o => !o.hidden).map(o => [o.value, o.textContent]);
      boxes.push(box);
    }
    pages.push({ w: +origin.width.toFixed(2), h: +origin.height.toFixed(2), boxes });
    host.remove();
  }
  return { pureXfa: true, pages };
};
window.ready = true;
</script></body></html>`;

const server = createServer((req, res) => {
  const url = decodeURIComponent(req.url.split("?")[0]);
  try {
    if (url === "/") { res.writeHead(200, { "content-type": "text/html" }); res.end(page); return; }
    if (url === "/doc.pdf") { res.writeHead(200, { "content-type": "application/pdf" }); res.end(pdfBytes); return; }
    if (url.startsWith("/pdfjs/") && !url.includes("..")) {
      const file = join(PDFJS_DIST, url.slice("/pdfjs/".length));
      const type = file.endsWith(".css") ? "text/css" : file.endsWith(".mjs") || file.endsWith(".js") ? "text/javascript" : "application/octet-stream";
      res.writeHead(200, { "content-type": type }); res.end(readFileSync(file)); return;
    }
  } catch { /* fall through to 404 */ }
  res.writeHead(404); res.end();
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const { port } = server.address();

const puppeteer = createRequire(join(PUPPETEER_CORE, "package.json"))(PUPPETEER_CORE);
const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--no-sandbox"] });
try {
  const tab = await browser.newPage();
  tab.on("pageerror", e => console.error("page error:", e.message));
  await tab.goto(`http://127.0.0.1:${port}/`);
  await tab.waitForFunction("window.ready === true");
  const result = await tab.evaluate(() => window.measure());
  for (const p of result.pages) {
    for (const box of p.boxes) {
      if (!box.options) continue;
      const joined = box.options.map(([value, label]) => `${value}\u001f${label}\u001e`).join("");
      box.options = { count: box.options.length, sha256: createHash("sha256").update(joined, "utf8").digest("hex") };
    }
  }
  const out = { pdfjs: pdfjsVersion, sha256: createHash("sha256").update(pdfBytes).digest("hex"), ...result };
  writeFileSync(outPath, JSON.stringify(out));
  const boxes = result.pages.reduce((n, p) => n + p.boxes.length, 0);
  console.log(`${pdfPath}: pdf.js ${pdfjsVersion}, pureXfa=${result.pureXfa}, ${result.pages.length} pages, ${boxes} boxes`);
} finally {
  await browser.close();
  server.close();
}
