import { build } from 'esbuild';
import { mkdirSync, existsSync, copyFileSync } from 'fs';
import { execSync } from 'child_process';

const zip = process.argv.includes('--zip');
const targets = [
  { name: 'chromium', define: { 'process.env.TARGET': '"chromium"' } },
  { name: 'firefox', define: { 'process.env.TARGET': '"firefox"' } },
];

for (const t of targets) {
  const out = `${t.name}/dist`;
  mkdirSync(out, { recursive: true });
  mkdirSync(`${out}/icons`, { recursive: true });

  for (const entry of ['background', 'content']) {
    await build({
      entryPoints: [`${t.name}/src/${entry}.ts`],
      bundle: true,
      outfile: `${out}/${entry}.js`,
      minify: true,
      format: 'iife',
      target: ['es2020'],
      define: t.define,
      logLevel: 'info',
    });
  }
  copyFileSync(`${t.name}/manifest.json`, `${out}/manifest.json`);
  copyFileSync(`shared/ui/options.html`, `${out}/options.html`);
  copyFileSync(`shared/ui/options.js`, `${out}/options.js`);
  for (const size of [16, 32, 48, 128]) {
    if (existsSync(`${t.name}/icons/icon${size}.png`)) {
      copyFileSync(`${t.name}/icons/icon${size}.png`, `${out}/icons/icon${size}.png`);
    }
  }
  if (zip) {
    execSync(`cd ${t.name}/dist && zip -qr ../nova-${t.name}-1.2.0.zip .`, { stdio: 'inherit' });
    console.log(`→ ${t.name}/nova-${t.name}-1.2.0.zip`);
  }
}
console.log('done');
