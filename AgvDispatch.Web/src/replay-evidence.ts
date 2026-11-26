export interface ReplayTime { time: number }
export interface ReplayPosition extends ReplayTime { x: number; y: number; angle?: number; site?: string }
export interface ReplayEvent extends ReplayTime {
  wallTime?: number;
  position?: ReplayPosition;
  kind: string;
  category: string;
  title: string;
  source: string;
  taskId?: string;
  vehicleId?: string;
  commandId?: string;
  vendorCommandId?: string;
  stationId?: string;
  resourceIds?: string[];
  holderVehicleIds?: string[];
  detail?: Record<string, unknown>;
}

export interface ReplayCoverage {
  clockAligned: boolean;
  warnings: string[];
  omittedEvents: number;
  unconfirmedCommands: string[];
}

export const replayPresets = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 4, 8, 16, 32, 64, 128];

export function replayRate(value: number): number {
  return Number.isFinite(value) ? Math.max(0.1, Math.min(128, value)) : 1;
}

export function recordingRate(value: number | undefined): number {
  return value !== undefined && Number.isFinite(value) && value > 0 ? value : 1;
}

export function replayDelayMs(start: number, end: number, base: number, speed: number): number {
  return Math.max(0, end - start) * 1000 / recordingRate(base) / replayRate(speed);
}

export function replayFrameIndex(frames: readonly ReplayTime[], time: number): number {
  let low = 0, high = frames.length;
  while (low < high) {
    const mid = Math.floor((low + high) / 2);
    if (frames[mid]!.time <= time) low = mid + 1;
    else high = mid;
  }
  return Math.max(0, low - 1);
}

export function replayClockFrame(frames: readonly ReplayTime[], start: number, elapsedMs: number, base: number, speed: number) {
  return replayFrameIndex(frames, start + elapsedMs * recordingRate(base) * replayRate(speed) / 1000);
}

export function replayCues(frames: readonly ReplayTime[], events: readonly ReplayTime[]): number[] {
  if (!frames.length) return [];
  return [...new Set([...frames.map(frame => frame.time),
    ...events.map(event => event.time)])].sort((a, b) => a - b);
}

export function replayEventSummary(event: ReplayEvent): string {
  const transition = event.kind === 'plc-bit' ? [event.detail?.address, event.detail?.before, event.detail?.value].join(' / ') : undefined;
  return [event.title, transition, event.vehicleId, event.taskId, event.commandId ?? event.vendorCommandId,
    event.resourceIds?.join('、'), event.holderVehicleIds?.length ? '占用：' + event.holderVehicleIds.join('、') : undefined]
    .filter(Boolean).join(' · ');
}

export function fitReplayText(context: CanvasRenderingContext2D, text: string, width: number): string {
  if (context.measureText(text).width <= width) return text;
  let low = 0, high = text.length;
  while (low < high) {
    const mid = Math.ceil((low + high) / 2);
    if (context.measureText(text.slice(0, mid) + '…').width <= width) low = mid;
    else high = mid - 1;
  }
  return text.slice(0, low) + '…';
}

export function drawReplayEvidence(canvas: HTMLCanvasElement, time: number, events: readonly ReplayEvent[], rate: number) {
  const context = canvas.getContext('2d');
  if (!context) return;
  const eventIndex = replayFrameIndex(events, time);
  const recent = events.slice(Math.max(0, eventIndex - 2), eventIndex + 1).filter(event => event.time <= time);
  const top = canvas.height - 94;
  context.save();
  context.fillStyle = '#101827';
  context.fillRect(0, top, canvas.width, 94);
  context.fillStyle = '#85cbe7';
  context.font = '14px sans-serif';
  context.fillText('最近事件 · 导出 ' + rate + '×', 26, top + 18);
  context.fillStyle = '#edf4ff';
  recent.forEach((event, index) => {
    const label = new Date(event.time * 1000).toLocaleTimeString() + ' · ' + replayEventSummary(event);
    context.fillText(fitReplayText(context, label, canvas.width - 52), 26, top + 38 + index * 18);
  });
  context.restore();
}

export async function recordReplayWebm<T extends ReplayTime>(options: {
  canvas: HTMLCanvasElement;
  frames: readonly T[];
  events: readonly ReplayEvent[];
  base: number;
  speed: number;
  draw: (frame: T, index: number) => void;
  progress: (percent: number) => void;
  signal?: AbortSignal;
}): Promise<Blob> {
  const { canvas, frames, events, base, speed, draw, progress, signal } = options;
  const mimeType = typeof MediaRecorder === 'undefined' ? undefined :
    ['video/webm;codecs=vp8', 'video/webm'].find(type => MediaRecorder.isTypeSupported(type));
  if (!mimeType || !canvas.captureStream) throw new Error('当前浏览器不支持 WebM 导出。');
  const cues = replayCues(frames, events);
  if (!cues.length) throw new Error('当前报告没有可导出的回放采样。');

  let drawnIndex = -1;
  const render = (time: number) => {
    const i = replayFrameIndex(frames, time);
    if (i !== drawnIndex) {
      draw(frames[i]!, i);
      drawnIndex = i;
    }
    drawReplayEvidence(canvas, time, events, speed);
  };
  const chunks: BlobPart[] = [];
  render(cues[0]!);
  const stream = canvas.captureStream(30);
  try {
    const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 2_500_000 });
    const finished = new Promise<Blob | null>(resolve => {
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
      recorder.onstop = () => resolve(new Blob(chunks, { type: mimeType }));
      recorder.onerror = () => resolve(null);
    });
    recorder.start(1000);
    try {
      const started = performance.now();
      const first = cues[0]!, last = cues.at(-1)!;
      let previousDue = Number.NEGATIVE_INFINITY;
      for (const [index, time] of cues.entries()) {
        const due = started + replayDelayMs(first, time, base, speed);
        if (index < cues.length - 1 && due - previousDue < 1000 / 30) continue;
        previousDue = due;
        // Break long waits so closing the report cancels recording promptly.
        while (!signal?.aborted && recorder.state === 'recording' && performance.now() < due) {
          await new Promise(resolve => setTimeout(resolve, Math.min(200, Math.max(1, due - performance.now()))));
        }
        signal?.throwIfAborted();
        if (recorder.state !== 'recording') break;
        render(time);
        progress(Math.round((last > first ? (time - first) / (last - first) : (index + 1) / cues.length) * 100));
      }
      await new Promise(resolve => setTimeout(resolve, 350));
    } finally {
      if (recorder.state === 'recording') recorder.stop();
    }
    const blob = await finished;
    if (!blob?.size) throw new Error('浏览器未生成可用的 WebM 视频。');
    return blob;
  } finally {
    stream.getTracks().forEach(track => track.stop());
  }
}
