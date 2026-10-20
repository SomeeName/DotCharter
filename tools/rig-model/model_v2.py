"""The v2 head: an autoregressive CHAIN DECODER over the same front-end.

v1 predicts each of the 7 chain slots INDEPENDENTLY from one pooled embedding.
That is what caps it: slot 3's softmax cannot see that slot 2 already took the
overdrive, so the model spreads its probability over every plausible ordering
and the argmax chain is frequently one no sampler would ever draw. Measured on
tonight's run: family F1 0.84 while occupied-slot accuracy is 0.42.

So v2 emits the chain as a SEQUENCE -- `<bos> m1 m2 ... <eos>` -- from a small
transformer decoder cross-attending the audio embedding. Slot 3 is conditioned
on slots 1 and 2 by construction, which is the whole of the fix.

Three things here are deliberate and worth reading before changing them:

1. **The token space is the slot space, shifted by two.** Token `k+2` is slot
   class `k`, so `EOS`(2) IS the `<none>` class and a module token maps to its
   slot class by subtraction. That is what lets the v2 export fill v1's
   `slot_probs` output with the decoder's own distribution instead of a second,
   drifting mapping. `PAD` is 0 and never appears in an emitted sequence.

2. **The knob regression is conditioned on the step that EMITTED the module,**
   and only that module's own params carry a gradient. The head still writes
   the full `knob_dim` vector (that keeps the ONNX a fixed shape) but
   `PARAM_MASK[token]` zeroes everything that does not belong to the token, on
   both the loss side and the export side, from ONE table.

3. **Confidence is the sequence log-prob, not a learned head.** v1 trained a
   sigmoid against the batch's realised slot accuracy, which is a second model
   of the first model's error; here `exp(mean log p)` over the emitted tokens
   is already calibrated by construction and costs no parameters. It is what
   the beam search ranks by, so the number the matcher reads and the number the
   decoder optimised are the same number.

Greedy decoding is UNROLLED to a fixed `slots` steps so it exports to opset 17
as plain ops -- no `Loop`, no dynamic control flow. Beam search (k=3) is a
python-side inference nicety and is deliberately NOT in the graph: the C# side
is a prior for a search that will refine anyway, and a beam in ONNX is a large
amount of graph for a fraction of a module.
"""

from __future__ import annotations

import math
from typing import NamedTuple

import torch
import torch.nn as nn
import torch.nn.functional as F

from model import MEL_CONFIGS, HfTower, LogMel, MelCnn, ModulationBranch

PAD, BOS, EOS = 0, 1, 2
#: Token `k + TOKEN_OFFSET` is slot class `k`; EOS is slot class 0 (`<none>`).
TOKEN_OFFSET = 2


def n_tokens(vocab) -> int:
    """PAD + BOS + EOS + one per module. (EOS doubles as the `<none>` class.)"""
    return len(vocab.modules) + TOKEN_OFFSET + 1


def param_mask_matrix(vocab) -> torch.Tensor:
    """`[n_tokens, knob_dim]`: which knobs belong to the module a token names.

    Row PAD/BOS/EOS is all-zero, so a step that emitted no module contributes
    no knob gradient and writes no knob value. ONE table, used by the loss, the
    greedy decode and the export.
    """
    m = torch.zeros(n_tokens(vocab), vocab.knob_dim)
    for i, entry in enumerate(vocab.modules):
        lo = entry.param_offset
        m[i + 1 + TOKEN_OFFSET, lo: lo + len(entry.params)] = 1.0
    return m


def chain_targets(slots: torch.Tensor, max_len: int | None = None) -> torch.Tensor:
    """Slot labels `[B, slots]` -> target tokens `[B, slots+1]`, EOS-terminated.

    The slot label array is prefix-packed (the encoder writes modules 0..n-1
    and leaves the rest 0), so the chain is the run of non-zero classes and the
    first zero is where EOS goes. Anything after EOS is PAD, which the CE
    ignores.
    """
    b, s = slots.shape
    length = (s + 1) if max_len is None else max_len
    out = torch.full((b, length), PAD, dtype=torch.long, device=slots.device)
    occupied = slots != 0
    # A run-length that stops at the FIRST zero, not a count of non-zeros: a
    # label with a hole in it must not silently compact into a shorter chain.
    alive = torch.cumprod(occupied.long(), dim=1)
    n = alive.sum(dim=1)
    tok = torch.where(occupied, slots.long() + TOKEN_OFFSET, torch.zeros_like(slots.long()))
    keep = min(s, length - 1)
    out[:, :keep] = torch.where(alive[:, :keep].bool(), tok[:, :keep],
                                torch.full_like(tok[:, :keep], PAD))
    out.scatter_(1, n.clamp(max=length - 1).unsqueeze(1), EOS)
    return out


