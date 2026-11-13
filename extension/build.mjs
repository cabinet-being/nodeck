// Builds the Chrome and Firefox packages from the same sources, without dependencies.
//   node extension/build.mjs [--out <dir>] [--version <x.y.z>]
// Writes <out>/chrome/, <out>/firefox/ (unpacked) and <out>/nodeck-chrome.zip, <out>/nodeck-firefox.zip.
import { mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync, cpSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateRawSync } from 'node:zlib';

const root = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const option = (name) => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const out = resolve(option('--out') ?? join(root, 'dist'));
const baseManifest = JSON.parse(readFileSync(join(root, 'manifest.json'), 'utf8'));

// Browsers need 1–4 dot-separated numbers; "v0.2.0-rc1" becomes "0.2.0", anything else keeps manifest.json's.
const requested = (option('--version') ?? process.env.NODECK_VERSION ?? '').replace(/^v/, '').match(/^\d+(\.\d+){0,3}/)?.[0];
const version = requested ?? baseManifest.version;

const targets = {
  chrome: { ...baseManifest, version },
  firefox: {
    ...baseManifest,
    version,
    background: { scripts: [baseManifest.background.service_worker], type: 'module' },
    browser_specific_settings: {
      gecko: {
        id: 'save-to-nodeck@nodeck.local',
        strict_min_version: '128.0',
        data_collection_permissions: { required: ['none'] },
      },
    },
  },
};

rmSync(out, { recursive: true, force: true });
for (const [browser, manifest] of Object.entries(targets)) {
  const dir = join(out, browser);
  mkdirSync(dir, { recursive: true });
  cpSync(join(root, 'src'), join(dir, 'src'), { recursive: true });
  cpSync(join(root, 'icons'), join(dir, 'icons'), { recursive: true });
  writeFileSync(join(dir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  writeFileSync(join(out, `nodeck-${browser}.zip`), zip(dir));
  console.log(`${browser}: ${relative(process.cwd(), join(out, `nodeck-${browser}.zip`))} (version ${version})`);
}

function files(dir) {
  return readdirSync(dir).sort().flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

/** Minimal ZIP writer (deflate, fixed timestamp so builds are reproducible). */
function zip(dir) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  const time = 0; // 00:00:00
  const date = (2026 - 1980) << 9 | 1 << 5 | 1; // 2026-01-01
  for (const path of files(dir)) {
    const name = Buffer.from(relative(dir, path).split('\\').join('/'));
    const data = readFileSync(path);
    const packed = deflateRawSync(data, { level: 9 });
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(8, 8);
    local.writeUInt16LE(time, 10);
    local.writeUInt16LE(date, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(packed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4); // made by Unix, so unzip keeps the permissions below
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(time, 12);
    central.writeUInt16LE(date, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(packed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE((0o100644 << 16) >>> 0, 38); // regular file, rw-r--r--
    central.writeUInt32LE(offset, 42);
    locals.push(local, name, packed);
    centrals.push(central, name);
    offset += local.length + name.length + packed.length;
  }
  const centralSize = centrals.reduce((sum, part) => sum + part.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(centrals.length / 2, 8);
  end.writeUInt16LE(centrals.length / 2, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, ...centrals, end]);
}
