#!/usr/bin/env python3
"""Hermes command STT provider backed by the mobile client's Fun-ASR protocol.

The API key stays in an owner-readable file. Only the transcript output file is
written; service responses, audio, and credentials never go to stdout/stderr.
"""

import argparse
import asyncio
import json
import re
import sys
import uuid
from pathlib import Path


SAMPLE_RATE = 16000
CHUNK_BYTES = 3200  # 100 ms of mono 16-bit PCM, matching the provider's file example.
DEFAULT_ENDPOINT = "wss://dashscope.aliyuncs.com/api-ws/v1/inference"


class TranscriptionError(Exception):
    pass


def endpoint_for(workspace: str) -> str:
    if not workspace:
        return DEFAULT_ENDPOINT
    if not re.fullmatch(r"[A-Za-z0-9-]{1,100}", workspace):
        raise TranscriptionError("Invalid workspace ID")
    return f"wss://{workspace}.cn-beijing.maas.aliyuncs.com/api-ws/v1/inference"


def update_sentence(sentences: dict[int, tuple[str, bool]], sentence: dict) -> None:
    if sentence.get("heartbeat") is True:
        return
    index = sentence.get("sentence_id")
    if not isinstance(index, int) or index < 1:
        raise TranscriptionError("Invalid recognition result")
    text = sentence.get("text", "")
    if not isinstance(text, str):
        raise TranscriptionError("Invalid recognition result")
    final = sentence.get("sentence_end") is True
    previous = sentences.get(index)
    if previous is None or not (previous[1] and not final):
        sentences[index] = (text, final)


async def transcribe(input_path: Path, key_file: Path, workspace: str) -> str:
    import aiohttp

    if not input_path.is_file() or input_path.stat().st_size == 0:
        raise TranscriptionError("Audio file is missing or empty")
    key = key_file.read_text(encoding="utf-8").strip()
    if not key.startswith("sk-") or len(key) > 512 or any(c.isspace() for c in key):
        raise TranscriptionError("API key file is invalid")

    task_id = str(uuid.uuid4())
    sentences: dict[int, tuple[str, bool]] = {}
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10)
    async with aiohttp.ClientSession(timeout=timeout) as session:
        async with session.ws_connect(
            endpoint_for(workspace),
            headers={"Authorization": "Bearer " + key},
            heartbeat=15,
            max_msg_size=256 * 1024,
            receive_timeout=60,
        ) as socket:
            await socket.send_json({
                "header": {"action": "run-task", "task_id": task_id, "streaming": "duplex"},
                "payload": {
                    "task_group": "audio", "task": "asr", "function": "recognition",
                    "model": "fun-asr-realtime",
                    "parameters": {
                        "format": "pcm", "sample_rate": SAMPLE_RATE,
                        "language_hints": ["zh"], "max_sentence_silence": 800,
                    },
                    "input": {},
                },
            })

            async def receive_event() -> dict:
                message = await socket.receive(timeout=60)
                if message.type != aiohttp.WSMsgType.TEXT:
                    raise TranscriptionError("Recognition connection closed")
                try:
                    event = json.loads(message.data)
                    if event["header"]["task_id"] != task_id:
                        raise TranscriptionError("Recognition task mismatch")
                    return event
                except (ValueError, KeyError, TypeError) as exc:
                    raise TranscriptionError("Invalid recognition response") from exc

            # The server must acknowledge run-task before any binary audio.
            while True:
                event = await receive_event()
                kind = event["header"].get("event")
                if kind == "task-started":
                    break
                if kind == "task-failed":
                    raise TranscriptionError("Recognition task rejected")

            async def receive_results() -> None:
                while True:
                    event = await receive_event()
                    kind = event["header"].get("event")
                    if kind == "task-failed":
                        raise TranscriptionError("Recognition task failed")
                    if kind == "result-generated":
                        sentence = event.get("payload", {}).get("output", {}).get("sentence")
                        if isinstance(sentence, dict):
                            update_sentence(sentences, sentence)
                    if kind == "task-finished":
                        return

            receiver = asyncio.create_task(receive_results())
            converter = await asyncio.create_subprocess_exec(
                "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", str(input_path), "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE),
                "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1",
                stdin=asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
            )
            try:
                sent_bytes = 0
                while True:
                    if receiver.done():
                        await receiver
                        raise TranscriptionError("Recognition ended before audio upload")
                    chunk = await asyncio.wait_for(converter.stdout.read(CHUNK_BYTES), timeout=30)
                    if not chunk:
                        break
                    await asyncio.wait_for(socket.send_bytes(chunk), timeout=5)
                    sent_bytes += len(chunk)
                    await asyncio.sleep(len(chunk) / (SAMPLE_RATE * 2))
                if await asyncio.wait_for(converter.wait(), timeout=10) != 0 or sent_bytes == 0:
                    raise TranscriptionError("Audio conversion failed")
                await socket.send_json({
                    "header": {"action": "finish-task", "task_id": task_id, "streaming": "duplex"},
                    "payload": {"input": {}},
                })
                await asyncio.wait_for(receiver, timeout=25)
            finally:
                if converter.returncode is None:
                    converter.kill()
                    await converter.wait()
                if not receiver.done():
                    receiver.cancel()
                    try:
                        await receiver
                    except asyncio.CancelledError:
                        pass

    transcript = "".join(sentences[index][0] for index in sorted(sentences)).strip()
    if not transcript:
        raise TranscriptionError("Recognition returned no words")
    return transcript


def main() -> int:
    parser = argparse.ArgumentParser(description="Hermes Fun-ASR command provider")
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--key-file", required=True, type=Path)
    parser.add_argument("--workspace", default="")
    args = parser.parse_args()
    try:
        transcript = asyncio.run(asyncio.wait_for(
            transcribe(args.input, args.key_file, args.workspace), timeout=600,
        ))
        args.output.write_text(transcript, encoding="utf-8")
        return 0
    except Exception:
        # Hermes includes command stderr in gateway logs. Never print provider
        # responses, file paths, request headers, or exception details here.
        print("Fun-ASR transcription failed; local STT fallback may be used.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