def decoder_inputs(targets: torch.Tensor) -> torch.Tensor:
    """Teacher-forcing input: `<bos>` then the target shifted right."""
    bos = torch.full((targets.shape[0], 1), BOS, dtype=torch.long, device=targets.device)
    return torch.cat([bos, targets[:, :-1]], dim=1)


def tokens_to_slots(tokens: torch.Tensor, slots: int) -> torch.Tensor:
    """Emitted tokens `[B, L]` -> v1-shaped slot classes `[B, slots]`.

    Everything at and after the first EOS/PAD is class 0, so v1 and v2 chains
    compare on exactly the same array and `slot_accuracy_occupied` means the
    same thing for both.
    """
    b = tokens.shape[0]
    out = torch.zeros(b, slots, dtype=torch.long, device=tokens.device)
    is_module = tokens > EOS
    alive = torch.cumprod(is_module.long(), dim=1).bool()
    keep = min(slots, tokens.shape[1])
    out[:, :keep] = torch.where(alive[:, :keep], tokens[:, :keep] - TOKEN_OFFSET,
                                torch.zeros_like(tokens[:, :keep]))
    return out


class ChainDecoder(nn.Module):
    """A small causal transformer decoder over chain tokens.

    The audio conditioning is `n_memory` learned projections of the trunk
    embedding, cross-attended at every layer. A pooled embedding is ONE vector,
    so a single memory token would make cross-attention a glorified bias; the
    projections give the decoder somewhere to put "which drive" separately from
    "how wet", and they cost one Linear.
    """

    def __init__(self, n_tok: int, knob_dim: int, cond_dim: int, d_model: int = 256,
                 layers: int = 3, heads: int = 4, ff: int = 1024, max_len: int = 9,
                 n_memory: int = 8, dropout: float = 0.1) -> None:
        super().__init__()
        self.d_model, self.max_len, self.n_memory = d_model, max_len, n_memory
        self.n_tok = n_tok
        self.token_emb = nn.Embedding(n_tok, d_model, padding_idx=PAD)
        self.pos_emb = nn.Parameter(torch.zeros(1, max_len, d_model))
        nn.init.normal_(self.pos_emb, std=0.02)
        self.memory = nn.Linear(cond_dim, n_memory * d_model)
        self.memory_norm = nn.LayerNorm(d_model)
        layer = nn.TransformerDecoderLayer(
            d_model=d_model, nhead=heads, dim_feedforward=ff, dropout=dropout,
            activation="gelu", batch_first=True, norm_first=True,
        )
        self.decoder = nn.TransformerDecoder(layer, num_layers=layers,
                                             norm=nn.LayerNorm(d_model))
        self.head_token = nn.Linear(d_model, n_tok)
        self.head_knob = nn.Sequential(
            nn.Linear(d_model, 512), nn.GELU(), nn.Linear(512, knob_dim),
        )
        # PAD and BOS are never legal continuations. As a BUFFER rather than an
        # in-place write on the logits: an item assignment inside the unrolled
        # greedy loop is seven ScatterND nodes in the exported graph, and one
        # of them silently not folding is a decoder that can emit a token the
        # slot mapping has no class for.
        bias = torch.zeros(n_tok)
        bias[PAD] = bias[BOS] = -1e30
        self.register_buffer("token_bias", bias.view(1, -1), persistent=False)

    def build_memory(self, z: torch.Tensor) -> torch.Tensor:
        mem = self.memory(z).view(z.shape[0], self.n_memory, self.d_model)
        return self.memory_norm(mem)

    def states(self, tokens: torch.Tensor, memory: torch.Tensor) -> torch.Tensor:
        """Decoder states for a token prefix `[B, L]` -> `[B, L, d]`."""
        t = tokens.shape[1]
        x = self.token_emb(tokens) * math.sqrt(self.d_model) + self.pos_emb[:, :t]
        # `is_causal` is not passed: it is an OPTIMISATION HINT that the
        # exporter and several attention backends ignore, and a decoder whose
        # mask is silently dropped trains perfectly and cheats completely.
        mask = torch.triu(torch.full((t, t), float("-inf"), device=tokens.device), diagonal=1)
        return self.decoder(x, memory, tgt_mask=mask)

    def forward(self, tokens: torch.Tensor, memory: torch.Tensor):
        h = self.states(tokens, memory)
        return self.head_token(h), self.head_knob(h)


