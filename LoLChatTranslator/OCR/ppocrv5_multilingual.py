import argparse
import faulthandler
import inspect
import json
import os
import sys
import time
import hashlib
import statistics
from numbers import Number

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

try:
    faulthandler.enable()
except Exception:
    pass

# PaddleOCR/PaddleX performs a model-source connectivity check on startup. The
# desktop helper already selects explicit PP-OCRv5 model names and must avoid
# blocking OCR startup on slow/offline model host probes. Disabling this check
# trades early source validation for predictable startup; model load failures
# are still reported in the ready/error payload.
os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

ENGINE_NAME = "PPOCRv5Multilingual"
DEFAULT_DET_MODEL = "PP-OCRv5_mobile_det"
SERVER_DET_MODEL = "PP-OCRv5_server_det"
DEFAULT_REC_MODEL = "PP-OCRv5_mobile_rec"
SUPPORTED_TASKS = {"full", "detect-only", "recognize-lines"}
VERSION_CACHE = None
BACKEND_CACHE = None
SCRIPT_METADATA_CACHE = None


def now_ms():
    return time.perf_counter() * 1000


def emit(**payload):
    payload.setdefault("success", not bool(payload.get("error")))
    payload.setdefault("engine", ENGINE_NAME)
    payload.setdefault("lines", [])
    payload.setdefault("raw_text", "\n".join(line.get("text", "") for line in payload.get("lines", [])))
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def safe_len(value):
    try:
        return len(value)
    except TypeError:
        return None


def is_number(value):
    return isinstance(value, Number)


def first_present(*values):
    for value in values:
        if value is not None:
            return value
    return None


def normalize_lang(value):
    value = (value or "auto").strip().lower().replace("-", "_")
    supported = {
        "auto",
        "ch",
        "en",
        "latin",
        "korean",
        "japan",
        "traditional_chinese",
        "eslav",
        "cyrillic",
        "th",
        "arabic",
        "devanagari",
        "ta",
        "te",
    }
    return value if value in supported else "auto"


def normalize_mode(value):
    value = (value or "stable").strip().lower().replace("-", "_")
    if value in {"standard", "balanced"}:
        return "stable"
    if value in {"quick"}:
        return "fast"
    if value in {"high", "high_accuracy"}:
        return "accurate"
    if value in {"exp"}:
        return "experimental"
    return value if value in {"stable", "fast", "accurate", "experimental"} else "stable"


def normalize_task(value):
    value = (value or "full").strip().lower().replace("_", "-")
    return value if value in SUPPORTED_TASKS else "full"


def resolve_ppocrv5_models(lang, mode):
    lang = normalize_lang(lang)
    mode = normalize_mode(mode)
    det_model = SERVER_DET_MODEL if mode in {"accurate", "experimental"} else DEFAULT_DET_MODEL
    rec_model_by_lang = {
        "auto": DEFAULT_REC_MODEL,
        "ch": DEFAULT_REC_MODEL,
        "en": "en_PP-OCRv5_mobile_rec",
        "latin": "latin_PP-OCRv5_mobile_rec",
        "korean": "korean_PP-OCRv5_mobile_rec",
        "japan": DEFAULT_REC_MODEL,
        "traditional_chinese": DEFAULT_REC_MODEL,
        "eslav": "eslav_PP-OCRv5_mobile_rec",
        "cyrillic": "cyrillic_PP-OCRv5_mobile_rec",
        "th": "th_PP-OCRv5_mobile_rec",
        "arabic": "arabic_PP-OCRv5_mobile_rec",
        "devanagari": "devanagari_PP-OCRv5_mobile_rec",
        "ta": "ta_PP-OCRv5_mobile_rec",
        "te": "te_PP-OCRv5_mobile_rec",
    }
    return det_model, rec_model_by_lang.get(lang, DEFAULT_REC_MODEL)


def use_space_char_for_lang(lang):
    return normalize_lang(lang) in {"auto", "ch", "en", "latin", "korean", "japan", "traditional_chinese", "eslav", "cyrillic"}


