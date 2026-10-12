"""Check the Windows worker against the repository's nine reference vectors."""
import argparse
import base64
import json
import math
import os
from pathlib import Path
import queue
import statistics
import struct
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--backend", required=True)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    replies = queue.Queue()
    log_path = args.output.with_suffix(".log")
    started = time.perf_counter()
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            [str(args.run / "digup-embed.exe")], cwd=args.run,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
            text=True, encoding="utf-8", env=os.environ.copy(),
        )
        def read_replies():
            for line in process.stdout:
                replies.put(line)
            replies.put(None)
        threading.Thread(target=read_replies, daemon=True).start()
        def receive():
            line = replies.get(timeout=180)
            if line is None:
                raise RuntimeError(f"worker stopped: {process.poll()}, see {log_path}")
            return json.loads(line)
        def request(op, inputs, **options):
            payload = {"id": 1, "op": op, "inputs": inputs, **options}
            # Match System.Text.Json in WorkerClient: ASCII escapes avoid the
            # Windows console input code page changing non-Latin prompts.
            process.stdin.write(json.dumps(payload, ensure_ascii=True) + "\n")
            process.stdin.flush()
            result = receive()
            if not result.get("ok") or any(result.get("errors", [])):
                raise RuntimeError(str(result))
            if op == "quit":
                return []
            raw = base64.b64decode(result["vectors"])
            numbers = struct.unpack("<" + "f" * (len(raw) // 4), raw)
            if result["dim"] != 768 or len(numbers) != 768 * len(inputs):
                raise RuntimeError("bad vector dimensions")
            return [numbers[i:i + 768] for i in range(0, len(numbers), 768)]
        try:
            ready = receive()
            if not ready.get("ready"):
                raise RuntimeError(str(ready))
            load_seconds = time.perf_counter() - started
            query = lambda text: "task: search result | query: " + text
            document = lambda title, text: f"title: {title} | text: {text}"
            names = ["query", "query-bn", "query-ar", "document", "document-long"]
            texts = [query("payment declined error"), query("ইলিশ মাছ রান্না"),
                query("متى تغلق المكتبة"), document("Tokyo itinerary",
                "Flight to Tokyo Haneda on November 3. Then the train to Kamakura to see the Great Buddha, and back for dinner in Shibuya."),
                document("none", " ".join(
                    f"Note {i}: the review moved to room {i % 7 + 2} because the projector in the main hall failed again, and the team agreed to send the slides the night before."
                    for i in range(1, 61)))]
            made = dict(zip(names, request("text", texts)))
            for name, filename, budget in [("screen-280", "screen.png", 280),
                    ("photo-140", "photo.jpg", 140), ("frame-70", "photo.jpg", 70)]:
                made[name] = request("image", [str(args.reference / filename)], budget=budget)[0]
            made["speech"] = request("audio", [str(args.reference / "speech.wav")])[0]
            expected = json.loads((args.reference / "vectors.json").read_text(encoding="utf-8"))["vectors"]
            checks = []
            for name, values in made.items():
                finite = all(math.isfinite(x) for x in values)
                norm = math.sqrt(sum(x * x for x in values))
                cosine = sum(a * b for a, b in zip(values, expected[name]))
                passed = finite and abs(norm - 1) < 0.001 and cosine >= 0.999
                checks.append({"name": name, "finite": finite, "norm": norm,
                    "cosine_with_reference": cosine, "passed": passed})
                print(f"{args.backend} {name}: cosine={cosine:.8f}, norm={norm:.6f}, passed={passed}", flush=True)
            durations = []
            for i in range(10):
                stamp = time.perf_counter()
                request("text", [query("Можно ли держать кота в съемной квартире?")])
                durations.append((time.perf_counter() - stamp) * 1000)
            report = {"backend": args.backend, "native_build": "official llama.cpp b11461",
                "no_logits_patch": False, "model_load_wall_seconds": load_seconds,
                "worker_info": ready["info"], "checks": checks,
                "warm_text_encode_ms": {"samples": durations, "median": statistics.median(durations)},
                "passed": all(row["passed"] for row in checks)}
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            print(f"load={load_seconds:.3f}s, warm encode median={statistics.median(durations):.2f}ms", flush=True)
            request("quit", [])
            process.wait(timeout=20)
            return 0 if report["passed"] else 1
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=20)


if __name__ == "__main__":
    raise SystemExit(main())
