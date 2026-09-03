// Renders the RoslynSense marketplace icon (256x256 PNG) without any native deps:
// 4x supersampled coverage rasterizer + zlib PNG encoding.
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';

const SIZE = 256, SS = 4, N = SIZE * SS; // supersampled grid

// --- geometry helpers (all in final 256-space coordinates) ---
const R = 56; // corner radius
function inRoundedRect(x, y) {
    const cx = Math.min(Math.max(x, R), SIZE - R);
    const cy = Math.min(Math.max(y, R), SIZE - R);
    const dx = x - cx, dy = y - cy;
    return dx * dx + dy * dy <= R * R;
}

// glyph transform: translate(128,128) scale(9) translate(-12,-12)
const S = 9, OFF = 128 - 12 * S;
const g = (u, v) => [OFF + u * S, OFF + v * S];

const strokes = [
    [g(8, 7.75), g(3.25, 12), g(8, 16.25)],
    [g(16, 7.75), g(20.75, 12), g(16, 16.25)],
];
const HALF_W = (1.5 * S) / 2; // round caps/joins => distance-to-polyline test

function distToSegment(px, py, [ax, ay], [bx, by]) {
    const vx = bx - ax, vy = by - ay;
    const t = Math.max(0, Math.min(1, ((px - ax) * vx + (py - ay) * vy) / (vx * vx + vy * vy)));
    const dx = px - (ax + t * vx), dy = py - (ay + t * vy);
    return Math.hypot(dx, dy);
}
function inStroke(x, y) {
    for (const pts of strokes) {
        for (let i = 0; i < pts.length - 1; i++) {
            if (distToSegment(x, y, pts[i], pts[i + 1]) <= HALF_W) return true;
        }
    }
    return false;
}

// diamond: |dx| + |dy| <= 3.75 in glyph units, centered at (12,12)
function inDiamond(x, y) {
    const dx = Math.abs(x - 128) / S, dy = Math.abs(y - 128) / S;
    return dx + dy <= 3.75;
}

const lerp = (a, b, t) => a + (b - a) * t;
const TOP = [0x70, 0x42, 0xe6], BOT = [0x3b, 0x1d, 0x96];

// --- render, supersampled ---
const px = new Float64Array(SIZE * SIZE * 4); // premultiplied rgba accumulators
for (let sy = 0; sy < N; sy++) {
    const y = (sy + 0.5) / SS;
    for (let sx = 0; sx < N; sx++) {
        const x = (sx + 0.5) / SS;
        if (!inRoundedRect(x, y)) continue;
        const t = (x + y) / (2 * SIZE);
        let r = lerp(TOP[0], BOT[0], t), gg = lerp(TOP[1], BOT[1], t), b = lerp(TOP[2], BOT[2], t);
        // top highlight
        if (y < 110) {
            const a = 0.14 * (1 - y / 110);
            r = lerp(r, 255, a); gg = lerp(gg, 255, a); b = lerp(b, 255, a);
        }
        if (inStroke(x, y) || inDiamond(x, y)) { r = 255; gg = 255; b = 255; }
        const i = ((sy >> 2) * SIZE + (sx >> 2)) * 4;
        px[i] += r; px[i + 1] += gg; px[i + 2] += b; px[i + 3] += 255;
    }
}

// --- PNG encode (RGBA8) ---
const raw = Buffer.alloc(SIZE * (SIZE * 4 + 1));
const SAMPLES = SS * SS;
for (let y = 0; y < SIZE; y++) {
    const row = y * (SIZE * 4 + 1);
    raw[row] = 0; // filter: none
    for (let x = 0; x < SIZE; x++) {
        const i = (y * SIZE + x) * 4, o = row + 1 + x * 4;
        const a = px[i + 3] / SAMPLES / 255; // coverage
        if (a > 0) {
            raw[o] = Math.round(px[i] / SAMPLES / a * (a > 1 ? 1 : 1)) & 0xff;
            raw[o] = Math.min(255, Math.round(px[i] / (px[i + 3] / 255)));
            raw[o + 1] = Math.min(255, Math.round(px[i + 1] / (px[i + 3] / 255)));
            raw[o + 2] = Math.min(255, Math.round(px[i + 2] / (px[i + 3] / 255)));
        }
        raw[o + 3] = Math.min(255, Math.round(px[i + 3] / SAMPLES));
    }
}

function chunk(type, data) {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const crcTable = [];
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        crcTable[n] = c >>> 0;
    }
    let crc = 0xffffffff;
    for (const byte of body) crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
    const crcBuf = Buffer.alloc(4); crcBuf.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([len, body, crcBuf]);
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(SIZE, 0); ihdr.writeUInt32BE(SIZE, 4);
ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA

const png = Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
]);
writeFileSync(process.argv[2], png);
console.log(`wrote ${process.argv[2]} (${png.length} bytes)`);
