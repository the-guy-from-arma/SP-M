from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np
from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "launcher" / "Assets"
BROLL = ASSETS / "Broll"
WIDTH = 960
HEIGHT = 540
FPS = 12
FRAMES_PER_SCENE = 36
FADE_FRAMES = 10


def cover_crop(image: Image.Image, width: int, height: int, zoom: float, pan: float) -> Image.Image:
    source_ratio = image.width / image.height
    target_ratio = width / height

    if source_ratio > target_ratio:
        base_height = image.height
        base_width = round(base_height * target_ratio)
    else:
        base_width = image.width
        base_height = round(base_width / target_ratio)

    crop_width = max(width, round(base_width / zoom))
    crop_height = max(height, round(base_height / zoom))
    travel_x = max(0, image.width - crop_width)
    travel_y = max(0, image.height - crop_height)
    x = round(travel_x * min(1.0, max(0.0, pan)))
    y = round(travel_y * (0.35 + 0.12 * math.sin(pan * math.pi)))

    return image.crop((x, y, x + crop_width, y + crop_height)).resize(
        (width, height), Image.Resampling.LANCZOS
    )


def grade(image: Image.Image) -> Image.Image:
    image = ImageEnhance.Color(image).enhance(0.82)
    image = ImageEnhance.Contrast(image).enhance(1.12)
    image = ImageEnhance.Brightness(image).enhance(0.88)

    overlay = Image.new("RGB", image.size, (4, 18, 28))
    image = Image.blend(image, overlay, 0.13)
    return image.filter(ImageFilter.GaussianBlur(radius=0.18))


def generate_broll() -> None:
    BROLL.mkdir(parents=True, exist_ok=True)
    for existing in BROLL.glob("frame-*.jpg"):
        existing.unlink()

    sources = [
        ASSETS / "storm-barrage.png",
        ASSETS / "rain-missile-launch.png",
        ASSETS / "carrier-group.png",
        ASSETS / "sunset-destroyer.png",
    ]
    images = [Image.open(path).convert("RGB") for path in sources]

    frame_index = 0
    for scene_index, image in enumerate(images):
        next_image = images[(scene_index + 1) % len(images)]
        reverse = scene_index % 2 == 1

        for local_frame in range(FRAMES_PER_SCENE):
            t = local_frame / max(1, FRAMES_PER_SCENE - 1)
            pan = 1.0 - t if reverse else t
            current = cover_crop(image, WIDTH, HEIGHT, 1.04 + 0.10 * t, pan)

            if local_frame >= FRAMES_PER_SCENE - FADE_FRAMES:
                fade_t = (local_frame - (FRAMES_PER_SCENE - FADE_FRAMES)) / FADE_FRAMES
                next_pan = 1.0 if reverse else 0.0
                upcoming = cover_crop(next_image, WIDTH, HEIGHT, 1.04, next_pan)
                current = Image.blend(current, upcoming, fade_t)

            current = grade(current)
            current.save(
                BROLL / f"frame-{frame_index:03d}.jpg",
                "JPEG",
                quality=78,
                optimize=True,
                progressive=True,
            )
            frame_index += 1

    (BROLL / "frame-count.txt").write_text(str(frame_index), encoding="utf-8")
    print(f"Generated {frame_index} b-roll frames at {FPS} fps.")


def generate_music() -> None:
    sample_rate = 22050
    duration = 42.0
    count = int(sample_rate * duration)
    t = np.arange(count, dtype=np.float64) / sample_rate
    rng = np.random.default_rng(4077)

    fade_in = np.clip(t / 5.0, 0.0, 1.0)
    fade_out = np.clip((duration - t) / 5.0, 0.0, 1.0)
    envelope = fade_in * fade_out

    drone = (
        0.34 * np.sin(2 * np.pi * 43.65 * t)
        + 0.22 * np.sin(2 * np.pi * 65.41 * t + 0.8)
        + 0.12 * np.sin(2 * np.pi * 87.31 * t + 1.7)
        + 0.08 * np.sin(2 * np.pi * 130.81 * t + 0.2)
    )
    slow_pulse = 0.76 + 0.24 * np.sin(2 * np.pi * 0.055 * t - 0.4)

    noise = rng.normal(0.0, 1.0, count)
    kernel = np.ones(1800, dtype=np.float64) / 1800.0
    sea_air = np.convolve(noise, kernel, mode="same") * 5.5
    distant_rumble = np.sin(2 * np.pi * 27.5 * t + 0.4 * np.sin(2 * np.pi * 0.03 * t))

    signal = (drone * slow_pulse + 0.11 * distant_rumble + 0.10 * sea_air) * envelope
    signal /= max(1.0, np.max(np.abs(signal)) / 0.82)

    left = signal * (0.94 + 0.06 * np.sin(2 * np.pi * 0.021 * t))
    right = signal * (0.94 + 0.06 * np.sin(2 * np.pi * 0.017 * t + 1.3))
    stereo = np.stack([left, right], axis=1)
    pcm = np.clip(stereo * 32767.0, -32768, 32767).astype("<i2")

    output = ASSETS / "launcher-ambient.wav"
    with wave.open(str(output), "wb") as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm.tobytes())
    print(f"Generated original ambient soundtrack: {output.name}")


if __name__ == "__main__":
    generate_broll()
    generate_music()
