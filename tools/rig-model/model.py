"""The rig estimator: a swappable front-end + four task heads.

`RigEstimator.forward` takes RAW MONO PCM, not a spectrogram. The mel lives
inside the module (`LogMel`, built from `torch.stft`, which opset 17 exports)
so the shipped ONNX graph carries the whole signal path and C# never
reimplements a mel. A second mel implementation on the C# side is the exact
failure this repo has been bitten by in other subsystems, and it is invisible:
the model still produces plausible numbers, just consistently wrong ones.

Front-ends:
  * `cnn`   -- the CONTROL. ~4 M params, mel in, from scratch. The study's
               recommendation, and the number the transformer has to beat.
  * `clap`  -- LAION CLAP's HTSAT audio tower (MIT). 48 kHz native, so no
               resampler is needed in the graph.
  * `ast`   -- MIT/ast-finetuned-audioset (Apache-2.0). 16 kHz, 128 mel; the
               graph decimates 48 -> 16 kHz with a fixed FIR (exactly 3:1).

Every front-end is preceded by the SAME modulation branch, because a mel CNN
cannot see a 4 Hz tremolo or a 300 ms delay: those live in the envelope's
spectrum, not in the spectrogram's texture.
"""

from __future__ import annotations

import math

import torch
import torch.nn as nn
import torch.nn.functional as F

# Mel configs, per front-end. The CNN control uses the study's 128-bin config;
# a pretrained tower MUST use the config it was pretrained with or its learned
# statistics no longer describe its input.
MEL_CONFIGS = {
    "cnn": dict(sample_rate=48000, n_fft=1024, hop=480, n_mels=128, fmin=20.0, fmax=20000.0),
    "clap": dict(sample_rate=48000, n_fft=1024, hop=480, n_mels=64, fmin=50.0, fmax=14000.0),
    "ast": dict(sample_rate=16000, n_fft=400, hop=160, n_mels=128, fmin=20.0, fmax=7600.0),
}


def hz_to_mel(f: float) -> float:
    return 2595.0 * math.log10(1.0 + f / 700.0)


def mel_to_hz(m):
    return 700.0 * (10.0 ** (m / 2595.0) - 1.0)


def mel_filterbank(n_fft: int, n_mels: int, sr: int, fmin: float, fmax: float) -> torch.Tensor:
    """Slaney-style triangular filterbank, [n_freq, n_mels]."""
    n_freq = n_fft // 2 + 1
    fft_hz = torch.linspace(0, sr / 2, n_freq)
    mels = torch.linspace(hz_to_mel(fmin), hz_to_mel(fmax), n_mels + 2)
    pts = mel_to_hz(mels)
    fb = torch.zeros(n_freq, n_mels)
    for i in range(n_mels):
        lo, mid, hi = pts[i].item(), pts[i + 1].item(), pts[i + 2].item()
        if hi <= lo:
            continue
        up = (fft_hz - lo) / max(mid - lo, 1e-6)
        dn = (hi - fft_hz) / max(hi - mid, 1e-6)
        fb[:, i] = torch.clamp(torch.minimum(up, dn), min=0.0)
    return fb


