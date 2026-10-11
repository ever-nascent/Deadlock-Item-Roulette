// Usage: node fetch-deadlock-assets.mjs <outDir>
// Saves items-full.json, the shop icon of every roulette-eligible item, and manifest.json.
import { mkdir, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';

const out = process.argv[2] ?? 'deadlock-assets';
const API = 'https://api.deadlock-api.com/v1/assets/items?language=english';

const res = await fetch(API);
if (!res.ok) throw new Error(`${API} -> ${res.status}`);
const text = await res.text();
await mkdir(join(out, 'icons'), { recursive: true });
await writeFile(join(out, 'items-full.json'), text);

const pool = JSON.parse(text).filter(i =>
  i.type === 'upgrade' && i.shopable && !i.disabled &&
  ['weapon', 'vitality', 'spirit'].includes(i.item_slot_type) && i.item_tier >= 1 && i.item_tier <= 4);

const failed = [];
const items = await Promise.all(pool.map(async i => {
  const url = i.shop_image ?? i.image;
  const icon = url && `icons/${i.class_name}.png`;
  try {
    const r = await fetch(url);
    if (!r.ok) throw new Error(r.status);
    await writeFile(join(out, icon), Buffer.from(await r.arrayBuffer()));
  } catch (e) {
    failed.push(`${i.class_name} (${url}): ${e.message}`);
  }
  return { class_name: i.class_name, name: i.name, slot: i.item_slot_type, tier: i.item_tier, cost: i.cost, active: i.is_active_item, imbue: !!i.imbue, icon, source: url && basename(url) };
}));

// Shop UI art: the catalog art dl-shop-panel uses (tabs, card backers, backgrounds), soul/corrupted icons, fonts.
const UI = /^(images\/shop\/catalog\/(cards\/)?[^/]+\.png|icons\/.*corrupt[^/]*\.svg|icons\/(hud\/icons\/icon_souls?|shop\/souls_backer|shop\/tier_corner_cap)\.svg|fonts\/(retaildemo|radiance)-[^/]+\.otf)$/i;
const urls = v => typeof v === 'string' ? [v] : Object.values(v ?? {}).flatMap(urls);
const index = (await Promise.all(['images', 'icons', 'fonts'].map(async e =>
  urls(await (await fetch(`https://api.deadlock-api.com/v1/assets/${e}`)).json())))).flat();
const ui = [...new Set(index)].map(url => [url, url.split('/assets-api-res/')[1]])
  .filter(([, p]) => p && UI.test(p) && !/_(psd|png)\.|_unfilled\./.test(p));
await Promise.all(ui.map(async ([url, p]) => {
  try {
    const r = await fetch(url);
    if (!r.ok) throw new Error(r.status);
    await mkdir(join(out, 'ui', p, '..'), { recursive: true });
    await writeFile(join(out, 'ui', p), Buffer.from(await r.arrayBuffer()));
  } catch (e) {
    failed.push(`${p} (${url}): ${e.message}`);
  }
}));
console.log(`ui art: ${ui.length} files`);

const manifest = JSON.stringify({ fetched: new Date().toISOString(), items, ui: ui.map(([, p]) => `ui/${p}`), failed }, null, 2);
await writeFile(join(out, 'manifest.json'), manifest);
await writeFile(join(out, 'manifest.js'), `window.MANIFEST = ${manifest};\n`); // lets mockup.html load it from file://

const counts = {};
for (const i of items) counts[`${i.slot} t${i.tier}`] = (counts[`${i.slot} t${i.tier}`] ?? 0) + 1;
console.log(`pool: ${items.length} items`);
console.table(counts);
console.log(failed.length ? `FAILED (${failed.length}):\n${failed.join('\n')}` : 'all icons downloaded');
