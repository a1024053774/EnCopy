// Renders the Xiaohongshu frame (transparent middle) and cover from frame.html.
//   node shoot.js <outdir>   -> frame.png, cover.png (cover needs xhs/still.png beside frame.html)
const path = require("path");
const puppeteer = require("puppeteer-core");
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
(async () => {
  const out = process.argv[2];
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--allow-file-access-from-files", "--force-color-profile=srgb"] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1080, height: 1440, deviceScaleFactor: 1 });
  for (const [name, q] of [["frame", ""], ["cover", "?cover"]]) {
    await page.goto("file://" + path.resolve(__dirname, "frame.html") + q, { waitUntil: "networkidle0" });
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(out, `${name}.png`), omitBackground: true });
  }
  await browser.close();
})();
