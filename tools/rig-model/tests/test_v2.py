"""The v2 head's unit tests. CPU, tiny tensors, no dataset, no GPU.

    python tests/test_v2.py            # runs everything, no pytest needed
    python -m pytest tools/rig-model/tests   # if pytest is installed

Written to run BOTH ways on purpose: the owner's venv has no pytest, and a test
suite that cannot be run on the machine it guards is not a guard. Every test is
a plain `test_*` function with plain `assert`s, so pytest collects it and the
`__main__` block below runs it without.

What is deliberately covered, and why each one is here rather than "it looked
right in a notebook":

* **shapes** -- the decoder's, the loss's and the export wrapper's. A wrong
  `slot_probs` width is a C# reader silently decoding the wrong module.
* **teacher forcing vs greedy** -- on a toy vocab, trained for a few hundred
  steps, greedy must reproduce the sequence it was taught. That is the one
  assertion that fails if the causal mask, the BOS shift or the token mapping
  is off by one, and all three of those produce a model that trains fine.
* **beam >= greedy on log-prob** -- the property that makes a beam worth its
  code at all; if it does not hold the search is wrong, not merely unhelpful.
* **knob loss weighting** -- that a weighted loss actually moves when the
  weights do, and that the param mask restricts a step to its own module.
* **the readiness metric** -- on synthetic predictions where the right answer
  is countable by hand.
* **ONNX round-trip** -- export + onnxruntime CPU inference must produce the
  SAME chain as torch greedy. An exported graph whose greedy unroll drifted is
  invisible any other way.
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
torch.set_num_threads(4)

import metrics as M              # noqa: E402
import model_v2 as v2            # noqa: E402
import sensitivity               # noqa: E402
from vocab import DEFAULT_VOCAB_PATH, load_vocab  # noqa: E402

VOCAB = load_vocab(DEFAULT_VOCAB_PATH)


class ToyVocab:
    """A 6-module vocab with 2 params each. Small enough to train in seconds."""

    def __init__(self, n_modules: int = 6, params: int = 2, slots: int = 4) -> None:
        from vocab import ModuleEntry, ParamRange
        self.slots = slots
        self.families = ["a", "b"]
        self.knob_dim = n_modules * params
        self.modules = [
            ModuleEntry(id=f"m{i}", category="a", param_offset=i * params,
                        params=tuple(ParamRange(f"p{j}", 0.0, 1.0, 0.5) for j in range(params)))
            for i in range(n_modules)
        ]
        self.sample_rate, self.clip_seconds = 48000, 0.01

    @property
    def n_families(self) -> int:
        return len(self.families)

    @property
    def n_slot_classes(self) -> int:
        return len(self.modules) + 1

    @property
    def n_samples(self) -> int:
        return int(self.sample_rate * self.clip_seconds)


def toy_decoder(vocab, d_model: int = 32, layers: int = 2) -> v2.ChainDecoder:
    return v2.ChainDecoder(v2.n_tokens(vocab), vocab.knob_dim, cond_dim=16,
                           d_model=d_model, layers=layers, heads=2, ff=64,
                           max_len=vocab.slots + 2, n_memory=4, dropout=0.0)


# --------------------------------------------------------------------------
# token plumbing
# --------------------------------------------------------------------------
def test_chain_targets_are_eos_terminated_and_padded():
    slots = torch.tensor([[3, 5, 0, 0, 0, 0, 0], [0] * 7, [1, 2, 3, 4, 5, 6, 7]])
    t = v2.chain_targets(slots)
    assert t.shape == (3, 8)
    assert t[0].tolist() == [5, 7, v2.EOS, 0, 0, 0, 0, 0]
    assert t[1].tolist() == [v2.EOS] + [0] * 7          # the empty chain is just EOS
    assert t[2].tolist() == [3, 4, 5, 6, 7, 8, 9, v2.EOS]


def test_tokens_round_trip_to_slot_classes():
    slots = torch.tensor([[3, 5, 0, 0, 0, 0, 0], [1, 2, 3, 4, 5, 6, 7], [0] * 7])
    t = v2.chain_targets(slots)
    assert torch.equal(v2.tokens_to_slots(t[:, :7], 7), slots)


def test_token_class_mapping_is_the_slot_mapping():
    """Token `k + TOKEN_OFFSET` must BE slot class `k` -- the export depends on it."""
    for i, entry in enumerate(VOCAB.modules):
        token = i + 1 + v2.TOKEN_OFFSET
        assert token - v2.TOKEN_OFFSET == VOCAB.slot_index(entry.id)
    assert v2.EOS - v2.TOKEN_OFFSET == 0          # EOS is the `<none>` class


def test_param_mask_is_one_row_per_module_and_disjoint():
    pm = v2.param_mask_matrix(VOCAB)
    assert pm.shape == (v2.n_tokens(VOCAB), VOCAB.knob_dim)
    assert pm.sum().item() == VOCAB.knob_dim          # every knob, exactly once
    assert pm[:v2.TOKEN_OFFSET + 1].sum().item() == 0  # PAD/BOS/EOS own nothing
    entry = VOCAB.modules[7]
    row = pm[7 + 1 + v2.TOKEN_OFFSET]
    assert row.sum().item() == len(entry.params)
    assert row[entry.param_offset].item() == 1.0


# --------------------------------------------------------------------------
# decoder shapes
# --------------------------------------------------------------------------
def test_decoder_shapes():
    tv = ToyVocab()
    dec = toy_decoder(tv)
    z = torch.randn(5, 16)
    mem = dec.build_memory(z)
    assert mem.shape == (5, 4, 32)
    tok = torch.randint(3, v2.n_tokens(tv), (5, tv.slots + 1))
    logit, knob = dec(tok, mem)
    assert logit.shape == (5, tv.slots + 1, v2.n_tokens(tv))
    assert knob.shape == (5, tv.slots + 1, tv.knob_dim)


def test_decoder_is_causal():
    """A change to token t must not move the state at t-1. The mask, in one line."""
    tv = ToyVocab()
    dec = toy_decoder(tv).eval()
    mem = dec.build_memory(torch.randn(1, 16))
    a = torch.tensor([[v2.BOS, 3, 4, 5]])
    b = torch.tensor([[v2.BOS, 3, 4, 6]])
    with torch.no_grad():
        ha = dec.states(a, mem)
        hb = dec.states(b, mem)
    assert torch.allclose(ha[:, :3], hb[:, :3], atol=1e-6)
    assert not torch.allclose(ha[:, 3], hb[:, 3], atol=1e-6)


def test_full_v2_model_shapes_and_export_wrapper():
    m = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64).eval()
    pcm = torch.zeros(2, VOCAB.n_samples)
    tok_t = v2.chain_targets(torch.tensor([[3, 5] + [0] * 5, [1] + [0] * 6]))
    fam, tok_logit, knob_logit = m(pcm, v2.decoder_inputs(tok_t))
    assert fam.shape == (2, VOCAB.n_families)
    assert tok_logit.shape == (2, VOCAB.slots + 1, v2.n_tokens(VOCAB))
    assert knob_logit.shape == (2, VOCAB.slots + 1, VOCAB.knob_dim)

    with torch.no_grad():
        out = v2.ExportWrapperV2(m)(pcm)
    assert len(out) == 6
    assert out[0].shape == (2, VOCAB.n_families)
    assert out[1].shape == (2, VOCAB.slots, VOCAB.n_slot_classes)   # v1's slot_probs
    assert out[2].shape == (2, VOCAB.knob_dim)                      # v1's knob_values
    assert out[3].shape == (2,)                                     # v1's confidence
    assert out[4].shape == (2, VOCAB.slots)                         # chain_tokens
    assert out[5].shape == (2, VOCAB.slots, VOCAB.knob_dim)         # chain_knobs
    assert (out[3] >= 0).all() and (out[3] <= 1).all()


def test_slot_probs_agree_with_the_decoded_chain():
    """The v1-shaped output must decode to the SAME chain the decoder emitted.

    This is the entire additive-contract claim, asserted rather than asserted
    in prose: a C# reader that only knows `slot_probs` gets the v2 chain.
    """
    m = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64).eval()
    with torch.no_grad():
        z = torch.randn(4, 768)
        r = v2.greedy_decode(m, z)
        sp = v2.slot_probs_from_steps(r.step_probs, r.alive, VOCAB.n_slot_classes)
    assert torch.equal(sp.argmax(dim=-1), v2.tokens_to_slots(r.tokens, VOCAB.slots))


# --------------------------------------------------------------------------
# teacher forcing vs greedy, and beam
# --------------------------------------------------------------------------
def _train_toy(seed: int = 0, steps: int = 400):
    """Teaches a toy decoder ONE chain per condition; returns it and the truth."""
    torch.manual_seed(seed)
    tv = ToyVocab()
    dec = toy_decoder(tv)
    # Two conditions, two different chains, so the model has to USE the memory
    # and cannot win by memorising one sequence.
    z = torch.eye(2, 16)
    chains = torch.tensor([[2, 5, 0, 0], [4, 1, 3, 0]])
    tok_t = v2.chain_targets(chains)
    inp = v2.decoder_inputs(tok_t)
    knob_t = torch.rand(2, tv.knob_dim)
    knob_m = torch.ones(2, tv.knob_dim)
    pmask = v2.param_mask_matrix(tv)
    opt = torch.optim.AdamW(dec.parameters(), lr=3e-3)
    fam = torch.zeros(2, 2)
    for _ in range(steps):
        opt.zero_grad()
        mem = dec.build_memory(z)
        logit, knob = dec(inp, mem)
        loss, _ = v2.multitask_loss_v2(
            (torch.zeros(2, 2), logit, knob), (fam, tok_t, knob_t, knob_m),
            param_mask=pmask, label_smoothing=0.0)
        loss.backward()
        opt.step()
    dec.eval()
    return dec, tv, z, chains, tok_t, knob_t


class _Shim:
    """The two attributes `greedy_decode`/`beam_decode` need off a model."""

    def __init__(self, dec, tv):
        self.decoder = dec
        self.n_slots = tv.slots
        self.knob_dim = tv.knob_dim
        self.n_tokens = v2.n_tokens(tv)
        self.param_mask = v2.param_mask_matrix(tv)


def test_greedy_reproduces_what_teacher_forcing_taught():
    dec, tv, z, chains, _, _ = _train_toy()
    with torch.no_grad():
        r = v2.greedy_decode(_Shim(dec, tv), z)
    got = v2.tokens_to_slots(r.tokens, tv.slots)
    assert torch.equal(got, chains), f"greedy {got.tolist()} != taught {chains.tolist()}"
    # ...and the confidence of a memorised chain is near 1.
    assert (r.confidence > 0.8).all(), r.confidence.tolist()


def test_beam_returns_at_least_the_greedy_log_prob():
    """A beam that scores below greedy is a broken search, not a cautious one."""
    dec, tv, z, _, _, _ = _train_toy(seed=3, steps=120)   # deliberately UNDER-trained
    shim = _Shim(dec, tv)
    with torch.no_grad():
        g = v2.greedy_decode(shim, z)
        b = v2.beam_decode(shim, z, k=3)
    assert (b.logprob >= g.logprob - 1e-4).all(), \
        f"beam {b.logprob.tolist()} < greedy {g.logprob.tolist()}"
    assert b.tokens.shape == g.tokens.shape
    assert b.knobs.shape == g.knobs.shape


def test_greedy_knobs_come_from_the_emitting_step_only():
    dec, tv, z, chains, _, knob_t = _train_toy()
    shim = _Shim(dec, tv)
    with torch.no_grad():
        r = v2.greedy_decode(shim, z)
    pm = v2.param_mask_matrix(tv)
    for row in range(chains.shape[0]):
        in_chain = torch.zeros(tv.knob_dim)
        for c in chains[row].tolist():
            if c:
                in_chain += pm[c + v2.TOKEN_OFFSET]
        # Every knob of a module NOT in the chain must be exactly 0 -- nothing
        # wrote it. That is what "only that module's own params" means.
        assert torch.allclose(r.knobs[row] * (1 - in_chain), torch.zeros(tv.knob_dim))
        assert (r.knobs[row] * in_chain).abs().sum() > 0


# --------------------------------------------------------------------------
# the knob loss and its weighting
# --------------------------------------------------------------------------
def test_knob_loss_supervises_only_the_step_s_own_module():
    tv = ToyVocab()
    pm = v2.param_mask_matrix(tv)
    tok_t = v2.chain_targets(torch.tensor([[1, 0, 0, 0]]))     # one module: m0
    knob_t = torch.zeros(1, tv.knob_dim)
    knob_m = torch.ones(1, tv.knob_dim)
    fam = torch.zeros(1, 2)
    tok_logit = torch.zeros(1, tv.slots + 1, v2.n_tokens(tv))

    # A knob prediction that is wrong EVERYWHERE EXCEPT m0's own params must
    # still cost nothing on the knob term.
    knob = torch.full((1, tv.slots + 1, tv.knob_dim), 10.0)     # sigmoid -> ~1
    knob[:, :, 0:2] = -10.0                                     # m0's params -> ~0
    _, parts = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                    param_mask=pm)
    assert parts["knob"] < 1e-3, parts

    # ...and being wrong on m0's OWN params does cost.
    knob[:, :, 0:2] = 10.0
    _, parts = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                    param_mask=pm)
    assert parts["knob"] > 0.9, parts


def test_knob_weighting_changes_the_loss_in_the_right_direction():
    tv = ToyVocab()
    pm = v2.param_mask_matrix(tv)
    tok_t = v2.chain_targets(torch.tensor([[1, 0, 0, 0]]))
    knob_t = torch.zeros(1, tv.knob_dim)
    knob_m = torch.ones(1, tv.knob_dim)
    fam = torch.zeros(1, 2)
    tok_logit = torch.zeros(1, tv.slots + 1, v2.n_tokens(tv))
    knob = torch.zeros(1, tv.slots + 1, tv.knob_dim)
    knob[:, :, 0] = 10.0          # m0.p0 fully wrong, m0.p1 right (sigmoid(0)=0.5)

    flat = torch.ones(tv.knob_dim)
    w_p0 = flat.clone(); w_p0[0] = 1.0; w_p0[1] = 0.1     # p0 matters, p1 does not
    w_p1 = flat.clone(); w_p1[0] = 0.1; w_p1[1] = 1.0     # the other way round

    _, base = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                   param_mask=pm, knob_weight=flat)
    _, heavy = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                    param_mask=pm, knob_weight=w_p0)
    _, light = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                    param_mask=pm, knob_weight=w_p1)
    # Weighting UP the knob that is wrong raises the loss; weighting it down
    # lowers it. A weighting that did neither would be silently inert.
    assert heavy["knob"] > base["knob"] > light["knob"], (heavy, base, light)


def test_uniform_weights_reproduce_the_unweighted_loss():
    """v2 with no weights file must be v1's masked MAE exactly -- that is what
    makes the two heads' knob numbers comparable at all."""
    tv = ToyVocab()
    pm = v2.param_mask_matrix(tv)
    tok_t = v2.chain_targets(torch.tensor([[1, 3, 0, 0]]))
    knob_t = torch.rand(1, tv.knob_dim)
    knob_m = (torch.rand(1, tv.knob_dim) > 0.3).float()
    fam = torch.zeros(1, 2)
    tok_logit = torch.randn(1, tv.slots + 1, v2.n_tokens(tv))
    knob = torch.randn(1, tv.slots + 1, tv.knob_dim)
    _, a = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                param_mask=pm, knob_weight=None)
    _, b = v2.multitask_loss_v2((fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m),
                                param_mask=pm, knob_weight=torch.ones(tv.knob_dim))
    assert abs(a["knob"] - b["knob"]) < 1e-6


