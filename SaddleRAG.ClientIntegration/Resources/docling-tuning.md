# Making Docling faster

Docling is user-managed. SaddleRAG reads its status, sends it documents, and never
installs, licenses, configures, starts, stops, or upgrades it. Everything on this page is
something **you** change on your own Docling install — SaddleRAG will simply go faster
once you have.

Measured on a real machine, an unaccelerated Docling took **33 minutes to OCR one
117-page scanned manual**. Most of that is recoverable.

---

## First: find out what is actually slow

The three pipeline stages, most expensive first:

1. **OCR** — reading text out of page images. Only runs on pages that need it.
2. **Table structure recognition** — working out cells, rows, and spans.
3. **PDF parsing** — turning the file into pages and text runs.

Which one dominates depends entirely on your documents:

| Your PDFs | Dominant cost | Where to start |
|---|---|---|
| **Scanned** (photocopies, faxes, image-only) | OCR, by a wide margin | GPU, then OCR engine choice |
| **Digital** (exported from Word, InDesign, CAD) | Parsing, then tables | PDF backend, then table mode |
| **Mixed** | Varies per file | GPU first — it helps every stage |

You can tell them apart by opening a PDF and trying to select a sentence. If the text
highlights, it is digital. If you only get a selection box over the whole page, it is scanned.

Docling can report its own stage timings. Set this on the Docling side and read its log:

```
DOCLING_DEBUG_PROFILE_PIPELINE_TIMINGS=true
```

---

## The big one: use your GPU

Docling auto-detects an NVIDIA GPU **only if PyTorch was installed with CUDA support**.
Installing Docling normally pulls the CPU-only PyTorch build, so a machine with an
expensive GPU can sit at 0% while the CPU grinds for half an hour. This is the single most
common reason Docling is slow, and it is silent — nothing warns you.

### Check what you have

```powershell
& "$env:USERPROFILE\Applications\Docling\venv\Scripts\python.exe" -c "import torch; print(torch.__version__); print('CUDA available:', torch.cuda.is_available())"
```

A result ending in `+cpu`, or `CUDA available: False`, means your GPU is idle. A CUDA build
prints something like `2.13.0+cu128` and `CUDA available: True`.

### Fix it

**Stop Docling first.** Replacing PyTorch underneath a running conversion will fail the
conversion and can leave a half-installed environment. Wait for any SaddleRAG document scan
to finish — the Directories page shows what is running.

**Pick the index before you uninstall anything.** Each CUDA index carries a different set of
PyTorch versions, and the newest PyTorch is often absent from the older index. Note the
version you have now, then ask each index what it actually offers:

```powershell
& "$env:USERPROFILE\Applications\Docling\venv\Scripts\python.exe" -m pip index versions torch --index-url https://download.pytorch.org/whl/cu130
```

Swap `cu130` for `cu128` and compare. Choose the index that carries **the version you already
have**, so you replace a CPU build with the CUDA build of the same release rather than
silently downgrading PyTorch underneath `docling-ibm-models` and `transformers`.

A real example: a machine on `torch 2.13.0+cpu` found that `cu128` stopped at `2.11.0`, while
`cu130` carried `2.13.0+cu130` exactly. `cu128` would have meant a two-release downgrade for
no reason. `nvidia-smi` tells you the highest CUDA version your driver supports; anything at
or below that works.

Then, with Docling stopped:

```powershell
& "$env:USERPROFILE\Applications\Docling\venv\Scripts\python.exe" -m pip uninstall -y torch torchvision
```

```powershell
& "$env:USERPROFILE\Applications\Docling\venv\Scripts\python.exe" -m pip install torch==2.13.0 torchvision==0.28.0 --index-url https://download.pytorch.org/whl/cu130
```

Substitute your own versions and index. The `--index-url` is the whole point — without it pip
serves the CPU-only build again. Pinning the versions means that if the index does not have
them, pip stops and changes nothing.

The download is large — roughly 2 GB for the CUDA build — so expect it to take a while.

Then verify, and expect a large speedup on layout detection immediately.

### If pip leaves a `~orch` folder behind

A message like `Failed to remove contents in a temporary directory ...\~orch` means a process
still had PyTorch's DLLs loaded while pip was working. **Docling Serve is not the only
candidate** — the Docling MCP server runs out of the same virtual environment and holds the
same DLLs. Stop that too, then delete the leftover `~orch` directory by hand. It is pip's own
orphaned scratch folder and nothing reads it.

### The catch nobody mentions: OCR may not use the GPU at all

Docling's own documentation is explicit that OCR engines are third-party, so **GPU support
depends on the engine**. The confirmed GPU-capable path is **RapidOCR with its torch
backend**. Tesseract is a CPU-only command-line binary — selecting it means your OCR stage
stays on the CPU no matter how good your graphics card is.

So on scanned documents there is a genuine trade-off:

