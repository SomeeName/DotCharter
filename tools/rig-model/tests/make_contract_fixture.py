"""Writes the tiny ONNX the C# side uses to prove the v2 contract is ADDITIVE.

    python tests/make_contract_fixture.py

The claim this exists to test is that **a C# reader that knows only v1's four
outputs decodes a v2 model correctly**. Testing that against a REAL v2 export is
not possible in the default gate: nothing is bundled, the real graph is tens of
MB, and its `LogMel` constants alone are 2 MB. So this writes a ~20 KB graph
with the v2 OUTPUT SIGNATURE and constant, hand-chosen answers -- the contract
without the model.

It is a build product, committed because the C# gate must not need python. Rerun
it when the output signature changes; `RigEstimatorContractTests` fails loudly
if the two disagree, which is the whole point.

The answers it publishes are deliberately unambiguous:
  slot 0 -> the FIRST catalogue module, slot 1 -> the SECOND, then `<none>`,
  knob_values 0.25 for every param of those two and 0 elsewhere,
  confidence 0.75, chain_tokens [3, 4, 0, 0, 0, 0, 0] AS INT64 (see below).
"""

from __future__ import annotations

import sys
from pathlib import Path

import torch
import torch.nn as nn

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from model_v2 import TOKEN_OFFSET, param_mask_matrix  # noqa: E402
from vocab import DEFAULT_VOCAB_PATH, load_vocab      # noqa: E402

OUT = (Path(__file__).resolve().parents[3] / "DotCharter.Tests" / "Ai" / "Model"
       / "Assets" / "rig-estimator-v2-contract.onnx")

#: The two modules the fixture's chain names, by catalogue INDEX.
CHAIN = (0, 1)
KNOB_VALUE = 0.25
CONFIDENCE = 0.75


class ContractFixture(nn.Module):
    """v2's six outputs, constant. The input is consumed so the graph is real."""

    def __init__(self, vocab) -> None:
        super().__init__()
        slots, classes = vocab.slots, vocab.n_slot_classes
        pm = param_mask_matrix(vocab)

        fam = torch.zeros(1, vocab.n_families)
        knobs = torch.zeros(1, vocab.knob_dim)
        sp = torch.zeros(1, slots, classes)
        tokens = torch.zeros(1, slots)
        chain_knobs = torch.zeros(1, slots, vocab.knob_dim)

        sp[:, :, 0] = 1.0                        # every slot `<none>` by default
        for step, module_ix in enumerate(CHAIN):
            cls = module_ix + 1                  # slot class of modules[module_ix]
            token = cls + TOKEN_OFFSET
            sp[:, step, 0] = 0.0
            sp[:, step, cls] = 1.0
            tokens[:, step] = float(token)
            knobs[0] += pm[token] * KNOB_VALUE
            chain_knobs[0, step] = pm[token] * KNOB_VALUE
            fam[0, vocab.family_index(vocab.modules[module_ix].category)] = 1.0

        for name, t in (("fam", fam), ("slots", sp), ("knobs", knobs),
                        ("tokens", tokens), ("chain_knobs", chain_knobs)):
            self.register_buffer(name, t)
        self.register_buffer("conf", torch.tensor([CONFIDENCE]))

    def forward(self, audio: torch.Tensor):
        # `zero` keeps the input in the graph (and keeps the batch axis honest)
        # without changing an answer: an exporter that constant-folded the whole
        # thing away would produce a graph with no input at all.
        zero = (audio.sum(dim=1, keepdim=True) * 0.0)
        return (
            self.fam + zero,
            self.slots + zero.unsqueeze(-1),
            self.knobs + zero,
            self.conf + zero.squeeze(-1),
            # INT64 -- and this fixture is deliberately STRICTER than the real
            # exporter, which publishes float32 tokens so that even a reader
            # which maps every output survives. The property worth pinning is
            # the general one: a model may publish an output whose TYPE this
            # reader has no opinion about, and the reader must not touch it.
            # A float here would make the test pass against the old blind
            # `results.ToDictionary(r => r.AsTensor<float>()…)` too, i.e.
            # assert nothing.
            (self.tokens + zero).to(torch.int64),
            self.chain_knobs + zero.unsqueeze(-1),
        )


def main() -> None:
    vocab = load_vocab(DEFAULT_VOCAB_PATH)
    names = ["family_presence", "slot_probs", "knob_values", "confidence",
             "chain_tokens", "chain_knobs"]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    model = ContractFixture(vocab).eval()
    torch.onnx.export(
        model, (torch.zeros(1, vocab.n_samples),), str(OUT),
        input_names=["audio"], output_names=names,
        dynamic_axes={"audio": {0: "batch"}, **{o: {0: "batch"} for o in names}},
        opset_version=17, do_constant_folding=True, dynamo=False,
    )
    print(f"wrote {OUT} ({OUT.stat().st_size / 1e3:.1f} KB)")
    print(f"chain: {[vocab.modules[i].id for i in CHAIN]}  "
          f"tokens {[i + 1 + TOKEN_OFFSET for i in CHAIN]}  knob {KNOB_VALUE}")


if __name__ == "__main__":
    main()