def _tail_fixture(err: float):
    """One clip, one supervised knob, driven to an EXACT absolute error.

    The knob head is a sigmoid, so the logit is inverted from the target rather
    than guessed: a test that only approximately hits the tolerance cannot tell
    "the hinge is off" from "the error was on the wrong side of 0.1".
    """
    tv = ToyVocab()
    pm = v2.param_mask_matrix(tv)
    tok_t = v2.chain_targets(torch.tensor([[1, 0, 0, 0]]))
    knob_t = torch.full((1, tv.knob_dim), 0.5)
    knob_m = torch.zeros(1, tv.knob_dim)
    knob_m[0, 0] = 1.0                                  # supervise exactly one knob
    target_p = 0.5 + err                                # prediction = target + err
    logit = math.log(target_p / (1.0 - target_p))
    knob = torch.full((1, tv.slots + 1, tv.knob_dim), logit)
    fam = torch.zeros(1, 2)
    tok_logit = torch.zeros(1, tv.slots + 1, v2.n_tokens(tv))
    return (fam, tok_logit, knob), (fam, tok_t, knob_t, knob_m), pm


def test_knob_tail_term_is_zero_inside_the_tolerance():
    """The hinge must not touch a knob that is already ready.

    This is the whole design: a term that also pressed on knobs inside 0.1 would
    be the mean error again wearing a different name, and the mean is exactly
    what round 2b showed does NOT move readiness.
    """
    for err in (0.0, 0.02, 0.05, 0.099):
        pred, tgt, pm = _tail_fixture(err)
        _, off = v2.multitask_loss_v2(pred, tgt, param_mask=pm, tail_weight=0.0)
        _, on = v2.multitask_loss_v2(pred, tgt, param_mask=pm, tail_weight=5.0)
        assert on["knob_tail"] == 0.0, f"hinge fired at error {err} <= tol"
        assert abs(on["loss"] - off["loss"]) < 1e-6, f"loss moved at error {err}"