def build_engine_kwargs(det_model, rec_model, mode, lang="auto"):
    mode = normalize_mode(mode)
    lang = normalize_lang(lang)
    kwargs = {
        "text_detection_model_name": det_model,
        "text_recognition_model_name": rec_model,
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": False,
        # PaddleOCR 3.x may default to oneDNN/MKLDNN on CPU. Some PP-OCRv5
        # model/operator combinations fail there in PaddlePaddle 3.3.x, so use
        # the stable Paddle CPU path for the desktop helper.
        "enable_mkldnn": False,
    }
    if mode == "experimental":
        kwargs.update(
            {
                "enable_hpi": True,
                "cpu_threads": max(2, min(8, os.cpu_count() or 4)),
                "enable_mkldnn": True,
            }
        )

    return kwargs


def format_parameters(mode, lang, kwargs, fallback_reason):
    lang = normalize_lang(lang)
    return (
        f"engine={ENGINE_NAME}; mode={mode}; lang={lang}; "
        f"det_model_name={kwargs.get('text_detection_model_name')}; "
        f"rec_model_name={kwargs.get('text_recognition_model_name')}; "
        f"use_doc_orientation_classify=false; use_doc_unwarping=false; "
        f"use_textline_orientation=false; use_space_char={str(use_space_char_for_lang(lang)).lower()}; "
        f"enable_mkldnn={kwargs.get('enable_mkldnn')}; "
        f"enable_hpi={kwargs.get('enable_hpi', '<default>')}; cpu_threads={kwargs.get('cpu_threads', '<default>')}; "
        f"fallback_reason={fallback_reason or '<none>'}"
    )