class RigEstimatorV2(nn.Module):
    """v1's front-end and family head, with the slot head replaced by a decoder.

    `head_family` stays and stays SUPERVISED: it is what the C# retrieval
    steering reads, it is the one output that is meaningful when the chain is
    wrong, and as an auxiliary multi-label task it regularises the trunk. What
    it is NOT any more is the thing the chain is read off.
    """

    def __init__(self, vocab, frontend: str = "cnn", freeze: bool = True,
                 hidden: int = 768, d_model: int = 256, layers: int = 3,
                 heads: int = 4, knob_head: str = "step") -> None:
        super().__init__()
        self.frontend_name = frontend
        self.head_kind = "v2"
        cfg = MEL_CONFIGS[frontend]
        decimate = 48000 // cfg["sample_rate"]
        self.mel = LogMel(**cfg, decimate=decimate)
        self.mod = ModulationBranch(cfg["n_mels"])
        if frontend == "cnn":
            self.tower = MelCnn(n_mels=cfg["n_mels"])
            tower_dim = self.tower.out_dim
        else:
            self.tower = HfTower(frontend, freeze=freeze)
            tower_dim = self.tower.out_dim

        self.trunk = nn.Sequential(
            nn.Linear(tower_dim + 128, hidden), nn.LayerNorm(hidden), nn.GELU(), nn.Dropout(0.1),
            nn.Linear(hidden, hidden), nn.LayerNorm(hidden), nn.GELU(),
        )
        self.n_families = vocab.n_families
        self.n_slots = vocab.slots
        self.n_slot_classes = vocab.n_slot_classes
        self.knob_dim = vocab.knob_dim
        self.n_tokens = n_tokens(vocab)

        self.head_family = nn.Linear(hidden, self.n_families)
        self.decoder = ChainDecoder(
            self.n_tokens, self.knob_dim, cond_dim=hidden, d_model=d_model,
            layers=layers, heads=heads, max_len=self.n_slots + 2,
        )

        # -- the HYBRID knob head ------------------------------------------
        # `knob_head="global"` regresses all knob_dim params ONCE from the
        # pooled trunk, which is v1's arrangement, while the chain still comes
        # from the decoder. It exists because round 2b measured that the knob
        # ceiling belongs to the HEAD and not to the audio: on the same clips,
        # the same tower and the same corpus, v1's global head reached
        # knob_mae 0.1342 where v2's per-step head stalled at 0.2163.
        #
        # The per-step head conditions each knob prediction on the decoder state
        # that emitted its module, which sounds strictly better and is not: a
        # step's hidden state is optimised to choose the NEXT TOKEN, and asking
        # the same 256-d vector to also carry 573 continuous values makes it do
        # two jobs. The global head sees the whole 768-d audio embedding and
        # does one.
        #
        # "step" remains the default, so every run before this is reproducible.
        if knob_head not in ("step", "global"):
            raise ValueError(f"knob_head must be 'step' or 'global', got {knob_head!r}")
        self.knob_head = knob_head
        if knob_head == "global":
            # Same shape as v1's knob head, deliberately -- this is the thing
            # being transplanted, so it should not also differ in capacity.
            self.head_knob_global = nn.Sequential(
                nn.Linear(hidden, 512), nn.GELU(), nn.Linear(512, self.knob_dim),
            )

        self.register_buffer("param_mask", param_mask_matrix(vocab), persistent=False)

    # -- shared front-end --------------------------------------------------
    def embed(self, pcm: torch.Tensor) -> torch.Tensor:
        # Same fp32 island as v1: the mel and the envelope DFT have dynamic
        # range well outside fp16's, and the failure is a silent NaN loss.
        with torch.amp.autocast("cuda", enabled=False):
            mel = self.mel(pcm.float())
            mod = self.mod(mel)
        return self.trunk(torch.cat([self.tower(mel), mod], dim=1))

    def forward(self, pcm: torch.Tensor, tokens_in: torch.Tensor):
        """TEACHER-FORCED forward: `(family_logits, token_logits, knob_preds)`.

        `knob_preds` is `[B, L, K]` under the step head and `[B, K]` under the
        global one. The RANK is the signal: `multitask_loss_v2` switches on it
        rather than on a flag, so a model and a loss can never disagree about
        which head is in play.
        """
        z = self.embed(pcm)
        mem = self.decoder.build_memory(z)
        tok_logit, step_knob = self.decoder(tokens_in, mem)
        knob_logit = self.head_knob_global(z) if self.knob_head == "global" else step_knob
        return self.head_family(z), tok_logit, knob_logit

    # -- inference ---------------------------------------------------------
    def greedy(self, pcm: torch.Tensor, steps: int | None = None):
        return greedy_decode(self, self.embed(pcm), steps)

    def beam(self, pcm: torch.Tensor, k: int = 3, steps: int | None = None):
        return beam_decode(self, self.embed(pcm), k=k, steps=steps)


