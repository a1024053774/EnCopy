// Stills of the Chinese film for the Xiaohongshu cards, without the film's own subtitle and keycap,
// since each card carries its own words.  node stills.js <outdir>
const path = require("path");
const puppeteer = require("puppeteer-core");
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const SHOTS = { pick: 16.8, paste: 18.15, search: 22.6, list: 27.7, share: 30.8, hold: 33.6 };
(async () => {
  const out = process.argv[2];
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--allow-file-access-from-files", "--force-color-profile=srgb"] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080, deviceScaleFactor: 1 });
  await page.goto("file://" + path.resolve(__dirname, "../../docs/intro.html") + "?t=0&lang=zh&theme=light", { waitUntil: "networkidle0" });
  await page.waitForFunction("window.ready === true");
  for (const [name, t] of Object.entries(SHOTS)) {
    await page.evaluate((t) => { window.render(t); for (const id of ["cap", "chip", "kinds"]) document.getElementById(id).style.visibility = "hidden"; }, t);
    await page.screenshot({ path: path.join(out, `${name}.png`) });
    await page.evaluate(() => { for (const id of ["cap", "chip", "kinds"]) document.getElementById(id).style.visibility = ""; });
  }
  await browser.close();
})();
