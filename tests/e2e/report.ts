/**
 * Benchmark report generator.
 *
 * Reads results/benchmark-*.json, picks the latest file per mode,
 * and prints three markdown tables to stdout.
 *
 * Usage:
 *   npm run report              # print to terminal
 *   npm run report | pbcopy     # copy for GitHub / Discord
 */

import * as fs from 'fs';
import * as path from 'path';
import { execSync } from 'child_process';

// ── Types (mirrors helpers.ts without importing Playwright) ───────────────────

interface TimingPair {
  infoTime: number;
  streamTime: number;
}

interface ChannelBenchmark {
  cold: TimingPair;
  warm: TimingPair[];
}

interface BenchmarkFile {
  mode: string;
  version: string;
  commit: string;
  date: string;
  channels: Record<string, ChannelBenchmark>;
}

// ── Constants ─────────────────────────────────────────────────────────────────

const MODES = ['baseline', 'with-stats', 'no-stats'] as const;
type Mode = typeof MODES[number];

const MODE_LABELS: Record<Mode, string> = {
  'baseline':   'Baseline',
  'with-stats': 'Plugin + Stats',
  'no-stats':   'Plugin, No Stats',
};

// ── File loading ──────────────────────────────────────────────────────────────

function loadLatestPerMode(resultsDir: string): Partial<Record<Mode, BenchmarkFile>> {
  const result: Partial<Record<Mode, BenchmarkFile>> = {};

  if (!fs.existsSync(resultsDir)) return result;

  const files = fs.readdirSync(resultsDir)
    .filter(f => f.startsWith('benchmark-') && f.endsWith('.json'))
    .sort(); // lexicographic sort — date suffix means latest is last

  for (const file of files) {
    try {
      const raw = fs.readFileSync(path.join(resultsDir, file), 'utf8');
      const data: BenchmarkFile = JSON.parse(raw);
      if (MODES.includes(data.mode as Mode)) {
        result[data.mode as Mode] = data; // later files overwrite earlier ones
      }
    } catch {
      // Skip malformed files.
    }
  }

  return result;
}

// ── Computation helpers ───────────────────────────────────────────────────────

/** streamTime = -1 means the stream failed to load; exclude from numeric stats. */
function streamFailed(pair: TimingPair): boolean {
  return pair.streamTime < 0;
}

function avg(pairs: TimingPair[], field: keyof TimingPair): number {
  const valid = pairs.filter(p => field === 'streamTime' ? !streamFailed(p) : true);
  if (!valid.length) return 0;
  return valid.reduce((s, p) => s + p[field], 0) / valid.length;
}

/**
 * Returns the total time for a pair, or undefined if the stream failed.
 * A failed stream (-1) makes the total meaningless, so we return undefined
 * and let callers render it as an error cell.
 */
function total(pair: TimingPair): number | undefined {
  if (streamFailed(pair)) return undefined;
  return pair.infoTime + pair.streamTime;
}

function fmt(n: number): string {
  return n.toFixed(2) + 's';
}

function pct(value: number, baseline: number): string {
  if (baseline === 0) return '';
  const diff = ((value - baseline) / baseline) * 100;
  const sign = diff >= 0 ? '+' : '';
  return ` (${sign}${diff.toFixed(0)}%)`;
}

/**
 * Format a table cell value.
 *   undefined  →  '--'   (data not available; mode was not run)
 *   null       →  'ERR'  (stream failed to load)
 *   number     →  '1.23s' with optional % diff from baseline
 */
function cell(value: number | null | undefined, baseline: number | null | undefined, bold = false): string {
  if (value === undefined) return '--';
  if (value === null) return bold ? '**ERR**' : 'ERR';
  const base = fmt(value);
  const suffix = (baseline !== undefined && baseline !== null) ? pct(value, baseline) : '';
  const text = base + suffix;
  return bold ? `**${text}**` : text;
}

// ── Meta ──────────────────────────────────────────────────────────────────────

