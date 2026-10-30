// Minimal, dependency-free ZIP writer (STORE method — no compression).
// T-026: the `zip` CLI does not exist on Windows runners ("'zip' is not
// recognized..."), so extension packaging now goes through this writer on
// every platform. Output is a standards-compliant zip that Chrome/Firefox
// load exactly like a zipped-by-CLI archive (extension payloads are small;
// STORE keeps it deterministic and auditable).
import { readdirSync, readFileSync, writeFileSync, statSync } from 'fs';

let table;
function crc32(buf) {
  if (!table) {
    table = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      table[n] = c;
    }
  }
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ table[(crc ^ buf[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}

function collect(dir) {
  const files = [];
  (function walk(d, prefix) {
    for (const name of readdirSync(d).sort()) {
      const full = `${d}/${name}`;
      const rel = prefix ? `${prefix}/${name}` : name;
      if (statSync(full).isDirectory()) walk(full, rel);
      else files.push({ full, rel });
    }
  })(dir, '');
  return files;
}

/** Writes every file under `dir` (recursively, sorted, forward-slash names)
 *  into `outPath` as a STORE zip. Returns the byte size. */
export function zipDirectory(dir, outPath) {
  const files = collect(dir);
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  const fixedTime = 0;      // 00:00:00
  const fixedDate = 0x21;   // 1980-01-01

  for (const f of files) {
    const data = readFileSync(f.full);
    const name = Buffer.from(f.rel, 'utf8');
    const crc = crc32(data);

    const lfh = Buffer.alloc(30);
    lfh.writeUInt32LE(0x04034b50, 0);   // local file header signature
    lfh.writeUInt16LE(20, 4);           // version needed
    lfh.writeUInt16LE(0, 6);            // flags
    lfh.writeUInt16LE(0, 8);            // method: STORE
    lfh.writeUInt16LE(fixedTime, 10);
    lfh.writeUInt16LE(fixedDate, 12);
    lfh.writeUInt32LE(crc, 14);
    lfh.writeUInt32LE(data.length, 18); // compressed size
    lfh.writeUInt32LE(data.length, 22); // uncompressed size
    lfh.writeUInt16LE(name.length, 26);
    lfh.writeUInt16LE(0, 28);           // extra length
    localParts.push(lfh, name, data);

    const cdh = Buffer.alloc(46);
    cdh.writeUInt32LE(0x02014b50, 0);   // central directory signature
    cdh.writeUInt16LE(20, 4);           // version made by
    cdh.writeUInt16LE(20, 6);           // version needed
    cdh.writeUInt16LE(0, 8);            // flags
    cdh.writeUInt16LE(0, 10);           // method: STORE
    cdh.writeUInt16LE(fixedTime, 12);
    cdh.writeUInt16LE(fixedDate, 14);
    cdh.writeUInt32LE(crc, 16);
    cdh.writeUInt32LE(data.length, 20);
    cdh.writeUInt32LE(data.length, 24);
    cdh.writeUInt16LE(name.length, 28);
    cdh.writeUInt16LE(0, 30);           // extra length
    cdh.writeUInt16LE(0, 32);           // comment length
    cdh.writeUInt16LE(0, 34);           // disk number start
    cdh.writeUInt16LE(0, 36);           // internal attributes
    cdh.writeUInt32LE(0, 38);           // external attributes
    cdh.writeUInt32LE(offset, 42);      // local header offset
    centralParts.push(cdh, name);

    offset += 30 + name.length + data.length;
  }

  const centralSize = centralParts.reduce((n, b) => n + b.length, 0);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);    // end of central directory signature
  eocd.writeUInt16LE(0, 4);             // disk number
  eocd.writeUInt16LE(0, 6);             // disk with central directory
  eocd.writeUInt16LE(files.length, 8);  // entries on this disk
  eocd.writeUInt16LE(files.length, 10); // total entries
  eocd.writeUInt32LE(centralSize, 12);
  eocd.writeUInt32LE(offset, 16);       // central directory offset
  eocd.writeUInt16LE(0, 20);            // comment length

  const out = Buffer.concat([...localParts, ...centralParts, eocd]);
  writeFileSync(outPath, out);
  return out.length;
}