class GreedyResult(NamedTuple):
    """What a decode produced. Named because six positional tensors is a trap."""

    #: `[B, S]` emitted tokens, PAD after the end.
    tokens: torch.Tensor
    #: `[B, knob_dim]` the flat v1-shaped knob vector, filled for emitted modules.
    knobs: torch.Tensor
    #: `[B]` total sequence log-probability.
    logprob: torch.Tensor
    #: `[B, S, n_tok]` per-step token distribution (None for beam).
    step_probs: torch.Tensor | None
    #: `[B, S]` 1 while the chain had not yet emitted EOS.
    alive: torch.Tensor
    #: `[B]` length-normalised `exp(mean log p)` -- the per-chain confidence.
    confidence: torch.Tensor
    #: `[B, S, knob_dim]` this step's knobs, already restricted to its module.
    step_knobs: torch.Tensor


def greedy_decode(model, z: torch.Tensor, steps: int | None = None) -> GreedyResult:
    """Fixed-length unrolled greedy decode. Exportable; no dynamic control flow.

    The loop runs exactly `steps` times whatever the chain does, so the graph
    has no `Loop` and no data-dependent shape; a finished chain simply emits
    PAD, and `alive` gates everything downstream. A chain that ended at step 2
    therefore cannot have step 5 write a knob.
    """
    steps = steps or model.n_slots
    b = z.shape[0]
    dev = z.device
    mem = model.decoder.build_memory(z)
    tokens = torch.full((b, 1), BOS, dtype=torch.long, device=dev)
    knob_out = torch.zeros(b, model.knob_dim, device=dev)
    # The GLOBAL head does not depend on the decode at all, so it is read once
    # here and the per-step accumulation below is skipped. `knob_out` keeps its
    # name, shape and meaning either way, which is what leaves the ONNX contract
    # untouched: `ExportWrapperV2` publishes `r.knobs` as `knob_values` and
    # neither the exporter nor RigEstimatorOnnx can tell which head filled it.
    global_knobs = (torch.sigmoid(model.head_knob_global(z))
                    if getattr(model, "knob_head", "step") == "global" else None)
    alive = torch.ones(b, device=dev)
    logp = torch.zeros(b, device=dev)
    emitted, probs_all, alive_all, knobs_all = [], [], [], []

    for _ in range(steps):
        tok_logit, knob_logit = model.decoder(tokens, mem)
        step_prob = torch.softmax(tok_logit[:, -1] + model.decoder.token_bias, dim=-1)
        nxt = step_prob.argmax(dim=-1)
        step_logp = torch.log(step_prob.gather(1, nxt.unsqueeze(1)).squeeze(1) + 1e-12)

        logp = logp + step_logp * alive
        emitted.append(torch.where(alive > 0, nxt, torch.full_like(nxt, PAD)))
        probs_all.append(step_prob)
        alive_all.append(alive)

        # The knob prediction of THIS step, restricted to the params of the
        # module this step emitted. `param_mask` is already zero for PAD/EOS,
        # so the `alive` factor is belt and braces rather than the guard.
        step_knob = (global_knobs if global_knobs is not None
                     else torch.sigmoid(knob_logit[:, -1]))
        mask = model.param_mask.index_select(0, nxt)
        knobs_all.append(step_knob * mask * alive.unsqueeze(1))
        if global_knobs is None:
            knob_out = knob_out + knobs_all[-1]

        alive = alive * (nxt > EOS).float()
        tokens = torch.cat([tokens, nxt.unsqueeze(1)], dim=1)

    if global_knobs is not None:
        # v1's semantics exactly: the flat vector is the head's raw output, NOT
        # masked to the decoded chain. RigEstimatorOnnx reads only the params of
        # the modules it decoded, so masking would change nothing it can see --
        # and leaving it unmasked keeps `knob_values` meaning the same thing it
        # means in every v1 export.
        knob_out = global_knobs

    out_tokens = torch.stack(emitted, dim=1)
    n = (out_tokens > EOS).float().sum(dim=1)
    # Mean log-prob over the emitted tokens INCLUDING the EOS that ended them:
    # a decoder that never stops must not be rewarded for a long, confident,
    # wrong chain, and dividing by n alone would do exactly that.
    conf = torch.exp(logp / (n + 1.0).clamp(min=1.0))
    return GreedyResult(out_tokens, knob_out, logp, torch.stack(probs_all, dim=1),
                        torch.stack(alive_all, dim=1), conf,
                        torch.stack(knobs_all, dim=1))


