// Read excise's visible document state through the macOS accessibility tree,
// for scripts/run-cold-launch-double-open.sh (#1819, #1629).
//
// Usage: osascript -l JavaScript scripts/cold-launch-ax-state.js <pid>
//
// Prints one line per window of that process:
//   WINDOW <TAB> native title <TAB> in-window title <TAB> tabs <TAB> status bar
// tabs are "name [i/n]" joined by "|", or "<no-strip>" when the tab strip is
// not in the tree (a window's only tab shows no strip, #1554).
//
// Read-only: it never activates the app. It never asks for `entire contents`
// (that pegs excise for minutes) and never walks the page content; it reads
// fixed index paths measured on the 3.13.0 bundle and reports what it finds
// there.
function run(argv) {
  const pid = parseInt(argv[0], 10);
  const se = Application("System Events");
  const procs = se.processes.whose({ unixId: pid });
  if (procs.length === 0) return "NOPROCESS";
  const proc = procs[0];

  function nm(el) { try { return el.name() || ""; } catch (e) { return ""; } }
  function rl(el) { try { return el.role() || ""; } catch (e) { return ""; } }
  function kids(el) { try { return el.uiElements(); } catch (e) { return []; } }
  function val(el) { try { const v = el.value(); return v == null ? "" : String(v); } catch (e) { return ""; } }
  function at(el, path) {
    let cur = el;
    for (const i of path) {
      const k = kids(cur);
      if (i >= k.length) return null;
      cur = k[i];
    }
    return cur;
  }
  function texts(el, maxDepth) {
    const acc = [];
    (function t(e, d) {
      if (d > maxDepth) return;
      if (rl(e) === "AXStaticText") { const s = val(e) || nm(e); if (s) acc.push(s); }
      const k = kids(e);
      for (let i = 0; i < k.length; i++) t(k[i], d + 1);
    })(el, 0);
    return acc;
  }
  function tabNames(listEl) {
    const names = [];
    (function walk(el, depth) {
      if (depth > 10) return;
      const n = nm(el);
      if (rl(el) === "AXButton" && /, tab \d+ of \d+$/.test(n)) {
        names.push(n.replace(/, tab (\d+) of (\d+)$/, " [$1/$2]"));
        return;
      }
      const ks = kids(el);
      for (let i = 0; i < ks.length; i++) walk(ks[i], depth + 1);
    })(listEl, 0);
    return names;
  }

  const out = [];
  const wins = proc.windows();
  for (let w = 0; w < wins.length; w++) {
    const win = wins[w];
    let inWindowTitle = "";
    const t = at(win, [1, 3, 0, 0, 0, 1]);
    if (t && rl(t) === "AXStaticText") inWindowTitle = val(t) || nm(t);
    const list = at(win, [1, 3, 0, 0, 2, 0, 0, 0, 0, 0]);
    const isStrip = list && rl(list) === "AXList" && nm(list) === "Open documents";
    const tabs = isStrip ? tabNames(list) : [];
    const sb = at(win, [1, 3, 0, 0, 4]);
    const status = sb ? texts(sb, 4) : [];
    out.push([
      "WINDOW",
      nm(win),
      inWindowTitle,
      isStrip ? (tabs.length ? tabs.join("|") : "<empty-strip>") : "<no-strip>",
      status.join("/"),
    ].join("\t"));
  }
  if (wins.length === 0) out.push("NOWINDOWS");
  return out.join("\n");
}