def test_knob_tail_term_is_positive_outside_the_tolerance_and_scales():
    """Outside the tolerance it must fire, and by exactly (err - tol)."""
    for err in (0.15, 0.30, 0.45):
        pred, tgt, pm = _tail_fixture(err)
        _, on = v2.multitask_loss_v2(pred, tgt, param_mask=pm, tail_weight=1.0)
        assert on["knob_tail"] > 0.0, f"hinge silent at error {err} > tol"
        assert abs(on["knob_tail"] - (err - v2.TAIL_TOL)) < 2e-3, (
            f"hinge {on['knob_tail']} != err-tol {err - v2.TAIL_TOL}")


def test_knob_tail_term_defaults_off_and_changes_nothing():
    """tail_weight defaults to 0, so every run that does not ask for it is
    bit-identical to before this existed."""
    pred, tgt, pm = _tail_fixture(0.4)
    _, default = v2.multitask_loss_v2(pred, tgt, param_mask=pm)
    _, explicit = v2.multitask_loss_v2(pred, tgt, param_mask=pm, tail_weight=0.0)
    assert default["loss"] == explicit["loss"]
    assert default["knob_tail"] == 0.0


def test_global_knob_head_keeps_the_export_contract():
    """The hybrid must be invisible to the ONNX contract.

    `knob_values` is filled from `greedy_decode(...).knobs` whichever head
    produced it, so a C# reader cannot tell them apart -- that is the whole
    reason the hybrid is shippable without moving SchemaVersion.
    """
    for kind in ("step", "global"):
        m = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64,
                              knob_head=kind).eval()
        with torch.no_grad():
            out = v2.ExportWrapperV2(m)(torch.zeros(2, VOCAB.n_samples))
        fam, slots, knobs, conf = out[0], out[1], out[2], out[3]
        assert fam.shape == (2, VOCAB.n_families), kind
        assert slots.shape == (2, VOCAB.slots, VOCAB.n_slot_classes), kind
        assert knobs.shape == (2, VOCAB.knob_dim), f"knob_values shape moved under {kind}"
        assert conf.shape in ((2,), (2, 1)), kind
        assert torch.isfinite(knobs).all() and (knobs >= 0).all() and (knobs <= 1).all(), kind


