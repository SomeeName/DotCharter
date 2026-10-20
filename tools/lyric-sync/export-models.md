# How the ONNX alignment models were produced

A reproducibility note, not a build script. **Every claim below is marked
VERIFIED (checked against the shipped `lyric-align` model on disk, 2026-09-13)
or INFERRED (the plausible reconstruction, not evidence).** If you regenerate a
model, replace the inferred half with what you actually ran.

The two models are `lyric-align` (MMS-FA, multilingual, required) and
`lyric-align-en` (`wav2vec2-large-960h-lv60-self`, English, optional). Each folder
holds exactly two files: `model.onnx` and `vocab.json`. Those two names are the
whole presence check (`LyricModelInstaller.Required`), so an export that emits
external weights beside them will *look* installed and fail at load.

---

## `lyric-align` (MMS-FA)

**VERIFIED — the shipped artefact**

- `model.onnx` is **340.5 MB, single file**, no `model.onnx.data` sidecar.
- Its ONNX header names producer `onnx.quantize`, version `0.1.0`. That is the
  stamp ONNX Runtime's own quantiser writes
  (`onnxruntime.quantization.quantize_dynamic`), so the file is a **dynamically
  quantised** graph rather than a float export. 340 MB for a 300M-parameter
  model is int8 arithmetic (fp32 would be ~1.2 GB, which is also what the
  in-repo guide records for the rejected fp32 export).
- The graph's first input is named **`input_values`**, and the first node is a
  `Shape`. `LyricAcousticModel.ResolveIoNames` DISCOVERS the input and output
  names rather than hard-coding them, because the published exports disagree
  about whether an `attention_mask` input is present — do not assume this one's
  names transfer.
- Node metadata carries `_empty_nn_module_stack_from_metadata_hook`, which is a
  **`torch.export`** (dynamo) artefact, not the legacy `torch.onnx` tracer.
- `vocab.json` is a flat `{token: id}` map of **exactly 31 entries**:

  ```
  <blank> 0   <pad> 1   </s> 2   <unk> 3
  a 4   i 5   e 6   n 7   o 8   u 9   t 10  s 11  r 12  m 13
  k 14  l 15  d 16  g 17  h 18  y 19  b 20  p 21  w 22  c 23
  v 24  j 25  z 26  f 27  '  28  q 29  x 30
  ```

  Four control tokens, the 26 roman letters, and an apostrophe. **The letters are
  in frequency order, not alphabetical** — `a i e n o u t s r m …` — which is
  torchaudio's `MMS_FA` bundle dictionary verbatim. `<blank>` is id 0, which is
  what `LyricVocabulary.FromTokens` reads as the CTC blank.

**INFERRED — how it was made**

```python
import torch, torchaudio, json
from torchaudio.pipelines import MMS_FA
from onnxruntime.quantization import quantize_dynamic, QuantType

model = MMS_FA.get_model()          # facebook/mms-300m-1130-forced-aligner
model.eval()

json.dump(MMS_FA.get_dict(), open("vocab.json", "w"))   # the 31-symbol map above

dummy = torch.zeros(1, 16000)       # 1 s of 16 kHz mono
torch.onnx.export(                  # dynamo=True -> torch.export, per the metadata
    model, (dummy,), "model.fp32.onnx",
    input_names=["input_values"], output_names=["logits"],
    dynamic_axes={"input_values": {1: "samples"}, "logits": {1: "frames"}},
    dynamo=True,
)

quantize_dynamic("model.fp32.onnx", "model.onnx", weight_type=QuantType.QInt8)
```

The dynamic axis on the sample count is required, not cosmetic: the runner feeds
overlapping windows of ~22 s and the last one is short.

---

## `lyric-align-en` (wav2vec2-large-960h-lv60-self)

**Not verified at all** — the optional English head is not installed on the
machine this note was written on. Everything here is INFERRED:

- Source: HF `facebook/wav2vec2-large-960h-lv60-self`, ~189 MB zipped, so the
  same int8 dynamic quantisation.
- Its `vocab.json` is the HF tokenizer's, which is **upper case** and carries a
  word-delimiter `|`. That is fine and needs no handling here:
  `LyricVocabulary.FromTokens` folds tokens to lower case, which is exactly what
  makes the two alphabets comparable for `CtcEmissionBlender`, and
  `LyricVocabulary.WordDelimiterId` picks the `|` up on its own (MMS-FA has none,
  so the delimiter option is a no-op for the multilingual model).

---

## Things a re-export must not change

- **`<blank>` must stay id 0.** The aligner reads the blank from the vocabulary,
  but every published wav2vec2 CTC head puts it there and nothing downstream
  re-checks it.
- **Ship ONE file.** An fp32 export writes external weights to
  `model.onnx.data`; the presence check does not know about that file, so the
  model would read as installed and then fail to load.
- **Do not "improve" accuracy with a float export.** Measured over the
  hand-synced corpus the two are indistinguishable — 76.1 % of onsets within
  50 ms either way, 90.4 % vs 90.2 % within 100 ms — because alignment reads
  which symbol wins per frame, not the fine structure of the distribution. The
  cost is 340 MB against 1.26 GB.
- **Re-run the probes before publishing a new export.**
  `LocalOnly_LyricAlignmentProbe` (romaji) and
  `LocalOnly_LyricAlignmentPsarcProbe` (English). The per-model onset bias in
  `LyricModelCatalog` is a property of where *that* CTC head fires inside a
  syllable, so a different export can invalidate it.
- **Publishing**: the archives are release assets named `lyric-align-v1.zip` and
  `lyric-align-en-v1.zip`, versioned in the NAME so an app release need not
  re-upload them. Bump the name, the SHA-256 and the byte count in
  `LyricModelCatalog.MultilingualDownload` / `EnglishDownload` together — the
  hash is the whole integrity story, and a mismatch discards the download
  silently apart from one log line.