| OCR engine | Uses the GPU | Notes |
|---|---|---|
| RapidOCR (torch backend) | Yes | The fast path. Verify it actually reads *your* documents before committing. |
| RapidOCR (default backend) | No | Has been observed returning empty results on some scanned manuals. |
| Tesseract | No | Reliable on difficult scans; stays on the CPU. Needs `TESSDATA_PREFIX` set. |

If you have scanned documents, test both on one representative file and compare the text
you get back, not just the clock. Fast and empty is not a win.

---

## Server-side settings

These are environment variables on the Docling process. If you launch Docling from the
SaddleRAG tray, set them in the `start-docling.ps1` that the tray runs, so every launch
gets them.

| Variable | Default | What it does |
|---|---|---|
| `DOCLING_DEVICE` | auto | `cuda`, `cuda:0`, `cpu`, `mps`. Set it explicitly to `cuda` so a broken CUDA install fails loudly instead of silently falling back to the CPU. |
| `DOCLING_NUM_THREADS` | 4 | Threads for CPU execution. Set to your physical core count. Matters a lot while you are still on the CPU. |
| `DOCLING_PERF_PAGE_BATCH_SIZE` | 4 | Pages processed together. Higher values let layout detection run in GPU batch mode. |
| `DOCLING_SERVE_OCR_BATCH_SIZE` | — | Batch size for the OCR stage. |
| `DOCLING_SERVE_LAYOUT_BATCH_SIZE` | — | Batch size for layout detection. |
| `DOCLING_SERVE_TABLE_BATCH_SIZE` | — | Batch size for table structure. |
| `DOCLING_CUDA_USE_FLASH_ATTENTION2` | false | Ampere or newer. Requires the `flash-attn` package. |

Batch sizes are a VRAM trade. Rough starting points:

| VRAM | OCR / layout batch |
|---|---|
| 32 GB | 64–128 |
| 24 GB | 32–64 |
| 12 GB | 16–32 |

Raise them until you see an out-of-memory error, then come back down one step. Bigger is
not automatically better — a batch that does not fit spends its time swapping.

Precedence is **environment variable > config file > defaults**. Note that when Docling is
run under uvicorn with `--reload` or multiple workers, command-line flags are ignored and
only the environment variables apply.

---

## Per-document settings

These are conversion options sent with each request. SaddleRAG currently sends a fixed set,
so changing them means changing SaddleRAG configuration, not Docling's.

What SaddleRAG sends today:

| Option | SaddleRAG sends | Docling default | Comment |
|---|---|---|---|
| `do_ocr` | `true` | `true` | Fixed. Turning it off is the largest possible saving **on digital-only PDFs** — and catastrophic on scanned ones. |
| `table_mode` | `accurate` | `accurate` | Fixed. `fast` is materially cheaper; `do_table_structure=false` skips the stage entirely if you never query table contents. |
| `pdf_backend` | *not sent* | `docling_parse` | `pypdfium2` is reported as substantially faster at loading large PDFs. `threaded_docling_parse` also exists. |
| `pipeline` | `standard` | `standard` | The classic layout/OCR path. Correct here — the `vlm` path is much heavier. |
| `image_export_mode` | `placeholder` | `placeholder` | Already the cheap setting; no images are generated. |
| `do_picture_description` | `false` | `false` | Already off. |
| `do_picture_classification` | `false` | `false` | Already off. |
| `do_code_enrichment` | `false` | `false` | Already off. |
| `do_formula_enrichment` | `false` | `false` | Already off. |

**SaddleRAG already has the enrichment stages off.** The common advice to "disable the
enrichment stages" is advice you have already taken — there is nothing to win there.

The remaining per-document wins are `do_ocr` on digital-only collections, `table_mode`,
and `pdf_backend`. Making those configurable is tracked work rather than something you can
change today.

### A note on `ocr_engine`

`ocr_engine` is **deprecated** in docling-serve 1.29 in favour of `ocr_preset`. It still
works. SaddleRAG's `DocumentIngestion:Docling:OcrEngine` setting writes that field, so it
will need to move to presets before the deprecated field is removed.

---

## What order to do this in

1. **Install CUDA PyTorch.** Biggest win, helps every stage, costs one command.
2. **Set `DOCLING_DEVICE=cuda` explicitly** so a broken install is loud rather than silent.
3. **Set `DOCLING_NUM_THREADS`** to your physical core count.
4. **Pick your OCR engine on evidence** — run one representative scanned document through
   each and compare the extracted text.
5. **Raise batch sizes** to suit your VRAM.
6. Only then worry about PDF backends and table modes.

---

## Official documentation

- [GPU support](https://docling-project.github.io/docling/usage/gpu/)
- [RTX GPU acceleration](https://docling-project.github.io/docling/getting_started/rtx/)
- [docling-serve configuration](https://github.com/docling-project/docling-serve/blob/main/docs/configuration.md)
- [docling-serve deployment](https://docling-project.github.io/docling/usage/api_server/deployment/)
- [Latest docling-serve release](https://github.com/docling-project/docling-serve/releases/latest)
- [Tesseract installation](https://tesseract-ocr.github.io/tessdoc/Installation.html)