@torch.no_grad()
def beam_decode(model, z: torch.Tensor, k: int = 3, steps: int | None = None) -> GreedyResult:
    """Beam search, python-side only. Returns the same `GreedyResult` shape.

    Scored by TOTAL log-prob, not by the length-normalised confidence: length
    normalisation inside the search systematically prefers longer chains, and a
    rig with two extra pedals in it is a worse prior than a short right one.
    The returned confidence is still the length-normalised number, because that
    is the quantity the matcher compares across clips.
    """
    steps = steps or model.n_slots
    b = z.shape[0]
    dev = z.device
    mem = model.decoder.build_memory(z)

    # [B, k, L] prefixes, [B, k] scores. Beam 0 starts alive, the rest at -inf
    # so the first step cannot pick the same continuation k times.
    seq = torch.full((b, k, 1), BOS, dtype=torch.long, device=dev)
    score = torch.full((b, k), float("-inf"), device=dev)
    score[:, 0] = 0.0
    done = torch.zeros(b, k, dtype=torch.bool, device=dev)

    # A finished beam may only extend with PAD, and at ZERO cost: letting it go
    # on accumulating log-probs means a short right chain can never outscore a
    # long one, which is the classic beam-search length bug.
    done_lp = torch.full((1, 1, model.n_tokens), -1e30, device=dev)
    done_lp[..., PAD] = 0.0

    for _ in range(steps):
        flat = seq.view(b * k, -1)
        mem_k = mem.unsqueeze(1).expand(b, k, *mem.shape[1:]).reshape(b * k, *mem.shape[1:])
        tok_logit, _ = model.decoder(flat, mem_k)
        lp = torch.log_softmax(tok_logit[:, -1] + model.decoder.token_bias, dim=-1).view(b, k, -1)
        lp = torch.where(done.unsqueeze(-1), done_lp.expand_as(lp), lp)
        cand = score.unsqueeze(-1) + lp
        flat_cand = cand.view(b, -1)
        top, ix = flat_cand.topk(k, dim=-1)
        beam_ix = ix // lp.shape[-1]
        tok_ix = ix % lp.shape[-1]
        seq = torch.cat([seq.gather(1, beam_ix.unsqueeze(-1).expand(-1, -1, seq.shape[-1])),
                         tok_ix.unsqueeze(-1)], dim=-1)
        done = done.gather(1, beam_ix) | (tok_ix <= EOS)
        score = top

    best = score.argmax(dim=1)
    tokens = seq[torch.arange(b, device=dev), best, 1:].contiguous()
    logprob = score.gather(1, best.unsqueeze(1)).squeeze(1)
    # Re-run the chosen chain teacher-forced to read its knobs off the states
    # that produced it. Cheaper and less error-prone than threading the knob
    # head through the beam bookkeeping, and it is provably the same states.
    inp = torch.cat([torch.full((b, 1), BOS, dtype=torch.long, device=dev), tokens[:, :-1]], dim=1)
    _, knob_logit = model.decoder(inp, mem)
    is_mod = tokens > EOS
    # `alive` is 1 at every step up to AND INCLUDING the one that emitted EOS.
    alive = torch.cat([torch.ones(b, 1, device=dev),
                       torch.cumprod(is_mod.long(), dim=1).float()[:, :-1]], dim=1)
    mask = model.param_mask.index_select(0, tokens.reshape(-1)).view(b, tokens.shape[1], -1)
    step_knobs = torch.sigmoid(knob_logit) * mask * alive.unsqueeze(-1)
    n = is_mod.float().sum(dim=1)
    conf = torch.exp(logprob / (n + 1.0).clamp(min=1.0))
    tokens = torch.where(alive > 0, tokens, torch.full_like(tokens, PAD))
    return GreedyResult(tokens, step_knobs.sum(dim=1), logprob, None, alive, conf, step_knobs)