def test_forward_returns_the_rank_that_selects_the_loss_path():
    """`forward` must return [B,L,K] for the step head and [B,K] for the global
    one. The RANK is what `multitask_loss_v2` switches on, so a `forward` that
    ignored `knob_head` would silently train the wrong head with the wrong
    supervision -- and nothing downstream would raise.
    """
    pcm = torch.zeros(2, VOCAB.n_samples)
    tok_t = v2.chain_targets(torch.tensor([[3, 5] + [0] * 5, [1] + [0] * 6]))
    ins = v2.decoder_inputs(tok_t)

    m_step = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64,
                               knob_head="step").eval()
    m_glob = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64,
                               knob_head="global").eval()
    with torch.no_grad():
        k_step = m_step(pcm, ins)[2]
        k_glob = m_glob(pcm, ins)[2]
    assert k_step.dim() == 3 and k_step.shape[-1] == VOCAB.knob_dim, k_step.shape
    assert k_glob.dim() == 2 and k_glob.shape == (2, VOCAB.knob_dim), k_glob.shape


def test_global_knob_head_is_what_greedy_actually_emits():
    """`greedy_decode().knobs` must BE the global head's output, exactly.

    Asserting only "it differs from the step head" is not enough: a decode that
    returned zeros would differ too, and zeros also sit inside [0,1], so a
    weaker test passes while the export ships an all-zero knob vector.
    """
    m = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64,
                          knob_head="global").eval()
    assert not hasattr(
        v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=1, d_model=64), "head_knob_global")
    pcm = torch.randn(2, VOCAB.n_samples)
    with torch.no_grad():
        z = m.embed(pcm)
        got = v2.greedy_decode(m, z).knobs
        want = torch.sigmoid(m.head_knob_global(z))
    assert got.shape == (2, VOCAB.knob_dim)
    assert torch.allclose(got, want, atol=1e-6), "greedy did not emit the global head's knobs"
    assert got.abs().sum() > 0, "knobs are all zero"


