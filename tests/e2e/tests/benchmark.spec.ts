/**
 * Channel-switch benchmark across three Emby configurations.
 *
 * Run with BENCHMARK_MODE set to one of: baseline | with-stats | no-stats
 *
 *   BENCHMARK_MODE=baseline    npm run benchmark
 *   BENCHMARK_MODE=with-stats  npm run benchmark
 *   BENCHMARK_MODE=no-stats    npm run benchmark
 *
 * Measurements per channel:
 *   infoTime   — click channel tile → play dialog visible
 *   streamTime — click Play → video reaches readyState >= 2
 *
 * Structure: 1 cold pass, 5s pause, 3 warm passes.
 * No assertions — purely observational data for the report.
 *
 * Navigation: uses the Channels tab (non-virtualized grid) rather than the Guide
 * timeline, so channels like BBC ONE and CNN are always reachable via
 * scrollIntoViewIfNeeded regardless of their position in the list.
 */

import { test, type Page } from '@playwright/test';
import { login, saveBenchmarkResults, ChannelBenchmark, TimingPair } from './helpers';

const VALID_MODES = ['baseline', 'with-stats', 'no-stats'] as const;
type BenchmarkMode = typeof VALID_MODES[number];

const BENCHMARK_MODE = process.env.BENCHMARK_MODE as BenchmarkMode | undefined;
const BENCHMARK_CHANNELS = ['NPO 1', 'BBC ONE', 'CNN'];

const CHANNELS_GRID = '.card, [data-type="Channel"], .channelCard, .gridItem';

test.setTimeout(600_000);

/**
 * Cached URL of the Channels tab page.
 * Set on the first successful navigation; reused on all subsequent calls so we
 * never have to click through the sidebar → tab sequence again (which fails when
 * the tab bar is scrolled out of view after video playback).
 */
let channelsUrl: string | null = null;

/** Navigate to the Live TV Channels tab (non-virtualized grid). */
async function goToChannels(page: Page): Promise<void> {
  if (channelsUrl) {
    // Direct navigation — works regardless of scroll position or current page.
    await page.goto(channelsUrl);
    await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
    // If Emby's SPA hash-navigation left a channel info dialog open, close it.
    const playBtn = page.getByRole('button', { name: 'Play', exact: true });
    if (await playBtn.count() > 0) {
      await page.keyboard.press('Escape');
      await page.waitForTimeout(500);
    }
    return;
  }
  // First call: navigate via the nav drawer sidebar then click the Channels tab.
  // Using the nav drawer button avoids ambiguity with the "Live TV" tile card that
  // also appears on the home page.
  await page.getByRole('button', { name: ' Live TV' }).first().click();
  await page.getByRole('button', { name: 'Channels', exact: true }).click({ timeout: 10_000 });
  await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
  // Cache the URL so subsequent calls bypass the sidebar entirely.
  channelsUrl = page.url();
}

/** Stop any active video and return to the Channels grid. */
async function returnToChannels(page: Page): Promise<void> {
  // Dismiss the video player and/or channel-info dialog.
  //
  // We check BEFORE pressing Escape rather than hard-coding 2 presses, because:
  //   - If the video already ended, pressing Escape into an idle page can trigger
  //     browser-history "back", which navigates to the channel detail page and
  //     corrupts subsequent navigation.
  //   - We stop as soon as neither a video element nor the Play dialog button are
  //     present, so at most one unnecessary Escape fires.
  for (let attempt = 0; attempt < 4; attempt++) {
    const hasVideo = (await page.locator('video').count()) > 0;
    const hasDialog = (await page.getByRole('button', { name: 'Play', exact: true }).count()) > 0;
    if (!hasVideo && !hasDialog) break;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(400);
  }
  await goToChannels(page);
  // Scroll back to top so findChannelButton always starts from the beginning.
  await page.mouse.move(640, 400);
  await page.mouse.wheel(0, -99999);
  await page.waitForTimeout(300);
}

/**
 * Scroll the channels grid until the button for `channel` appears in the DOM.
 * The Channels tab uses virtual scrolling, so channels far down the list are
 * not rendered until the user scrolls past them.
 *
 * Channel buttons have accessible names like "109 BBC ONE" or "101 NPO 1".
 * We match the name ending with the channel string (word-boundary anchor) to
 * avoid false substring matches — e.g. "NPO 1" must NOT match "NPO 1 EXTRA 71".
 */
async function findChannelButton(page: Page, channel: string) {
  const escaped = channel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const btn = () => page.getByRole('button', { name: new RegExp(`\\b${escaped}$`) }).first();

  // Position mouse in content area then wheel-scroll down.
  // Using mouse.wheel avoids having to find the exact scroll container element.
  await page.mouse.move(640, 400);
  for (let i = 0; i < 60; i++) {
    if (await btn().count() > 0) return btn();
    await page.mouse.wheel(0, 800);
    await page.waitForTimeout(150);
  }
  throw new Error(`Channel button for "${channel}" not found after scrolling`);
}

