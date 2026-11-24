// Renders the Xiaohongshu pictures: the eight cards from cards.html, card-1.png … card-8.png.
//   node shoot.js <outdir>   (cards 1 and 3–6 need img/ from stills.js)
const path = require("path");
const puppeteer = require("puppeteer-core");
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
(async () => {
  const out = process.argv[2];
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--allow-file-access-from-files", "--force-color-profile=srgb"] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1080, height: 1440, deviceScaleFactor: 1 });
  for (let n = 1; n <= 8; n++) {
    await page.goto("file://" + path.resolve(__dirname, "cards.html") + `?n=${n}`, { waitUntil: "networkidle0" });
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(out, `card-${n}.png`) });
  }
  await browser.close();
})();