def slot_probs_from_steps(step_probs: torch.Tensor, alive: torch.Tensor,
                          n_slot_classes: int) -> torch.Tensor:
    """Decoder step distributions -> v1's `[B, slots, classes]` slot_probs.

    This is what keeps the ONNX contract ADDITIVE: a C# reader that knows only
    v1's four outputs decodes the v2 chain correctly, because token `k+2` IS
    slot class `k` and a dead step is all-`<none>`.
    """
    # class 0 (<none>) <- EOS, class c <- token c + TOKEN_OFFSET.
    none_col = step_probs[:, :, EOS: EOS + 1]
    modules = step_probs[:, :, TOKEN_OFFSET + 1: TOKEN_OFFSET + n_slot_classes]
    out = torch.cat([none_col, modules], dim=-1) * alive.unsqueeze(-1)
    # A step past the end is `<none>` with CERTAINTY, not an all-zero row whose
    # argmax is an accident of tie-breaking in whichever runtime reads it.
    dead = (1.0 - alive).unsqueeze(-1)
    return out + F.pad(dead, (0, n_slot_classes - 1))


class ExportWrapperV2(nn.Module):
    """The v2 ONNX graph: v1's four outputs PLUS the chain the decoder emitted.

    The contract is ADDITIVE on purpose. `family_presence`, `slot_probs`,
    `knob_values` and `confidence` keep their v1 names, shapes and meaning --
    filled from the greedy decode -- so `RigLabelVocabulary.SchemaVersion` does
    not move. The two new outputs (`chain_tokens`, `chain_knobs`) are there for
    a future C# reader that wants the sequence and its per-step knobs directly.

    **Additive is not free**: the C# reader used to convert EVERY output of the
    run to `float[]`, so it threw on any extra output that was not float. Hence
    `chain_tokens` is float32 here, and `RigEstimatorOnnx` now reads only the
    four it needs, by name (`RigEstimatorContractTests`).

    **A chain containing the SAME module twice is exactly what `chain_knobs`
    is for.** The flat `knob_values` vector has one slot per catalogue param,
    so two `delay`s in one chain sum into the same indices -- a v1 limitation
    the v2 flat output inherits by construction, not a regression. `chain_knobs
    [step]` keeps each occurrence's own values. The sampler's chains are
    overwhelmingly single-occurrence, which is why this is a footnote and not a
    redesign of the flat vector.
    """

    def __init__(self, model: RigEstimatorV2) -> None:
        super().__init__()
        self.model = model

    def forward(self, audio: torch.Tensor):
        m = self.model
        z = m.embed(audio)
        r = greedy_decode(m, z)
        slots = slot_probs_from_steps(r.step_probs, r.alive, m.n_slot_classes)
        return (
            torch.sigmoid(m.head_family(z)),
            slots,
            r.knobs,
            r.confidence,
            # FLOAT, not int64, and that is deliberate. `RigEstimatorOnnx` maps
            # an ONNX output to `float[]`; an integer-typed output makes any
            # reader that touches it throw, which would turn "v2 publishes MORE"
            # into "v1 cannot load v2" -- the exact opposite of an additive
            # contract. Token ids top out at 102 and are exact in fp32.
            r.tokens.to(torch.float32),
            r.step_knobs,
        )


#: The readiness tolerance the tail term chases. Deliberately the same number as
#: `metrics.READY_TOL`, imported by value rather than cross-imported to keep this
#: module free of a dependency on the metrics module.
TAIL_TOL = 0.1


