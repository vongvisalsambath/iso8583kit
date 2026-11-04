// The published viewer in Chromium, served with the production headers from vercel.json.
// Usage: node site/test/e2e.cjs <published wwwroot folder>
const { chromium, devices } = require("playwright");
const { spawn } = require("child_process");
const path = require("path");

const root = path.resolve(process.argv[2] || "out/wwwroot");
const repo = path.resolve(__dirname, "../..");
const port = 4300 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${port}/`;
const VISA = "4111111111111111";
let failures = 0;
const check = (ok, name, detail = "") => {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${!ok && detail ? "  " + detail : ""}`);
  if (!ok) failures++;
};
const squash = s => (s || "").replace(/\s+/g, " ").trim();

(async () => {
  const server = spawn("python3", [path.join(__dirname, "serve.py"), process.env.VERCEL_JSON || path.join(repo, "vercel.json"), root, String(port)], { stdio: "inherit" });
  try {
    for (let i = 0; i < 50; i++) {
      try { if ((await fetch(base)).ok) break; } catch { /* starting */ }
      await new Promise(r => setTimeout(r, 100));
    }
    const head = await fetch(base);
    const csp = head.headers.get("content-security-policy") || "";
    check((csp.match(/script-src [^;]*/) || [""])[0] === "script-src 'self' 'wasm-unsafe-eval'" && csp.includes("frame-ancestors 'none'"), "strict Content-Security-Policy");
    for (const f of ["dotnet.js", "blazor.webassembly.js"])
      check((await fetch(base + "_framework/" + f)).headers.get("cache-control") === "no-cache", `${f} isn't cached as immutable`);

    const browser = await chromium.launch();
    const open = async (options = {}) => {
      const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...options });
      const p = await ctx.newPage();
      const problems = [];
      p.on("pageerror", e => problems.push("error: " + e.message));
      p.on("dialog", d => { problems.push("dialog: " + d.message()); d.dismiss(); });
      p.on("console", m => { if (m.type() === "error") problems.push("console: " + m.text().slice(0, 200)); });
      p.on("request", r => { if (!r.url().startsWith(base)) problems.push("request: " + r.url()); });
      await p.addInitScript(() => { window.__csp = []; document.addEventListener("securitypolicyviolation", e => window.__csp.push(e.violatedDirective + " " + e.blockedURI)); });
      await p.goto(base);
      await p.waitForSelector(".verdict", { timeout: 60000 });
      const done = async () => [...problems, ...(await p.evaluate(() => window.__csp)).map(v => "csp: " + v)];
      return { ctx, p, done };
    };
    const setText = async (p, text) => {
      await p.evaluate(text => { const t = document.getElementById("msg"); t.value = text; t.dispatchEvent(new Event("input", { bubbles: true })); }, text);
      await p.waitForTimeout(300);
    };
    const results = p => p.evaluate(() => document.querySelector(".results").innerText);

    {
      const { ctx, p, done } = await open();
      const expect = {
        "Chip purchase (ASCII)": ["A well-formed message", "ARQC", "approved"],
        "Chip purchase (BCD, hex)": ["BCD with a binary bitmap", "chip; the terminal can accept a PIN", "approved"],
        "Declined: insufficient funds": ["0210", "insufficient funds", null],
        "ATM withdrawal in riel": ["cash withdrawal, from savings", "100.00 KHR", "approved"],
        "Echo test": ["network management request", "echo test", "approved"],
        "A mistyped card number": ["PAN-LUHN", "check digit WRONG", "invalid card number"],
        "A cut-off message": ["can't be read", "TRUNCATED", null],
      };
      for (const [label, words] of Object.entries(expect)) {
        await p.click(`.examples .chip >> text="${label}"`);
        await p.waitForTimeout(200);
        const text = squash(await results(p));
        const missing = words.filter(w => w && !text.includes(w));
        check(missing.length === 0 && !text.includes(VISA) && !text.includes("28121010000012300000"), `example: ${label}`, missing.join(", ") || "a card number or track data is visible");
      }
      const issues = await done();
      check(issues.length === 0, "examples: no errors, policy violations or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open();
      for (const [name, text] of [
        ["markup", "0200<img src=x onerror=alert(1)><script>alert(2)</script>"],
        ["control characters", "0200\u001b[2J\u202E\u0000"],
        ["a huge input", "30".repeat(30000)],
        ["a field claiming 999 bytes", "02000000000000000100999"],
      ]) {
        await setText(p, text);
        const injected = await p.evaluate(() => document.querySelectorAll(".results img, .results script").length);
        const visible = await results(p);
        check(injected === 0 && !/[\u0000-\u0008\u001b\u202E]/.test(visible) && (await p.locator(".verdict").count()) === 1, "hostile input: " + name, visible.slice(0, 120));
      }
      const issues = await done();
      check(issues.length === 0, "hostile input: no errors, dialogs or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open({ ...devices["iPhone 13"] });
      const [scroll, width] = await p.evaluate(() => [document.documentElement.scrollWidth, innerWidth]);
      check(scroll <= width, "no sideways scrolling on a phone", `page ${scroll}px, screen ${width}px`);
      const spill = await p.evaluate(() => {
        const card = document.querySelector(".results").getBoundingClientRect();
        return [...document.querySelectorAll(".results *")].filter(e => e.getBoundingClientRect().right > card.right + 1).length;
      });
      check(spill === 0, "nothing spills out of the results on a phone", `${spill} elements`);
      check((await done()).length === 0, "phone: no errors");
      await ctx.close();
    }

    {
      const ctx = await browser.newContext();
      const p = await ctx.newPage();
      await p.setContent(`<iframe src="${base}" width="400" height="300"></iframe>`);
      await p.waitForTimeout(1500);
      const frame = p.frames().find(f => f !== p.mainFrame());
      const framed = frame ? await frame.evaluate(() => !!document.querySelector(".brand")).catch(() => false) : false;
      check(!framed, "the page can't be framed");
      await ctx.close();
    }
    await browser.close();
  } finally {
    server.kill();
  }
  console.log(failures ? `\n${failures} failed` : "\nall passed");
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
