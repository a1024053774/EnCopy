"""Synthesizes the intro's soundtrack: a light mallet groove plus sound effects timed to the video's events.

    python3 soundtrack.py <events.json> <out.wav>

events.json comes from `node render.js video`, so every peg click and key press lands on its frame.
Only numpy; filters are done in the frequency domain.
"""
import json
import sys
import wave

import numpy as np

SR = 44100
rng = np.random.default_rng(7)


def env(n, attack, decay):
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    return a * np.exp(-t / decay)


def band(x, lo, hi):
    f = np.fft.rfftfreq(len(x), 1 / SR)
    X = np.fft.rfft(x)
    X[(f < lo) | (f > hi)] = 0
    return np.fft.irfft(X, len(x))


def noise(sec):
    return rng.standard_normal(int(sec * SR))


def mallet(freq, sec=0.9, vel=1.0):
    n = int(sec * SR)
    t = np.arange(n) / SR
    body = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(2 * np.pi * freq * 4.0 * t) * np.exp(-t / 0.05)
    return vel * body * env(n, 0.003, 0.32)


def bass(freq, sec=0.5):
    n = int(sec * SR)
    t = np.arange(n) / SR
    x = np.tanh(1.6 * np.sin(2 * np.pi * freq * t)) * env(n, 0.006, 0.28)
    return x


def kick():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    f = 45 + 85 * np.exp(-t / 0.04)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * env(n, 0.001, 0.12)


def clap():
    return band(noise(0.18), 900, 5000) * env(int(0.18 * SR), 0.001, 0.05)


def shaker():
    return band(noise(0.08), 6000, 15000) * env(int(0.08 * SR), 0.004, 0.018)


def sfx(kind):
    if kind in ("key", "type"):
        n = int(0.06 * SR)
        x = band(noise(0.06), 1500, 7000) * env(n, 0.0005, 0.008)
        x += 0.6 * np.sin(2 * np.pi * 180 * np.arange(n) / SR) * env(n, 0.001, 0.015)
        return x * (1.0 if kind == "key" else 0.45)
    if kind == "tick":
        n = int(0.04 * SR)
        return band(noise(0.04), 3000, 9000) * env(n, 0.0005, 0.006) * 0.8
    if kind == "peg":
        n = int(0.12 * SR)
        t = np.arange(n) / SR
        x = np.sin(2 * np.pi * 820 * t) * env(n, 0.0005, 0.03) + 0.5 * np.sin(2 * np.pi * 1730 * t) * env(n, 0.0005, 0.015)
        return x + 0.3 * band(noise(0.12), 2000, 6000) * env(n, 0.0005, 0.006)
    if kind == "shutter":
        a = band(noise(0.05), 1200, 8000) * env(int(0.05 * SR), 0.0005, 0.01)
        x = np.zeros(int(0.15 * SR))
        x[: len(a)] += a
        x[int(0.07 * SR): int(0.07 * SR) + len(a)] += 0.8 * a
        return x
    if kind == "whoosh":
        sec = 0.55
        n = int(sec * SR)
        t = np.arange(n) / SR
        shape = np.sin(np.pi * t / sec) ** 2
        return band(noise(sec), 300, 3500) * shape * 0.35
    if kind == "drop":
        n = int(0.4 * SR)
        t = np.arange(n) / SR
        f = 60 + 120 * np.exp(-t / 0.05)
        thud = np.sin(2 * np.pi * np.cumsum(f) / SR) * env(n, 0.001, 0.09)
        w = sfx("whoosh")
        thud[: min(n, len(w))] += 0.4 * w[:n]
        return thud * 0.8
    if kind == "paste":
        a = mallet(1046.5, 0.5, 0.6)
        b = mallet(1568.0, 0.5, 0.6)
        x = np.zeros(len(a) + int(0.07 * SR))
        x[: len(a)] += a
        x[int(0.07 * SR):] += b
        return x
    raise ValueError(kind)


def add(buf, x, at, gain):
    i = int(at * SR)
    if i >= len(buf):
        return
    j = min(len(buf), i + len(x))
    buf[i:j] += gain * x[: j - i]


def note(name):
    names = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "Bb": 10, "B": 11}
    pitch, octave = name[:-1], int(name[-1])
    return 440.0 * 2 ** ((names[pitch] + 12 * (octave + 1) - 69) / 12)


def main(events_path, out):
    data = json.load(open(events_path))
    dur = data["duration"]
    music = np.zeros(int((dur + 1) * SR))
    fx = np.zeros_like(music)

    bpm = 108
    beat = 60 / bpm
    # The groove's start, end and quiet stretch (under the themes) come from intro.html with the events.
    g = data["groove"]
    start, end, quiet = g["start"], g["end"], g["light"]
    chords = [  # F, Dm, Bb, C (I vi IV V)
        (["F4", "A4", "C5", "F5"], "F2"),
        (["D4", "F4", "A4", "D5"], "D2"),
        (["Bb3", "D4", "F4", "Bb4"], "Bb1"),
        (["C4", "E4", "G4", "C5"], "C2"),
    ]
    arp = [0, 2, 1, 3, 2, 1, 3, 2]

    # intro: a soft pad under the clipboard scene
    n = int(start * SR)
    t = np.arange(n) / SR
    pad = sum(np.sin(2 * np.pi * note(p) * t) for p in ["D3", "A3", "F4"]) / 3
    music[:n] += 0.18 * pad * np.clip(t / 1.2, 0, 1) * np.clip((start - t) / 0.4, 0, 1)

    i = 0
    tb = start
    while tb < end:
        bar = (i // 8) % 4
        tones, root = chords[bar]
        step = i % 8
        # themes section gets a lighter feel
        light = quiet[0] <= tb < quiet[1]
        add(music, mallet(note(tones[arp[step]]), vel=0.55 if step % 2 == 0 else 0.4), tb, 0.5)
        if step % 2 == 0:
            add(music, shaker(), tb + beat / 4, 0.25)
        add(music, shaker(), tb, 0.18)
        if step in (0, 4):
            add(music, kick(), tb, 0.0 if light else 0.75)
            add(music, bass(note(root), beat * 1.6), tb, 0.45)
        if step in (2, 6) and not light:
            add(music, clap(), tb, 0.22)
        tb += beat / 2
        i += 1
    # ending chord
    for p in ["F3", "A3", "C4", "F4", "A4"]:
        add(music, mallet(note(p), 3.5, 0.8), end, 0.35)
    add(music, bass(note("F2"), 2.5), end, 0.5)

    for e in data["events"]:
        gain = {"key": 0.5, "type": 0.35, "tick": 0.35, "peg": 0.45, "shutter": 0.6, "whoosh": 0.9, "drop": 0.7, "paste": 0.4}[e["type"]]
        add(fx, sfx(e["type"]), e["t"], gain)

    mix = 0.75 * music + fx
    mix = mix[: int(dur * SR)]
    fade = int(1.2 * SR)
    mix[-fade:] *= np.linspace(1, 0, fade)
    mix = np.tanh(1.2 * mix / (np.max(np.abs(mix)) + 1e-9)) * 0.85
    pcm = (mix * 32767).astype(np.int16)
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
