// The client sends no input to the game: no mouse, no keyboard, no window messages. This test greps the sources (src, app, ui, tools) and, when
// the exe is built (dist/Wardsage.exe), its metadata (the P/Invoke names of the IL) for the APIs that could do it.
import fs from 'node:fs';
import path from 'node:path';

let bad = 0;
const ok = (c, m) => { if (!c) { bad++; console.log('FAIL: ' + m); } };
const FORBIDDEN = /\b(SendInput|SendKeyInput|mouse_event|keybd_event|PostMessage|PostThreadMessage|SendMessage|SetCursorPos|AttachThreadInput|BringWindowToTop|SendNotifyMessage|SendMessageTimeout|INPUT_MOUSE|INPUT_KEYBOARD|KEYEVENTF_\w+|MOUSEEVENTF_\w+|LEFTDOWN|LEFTUP)\b/;
// not the client: the game-analysis scripts name the GAME's own functions; the lab runner is a separate tool of the developers' VM
const SKIP_DIRS = new Set(['node_modules', 'lab', 'fixtures', '.git']);
const SKIP_FILES = new Set(['screen-map.py', 'ui-graph.py']);   // (the *Data.cs files are tables of the GAME's own names, e.g. its SendMessage_* functions)
// the one reviewed use: the code box's placeholder (EM_SETCUEBANNER) of the old WinForms form, not the game
const ALLOWED = [{ file: 'src/MainForm.cs', re: /SendMessage\(IntPtr hWnd|SendMessage\(codeBox\.Handle, 0x1501/ }];
const roots = ['src', 'app', 'ui', 'tools'];
let files = 0;
function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name)) walk(path.join(d, e.name)); continue; }
    if (SKIP_FILES.has(e.name) || /Data\.cs$/.test(e.name) || /\.(png|ttf|woff2?|dll|exe|jpg|ico|gz|bin)$/i.test(e.name)) continue;
    const p = path.join(d, e.name).split(path.sep).join('/');
    let text; try { text = fs.readFileSync(p, 'utf8'); } catch (x) { continue; }
    files++;
    text.split('\n').forEach((line, i) => {
      if (!FORBIDDEN.test(line)) return;
      if (ALLOWED.some((a) => a.file === p && a.re.test(line))) return;
      ok(false, p + ':' + (i + 1) + ': ' + line.trim().slice(0, 120));
    });
  }
}
for (const r of roots) if (fs.existsSync(r)) walk(r);
ok(files > 50, 'the scan saw the sources (' + files + ' files)');

// the code that decided clicks is gone: no such files, no config / switch / setting of it
for (const gone of ['src/AutoPilot.cs', 'src/LocatePilot.cs', 'src/SellPilot.cs', 'src/FilterPilot.cs', 'src/HeroPilot.cs', 'src/Navigator.cs', 'src/NavAct.cs', 'src/Explorer.cs', 'app/OverlayNav.cs']) ok(!fs.existsSync(gone), gone + ' is deleted');
const cfg = fs.readFileSync('src/Config.cs', 'utf8');
ok(!/AutoClick|AutoConfirm|NavCore|NavHotkey/.test(cfg), 'Config.cs has no auto click / confirm / navCore setting');
const rc = fs.readFileSync('src/RemoteConfig.cs', 'utf8').split('\n').filter((l) => /Features =|DefaultOff =/.test(l)).join('\n');
ok(!/autoclick|locateAuto|artAuto|navCore/.test(rc), 'the site switches of the auto click are gone from RemoteConfig.cs');
const appjs = fs.readFileSync('ui/app.js', 'utf8');
ok(!/setAutoClick|setAutoConfirm|equip\.run|data-act="run"/.test(appjs), 'the page has no auto click setting or run command');

// the built exe: the names of its P/Invoke imports and members are plain ASCII in the metadata
const exe = 'dist/Wardsage.exe';
if (fs.existsSync(exe)) {
  const b = fs.readFileSync(exe).toString('latin1');
  for (const name of ['SendInput', 'SendKeyInput', 'mouse_event', 'keybd_event', 'PostMessage', 'SendMessage', 'SetCursorPos', 'AttachThreadInput', 'BringWindowToTop', 'GetAsyncKeyState', 'SendKeys']) ok(b.indexOf(name) < 0, exe + ' does not mention ' + name);
  ok(b.indexOf('GetLastInputInfo') >= 0, 'sanity: the read-only idle time import is there (the scan reads this exe)');
} else console.log('exe check skipped (build first: tools/build-app.sh)');
console.log(bad ? bad + ' failure(s)' : 'no-input: ok');
process.exit(bad ? 1 : 0);