class LogMel(nn.Module):
    """Raw PCM -> log-mel, as two real convolutions. ONNX-exportable at opset 17.

    This does NOT use `torch.stft`. The opset-17 exporter refuses it --
    "STFT does not currently support complex types" -- and the dynamo exporter
    route produced a 0.2 MB graph whose every output was NaN. A DFT is a fixed
    linear map, so the real and imaginary parts are just two `conv1d`s against
    precomputed windowed cos/sin kernels, strided by the hop. That is exactly
    the same arithmetic, it exports as plain Conv, and it is the whole reason
    C# can feed raw PCM.
    """

    def __init__(self, sample_rate: int, n_fft: int, hop: int, n_mels: int,
                 fmin: float, fmax: float, decimate: int = 1) -> None:
        super().__init__()
        self.n_fft, self.hop, self.decimate = n_fft, hop, decimate

        window = torch.hann_window(n_fft, dtype=torch.float64)
        n_freq = n_fft // 2 + 1
        idx = torch.arange(n_fft, dtype=torch.float64)
        freq = torch.arange(n_freq, dtype=torch.float64)
        angle = 2.0 * math.pi * freq[:, None] * idx[None, :] / n_fft
        self.register_buffer("dft_cos",
                             (window * torch.cos(angle)).to(torch.float32).unsqueeze(1),
                             persistent=False)
        self.register_buffer("dft_sin",
                             (-window * torch.sin(angle)).to(torch.float32).unsqueeze(1),
                             persistent=False)
        self.register_buffer("fb", mel_filterbank(n_fft, n_mels, sample_rate, fmin, fmax),
                             persistent=False)
        if decimate > 1:
            # A 48 -> 16 kHz decimator is exactly 3:1, so one fixed windowed-sinc
            # low-pass at the OUTPUT Nyquist plus a stride is the whole job. It
            # is a buffer, not a resampler call, so it exports.
            taps = 96 * decimate + 1
            t = torch.arange(taps, dtype=torch.float64) - (taps - 1) / 2
            cutoff = 0.5 / decimate
            h = 2 * cutoff * torch.sinc(2 * cutoff * t)
            h = h * torch.hamming_window(taps, periodic=False, dtype=torch.float64)
            h = (h / h.sum()).to(torch.float32)
            self.register_buffer("aa", h.view(1, 1, -1), persistent=False)

    def forward(self, pcm: torch.Tensor) -> torch.Tensor:
        """`pcm` is [B, N] -> [B, n_mels, T]."""
        if self.decimate > 1:
            pad = (self.aa.shape[-1] - 1) // 2
            pcm = F.conv1d(pcm.unsqueeze(1), self.aa, padding=pad).squeeze(1)
            pcm = pcm[:, :: self.decimate]
        # center=True, pad_mode="constant": the same framing torch.stft would
        # produce, so frame counts and alignment are unchanged.
        half = self.n_fft // 2
        x = F.pad(pcm.unsqueeze(1), (half, half))
        re = F.conv1d(x, self.dft_cos, stride=self.hop)       # [B, F, T]
        im = F.conv1d(x, self.dft_sin, stride=self.hop)
        power = re.pow(2) + im.pow(2)                         # [B, F, T]
        mel = torch.matmul(power.transpose(1, 2), self.fb)   # [B, T, M]
        # +1e-10 before the log is what keeps a digital-silence frame finite.
        return torch.log(mel + 1e-10).transpose(1, 2)        # [B, M, T]