function resolveVersion(): string {
  if (process.env.PLUGIN_VERSION) return process.env.PLUGIN_VERSION;
  try {
    return execSync('git describe --tags --abbrev=0', { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

function resolveCommit(): string {
  try {
    return execSync('git rev-parse --short HEAD', { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

// ── Table builders ────────────────────────────────────────────────────────────

function buildColdTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  const lines: string[] = [
    '### Cold Start (first play)',
    '',
    '| Channel  | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|----------|----------|----------------|-----------------|',
  ];

  // null = stream failed; undefined = mode not run / channel missing
  const avgs = { baseline: 0, withStats: 0, noStats: 0 };
  let bCount = 0, wCount = 0, nCount = 0;

  for (const ch of channels) {
    const b: number | null | undefined = baseline?.channels[ch]
      ? (total(baseline.channels[ch].cold) ?? null)
      : undefined;
    const w: number | null | undefined = withStats?.channels[ch]
      ? (total(withStats.channels[ch].cold) ?? null)
      : undefined;
    const n: number | null | undefined = noStats?.channels[ch]
      ? (total(noStats.channels[ch].cold) ?? null)
      : undefined;

    lines.push(`| ${ch.padEnd(8)} | ${cell(b, undefined)} | ${cell(w, b)} | ${cell(n, b)} |`);

    if (typeof b === 'number') { avgs.baseline += b; bCount++; }
    if (typeof w === 'number') { avgs.withStats += w; wCount++; }
    if (typeof n === 'number') { avgs.noStats += n; nCount++; }
  }

  const ab = bCount ? avgs.baseline / bCount : undefined;
  const aw = wCount ? avgs.withStats / wCount : (withStats ? null : undefined);
  const an = nCount ? avgs.noStats / nCount : (noStats ? null : undefined);

  lines.push(`| **Avg**  | ${cell(ab, undefined, true)} | ${cell(aw, ab, true)} | ${cell(an, ab, true)} |`);

  return lines.join('\n');
}

function buildWarmTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  const lines: string[] = [
    '### Warm Start (avg of 3 runs)',
    '',
    '| Channel  | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|----------|----------|----------------|-----------------|',
  ];

  // Average info + stream across warm runs, excluding failed streams.
  // Returns undefined if there are no warm measurements, null if all streams failed.
  function warmAvgTotal(warm: TimingPair[] | undefined): number | null | undefined {
    if (!warm?.length) return undefined;
    if (!warm.some(p => !streamFailed(p))) return null; // all streams failed
    return avg(warm, 'infoTime') + avg(warm, 'streamTime');
  }

  const avgs = { baseline: 0, withStats: 0, noStats: 0 };
  let bCount = 0, wCount = 0, nCount = 0;

  for (const ch of channels) {
    const bWarm = baseline?.channels[ch]?.warm;
    const wWarm = withStats?.channels[ch]?.warm;
    const nWarm = noStats?.channels[ch]?.warm;

    // null = data exists but all streams failed; undefined = mode not run
    const bTotal: number | null | undefined = bWarm !== undefined
      ? warmAvgTotal(bWarm)
      : undefined;
    const wTotal: number | null | undefined = wWarm !== undefined
      ? warmAvgTotal(wWarm)
      : undefined;
    const nTotal: number | null | undefined = nWarm !== undefined
      ? warmAvgTotal(nWarm)
      : undefined;

    lines.push(`| ${ch.padEnd(8)} | ${cell(bTotal, undefined)} | ${cell(wTotal, bTotal)} | ${cell(nTotal, bTotal)} |`);

    if (typeof bTotal === 'number') { avgs.baseline += bTotal; bCount++; }
    if (typeof wTotal === 'number') { avgs.withStats += wTotal; wCount++; }
    if (typeof nTotal === 'number') { avgs.noStats += nTotal; nCount++; }
  }

  const ab = bCount ? avgs.baseline / bCount : undefined;
  const aw = wCount ? avgs.withStats / wCount : (withStats ? null : undefined);
  const an = nCount ? avgs.noStats / nCount : (noStats ? null : undefined);

  lines.push(`| **Avg**  | ${cell(ab, undefined, true)} | ${cell(aw, ab, true)} | ${cell(an, ab, true)} |`);

  return lines.join('\n');
}

function buildPhaseTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  // Aggregate across channels (cold only); skip channels where the stream failed.
  let bInfo = 0, bStream = 0, wInfo = 0, wStream = 0, nInfo = 0, nStream = 0;
  let count = 0;

  for (const ch of channels) {
    const bc = baseline?.channels[ch]?.cold;
    const wc = withStats?.channels[ch]?.cold;
    const nc = noStats?.channels[ch]?.cold;
    if (bc && !streamFailed(bc)) { bInfo += bc.infoTime; bStream += bc.streamTime; count++; }
    if (wc && !streamFailed(wc)) { wInfo += wc.infoTime; wStream += wc.streamTime; }
    if (nc && !streamFailed(nc)) { nInfo += nc.infoTime; nStream += nc.streamTime; }
  }

  const c = count || 1;
  const ab_info   = count ? bInfo / c : undefined;
  const ab_stream = count ? bStream / c : undefined;
  const aw_info   = withStats ? wInfo / c : undefined;
  const aw_stream = withStats ? wStream / c : undefined;
  const an_info   = noStats ? nInfo / c : undefined;
  const an_stream = noStats ? nStream / c : undefined;

  const ab_total = ab_info !== undefined && ab_stream !== undefined ? ab_info + ab_stream : undefined;
  const aw_total = aw_info !== undefined && aw_stream !== undefined ? aw_info + aw_stream : undefined;
  const an_total = an_info !== undefined && an_stream !== undefined ? an_info + an_stream : undefined;

  const lines: string[] = [
    '### Where Time is Spent (cold start, avg across channels)',
    '',
    '| Phase        | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|--------------|----------|----------------|-----------------|',
    `| Info dialog  | ${cell(ab_info, undefined)} | ${cell(aw_info, ab_info)} | ${cell(an_info, ab_info)} |`,
    `| Stream start | ${cell(ab_stream, undefined)} | ${cell(aw_stream, ab_stream)} | ${cell(an_stream, ab_stream)} |`,
    `| **Total**    | ${cell(ab_total, undefined, true)} | ${cell(aw_total, ab_total, true)} | ${cell(an_total, ab_total, true)} |`,
  ];

  return lines.join('\n');
}

// ── Missing modes note ────────────────────────────────────────────────────────

function missingNote(data: Partial<Record<Mode, BenchmarkFile>>): string | null {
  const missing = MODES.filter(m => !data[m]).map(m => `\`${m}\``);
  if (!missing.length) return null;
  return `> **Note**: Modes still to run: ${missing.join(', ')}. Run \`BENCHMARK_MODE=<mode> npm run benchmark\` to fill them in.`;
}

// ── Main ──────────────────────────────────────────────────────────────────────

function main(): void {
  const resultsDir = path.resolve(__dirname, 'results');
  const data = loadLatestPerMode(resultsDir);

  const version = resolveVersion();
  const commit = resolveCommit();
  const date = new Date().toISOString().slice(0, 10);

  // Collect all channel names seen across any mode (preserve insertion order of BENCHMARK_CHANNELS)
  const BENCHMARK_CHANNELS = ['NPO 1', 'BBC ONE', 'CNN'];
  const seenChannels = new Set<string>(BENCHMARK_CHANNELS);
  for (const file of Object.values(data)) {
    if (file) Object.keys(file.channels).forEach(ch => seenChannels.add(ch));
  }
  const channels = [...seenChannels];

  const output: string[] = [
    '## Emby Channel Switch Benchmark',
    `_Plugin ${version} (${commit}) | ${date}_`,
    '',
    buildColdTable(channels, data),
    '',
    buildWarmTable(channels, data),
    '',
    buildPhaseTable(channels, data),
  ];

  const note = missingNote(data);
  if (note) {
    output.push('');
    output.push(note);
  }

  console.log(output.join('\n'));
}

main();