def compute_file_sha256(path):
    try:
        digest = hashlib.sha256()
        with open(path, "rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except Exception as exc:
        return f"unavailable ({exc})"


def get_script_metadata():
    global SCRIPT_METADATA_CACHE
    if SCRIPT_METADATA_CACHE is not None:
        return SCRIPT_METADATA_CACHE

    script_path = os.path.abspath(__file__)
    try:
        script_last_write_time = time.strftime(
            "%Y-%m-%d %H:%M:%S %z",
            time.localtime(os.path.getmtime(script_path)),
        )
    except Exception as exc:
        script_last_write_time = f"unavailable ({exc})"

    SCRIPT_METADATA_CACHE = {
        "worker_script_path": script_path,
        "worker_script_last_write_time": script_last_write_time,
        "worker_script_sha256": compute_file_sha256(script_path),
    }
    return SCRIPT_METADATA_CACHE


def classify_error_kind(exc, default_kind):
    message = str(exc).lower()
    exc_name = type(exc).__name__
    if isinstance(exc, (ImportError, ModuleNotFoundError)) or "no module named" in message:
        return "dependency_missing" if "paddle" in message or "paddleocr" in message else "import_failed"
    if "缺少 paddle" in message or "无法导入" in message or "could not import" in message:
        return "dependency_missing" if "paddle" in message or "paddleocr" in message else "import_failed"
    if exc_name in {"UnboundLocalError", "NameError", "SyntaxError"}:
        return "worker_script_error"
    return default_kind


def detect_versions():
    global VERSION_CACHE
    if VERSION_CACHE is not None:
        return VERSION_CACHE

    paddleocr_version = "unknown"
    paddlepaddle_version = "unknown"
    try:
        import paddleocr

        paddleocr_version = getattr(paddleocr, "__version__", "unknown")
    except Exception as exc:
        paddleocr_version = f"unavailable ({exc})"

    try:
        import paddle

        paddlepaddle_version = getattr(paddle, "__version__", "unknown")
    except Exception as exc:
        paddlepaddle_version = f"unavailable ({exc})"

    VERSION_CACHE = (paddleocr_version, paddlepaddle_version)
    return VERSION_CACHE


def detect_backend():
    global BACKEND_CACHE
    if BACKEND_CACHE is not None:
        return BACKEND_CACHE

    paddleocr_version, paddlepaddle_version = detect_versions()
    parts = [ENGINE_NAME, f"PaddleOCR {paddleocr_version}", f"PaddlePaddle {paddlepaddle_version}"]
    try:
        import paddle

        if hasattr(paddle, "is_compiled_with_cuda"):
            parts.append(f"cuda_compiled={paddle.is_compiled_with_cuda()}")
        if hasattr(paddle, "device") and hasattr(paddle.device, "get_device"):
            parts.append(f"device={paddle.device.get_device()}")
    except Exception as exc:
        parts.append(f"paddle_backend_probe_failed={exc}")

    BACKEND_CACHE = "; ".join(parts)
    return BACKEND_CACHE


def import_paddleocr():
    try:
        import paddle  # noqa: F401
    except Exception as exc:
        raise RuntimeError(
            "PP-OCRv5 环境缺少 paddlepaddle 运行库或无法导入："
            f"{exc}。请在设置中安装 PP-OCRv5 OCR 环境。"
        )

    try:
        from paddleocr import PaddleOCR
    except Exception as exc:
        raise RuntimeError(
            "PP-OCRv5 环境缺少 paddleocr>=3.0,<4.0 或无法导入："
            f"{exc}。请在设置中安装 PP-OCRv5 OCR 环境。"
        )

    return PaddleOCR


def filter_constructor_kwargs(PaddleOCR, kwargs):
    unsupported = []
    try:
        signature = inspect.signature(PaddleOCR)
    except (TypeError, ValueError):
        return dict(kwargs), unsupported

    parameters = signature.parameters
    if any(parameter.kind == inspect.Parameter.VAR_KEYWORD for parameter in parameters.values()):
        return dict(kwargs), unsupported

    filtered = {}
    for key, value in kwargs.items():
        if key in parameters:
            filtered[key] = value
        else:
            unsupported.append(key)

    return filtered, unsupported


def instantiate_paddleocr(PaddleOCR, kwargs, unsupported):
    remaining = dict(kwargs)
    while True:
        try:
            return PaddleOCR(**remaining), remaining
        except Exception as exc:
            message = str(exc)
            marker = "Unknown argument:"
            if marker not in message:
                raise

            unknown = message.split(marker, 1)[1].strip().split()[0].strip("'\".,")
            if not unknown or unknown not in remaining:
                raise

            unsupported.append(unknown)
            remaining.pop(unknown, None)


def create_engine(PaddleOCR, mode, lang):
    start = now_ms()
    mode = normalize_mode(mode)
    requested_det, requested_rec = resolve_ppocrv5_models(lang, mode)
    det_candidates = [requested_det]
    if requested_det != DEFAULT_DET_MODEL:
        det_candidates.append(DEFAULT_DET_MODEL)

    rec_candidates = [requested_rec]
    if requested_rec != DEFAULT_REC_MODEL:
        rec_candidates.append(DEFAULT_REC_MODEL)

    errors = []
    for det_model in det_candidates:
        for rec_model in rec_candidates:
            raw_kwargs = build_engine_kwargs(det_model, rec_model, mode, lang)
            kwargs, unsupported_parameters = filter_constructor_kwargs(PaddleOCR, raw_kwargs)
            fallback_parts = []
            if det_model != requested_det:
                fallback_parts.append(f"det {requested_det} -> {det_model}")
            if rec_model != requested_rec:
                fallback_parts.append(f"rec {requested_rec} -> {rec_model}")
            fallback_reason = "; ".join(fallback_parts)

            try:
                engine, actual_kwargs = instantiate_paddleocr(PaddleOCR, kwargs, unsupported_parameters)
                model_init_ms = round(now_ms() - start)
                return engine, actual_kwargs, fallback_reason, model_init_ms, sorted(set(unsupported_parameters))
            except Exception as exc:
                errors.append(f"{kwargs}: {exc}")

    raise RuntimeError("; ".join(errors))


def box_to_rect(box):
    if box is None:
        return 0, 0, 0, 0

    box_len = safe_len(box)
    if box_len is None or box_len == 0:
        return 0, 0, 0, 0

    if box_len == 4 and all(is_number(box[index]) for index in range(4)):
        x_min, y_min, x_max, y_max = [float(box[index]) for index in range(4)]
        x = min(x_min, x_max)
        y = min(y_min, y_max)
        return x, y, abs(x_max - x_min), abs(y_max - y_min)

    xs = []
    ys = []
    for point in box:
        point_len = safe_len(point)
        if point_len is None or point_len < 2:
            continue
        xs.append(float(point[0]))
        ys.append(float(point[1]))

    if not xs or not ys:
        return 0, 0, 0, 0

    x = min(xs)
    y = min(ys)
    return x, y, max(xs) - x, max(ys) - y


def is_legacy_ocr_item(value):
    return (
        isinstance(value, (list, tuple))
        and len(value) >= 2
        and isinstance(value[1], (tuple, list))
        and len(value[1]) >= 1
        and isinstance(value[1][0], str)
    )


def iter_v3_items(result):
    if is_legacy_ocr_item(result):
        return

    if isinstance(result, (list, tuple)):
        for item in result:
            yield from iter_v3_items(item)
        return

    if isinstance(result, dict):
        if "res" in result:
            yield from iter_v3_items(result["res"])
            return

        texts = first_present(result.get("rec_texts"), result.get("texts"))
        scores = first_present(result.get("rec_scores"), result.get("scores"))
        boxes = first_present(result.get("dt_polys"), result.get("rec_polys"), result.get("polys"), result.get("rec_boxes"))
        text_count = safe_len(texts) if texts is not None else 0
        score_count = safe_len(scores) if scores is not None else 0
        box_count = safe_len(boxes) if boxes is not None else 0
        if text_count is not None and text_count > 0:
            for index in range(text_count):
                text = texts[index]
                score = scores[index] if scores is not None and score_count is not None and index < score_count else 1.0
                box = boxes[index] if boxes is not None and box_count is not None and index < box_count else None
                yield box, (text, score)
            return

    if hasattr(result, "json"):
        try:
            yield from iter_v3_items(result.json)
            return
        except Exception:
            pass

    if hasattr(result, "to_dict"):
        try:
            yield from iter_v3_items(result.to_dict())
            return
        except Exception:
            pass


def iter_legacy_items(result):
    if result is None:
        return

    if not isinstance(result, (list, tuple)):
        return

    for page in result:
        if page is None or not isinstance(page, (list, tuple)):
            continue

        page_len = safe_len(page)
        if page is None or page_len == 0:
            continue

        page_score_len = safe_len(page[1]) if page_len is not None and page_len >= 2 else 0
        if (
            page_len is not None
            and page_len >= 2
            and isinstance(page[1], (tuple, list))
            and page_score_len is not None
            and page_score_len > 0
            and isinstance(page[1][0], str)
        ):
            yield page
            continue

        for item in page:
            if isinstance(item, (list, tuple)):
                yield item


def has_valid_bbox(line):
    try:
        return float(line.get("width", 0)) > 0 and float(line.get("height", 0)) > 0
    except Exception:
        return False


def deduplicate_lines(lines):
    deduped = []
    seen = set()
    for line in lines:
        key = (
            line.get("text", ""),
            round(float(line.get("x", 0)), 1),
            round(float(line.get("y", 0)), 1),
            round(float(line.get("width", 0)), 1),
            round(float(line.get("height", 0)), 1),
        )
        if key in seen:
            continue

        seen.add(key)
        deduped.append(line)

    return deduped


def sort_lines_by_reading_order(lines):
    if not any(has_valid_bbox(line) for line in lines):
        for index, line in enumerate(lines):
            line["raw_index"] = line.get("raw_index", index)
            line["visual_order"] = index
        return lines, "raw_fallback"

    heights = [float(line.get("height", 0)) for line in lines if has_valid_bbox(line)]
    median_height = statistics.median(heights) if heights else 10
    row_tolerance = max(6, median_height * 0.6)

    indexed = list(enumerate(lines))
    indexed.sort(key=lambda item: (
        float(item[1].get("y", 0)) if has_valid_bbox(item[1]) else float("inf"),
        float(item[1].get("x", 0)) if has_valid_bbox(item[1]) else float("inf"),
        int(item[1].get("raw_index", item[0])),
    ))

    rows = []
    for original_position, line in indexed:
        if not has_valid_bbox(line):
            rows.append({"top": float("inf"), "items": [(original_position, line)]})
            continue

        y = float(line.get("y", 0))
        target_row = None
        for row in rows:
            if row["top"] != float("inf") and abs(y - row["top"]) <= row_tolerance:
                target_row = row
                break

        if target_row is None:
            target_row = {"top": y, "items": []}
            rows.append(target_row)

        target_row["items"].append((original_position, line))
        valid_row_ys = [float(item[1].get("y", 0)) for item in target_row["items"] if has_valid_bbox(item[1])]
        if valid_row_ys:
            target_row["top"] = statistics.median(valid_row_ys)

    ordered = []
    rows.sort(key=lambda row: row["top"])
    for row in rows:
        row["items"].sort(key=lambda item: (
            float(item[1].get("x", 0)) if has_valid_bbox(item[1]) else float("inf"),
            int(item[1].get("raw_index", item[0])),
        ))
        ordered.extend(line for _, line in row["items"])

    for visual_order, line in enumerate(ordered):
        line["visual_order"] = visual_order
        line["raw_index"] = line.get("raw_index", visual_order)

    return ordered, "box_sort"


def parse_result(result):
    v3_lines = []
    line_id = 0
    for box, text_score in iter_v3_items(result):
        text = str(text_score[0]).strip()
        confidence = float(text_score[1])
        x, y, width, height = box_to_rect(box)
        if text:
            v3_lines.append(
                {
                    "id": f"line-{line_id}",
                    "text": text,
                    "score": confidence,
                    "confidence": confidence,
                    "x": x,
                    "y": y,
                    "width": width,
                    "height": height,
                    "raw_index": line_id,
                    "visual_order": line_id,
                    "bbox": {"x": x, "y": y, "width": width, "height": height},
                    "crop_rect": None,
                }
            )
            line_id += 1

    if v3_lines:
        return sort_lines_by_reading_order(deduplicate_lines(v3_lines))

    lines = []
    for item in iter_legacy_items(result):
        item_len = safe_len(item)
        if item is None or item_len is None or item_len < 2:
            continue

        box = item[0]
        text_score = item[1]
        text_score_len = safe_len(text_score)
        if text_score is None or text_score_len is None or text_score_len < 2:
            continue

        text = str(text_score[0]).strip()
        confidence = float(text_score[1])
        x, y, width, height = box_to_rect(box)
        if text:
            lines.append(
                {
                    "id": f"line-{line_id}",
                    "text": text,
                    "score": confidence,
                    "confidence": confidence,
                    "x": x,
                    "y": y,
                    "width": width,
                    "height": height,
                    "raw_index": line_id,
                    "visual_order": line_id,
                    "bbox": {"x": x, "y": y, "width": width, "height": height},
                    "crop_rect": None,
                }
            )
            line_id += 1

    return sort_lines_by_reading_order(deduplicate_lines(lines))


def recognize(ocr, image_path):
    if hasattr(ocr, "predict"):
        return ocr.predict(image_path)

    if hasattr(ocr, "ocr"):
        try:
            return ocr.ocr(image_path, cls=False)
        except TypeError:
            return ocr.ocr(image_path)

    raise RuntimeError("当前 PaddleOCR 3.x 对象没有 predict 或 ocr 方法。")


def build_payload(
    lines,
    timing,
    mode,
    lang,
    kwargs,
    fallback_reason,
    error=None,
    error_kind=None,
    request=None,
    action=None,
    task="full",
    unsupported_parameters=None,
):
    paddleocr_version, paddlepaddle_version = detect_versions()
    elapsed_ms = timing.get("ocr_total_ms") or timing.get("total_ms")
    mode = normalize_mode(mode)
    task = normalize_task(task)
    payload = {
        "request_id": (request or {}).get("request_id"),
        "action": action or (request or {}).get("action"),
        "success": not bool(error),
        "engine": ENGINE_NAME,
        "task": task,
        "model_name": "PP-OCRv5",
        "det_model_name": kwargs.get("text_detection_model_name"),
        "rec_model_name": kwargs.get("text_recognition_model_name"),
        "lang": normalize_lang(lang),
        "use_space_char": use_space_char_for_lang(lang),
        "performance_mode": mode,
        "elapsed_ms": elapsed_ms,
        "init_ms": timing.get("model_init_ms"),
        "ocr_ms": timing.get("ocr_ms") or timing.get("ocr_total_ms"),
        "lines": lines,
        "raw_text": "\n".join(line.get("text", "") for line in lines),
        "reading_order": timing.get("reading_order", "raw_fallback" if lines else "none"),
        "error": error,
        "error_kind": error_kind or ("ocr_failed" if error else None),
        "timing": timing,
        "backend": detect_backend(),
        "mode": mode,
        "parameters": format_parameters(mode, normalize_lang(lang), kwargs, fallback_reason),
        "fallback_reason": fallback_reason or None,
        "unsupported_parameters": unsupported_parameters or [],
        "paddleocr_version": paddleocr_version,
        "paddlepaddle_version": paddlepaddle_version,
    }
    payload.update(get_script_metadata())
    return payload


def call_engine(engine, image_path, task="full"):
    task = normalize_task(task)
    if not image_path or not os.path.exists(image_path):
        raise FileNotFoundError(f"image not found: {image_path}")
    if task == "detect-only":
        raise NotImplementedError("task=detect-only is not supported by the PaddleOCR 3.x high-level API; use full OCR fallback.")
    if task == "recognize-lines":
        raise NotImplementedError("task=recognize-lines is not supported by the PaddleOCR 3.x high-level API in this worker; use local-region full OCR fallback.")

    start = now_ms()
    result = recognize(engine, image_path)
    elapsed = round(now_ms() - start)
    lines, reading_order = parse_result(result)
    return lines, {
        "ocr_detect_ms": None,
        "ocr_recognize_ms": None,
        "ocr_total_ms": elapsed,
        "ocr_ms": elapsed,
        "total_ms": elapsed,
        "reading_order": reading_order,
    }


def one_shot(image_path, mode, lang, task):
    mode = normalize_mode(mode)
    task = normalize_task(task)
    kwargs = build_engine_kwargs(DEFAULT_DET_MODEL, DEFAULT_REC_MODEL, mode, lang)
    try:
        PaddleOCR = import_paddleocr()
        engine, kwargs, fallback_reason, model_init_ms, unsupported_parameters = create_engine(PaddleOCR, mode, lang)
        lines, timing = call_engine(engine, image_path, task)
        timing.setdefault("model_init_ms", model_init_ms)
        emit(**build_payload(lines, timing, mode, lang, kwargs, fallback_reason, task=task, unsupported_parameters=unsupported_parameters))
        return 0
    except NotImplementedError as exc:
        emit(
            **build_payload(
                [],
                {},
                mode,
                lang,
                kwargs,
                None,
                error=str(exc),
                error_kind="unsupported_task",
                task=task,
                unsupported_parameters=[f"task={task}"]))
        return 5
    except FileNotFoundError as exc:
        emit(**build_payload([], {}, mode, lang, kwargs, None, error=str(exc), error_kind="image_not_found", task=task))
        return 6
    except Exception as exc:
        emit(**build_payload([], {}, mode, lang, kwargs, None, error=f"PP-OCRv5 识别失败：{exc}", error_kind=classify_error_kind(exc, "ocr_failed"), task=task))
        return 4


def worker(mode, lang):
    mode = normalize_mode(mode)
    kwargs = build_engine_kwargs(DEFAULT_DET_MODEL, DEFAULT_REC_MODEL, mode, lang)
    unsupported_parameters = []
    try:
        PaddleOCR = import_paddleocr()
        engine, kwargs, fallback_reason, model_init_ms, unsupported_parameters = create_engine(PaddleOCR, mode, lang)
        ready_payload = build_payload(
            [],
            {"model_init_ms": model_init_ms},
            mode,
            lang,
            kwargs,
            fallback_reason,
            unsupported_parameters=unsupported_parameters)
        ready_payload["ready"] = True
        emit(**ready_payload)
    except Exception as exc:
        emit(
            ready=True,
            **build_payload(
                [],
                {},
                mode,
                lang,
                kwargs,
                None,
                error=f"PP-OCRv5 worker 初始化失败：{exc}",
                error_kind=classify_error_kind(exc, "init_failed")))
        return 3

    for raw_line in sys.stdin:
        request = {}
        try:
            raw_line = raw_line.lstrip("\ufeff").strip()
            if not raw_line:
                continue

            request = json.loads(raw_line)
            request_id = request.get("request_id")
            action = request.get("action", "recognize")
            request_lang = normalize_lang(request.get("lang") or lang)
            task = normalize_task(request.get("task") or "full")
            if action == "exit":
                payload = build_payload([], {}, mode, request_lang, kwargs, fallback_reason, request=request, action=action, task=task, unsupported_parameters=unsupported_parameters)
                payload["request_id"] = request_id
                emit(**payload)
                return 0

            image_path = request.get("image_path")
            if not image_path:
                emit(**build_payload([], {}, mode, request_lang, kwargs, fallback_reason, "缺少图片路径。", error_kind="image_not_found", request=request, action=action, task=task, unsupported_parameters=unsupported_parameters))
                continue

            try:
                lines, timing = call_engine(engine, image_path, task)
                emit(**build_payload(lines, timing, mode, request_lang, kwargs, fallback_reason, request=request, action=action, task=task, unsupported_parameters=unsupported_parameters))
            except NotImplementedError as exc:
                emit(
                    **build_payload(
                        [],
                        {},
                        mode,
                        request_lang,
                        kwargs,
                        fallback_reason,
                        error=str(exc),
                        error_kind="unsupported_task",
                        request=request,
                        action=action,
                        task=task,
                        unsupported_parameters=[*unsupported_parameters, f"task={task}"]))
            except FileNotFoundError as exc:
                emit(
                    **build_payload(
                        [],
                        {},
                        mode,
                        request_lang,
                        kwargs,
                        fallback_reason,
                        error=str(exc),
                        error_kind="image_not_found",
                        request=request,
                        action=action,
                        task=task,
                        unsupported_parameters=unsupported_parameters))
        except Exception as exc:
            emit(
                **build_payload(
                    [],
                    {},
                    mode,
                    request.get("lang") or lang,
                    kwargs,
                    fallback_reason,
                    error=f"PP-OCRv5 识别失败：{exc}",
                    error_kind=classify_error_kind(exc, "ocr_failed"),
                    request=request,
                    action=request.get("action", "recognize"),
                    task=request.get("task", "full"),
                    unsupported_parameters=unsupported_parameters))

    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("image_path", nargs="?")
    parser.add_argument("--image")
    parser.add_argument("--worker", action="store_true")
    parser.add_argument("--mode", default="fast", choices=["stable", "fast", "accurate", "experimental", "standard"])
    parser.add_argument("--task", default="full", choices=["full", "detect-only", "recognize-lines"])
    parser.add_argument(
        "--lang",
        default="auto",
        choices=[
            "auto",
            "ch",
            "en",
            "latin",
            "korean",
            "japan",
            "traditional_chinese",
            "eslav",
            "cyrillic",
            "th",
            "arabic",
            "devanagari",
            "ta",
            "te",
        ],
    )
    parser.add_argument("--output-json", action="store_true")
    args = parser.parse_args()

    if args.worker:
        return worker(args.mode, args.lang)

    image_path = args.image or args.image_path
    if not image_path:
        emit(lines=[], error="缺少图片路径。", engine=ENGINE_NAME, mode=normalize_mode(args.mode), lang=normalize_lang(args.lang), task=normalize_task(args.task))
        return 2

    return one_shot(image_path, args.mode, args.lang, args.task)


if __name__ == "__main__":
    raise SystemExit(main())
