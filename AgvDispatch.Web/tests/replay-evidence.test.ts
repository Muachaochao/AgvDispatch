import assert from 'node:assert/strict'
import { test } from 'node:test'
import { replayCues, replayFrameIndex, replayDelayMs, replayRate, replayClockFrame } from '../src/replay-evidence.ts'

test('maps log timestamps to the previous vehicle observation and keeps between-frame events in export', () => {
  const observations = [{ time: 100 }, { time: 108 }, { time: 116 }]
  assert.equal(replayFrameIndex(observations, 105), 0)
  assert.equal(replayFrameIndex(observations, 108), 1)
  const events = [{ time: 103 }, { time: 108 }, { time: 115 }]
  assert.deepEqual(replayCues(observations, events), [100, 103, 108, 115, 116])
})

test('playback and export use recording rate with independent speed and without an artificial per-cue floor', () => {
  assert.equal(replayDelayMs(100, 108, 4, 0.5), 4000)
  assert.equal(replayDelayMs(100, 108, 4, 8), 250)
  assert.equal(replayDelayMs(100, 100.5, 4, 128), 0.9765625)
  assert.equal(replayRate(0), 0.1)
  assert.equal(replayRate(Number.NaN), 1)
  assert.equal(replayRate(200), 128)
  assert.equal(replayClockFrame([{ time: 100 }, { time: 108 }, { time: 116 }], 100, 250, 4, 8), 1)
})
