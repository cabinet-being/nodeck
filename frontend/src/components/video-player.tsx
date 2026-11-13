import * as React from 'react';
import { Maximize2, Minimize2, Pause, Play, Repeat, Volume2, VolumeX } from 'lucide-react';

import { cn } from '@/lib/utils';

const loopKey = 'nodeck.video.loop';
const volumeKey = 'nodeck.video.volume';
const mutedKey = 'nodeck.video.muted';
const speeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2];
const frameSeconds = 1 / 30;
const idleHideMs = 2500;

/** Browser storage can throw (private mode, blocked site data); the player works without it. */
function readStored(key: string) {
  try { return window.localStorage.getItem(key); } catch { return null; }
}

function writeStored(key: string, value: string) {
  try { window.localStorage.setItem(key, value); } catch { /* preference is optional */ }
}

export function formatTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds < 0) seconds = 0;
  const whole = Math.floor(seconds);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = String(whole % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}

type Segment = { start: number; end: number | null };

/**
 * Video player with its own controls. Unlike the browser's built-in controls it can loop
 * the whole video (remembered between videos) or a chosen A–B segment.
 *
 * Keys while the player has focus: Space/K play, ←/→ seek 5 s, ↑/↓ volume, M mute,
 * R loop, A segment loop, ,/. one frame (paused), [/] speed, F fullscreen.
 */
