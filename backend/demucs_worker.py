import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

MODEL_NAME = "htdemucs_6s"
MODEL_MARKER = ".stemstudio-model-ready.json"
HF_REPOSITORY_CACHE = Path("hub") / "models--adefossez--HTDemucs-6s"
SUPPORTED_STEMS = {"vocals", "guitar", "bass", "drums"}


def report(message):
    print(message, flush=True)


def configure_cache(path):
    cache_path = Path(path).expanduser().resolve()
    cache_path.mkdir(parents=True, exist_ok=True)
    os.environ["TORCH_HOME"] = str(cache_path)
    huggingface_home = cache_path / "huggingface"
    os.environ["HF_HOME"] = str(huggingface_home)
    os.environ["HF_HUB_CACHE"] = str(huggingface_home / "hub")
    return cache_path


def is_cached_file(path):
    try:
        return path.is_file() and path.stat().st_size > 0
    except OSError as error:
        if getattr(error, "winerror", None) != 448:
            raise
        return path.is_symlink()


def has_cached_model(cache_path):
    snapshots_path = cache_path / "huggingface" / HF_REPOSITORY_CACHE / "snapshots"
    if not snapshots_path.is_dir():
        return False

    return any(
        is_cached_file(snapshot / f"{MODEL_NAME}.yaml")
        and any(is_cached_file(weight) for weight in snapshot.glob("*.safetensors"))
        for snapshot in snapshots_path.iterdir()
        if snapshot.is_dir()
    )


def model_is_ready(cache_path):
    return (cache_path / MODEL_MARKER).is_file() and has_cached_model(cache_path)


def prepare_model(cache_path):
    report("正在檢查 Demucs 模型…")
    from demucs.pretrained import get_model

    get_model(MODEL_NAME)
    if not has_cached_model(cache_path):
        raise RuntimeError("模型載入完成，但找不到已下載的模型權重。")

    marker_path = cache_path / MODEL_MARKER
    marker_path.write_text(
        json.dumps({"model": MODEL_NAME}),
        encoding="utf-8",
    )
    report("MODEL_READY|true")


def separate(cache_path, input_path, output_path, stems):
    source = Path(input_path).expanduser().resolve()
    destination = Path(output_path).expanduser().resolve()
    if not source.is_file():
        raise FileNotFoundError(f"找不到輸入音訊：{source}")

    selected_stems = [stem.strip().lower() for stem in stems.split(",") if stem.strip()]
    invalid_stems = set(selected_stems) - SUPPORTED_STEMS
    if not selected_stems or invalid_stems:
        raise ValueError(f"音軌選擇無效：{', '.join(sorted(invalid_stems))}")

    if not model_is_ready(cache_path):
        raise RuntimeError("模型尚未準備完成，請先下載 Demucs 模型。")

    staging_root = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "StemStudio" / "staging"
    staging_root.mkdir(parents=True, exist_ok=True)
    destination.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="demucs-", dir=staging_root) as temporary:
        report(f"正在分離：{source.name}")
        process = subprocess.Popen(
            [
                sys.executable,
                "-m",
                "demucs.separate",
                "--name",
                MODEL_NAME,
                "--out",
                temporary,
                str(source),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        assert process.stdout is not None
        for line in process.stdout:
            report(line.rstrip())
        result = process.wait()
        if result != 0:
            raise RuntimeError(f"Demucs 分軌失敗，結束碼：{result}")

        model_output = Path(temporary) / MODEL_NAME / source.stem
        for stem in selected_stems:
            source_stem = model_output / f"{stem}.wav"
            if not source_stem.is_file():
                raise FileNotFoundError(f"Demucs 沒有產生預期的音軌：{source_stem.name}")
            shutil.copy2(source_stem, destination / source_stem.name)

    report("分軌完成。")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("model-status", "prepare-model", "separate"))
    parser.add_argument("--model-cache", required=True)
    parser.add_argument("--input")
    parser.add_argument("--output")
    parser.add_argument("--stems")
    args = parser.parse_args()
    cache_path = configure_cache(args.model_cache)

    if args.command == "model-status":
        report(f"MODEL_READY|{str(model_is_ready(cache_path)).lower()}")
    elif args.command == "prepare-model":
        prepare_model(cache_path)
    else:
        if not args.input or not args.output or not args.stems:
            raise ValueError("separate 指令需要 --input、--output 和 --stems。")
        separate(cache_path, args.input, args.output, args.stems)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"ERROR|{error}", file=sys.stderr, flush=True)
        raise