async function measureChannel(page: Page, channel: string): Promise<TimingPair> {
  // ── Info screen time ──────────────────────────────────────────────────────
  const t0 = performance.now();

  // Scroll the virtual-scroll grid until the channel button is in the DOM, then click.
  const channelBtn = await findChannelButton(page, channel);
  await channelBtn.scrollIntoViewIfNeeded({ timeout: 10_000 });
  await channelBtn.click({ timeout: 10_000 });

  // The channel info dialog contains a "Play" button. Use role+name to avoid
  // false matches against other buttons earlier in DOM order.
  const playBtn = page.getByRole('button', { name: 'Play', exact: true });
  await playBtn.waitFor({ state: 'visible', timeout: 60_000 });

  const infoTime = (performance.now() - t0) / 1000;

  // ── Stream start time ─────────────────────────────────────────────────────
  const t1 = performance.now();

  await playBtn.click();

  // Wait for the video to reach HAVE_CURRENT_DATA (readyState ≥ 2).
  //
  // Note on waitForFunction call signature:
  //   page.waitForFunction(fn, arg, options) — arg must be passed explicitly so
  //   that { timeout, polling } lands in the options slot, not the arg slot.
  //   Passing undefined as arg is safe because the fn ignores its argument.
  //
  // If the stream fails (Playback Error dialog), we catch the timeout, dismiss
  // the dialog, and record streamTime = -1 so the benchmark run continues.
  let streamTime: number;
  try {
    await page.waitForSelector('video', { state: 'attached', timeout: 15_000 });
    await page.waitForFunction(
      () => {
        const v = document.querySelector('video');
        return v !== null && (v as HTMLVideoElement).readyState >= 2; // HAVE_CURRENT_DATA
      },
      undefined, // no arg passed to the pageFunction
      { timeout: 30_000, polling: 200 },
    );
    streamTime = (performance.now() - t1) / 1000;
  } catch {
    // Stream failed — dismiss any error dialog so the benchmark can continue.
    const gotIt = page.getByRole('button', { name: 'Got It' });
    if (await gotIt.count() > 0) await gotIt.click();
    streamTime = -1;
    console.log(`  [WARN] stream failed for ${channel} — recorded streamTime = -1`);
  }

  return { infoTime, streamTime };
}

test.describe('channel switch benchmark', () => {
  test.skip(
    !BENCHMARK_MODE || !VALID_MODES.includes(BENCHMARK_MODE),
    `Set BENCHMARK_MODE to one of: ${VALID_MODES.join(', ')}`,
  );

  test(`benchmark [${BENCHMARK_MODE ?? 'unset'}]`, async ({ page }) => {
    const mode = BENCHMARK_MODE!;

    await login(page);
    await goToChannels(page);

    const results: Record<string, ChannelBenchmark> = {};
    for (const ch of BENCHMARK_CHANNELS) {
      results[ch] = { cold: { infoTime: 0, streamTime: 0 }, warm: [] };
    }

    // ── Cold pass ─────────────────────────────────────────────────────────────
    console.log('\n=== Cold pass ===');
    for (let i = 0; i < BENCHMARK_CHANNELS.length; i++) {
      const ch = BENCHMARK_CHANNELS[i];
      const timing = await measureChannel(page, ch);
      results[ch].cold = timing;
      console.log(
        `[cold][${ch}] info=${timing.infoTime.toFixed(2)}s  stream=${timing.streamTime.toFixed(2)}s` +
        `  total=${(timing.infoTime + timing.streamTime).toFixed(2)}s`,
      );
      if (i < BENCHMARK_CHANNELS.length - 1) {
        await returnToChannels(page);
      }
    }

    // ── 5s pause between cold and warm ───────────────────────────────────────
    console.log('\nWaiting 5s before warm passes…');
    await page.waitForTimeout(5_000);

    // ── Warm passes (3×) ─────────────────────────────────────────────────────
    for (let pass = 1; pass <= 3; pass++) {
      console.log(`\n=== Warm pass ${pass}/3 ===`);

      await returnToChannels(page);

      for (let i = 0; i < BENCHMARK_CHANNELS.length; i++) {
        const ch = BENCHMARK_CHANNELS[i];
        const timing = await measureChannel(page, ch);
        results[ch].warm.push(timing);
        console.log(
          `[warm ${pass}][${ch}] info=${timing.infoTime.toFixed(2)}s  stream=${timing.streamTime.toFixed(2)}s` +
          `  total=${(timing.infoTime + timing.streamTime).toFixed(2)}s`,
        );
        if (i < BENCHMARK_CHANNELS.length - 1) {
          await returnToChannels(page);
        }
      }
    }

    saveBenchmarkResults(mode, { channels: results });
  });
});
