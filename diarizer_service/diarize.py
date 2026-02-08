#!/usr/bin/env python3
"""
SpeechBrain-based diarization script.
MVP: ECAPA speaker embeddings + clustering (Agglomerative).
Input must be mono 16kHz PCM WAV (same file as whisper.cpp).
Outputs speakerSegments with timestamps relative to the input window.
"""

import argparse
import json
import numpy as np
import soundfile as sf
import torch

from speechbrain.inference.speaker import EncoderClassifier
from sklearn.cluster import AgglomerativeClustering


def load_audio_mono_16k(path: str):
    audio, sr = sf.read(path)
    if audio.ndim > 1:
        audio = np.mean(audio, axis=1)

    if sr != 16000:
        raise ValueError(f"Expected 16kHz WAV, got sr={sr}. Please resample to 16000Hz.")

    # ensure float32
    audio = audio.astype(np.float32, copy=False)
    return audio, sr


def slice_frames(audio: np.ndarray, sr: int, frame_sec: float, hop_sec: float):
    frame_len = int(frame_sec * sr)
    hop_len = int(hop_sec * sr)

    frames = []
    times = []  # (startSec, endSec)

    if len(audio) < frame_len:
        # not enough audio for one frame
        return frames, times

    for start in range(0, len(audio) - frame_len + 1, hop_len):
        end = start + frame_len
        frames.append(audio[start:end])
        times.append((start / sr, end / sr))

    return frames, times


def frames_to_segments(labels: np.ndarray, times: list):
    # Merge adjacent frames with same label into continuous segments
    segments = []
    cur_label = labels[0]
    seg_start = times[0][0]
    seg_end = times[0][1]

    for i in range(1, len(labels)):
        if labels[i] == cur_label:
            seg_end = times[i][1]
        else:
            segments.append({
                "start": round(seg_start, 3),
                "end": round(seg_end, 3),
                "label": str(cur_label),
            })
            cur_label = labels[i]
            seg_start = times[i][0]
            seg_end = times[i][1]

    segments.append({
        "start": round(seg_start, 3),
        "end": round(seg_end, 3),
        "label": str(cur_label),
    })

    return segments


def main():
    parser = argparse.ArgumentParser(description="Diarize audio using SpeechBrain ECAPA + clustering (MVP)")
    parser.add_argument("--in", dest="input_wav", required=True, help="Input WAV file path (mono 16kHz)")
    parser.add_argument("--out", dest="output_json", required=True, help="Output JSON file path")
    parser.add_argument("--max_speakers", type=int, default=6, help="Maximum number of speakers")
    parser.add_argument("--device", type=str, default="cpu", help="cpu or cuda")
    args = parser.parse_args()

    audio, sr = load_audio_mono_16k(args.input_wav)

    frame_sec = 1.5
    hop_sec = 0.75

    frames, times = slice_frames(audio, sr, frame_sec, hop_sec)

    if len(frames) == 0:
        # too short: return empty
        with open(args.output_json, "w", encoding="utf-8") as f:
            json.dump({"speakerSegments": []}, f, indent=2)
        return

    classifier = EncoderClassifier.from_hparams(
        source="speechbrain/spkrec-ecapa-voxceleb",
        run_opts={"device": args.device},
    )

    embeddings = []
    for frame in frames:
        wav = torch.from_numpy(frame).float().unsqueeze(0)  # (1, T)
        
        with torch.no_grad():
            emb = classifier.encode_batch(wav)  # (1, 1, D) usually
            emb = emb.squeeze()                 # (D,)
            emb = emb.cpu().numpy()
        
        embeddings.append(emb)

    embeddings = np.stack(embeddings, axis=0)  # (N, D)
    
    # Force 2D shape: reshape if needed
    if embeddings.ndim != 2:
        embeddings = embeddings.reshape(embeddings.shape[0], -1)
    
    # Guard: need at least 2 samples for clustering
    if embeddings.shape[0] < 2:
        with open(args.output_json, "w", encoding="utf-8") as f:
            json.dump({"speakerSegments": []}, f, indent=2)
        return

    # K (MVP option A): fixed to max_speakers but capped by N
    n_clusters = min(args.max_speakers, len(embeddings))
    if n_clusters < 1:
        n_clusters = 1

    if n_clusters == 1:
        labels = np.zeros((len(embeddings),), dtype=int)
    else:
        clustering = AgglomerativeClustering(
            n_clusters=n_clusters,
            metric="cosine",
            linkage="average",
        )
        labels = clustering.fit_predict(embeddings)

    speaker_segments = frames_to_segments(labels, times)

    with open(args.output_json, "w", encoding="utf-8") as f:
        json.dump({"speakerSegments": speaker_segments}, f, indent=2)


if __name__ == "__main__":
    main()