export function VideoPlayer({
  src,
  poster,
  label,
  className,
  videoClassName,
}: {
  src: string;
  poster?: string;
  label: string;
  className?: string;
  /** Extra classes for the <video>, for example a max height that matches the stage. */
  videoClassName?: string;
}) {
  const frameRef = React.useRef<HTMLDivElement>(null);
  const videoRef = React.useRef<HTMLVideoElement>(null);
  const hideTimer = React.useRef<number | undefined>(undefined);
  const [playing, setPlaying] = React.useState(false);
  const [current, setCurrent] = React.useState(0);
  const [duration, setDuration] = React.useState(0);
  const [buffered, setBuffered] = React.useState(0);
  const [loop, setLoop] = React.useState(() => readStored(loopKey) !== 'false');
  const [segment, setSegment] = React.useState<Segment | null>(null);
  const [volume, setVolume] = React.useState(() => {
    const stored = Number(readStored(volumeKey));
    return readStored(volumeKey) !== null && Number.isFinite(stored) ? Math.min(1, Math.max(0, stored)) : 1;
  });
  const [muted, setMuted] = React.useState(() => readStored(mutedKey) === 'true');
  const [speed, setSpeed] = React.useState(1);
  const [fullscreen, setFullscreen] = React.useState(false);
  const [controlsVisible, setControlsVisible] = React.useState(true);
  const canFullscreen = typeof document !== 'undefined' && document.fullscreenEnabled === true;

  // A new source starts from scratch; the loop, volume and mute preferences stay.
  React.useEffect(() => {
    setPlaying(false);
    setCurrent(0);
    setDuration(0);
    setBuffered(0);
    setSegment(null);
  }, [src]);

  React.useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    video.volume = volume;
    video.muted = muted;
  }, [volume, muted, src]);

  React.useEffect(() => {
    if (videoRef.current) videoRef.current.playbackRate = speed;
  }, [speed, src]);

  React.useEffect(() => {
    function sync() { setFullscreen(document.fullscreenElement === frameRef.current); }
    document.addEventListener('fullscreenchange', sync);
    return () => document.removeEventListener('fullscreenchange', sync);
  }, []);

  React.useEffect(() => () => window.clearTimeout(hideTimer.current), []);

  const activeSegment = segment && segment.end !== null ? segment as { start: number; end: number } : null;

  // Native `loop` handles the whole video; a segment is enforced on every time update.
  function onTimeUpdate() {
    const video = videoRef.current;
    if (!video) return;
    if (activeSegment && video.currentTime >= activeSegment.end) {
      video.currentTime = activeSegment.start;
      if (video.paused) void video.play().catch(() => undefined);
    }
    setCurrent(video.currentTime);
    if (video.buffered.length > 0) setBuffered(video.buffered.end(video.buffered.length - 1));
  }

  function onEnded() {
    const video = videoRef.current;
    if (video && activeSegment) {
      video.currentTime = activeSegment.start;
      void video.play().catch(() => undefined);
    }
  }

  function showControls() {
    setControlsVisible(true);
    window.clearTimeout(hideTimer.current);
    hideTimer.current = window.setTimeout(() => {
      if (videoRef.current && !videoRef.current.paused) setControlsVisible(false);
    }, idleHideMs);
  }

  function togglePlay() {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) {
      if (activeSegment && (video.currentTime < activeSegment.start || video.currentTime >= activeSegment.end))
        video.currentTime = activeSegment.start;
      void video.play().catch(() => undefined);
    } else {
      video.pause();
    }
    showControls();
  }

  function seekTo(time: number) {
    const video = videoRef.current;
    if (!video || !Number.isFinite(time)) return;
    const end = Number.isFinite(video.duration) ? video.duration : time;
    video.currentTime = Math.min(Math.max(0, time), end);
    setCurrent(video.currentTime);
  }

  function toggleLoop() {
    setLoop((value) => {
      writeStored(loopKey, String(!value));
      return !value;
    });
  }

  /** Off → A set at the current time → A–B loop → off. */
  function cycleSegment() {
    const video = videoRef.current;
    if (!video) return;
    const time = video.currentTime;
    if (!segment) setSegment({ start: time, end: null });
    else if (segment.end === null) {
      // B before (or at) A just moves A, so the segment always runs forward.
      if (time - segment.start < 0.1) setSegment({ start: time, end: null });
      else {
        setSegment({ start: segment.start, end: time });
        video.currentTime = segment.start;
      }
    } else setSegment(null);
    showControls();
  }

  function changeVolume(next: number) {
    const value = Math.min(1, Math.max(0, next));
    setVolume(value);
    writeStored(volumeKey, String(value));
    if (value > 0 && muted) {
      setMuted(false);
      writeStored(mutedKey, 'false');
    }
  }

  function toggleMute() {
    setMuted((value) => {
      writeStored(mutedKey, String(!value));
      return !value;
    });
  }

  function changeSpeed(direction: 1 | -1) {
    setSpeed((value) => {
      const index = speeds.indexOf(value);
      return speeds[Math.min(speeds.length - 1, Math.max(0, (index < 0 ? 3 : index) + direction))];
    });
  }

  function cycleSpeed() {
    setSpeed((value) => speeds[(speeds.indexOf(value) + 1) % speeds.length] ?? 1);
  }

  function stepFrame(direction: 1 | -1) {
    const video = videoRef.current;
    if (!video) return;
    video.pause();
    seekTo(video.currentTime + direction * frameSeconds);
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void frameRef.current?.requestFullscreen?.().catch(() => undefined);
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    const video = videoRef.current;
    if (!video) return;
    const onSlider = event.target instanceof HTMLInputElement;
    const handled = (() => {
      switch (event.key) {
        case ' ':
        case 'k':
        case 'K':
          if (event.target instanceof HTMLButtonElement && event.key === ' ') return false;
          togglePlay();
          return true;
        case 'ArrowLeft':
          if (onSlider) return false;
          seekTo(video.currentTime - 5);
          return true;
        case 'ArrowRight':
          if (onSlider) return false;
          seekTo(video.currentTime + 5);
          return true;
        case 'ArrowUp':
          if (onSlider) return false;
          changeVolume(volume + 0.1);
          return true;
        case 'ArrowDown':
          if (onSlider) return false;
          changeVolume(volume - 0.1);
          return true;
        case 'm':
        case 'M':
          toggleMute();
          return true;
        case 'r':
        case 'R':
          toggleLoop();
          return true;
        case 'a':
        case 'A':
          cycleSegment();
          return true;
        case ',':
          stepFrame(-1);
          return true;
        case '.':
          stepFrame(1);
          return true;
        case '[':
          changeSpeed(-1);
          return true;
        case ']':
          changeSpeed(1);
          return true;
        case 'f':
        case 'F':
          if (!canFullscreen) return false;
          toggleFullscreen();
          return true;
        default:
          return false;
      }
    })();
    if (handled) {
      // Keep the comic reader and set viewer from turning pages on the same keys.
      event.preventDefault();
      event.stopPropagation();
      showControls();
    }
  }

  const progress = duration > 0 ? (current / duration) * 100 : 0;
  const bufferedPercent = duration > 0 ? Math.min(100, (buffered / duration) * 100) : 0;
  const segmentStyle = segment && duration > 0 ? {
    left: `${(segment.start / duration) * 100}%`,
    width: segment.end === null ? '2px' : `${Math.max(0.5, ((segment.end - segment.start) / duration) * 100)}%`,
  } : null;
  const segmentLabel = !segment ? 'Loop a segment: set start (A)'
    : segment.end === null ? `Segment starts at ${formatTime(segment.start)}: set end (A)`
      : `Looping ${formatTime(segment.start)}–${formatTime(segment.end)}: clear (A)`;
  const showBar = controlsVisible || !playing;
  const buttonClass = 'inline-flex size-8 shrink-0 items-center justify-center rounded-full text-white outline-none transition-colors hover:bg-white/15 focus-visible:ring-2 focus-visible:ring-ring pointer-coarse:size-10 [&_svg]:size-4 aria-pressed:bg-primary aria-pressed:text-primary-foreground aria-pressed:hover:bg-primary/85';

  return (
    <div
      ref={frameRef}
      role="group"
      aria-label={`Video player: ${label}`}
      tabIndex={0}
      data-video-player=""
      // A minimum width keeps every control inside the frame for small or portrait videos.
      className={cn('group/player relative z-10 inline-flex max-w-full min-w-[min(100%,26rem)] items-center justify-center outline-none focus-visible:ring-2 focus-visible:ring-ring',
        fullscreen && 'h-dvh w-dvw items-center justify-center bg-black', !showBar && 'cursor-none', className)}
      onKeyDown={onKeyDown}
      onPointerMove={showControls}
      onPointerDown={showControls}
      onFocus={showControls}
    >
      <video
        ref={videoRef}
        src={src}
        poster={poster}
        loop={loop && !activeSegment}
        playsInline
        preload="metadata"
        aria-label={label}
        className={cn('block max-w-full object-contain', fullscreen ? 'max-h-dvh' : videoClassName)}
        onClick={togglePlay}
        onDoubleClick={() => { if (canFullscreen) toggleFullscreen(); }}
        onPlay={() => { setPlaying(true); showControls(); }}
        onPause={() => { setPlaying(false); setControlsVisible(true); }}
        onTimeUpdate={onTimeUpdate}
        onLoadedMetadata={(event) => {
          setDuration(event.currentTarget.duration);
          event.currentTarget.playbackRate = speed;
        }}
        onDurationChange={(event) => setDuration(event.currentTarget.duration)}
        onEnded={onEnded}
      />

      {!playing ? (
        <button type="button" aria-label="Play" onClick={togglePlay} tabIndex={-1}
          className="absolute top-1/2 left-1/2 inline-flex size-14 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-black/55 text-white backdrop-blur transition-transform hover:scale-105 [&_svg]:size-6">
          <Play className="translate-x-px fill-current" />
        </button>
      ) : null}

      <div
        className={cn('absolute inset-x-0 bottom-0 grid gap-1 bg-gradient-to-t from-black/80 via-black/40 to-transparent px-2 pt-8 pb-1.5 transition-opacity duration-200',
          showBar ? 'opacity-100' : 'pointer-events-none opacity-0 group-focus-within/player:pointer-events-auto group-focus-within/player:opacity-100')}
        onClick={(event) => event.stopPropagation()}
        onDoubleClick={(event) => event.stopPropagation()}
        onTouchStart={(event) => event.stopPropagation()}
        onTouchEnd={(event) => event.stopPropagation()}
      >
        <div className="relative h-4">
          <div className="pointer-events-none absolute inset-x-0 top-1/2 h-1 -translate-y-1/2 overflow-hidden rounded-full bg-white/25">
            <div className="absolute inset-y-0 left-0 bg-white/35" style={{ width: `${bufferedPercent}%` }} />
            {segmentStyle ? <div className="bg-primary/70 absolute inset-y-0" style={segmentStyle} /> : null}
            <div className="bg-primary absolute inset-y-0 left-0" style={{ width: `${progress}%` }} />
          </div>
          <input
            type="range"
            aria-label="Seek"
            aria-valuetext={`${formatTime(current)} of ${formatTime(duration)}`}
            min={0}
            max={duration || 0}
            step={0.01}
            value={Math.min(current, duration || 0)}
            onChange={(event) => seekTo(Number(event.target.value))}
            className="absolute inset-0 w-full cursor-pointer appearance-none bg-transparent opacity-0 focus-visible:opacity-100 [&::-webkit-slider-thumb]:size-3 [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:rounded-full [&::-webkit-slider-thumb]:bg-white"
          />
        </div>

        <div className="flex items-center gap-1 text-white">
          <button type="button" className={buttonClass} onClick={togglePlay}
            aria-label={playing ? 'Pause (K)' : 'Play (K)'} title={playing ? 'Pause (K)' : 'Play (K)'}>
            {playing ? <Pause className="fill-current" /> : <Play className="fill-current" />}
          </button>
          <button type="button" className={buttonClass} onClick={toggleMute}
            aria-label={muted ? 'Unmute (M)' : 'Mute (M)'} title={muted ? 'Unmute (M)' : 'Mute (M)'}>
            {muted || volume === 0 ? <VolumeX /> : <Volume2 />}
          </button>
          <input
            type="range"
            aria-label="Volume"
            min={0}
            max={1}
            step={0.05}
            value={muted ? 0 : volume}
            onChange={(event) => changeVolume(Number(event.target.value))}
            className="accent-primary hidden h-1 w-20 cursor-pointer sm:block pointer-coarse:hidden"
          />
          <span className="ml-1 shrink-0 text-xs whitespace-nowrap tabular-nums" aria-live="off">
            {formatTime(current)} / {formatTime(duration)}
          </span>

          <span className="ml-auto" />
          <button type="button" className={cn(buttonClass, 'w-auto px-2 text-xs font-medium tabular-nums')} onClick={cycleSpeed}
            aria-label={`Playback speed ${speed}× ([ / ])`} title="Playback speed ([ / ])">
            {speed}×
          </button>
          <button type="button" className={cn(buttonClass, 'w-auto px-2 text-xs font-semibold')} onClick={cycleSegment}
            aria-pressed={!!segment} aria-label={segmentLabel} title={segmentLabel}>
            {!segment ? 'A–B' : segment.end === null ? 'A–…' : 'A–B ✕'}
          </button>
          <button type="button" className={buttonClass} onClick={toggleLoop}
            aria-pressed={loop} aria-label={loop ? 'Loop on (R)' : 'Loop off (R)'} title={loop ? 'Loop on (R)' : 'Loop off (R)'}>
            <Repeat />
          </button>
          {canFullscreen ? (
            <button type="button" className={buttonClass} onClick={toggleFullscreen}
              aria-label={fullscreen ? 'Exit fullscreen (F)' : 'Fullscreen (F)'} title={fullscreen ? 'Exit fullscreen (F)' : 'Fullscreen (F)'}>
              {fullscreen ? <Minimize2 /> : <Maximize2 />}
            </button>
          ) : null}
        </div>
      </div>
    </div>
  );
}
