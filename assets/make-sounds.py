"""Original AudioSwitcher chimes: a soft struck-glass note and a warm muted error.

Reproducible offline modal synthesis, using only Python's standard library.
No MIDI, borrowed notification recordings, or third-party samples.
"""
import math
from pathlib import Path
import random
import wave
import array

ROOT = Path(__file__).resolve().parent
RATE = 48000

def strike(frequency, duration, modes, seed):
    random.seed(seed)
    frames = round(RATE * duration)
    result = []
    soft_noise = 0.0
    for i in range(frames):
        t = i / RATE
        # A rounded physical strike, not a square-edged electronic beep.
        attack = (1.0 - math.exp(-t / 0.0018)) ** 2
        value = sum(level * math.sin(math.tau * frequency * ratio * t + phase)
                    * math.exp(-t / decay)
                    for ratio, level, decay, phase in modes)
        soft_noise = 0.85 * soft_noise + 0.15 * random.uniform(-1, 1)
        value += 0.045 * soft_noise * math.exp(-t / 0.006)
        # Let the body ring; taper the final 35 ms smoothly to zero.
        release = min(1.0, max(0.0, (duration - t) / 0.035))
        release = release * release * (3 - 2 * release)
        result.append(value * attack * release)
    return result

# Slightly inharmonic upper partials give the note a soft glass/wood texture.
confirm = strike(830.61, 0.39, [
    (1.0, 1.0, 0.090, 0.0),
    (0.5, 0.28, 0.072, 0.3),
    (2.006, 0.13, 0.048, 0.1),
    (2.76, 0.045, 0.030, 0.5),
    (4.08, 0.017, 0.017, 0.2),
], 13)
error = strike(311.13, 0.36, [
    (1.0, 1.0, 0.070, 0.0),
    (1.1892, 0.36, 0.067, 0.0),
    (2.003, 0.08, 0.027, 0.2),
    (3.07, 0.025, 0.018, 0.4),
], 37)

for name, samples, target_peak in [('confirm.wav', confirm, 0.23), ('error.wav', error, 0.20)]:
    gain = target_peak * 32767 / max(abs(x) for x in samples)
    pcm = array.array('h', (round(x * gain) for x in samples))
    with wave.open(str(ROOT / name), 'wb') as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(RATE)
        output.writeframes(pcm.tobytes())
    print(f'{name}: {len(samples)/RATE:.2f}s, original struck-note sound')
