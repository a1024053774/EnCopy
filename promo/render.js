// Renders docs/intro.html (INTRO_LANG=zh for Chinese) frame by frame with headless Chrome.
//   node render.js stills <outdir> 1.0 5.5 ...   -> PNG stills at those times
//   node render.js video <out.mp4>               -> silent H.264 video + <out>.events.json beside it
// Needs puppeteer-core (NODE_PATH) and Google Chrome; ffmpeg on PATH.
// Headless Chrome's tab closes after a few hundred screenshots in one session on this Mac, so the
// video is rendered in chunks, each with a fresh browser, into JPEG frames that ffmpeg encodes at the end.
const path = require("path");
const fs = require("fs");
const { execFileSync } = require("child_process");
const puppeteer = require("puppeteer-core");

const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const LANG = process.env.INTRO_LANG === "zh" ? "zh" : "en";
const PAGE = "file://" + path.resolve(__dirname, "../docs/intro.html") + `?t=0&lang=${LANG}&theme=light`;
const CHUNK = 150;

async function open() {
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--allow-file-access-from-files", "--force-color-profile=srgb"] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080, deviceScaleFactor: 1 });
  await page.goto(PAGE, { waitUntil: "networkidle0" });
  await page.waitForFunction("window.ready === true");
  return { browser, page };
}

(async () => {
  const [mode, out, ...rest] = process.argv.slice(2);
  let { browser, page } = await open();
  const { DURATION, FPS, EVENTS, GROOVE } = await page.evaluate(() => ({ DURATION: window.DURATION, FPS: window.FPS, EVENTS: window.EVENTS, GROOVE: window.GROOVE }));

  if (mode === "stills") {
    fs.mkdirSync(out, { recursive: true });
    for (const s of rest) {
      await page.evaluate((t) => window.render(t), +s);
      await page.screenshot({ path: path.join(out, `t${(+s).toFixed(2).padStart(6, "0")}.png`) });
    }
    await browser.close();
    return;
  }

  fs.writeFileSync(out.replace(/\.mp4$/, ".events.json"), JSON.stringify({ duration: DURATION, groove: GROOVE, events: EVENTS }, null, 1));
  const dir = out.replace(/\.mp4$/, "-frames");
  fs.mkdirSync(dir, { recursive: true });
  const n = Math.round(DURATION * FPS);
  for (let i = 0; i < n; i++) {
    const file = path.join(dir, `f${String(i).padStart(5, "0")}.jpg`);
    if (fs.existsSync(file)) continue; // resume after a crash
    if (i % CHUNK === 0 && i > 0) { await browser.close(); ({ browser, page } = await open()); console.log(`frame ${i}/${n}`); }
    await page.evaluate((t) => window.render(t), i / FPS);
    await page.screenshot({ path: file, type: "jpeg", quality: 95 });
  }
  await browser.close();
  execFileSync("ffmpeg", ["-loglevel", "error", "-y", "-framerate", String(FPS), "-i", path.join(dir, "f%05d.jpg"),
    "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out], { stdio: "inherit" });
})();