def multitask_loss_v2(pred, target, knob_weight: torch.Tensor | None = None,
                      param_mask: torch.Tensor | None = None,
                      weights=(1.0, 1.0, 2.0), label_smoothing: float = 0.05,
                      tail_weight: float = 0.0, tail_tol: float = TAIL_TOL):
    """BCE (families) + CE (chain tokens) + SENSITIVITY-WEIGHTED masked L1 (knobs).

    `pred` is `(family_logits, token_logits [B,L,V], knob_logits [B,L,K])`;
    `target` is `(family, tokens [B,L], knobs [B,K], knob_mask [B,K])`.

    Two maskings stack on the knob term and both are load-bearing:
      * `param_mask[target_token]` -- step `s` is supervised ONLY on the params
        of the module it was supposed to emit. Without it every step regresses
        all 573 knobs and the head learns the dataset's marginal, which is
        exactly v1's failure mode one level in.
      * `knob_mask` -- the row's own "this param is in the rig" mask, as v1.

    The weight vector is the AUDIBILITY measure from `sensitivity.py`; the
    denominator is `sum(mask x weight)` so the term stays a weighted MEAN and
    remains on the same scale as v1's MAE. A run with no weights file passes
    `None` and the term is v1's masked MAE exactly -- which is what makes the
    two heads comparable on the same number.
    """
    w_fam, w_tok, w_knob = weights
    fam_logit, tok_logit, knob_logit = pred
    fam_t, tok_t, knob_t, knob_m = target

    l_fam = F.binary_cross_entropy_with_logits(fam_logit, fam_t)
    l_tok = F.cross_entropy(
        tok_logit.reshape(-1, tok_logit.shape[-1]), tok_t.reshape(-1),
        ignore_index=PAD, label_smoothing=label_smoothing,
    )

    knob_p = torch.sigmoid(knob_logit)
    if knob_p.dim() == 2:
        # GLOBAL knob head: one [B, K] prediction for the whole clip, so there
        # is no step to restrict it to. The supervision is v1's exactly -- the
        # row's own "this param is in the rig" mask and nothing else. Switching
        # on the RANK rather than on a flag means the loss cannot be told one
        # thing while the model does another.
        base_mask = knob_m
        knob_ref = knob_t
    else:
        step_mask = param_mask.index_select(0, tok_t.reshape(-1)).view(
            *tok_t.shape, knob_p.shape[-1]) if param_mask is not None \
            else torch.ones_like(knob_p)
        base_mask = step_mask * knob_m.unsqueeze(1)
        knob_ref = knob_t.unsqueeze(1)
    if knob_weight is not None:
        mask = base_mask * knob_weight.view(*([1] * (base_mask.dim() - 1)), -1)
    else:
        mask = base_mask
    raw = (knob_p - knob_ref).abs()
    l_knob = (raw * mask).sum() / mask.sum().clamp(min=1e-6)

    # ---- the TAIL term -----------------------------------------------------
    # Readiness is all-or-nothing over every knob of the rig, so it is decided by
    # the WORST knob in a clip, not the mean. Measured on round 2 vs 2b: v1's
    # mean knob error is 38 % lower than v2's (0.1342 vs 0.2163) and its
    # probability of passing GIVEN an exact chain is no better (0.0634 vs
    # 0.0662). Lowering the mean does not move readiness; the tail has to come
    # in.
    #
    # So: a hinge that is exactly zero for any knob already inside the tolerance
    # and grows linearly outside it. Only the knobs that are actually failing
    # the readiness test pay, which is the point -- a term that also pressed on
    # knobs already within 0.1 would just be the mean again under another name.
    #
    # The hinge uses the UNWEIGHTED mask even when audibility weights are in
    # play, because the readiness criterion it chases is itself unweighted. A
    # knob at the 0.1 weight floor still has to land inside the tolerance for
    # the clip to count as ready.
    l_tail = raw.new_zeros(())
    if tail_weight > 0.0:
        hinge = (raw - tail_tol).clamp(min=0.0) * base_mask
        l_tail = hinge.sum() / base_mask.sum().clamp(min=1e-6)

    total = w_fam * l_fam + w_tok * l_tok + w_knob * (l_knob + tail_weight * l_tail)
    return total, {
        "loss": total.item(), "fam": l_fam.item(), "slot": l_tok.item(),
        "knob": l_knob.item(), "conf": 0.0, "knob_tail": float(l_tail),
    }