class ModulationBranch(nn.Module):
    """Envelope spectrum: where tremolo rate, chorus rate and delay time live.

    The mel frame rate is 100 Hz, so the envelope's rFFT resolves to 50 Hz and
    the first `n_bins` cover 0-20 Hz -- every LFO rate the catalogue can dial,
    plus the repeat rate of a delay short enough to read as one.
    """

    def __init__(self, n_mels: int, frame_rate: float = 100.0, max_hz: float = 20.0,
                 out_dim: int = 128) -> None:
        super().__init__()
        self.frame_rate, self.max_hz = frame_rate, max_hz
        self.n_bins = 96
        self.n_dft = 1024
        # Same story as LogMel: `torch.fft.rfft` has no opset-17 export either,
        # and a DFT of a FIXED length is just a matmul against a constant
        # matrix. Only the first n_bins are ever used, so only those rows are
        # stored -- 96 rows instead of 513.
        idx = torch.arange(self.n_dft, dtype=torch.float64)
        freq = torch.arange(self.n_bins, dtype=torch.float64)
        angle = 2.0 * math.pi * freq[:, None] * idx[None, :] / self.n_dft
        self.register_buffer("dft_cos", torch.cos(angle).to(torch.float32), persistent=False)
        self.register_buffer("dft_sin", (-torch.sin(angle)).to(torch.float32), persistent=False)
        self.net = nn.Sequential(
            nn.Linear(self.n_bins * 2, 256), nn.GELU(),
            nn.Linear(256, out_dim), nn.GELU(),
        )

    def forward(self, mel: torch.Tensor) -> torch.Tensor:
        # Broadband envelope, and a high-band one: a tremolo modulates both, a
        # phaser or a filter sweep moves energy BETWEEN them.
        #
        # The envelope is taken in the LOG domain, not by exponentiating back to
        # linear power. Two reasons, and the second one is why this is written
        # down: a multiplicative modulation (which is what a tremolo IS) becomes
        # ADDITIVE in log, so its rFFT peak is cleaner; and `exp(mel)` overflows
        # to inf under AMP for any loud frame, which made every loss NaN from
        # the very first step.
        m = mel.shape[1]
        env_all = mel.mean(dim=1)
        env_hi = mel[:, m // 2:, :].mean(dim=1)
        feats = []
        for env in (env_all, env_hi):
            env = env - env.mean(dim=1, keepdim=True)
            t = env.shape[1]
            env = F.pad(env, (0, self.n_dft - t)) if t < self.n_dft else env[:, : self.n_dft]
            re = torch.matmul(env, self.dft_cos.t())
            im = torch.matmul(env, self.dft_sin.t())
            band = torch.sqrt(re.pow(2) + im.pow(2) + 1e-12)
            feats.append(torch.log1p(band))
        x = torch.cat(feats, dim=1)
        x = x / (x.amax(dim=1, keepdim=True) + 1e-6)
        return self.net(x)


class MelCnn(nn.Module):
    """The control front-end: a small VGG-ish CNN over log-mel."""

    def __init__(self, n_mels: int = 128, width: int = 64, out_dim: int = 512) -> None:
        super().__init__()

        def block(i, o):
            return nn.Sequential(
                nn.Conv2d(i, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.GELU(),
                nn.Conv2d(o, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.GELU(),
                nn.AvgPool2d(2),
            )

        self.stem = nn.BatchNorm2d(1)
        self.blocks = nn.Sequential(
            block(1, width), block(width, width * 2),
            block(width * 2, width * 4), block(width * 4, width * 8),
        )
        self.proj = nn.Linear(width * 8 * 2, out_dim)
        self.out_dim = out_dim

    def forward(self, mel: torch.Tensor) -> torch.Tensor:
        x = self.stem(mel.unsqueeze(1))
        x = self.blocks(x)
        # Mean AND max over time+freq: a delay tail is a mean, a transient is a max.
        avg = x.mean(dim=(2, 3))
        mx = x.amax(dim=(2, 3))
        return self.proj(torch.cat([avg, mx], dim=1))


def load_clap_tower():
    """LAION CLAP's HTSAT audio tower, with its PRETRAINED weights actually in it.

    `ClapAudioModel.from_pretrained("laion/clap-htsat-unfused")` does NOT work on
    transformers 5.x: the checkpoint keys are `audio_model.audio_encoder.*` and
    the loader leaves every `audio_encoder.*` parameter MISSING, i.e. randomly
    initialised. It still returns a model, still runs, and still trains -- it is
    just no longer pretrained, which is the entire reason for choosing it. The
    only signal is a warning block in the middle of a download log.

    So the weights are loaded explicitly and the result is ASSERTED: zero
    missing keys, and a patch-embedding whose spread is not the random-init
    spread. A silently-random "pretrained" front-end would make the whole
    transformer-vs-CNN comparison meaningless.
    """
    import glob
    import os

    import torch as _torch
    from huggingface_hub import snapshot_download
    from transformers import ClapAudioModel, ClapConfig

    path = snapshot_download("laion/clap-htsat-unfused")
    cfg = ClapConfig.from_pretrained(path).audio_config
    tower = ClapAudioModel(cfg)

    blobs = glob.glob(os.path.join(path, "*.safetensors")) or glob.glob(os.path.join(path, "*.bin"))
    if not blobs:
        raise RuntimeError(f"no weight file in {path}")
    if blobs[0].endswith(".safetensors"):
        import safetensors.torch as st
        state = st.load_file(blobs[0])
    else:
        state = _torch.load(blobs[0], map_location="cpu", weights_only=True)

    prefix = "audio_model."
    stripped = {k[len(prefix):]: v for k, v in state.items() if k.startswith(prefix)}
    if not stripped:
        raise RuntimeError(f"no '{prefix}*' keys in the CLAP checkpoint; naming has changed again")

    result = tower.load_state_dict(stripped, strict=False)
    if result.missing_keys:
        raise RuntimeError(
            f"CLAP tower has {len(result.missing_keys)} randomly-initialised parameter(s) "
            f"(e.g. {result.missing_keys[:3]}). It is NOT pretrained; refusing to train on it."
        )
    spread = tower.audio_encoder.patch_embed.proj.weight.std().item()
    if spread < 0.02:
        raise RuntimeError(
            f"CLAP patch-embed std {spread:.5f} looks like a fresh random init (~0.013), "
            "not the pretrained weights (~0.051)."
        )
    return tower, cfg


class HfTower(nn.Module):
    """A pretrained HuggingFace audio tower, fed OUR mel, pooled to a vector."""

    def __init__(self, kind: str, freeze: bool = True) -> None:
        super().__init__()
        self.kind = kind
        if kind == "clap":
            self.tower, cfg = load_clap_tower()
            self.frames = 1001
            self.out_dim = cfg.hidden_size
        elif kind == "ast":
            from transformers import ASTModel
            self.tower = ASTModel.from_pretrained("MIT/ast-finetuned-audioset-10-10-0.4593")
            self.frames = self.tower.config.max_length
            self.out_dim = self.tower.config.hidden_size
        else:
            raise ValueError(kind)

        # Probe the real pooled width rather than trusting a config field: the
        # HTSAT tower returns [B, C, H, W] and the C is NOT config.hidden_size
        # on every version. A wrong width here is a shape error at best and a
        # silently mis-projected embedding at worst.
        self.tower.eval()
        with torch.no_grad():
            mels = {"clap": 64, "ast": 128}[kind]
            probe = torch.zeros(1, self.frames, mels)
            if kind == "ast":
                out = self.tower(probe).last_hidden_state
                self.out_dim = out.shape[-1]
            else:
                out = self.tower(probe.unsqueeze(1)).last_hidden_state
                self.out_dim = out.shape[1] if out.dim() == 4 else out.shape[-1]

        self.frozen = freeze
        self.checkpointing = False
        if freeze:
            for p in self.tower.parameters():
                p.requires_grad = False

    def enable_checkpointing(self) -> str:
        """Gradient checkpointing over the tower. Returns which route was taken.

        `ClapAudioModel` REFUSES HuggingFace's own
        `gradient_checkpointing_enable` ("does not support gradient
        checkpointing") -- measured 2026-09-13 on transformers 5.x -- which is
        exactly the front-end a full fine-tune needs it for. So the fallback is
        one `torch.utils.checkpoint` segment around the WHOLE tower call: every
        internal activation is dropped and recomputed in the backward, at the
        cost of one extra forward. Coarser than per-layer checkpointing and it
        saves more, which is the right trade at batch 24 on 10 GB.

        It is a no-op unless the tower is also UNFROZEN: a frozen tower runs
        under `no_grad` and has no activations to save, and checkpointing a
        no-grad segment silently costs a second forward for nothing.
        """
        fn = getattr(self.tower, "gradient_checkpointing_enable", None)
        if fn is not None:
            try:
                fn(gradient_checkpointing_kwargs={"use_reentrant": False})
                return "huggingface"
            except (ValueError, TypeError):
                pass
        self.checkpointing = True
        return "whole-tower"

    def train(self, mode: bool = True):
        super().train(mode)
        # A frozen tower must stay in eval: its BatchNorm/dropout would
        # otherwise keep updating running stats from our data and drift away
        # from the weights we deliberately froze.
        if self.frozen:
            self.tower.eval()
        return self

    def forward(self, mel: torch.Tensor) -> torch.Tensor:
        # mel is [B, M, T]; both towers want [.., frames, mel_bins].
        x = mel.transpose(1, 2)                       # [B, T, M]
        t = x.shape[1]
        if t < self.frames:
            x = F.pad(x, (0, 0, 0, self.frames - t))
        else:
            x = x[:, : self.frames]
        ctx = torch.no_grad() if self.frozen else torch.enable_grad()
        with ctx:
            if self.checkpointing and self.training and not self.frozen:
                pooled = torch.utils.checkpoint.checkpoint(
                    self._pooled, x, use_reentrant=False)
            else:
                pooled = self._pooled(x)
        return pooled.float()

    def _pooled(self, x: torch.Tensor) -> torch.Tensor:
        if self.kind == "ast":
            out = self.tower(x).last_hidden_state              # [B, tokens, D]
            return out.mean(dim=1)
        out = self.tower(x.unsqueeze(1)).last_hidden_state     # [B, D, H, W]
        return out.mean(dim=(2, 3)) if out.dim() == 4 else out.mean(dim=1)


class RigEstimator(nn.Module):
    """PCM in; family presence, slot chain, knobs and a confidence out."""

    def __init__(self, vocab, frontend: str = "cnn", freeze: bool = True,
                 hidden: int = 768) -> None:
        super().__init__()
        self.frontend_name = frontend
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

        self.head_family = nn.Linear(hidden, self.n_families)
        # One output block per slot: position is in the PARAMETERS, which is
        # what makes the chain head order-aware without a decoder.
        self.head_slots = nn.Linear(hidden, self.n_slots * self.n_slot_classes)
        self.head_knobs = nn.Linear(hidden, self.knob_dim)
        self.head_conf = nn.Linear(hidden, 1)

    def forward(self, pcm: torch.Tensor):
        # The signal front-end runs in fp32 even under AMP. An STFT, a mel
        # matmul over a 1024-bin power spectrum and an rFFT all have dynamic
        # range well outside fp16's, and the failure is silent: the loss is NaN
        # from step one and every metric reads 0.
        with torch.amp.autocast("cuda", enabled=False):
            mel = self.mel(pcm.float())
            mod = self.mod(mel)
        z = torch.cat([self.tower(mel), mod], dim=1)
        z = self.trunk(z)
        return (
            self.head_family(z),                                                   # logits [B,F]
            self.head_slots(z).view(-1, self.n_slots, self.n_slot_classes),        # logits
            self.head_knobs(z),                                                    # logits -> sigmoid
            self.head_conf(z).squeeze(-1),                                         # logit
        )


class ExportWrapper(nn.Module):
    """What the ONNX graph exposes: probabilities, not logits.

    The C# estimator must not have to know which head needed a sigmoid and
    which a softmax -- that is four more chances for the two sides to disagree,
    so the activations are baked in and the outputs are named.
    """

    def __init__(self, model: RigEstimator) -> None:
        super().__init__()
        self.model = model

    def forward(self, audio: torch.Tensor):
        fam, slots, knobs, conf = self.model(audio)
        return (
            torch.sigmoid(fam),
            torch.softmax(slots, dim=-1),
            torch.sigmoid(knobs),
            torch.sigmoid(conf),
        )


def count_params(m: nn.Module, trainable_only: bool = False) -> int:
    return sum(p.numel() for p in m.parameters() if p.requires_grad or not trainable_only)


def multitask_loss(pred, target, weights=(1.0, 1.0, 2.0, 0.2)):
    """BCE (families) + CE (slots) + masked L1 (knobs) + confidence.

    The knob term is MASKED by module presence: a param whose module is not in
    the rig has no correct value, and 0 is a real value for most ranges, so an
    unmasked L1 would train every absent module towards its range minimum.

    The confidence head is trained against the batch's own realised slot
    accuracy (detached) -- it predicts how right this estimate is, which is
    what the matcher needs to decide whether to trust the prior.
    """
    w_fam, w_slot, w_knob, w_conf = weights
    fam_logit, slot_logit, knob_logit, conf_logit = pred
    fam_t, slot_t, knob_t, knob_m = target

    l_fam = F.binary_cross_entropy_with_logits(fam_logit, fam_t)
    l_slot = F.cross_entropy(
        slot_logit.reshape(-1, slot_logit.shape[-1]), slot_t.reshape(-1), label_smoothing=0.05
    )

    knob_p = torch.sigmoid(knob_logit)
    denom = knob_m.sum().clamp(min=1.0)
    l_knob = ((knob_p - knob_t).abs() * knob_m).sum() / denom

    with torch.no_grad():
        correct = (slot_logit.argmax(dim=-1) == slot_t).float().mean(dim=1)
    l_conf = F.binary_cross_entropy_with_logits(conf_logit, correct)

    total = w_fam * l_fam + w_slot * l_slot + w_knob * l_knob + w_conf * l_conf
    return total, {
        "loss": total.item(), "fam": l_fam.item(), "slot": l_slot.item(),
        "knob": l_knob.item(), "conf": l_conf.item(),
    }