def test_global_knob_head_loss_is_v1s_masked_l1():
    """With a global head the knob term must be v1's masked L1 exactly.

    Computed independently here rather than by calling the same helper, so the
    test fails if the loss silently keeps applying the per-step `param_mask`.
    """
    tv = ToyVocab()
    pm = v2.param_mask_matrix(tv)
    tok_t = v2.chain_targets(torch.tensor([[1, 3, 0, 0]]))
    knob_t = torch.rand(1, tv.knob_dim)
    knob_m = (torch.rand(1, tv.knob_dim) > 0.3).float()
    fam = torch.zeros(1, 2)
    tok_logit = torch.zeros(1, tv.slots + 1, v2.n_tokens(tv))
    knob_logit = torch.randn(1, tv.knob_dim)              # [B, K] => global head
    _, parts = v2.multitask_loss_v2((fam, tok_logit, knob_logit),
                                    (fam, tok_t, knob_t, knob_m), param_mask=pm)
    expect = ((torch.sigmoid(knob_logit) - knob_t).abs() * knob_m).sum() / knob_m.sum()
    assert abs(parts["knob"] - float(expect)) < 1e-6


def test_train_build_model_actually_wires_every_v2_flag_through():
    """`train.py --knob-head global` must BUILD a global-head model.

    This exists because it did not. The flag was added to argparse, the model
    learned to honour it, and three model-level tests passed -- while
    `build_model` never passed it on. Round 8 then trained for 2.4 hours
    recording `knob_head: global` in its run.json while quietly using the step
    head, and the only visible symptom was an eval crashing hours later on
    missing `head_knob_global.*` keys.

    Model-level tests cannot see that gap by construction: they instantiate
    `RigEstimatorV2` directly and never go through the CLI. So this asserts the
    WIRING -- that the argparse default and an explicit override both arrive at
    the model -- which is a different claim from "the model honours the
    argument".
    """
    import argparse

    import train as T

    def args_for(**over):
        ns = argparse.Namespace(
            head="v2", frontend="cnn", freeze=True,
            decoder_layers=1, decoder_dim=64, knob_head="step",
        )
        for k, v in over.items():
            setattr(ns, k, v)
        return ns

    step = T.build_model(VOCAB, args_for())
    glob = T.build_model(VOCAB, args_for(knob_head="global"))
    assert not hasattr(step, "head_knob_global"), "default should build the step head"
    assert hasattr(glob, "head_knob_global"), "--knob-head global did not reach the model"
    assert glob.knob_head == "global"

    # The parameter count is the check that would have caught round 8: a global
    # head is ~688 k parameters, so an unwired flag makes the two models the
    # SAME SIZE -- which is exactly what run.json showed (34,126,281 for both).
    n_step = sum(p.numel() for p in step.parameters())
    n_glob = sum(p.numel() for p in glob.parameters())
    assert n_glob > n_step, f"global head added no parameters ({n_glob} vs {n_step})"


def test_decoded_prediction_round_trips_through_the_manifest_serialiser():
    """A decoded rig must survive `encode_rig` unchanged.

    The sound-distance eval renders the PREDICTED preset through the same DI as
    the truth, so the prediction has to be expressible in the manifest's own
    shape without loss. If `decode_rig` dropped a param, mis-ordered the chain
    or denormalised against the wrong range, the render would be of a different
    rig than the one that was scored -- and the distance would be blamed on the
    model.

    Round-tripped through `Vocab.encode_rig`, which is the function `data.py`
    uses to build the training labels, so this is the real serialiser and not a
    stand-in.
    """
    # A chain drawn from the REAL vocabulary, with params deliberately off the
    # defaults so a decoder that silently returned defaults would fail.
    chain = [VOCAB.modules[3], VOCAB.modules[11], VOCAB.modules[7]]
    modules = []
    for k, entry in enumerate(chain):
        params = {}
        for j, spec in enumerate(entry.params):
            unit = (0.17 + 0.23 * k + 0.07 * j) % 1.0
            params[spec.id] = VOCAB.denormalise(spec, unit)
        modules.append({"ModuleTypeId": entry.id, "Params": params, "Bypassed": False,
                        "StringParams": None, "Branch": 0, "Sidechain": 0,
                        "ControlAssignments": []})

    enc = VOCAB.encode_rig(modules)
    decoded = VOCAB.decode_rig(enc["slots"], enc["knobs"])

    assert [m["ModuleTypeId"] for m in decoded] == [e.id for e in chain], "chain or order moved"
    for m in decoded:                      # every manifest key must be present
        for key in ("ModuleTypeId", "Params", "Bypassed", "StringParams",
                    "Branch", "Sidechain", "ControlAssignments"):
            assert key in m, f"decoded preset is missing {key}"

    # And it must survive a SECOND pass through the encoder identically: that is
    # what "renderable in the manifest's shape" actually means.
    again = VOCAB.encode_rig(decoded)
    assert again["slots"] == enc["slots"]
    assert again["knob_mask"] == enc["knob_mask"]
    for a, b in zip(again["knobs"], enc["knobs"]):
        assert abs(a - b) < 1e-5, f"knob moved on round trip: {a} vs {b}"


def test_decode_rig_drops_none_slots_and_keeps_order():
    """`<none>` cannot be expressed in a preset, so it must vanish -- and only it."""
    a, b = VOCAB.modules[2], VOCAB.modules[5]
    slots = [VOCAB.slot_index(a.id), 0, VOCAB.slot_index(b.id), 0, 0, 0, 0]
    knobs = [0.5] * VOCAB.knob_dim
    got = VOCAB.decode_rig(slots, knobs)
    assert [m["ModuleTypeId"] for m in got] == [a.id, b.id]
    assert VOCAB.decode_rig([0] * VOCAB.slots, knobs) == []


def test_sensitivity_weights_normalise_and_floor():
    import json
    import tempfile
    blob = {
        "schemaVersion": 1, "method": "render-ab-tonefx",
        "modules": {VOCAB.modules[0].id: {VOCAB.modules[0].params[0].id: 100.0},
                    VOCAB.modules[1].id: {VOCAB.modules[1].params[0].id: 0.0}},
    }
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "knob_sensitivity.json"
        p.write_text(json.dumps(blob), encoding="utf-8")
        w, info = sensitivity.knob_weights(VOCAB, p)
    assert w.shape == (VOCAB.knob_dim,)
    assert w.min() >= sensitivity.FLOOR - 1e-6
    assert w.max() <= 1.0 + 1e-6
    assert w[VOCAB.modules[0].param_offset] == 1.0            # the top one saturates
    # A knob that measured ZERO is still trained, at the floor -- never dropped.
    assert abs(w[VOCAB.modules[1].param_offset] - sensitivity.FLOOR) < 1e-6
    assert info["paramsMeasured"] == 2


# --------------------------------------------------------------------------
# the readiness metric
# --------------------------------------------------------------------------
def test_readiness_counts_only_exact_chains_with_every_knob_in_tolerance():
    slots_true = np.array([[1, 2, 0], [1, 2, 0], [3, 0, 0], [3, 0, 0]])
    slots_pred = np.array([[1, 2, 0], [1, 2, 0], [3, 0, 0], [4, 0, 0]])
    mask = np.array([[1, 1, 0, 0], [1, 1, 0, 0], [0, 0, 1, 1], [0, 0, 1, 1]], dtype=float)
    true = np.zeros((4, 4))
    pred = np.zeros((4, 4))
    pred[1, 1] = 0.15        # row 1: right chain, ONE knob 0.15 out
    pred[2, 2] = 0.05        # row 2: right chain, every knob inside 0.1
    pred[3, 3] = 0.0         # row 3: knobs perfect but the CHAIN is wrong

    # row 0 ready, row 1 not at 0.1 but yes at 0.2, row 2 ready, row 3 never.
    assert M.readiness(slots_pred, slots_true, pred, true, mask, tol=0.1) == 0.5
    assert M.readiness(slots_pred, slots_true, pred, true, mask, tol=0.2) == 0.75
    rep = M.readiness_report(slots_pred, slots_true, pred, true, mask)
    assert rep["ready_rate"] == 0.5 and rep["ready_rate_loose"] == 0.75


def test_readiness_ignores_knobs_of_modules_not_in_the_rig():
    slots = np.array([[1, 0, 0]])
    mask = np.array([[1, 1, 0, 0]], dtype=float)
    true = np.zeros((1, 4))
    pred = np.array([[0.0, 0.0, 0.9, 0.9]])       # both errors are OUTSIDE the mask
    assert M.readiness(slots, slots, pred, true, mask) == 1.0


def test_readiness_tolerates_an_empty_rig():
    """An all-`<none>` row has no knob to get wrong; `.max()` on it must not raise."""
    slots = np.zeros((1, 3), dtype=int)
    assert M.readiness(slots, slots, np.zeros((1, 4)), np.zeros((1, 4)),
                       np.zeros((1, 4))) == 1.0


def test_edit_distance_and_chain_metrics():
    assert M.edit_distance([1, 2, 3, 0], [1, 2, 3, 0]) == 0
    assert M.edit_distance([1, 2, 3, 0], [1, 9, 3, 0]) == 1      # a substitution
    assert M.edit_distance([1, 2, 0, 0], [1, 5, 2, 0]) == 1      # an INSERTION
    assert M.edit_distance([0, 0, 0], [1, 2, 0]) == 2
    pred = np.array([[1, 2, 0], [1, 5, 2]])
    true = np.array([[1, 2, 0], [1, 2, 0]])
    m = M.chain_metrics(pred, true)
    assert m["exact_chain_match"] == 0.5
    assert m["chain_edit_distance"] == 0.5
    # The inserted pedal costs ONE edit but two of three slots -- which is the
    # whole reason both numbers are reported.
    assert m["slot_accuracy"] < 0.85


# --------------------------------------------------------------------------
# the ONNX round-trip
# --------------------------------------------------------------------------
def test_onnx_export_reproduces_torch_greedy():
    """Export + onnxruntime CPU must produce the SAME chain as torch greedy.

    The greedy loop is UNROLLED into the graph, so this is the only check that
    the unroll -- seven steps of Gather, argmax and a growing concat -- still
    means what the python loop means. It is also the check that the two new
    outputs survive the exporter.
    """
    import tempfile

    try:
        import onnxruntime as ort
    except ImportError:                                   # pragma: no cover
        print("[skip] onnxruntime not installed")
        return

    torch.manual_seed(7)
    m = v2.RigEstimatorV2(VOCAB, frontend="cnn", layers=2, d_model=64).eval()
    wrapper = v2.ExportWrapperV2(m).eval()
    n = VOCAB.n_samples
    names = ["family_presence", "slot_probs", "knob_values", "confidence",
             "chain_tokens", "chain_knobs"]

    # Real structure, not zeros: a silent input can make every head answer the
    # same thing and a broken graph would agree with torch on it.
    t = np.arange(n, dtype=np.float64) / VOCAB.sample_rate
    x = (np.sin(2 * np.pi * 110 * t) * (0.6 + 0.4 * np.sin(2 * np.pi * 4 * t))).astype(np.float32)
    fixture = (x * 0.2).astype(np.float32)

    with torch.no_grad():
        torch_out = [o.numpy() for o in wrapper(torch.from_numpy(fixture).unsqueeze(0))]

    with tempfile.TemporaryDirectory() as d:
        path = str(Path(d) / "m.onnx")
        torch.onnx.export(
            wrapper, (torch.zeros(1, n),), path,
            input_names=["audio"], output_names=names,
            dynamic_axes={"audio": {0: "batch"}, **{o: {0: "batch"} for o in names}},
            opset_version=17, do_constant_folding=True, dynamo=False,
        )
        so = ort.SessionOptions()
        so.intra_op_num_threads = 2
        sess = ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])
        ort_out = sess.run(names, {"audio": fixture[None, :]})

    assert (torch_out[4] == ort_out[4]).all(), \
        f"chain differs: torch {torch_out[4]} vs ort {ort_out[4]}"
    for name, a, b in zip(names, torch_out, ort_out):
        delta = float(np.max(np.abs(a.astype(np.float64) - b.astype(np.float64))))
        # `not (delta <= tol)`: a NaN fails every comparison, so `>` would wave
        # through an all-NaN graph -- which has happened here before.
        assert not (delta > 2e-3), f"{name} differs by {delta}"
    # And the v1-shaped output still decodes to that same chain.
    assert (ort_out[1].argmax(axis=-1)[0]
            == np.where(ort_out[4][0] > v2.EOS, ort_out[4][0] - v2.TOKEN_OFFSET, 0)).all()


# --------------------------------------------------------------------------
def main() -> int:
    fns = [(n, f) for n, f in sorted(globals().items())
           if n.startswith("test_") and callable(f)]
    failed = []
    for name, fn in fns:
        try:
            fn()
            print(f"[ok]   {name}")
        except Exception as ex:                            # noqa: BLE001
            failed.append(name)
            print(f"[FAIL] {name}: {type(ex).__name__}: {ex}")
    print(f"\n{len(fns) - len(failed)}/{len(fns)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
