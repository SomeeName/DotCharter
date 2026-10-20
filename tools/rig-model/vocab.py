"""The label vocabulary, loaded from the sidecar C# exports.

`RigLabelVocabulary.ToJson()` in DotCharter.Services is the SINGLE source of
truth for the module list, the param ranges and the normalisation. This module
only reads it. Never re-type a range here -- that is exactly the drift the
sidecar exists to prevent.

The normalisation below is the line-for-line twin of
`RigLabelVocabulary.Normalise` / `.Denormalise`; `RigEstimatorParityTests`
compares the two on real numbers, so any change here is a change there.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

SCHEMA_VERSION = 1


@dataclass(frozen=True)
class ParamRange:
    id: str
    min: float
    max: float
    default: float


@dataclass(frozen=True)
class ModuleEntry:
    id: str
    category: str
    param_offset: int
    params: tuple[ParamRange, ...]


class Vocab:
    """The label space: families, modules, the flat knob vector and its ranges."""

    def __init__(self, blob: dict) -> None:
        if blob.get("schemaVersion") != SCHEMA_VERSION:
            raise ValueError(
                f"sidecar schemaVersion {blob.get('schemaVersion')} != {SCHEMA_VERSION}; "
                "re-export it from C# (dotnet test --filter-class '*RigLabelVocabularyTests')"
            )
        self.sample_rate: int = int(blob["sampleRate"])
        self.clip_seconds: float = float(blob["clipSeconds"])
        self.slots: int = int(blob["slots"])
        self.none_label: str = blob["noneLabel"]
        self.knob_dim: int = int(blob["knobDim"])
        self.families: list[str] = list(blob["families"])
        self.modules: list[ModuleEntry] = [
            ModuleEntry(
                id=m["id"],
                category=m["category"],
                param_offset=int(m["paramOffset"]),
                params=tuple(
                    ParamRange(p["id"], float(p["min"]), float(p["max"]), float(p["default"]))
                    for p in m["params"]
                ),
            )
            for m in blob["modules"]
        ]
        self._by_id = {m.id: m for m in self.modules}
        self._family_ix = {f: i for i, f in enumerate(self.families)}
        # Slot softmax: index 0 is "none", index i+1 is modules[i].
        self._module_ix = {m.id: i + 1 for i, m in enumerate(self.modules)}
        computed = sum(len(m.params) for m in self.modules)
        if computed != self.knob_dim:
            raise ValueError(f"sidecar knobDim {self.knob_dim} != sum of params {computed}")

    # -- shapes -----------------------------------------------------------
    @property
    def n_families(self) -> int:
        return len(self.families)

    @property
    def n_slot_classes(self) -> int:
        """100 modules + the empty-slot label."""
        return len(self.modules) + 1

    @property
    def n_samples(self) -> int:
        return int(round(self.sample_rate * self.clip_seconds))

    # -- lookups ----------------------------------------------------------
    def module(self, type_id: str) -> ModuleEntry | None:
        return self._by_id.get(type_id)

    def slot_index(self, type_id: str | None) -> int:
        """Slot-softmax class for a module id; 0 for None / unknown."""
        if type_id is None:
            return 0
        return self._module_ix.get(type_id, 0)

    def slot_label(self, index: int) -> str:
        if index <= 0 or index > len(self.modules):
            return self.none_label
        return self.modules[index - 1].id

    def family_index(self, family: str) -> int:
        return self._family_ix[family]

    # -- normalisation (mirror of RigLabelVocabulary) ----------------------
    @staticmethod
    def normalise(spec: ParamRange, value: float) -> float:
        if not math.isfinite(value):
            value = spec.default
        span = spec.max - spec.min
        if not span > 0.0:
            return 0.0
        return min(1.0, max(0.0, (value - spec.min) / span))

    @staticmethod
    def denormalise(spec: ParamRange, unit: float) -> float:
        if not math.isfinite(unit):
            unit = 0.0
        span = spec.max - spec.min
        if not span > 0.0:
            return spec.min
        unit = min(1.0, max(0.0, unit))
        return min(spec.max, max(spec.min, spec.min + unit * span))

    # -- encoding ---------------------------------------------------------
    def encode_rig(self, modules: list[dict]) -> dict:
        """A manifest row's rig -> the four label tensors, as plain lists.

        `modules` is the `DcRigPreset.Modules` array as the manifest serialises
        it: each entry has `ModuleTypeId` (or `moduleTypeId`) and `Params`.
        Modules past `slots` are dropped -- the sampler is capped at the same
        number, so this only bites on a manifest that disagrees with the
        sidecar, and the trainer counts those.
        """
        fam = [0.0] * self.n_families
        slots = [0] * self.slots
        knobs = [0.0] * self.knob_dim
        mask = [0.0] * self.knob_dim
        overflow = 0

        for i, mod in enumerate(modules):
            type_id = mod.get("ModuleTypeId") or mod.get("moduleTypeId")
            entry = self.module(type_id) if type_id else None
            if entry is None:
                continue
            fam[self.family_index(entry.category)] = 1.0
            if i < self.slots:
                slots[i] = self.slot_index(entry.id)
            else:
                overflow += 1
            params = mod.get("Params") or mod.get("params") or {}
            for j, spec in enumerate(entry.params):
                ix = entry.param_offset + j
                raw = params.get(spec.id, spec.default)
                knobs[ix] = self.normalise(spec, float(raw))
                # Masked L1: a param only carries a gradient when its module is
                # actually in the rig. Without this every absent module trains
                # towards 0, which is a real value for most ranges.
                mask[ix] = 1.0

        return {"families": fam, "slots": slots, "knobs": knobs, "knob_mask": mask, "overflow": overflow}

    def decode_rig(self, slots, knobs) -> list[dict]:
        """The INVERSE of `encode_rig`: slot classes + a `[0,1]` knob vector ->
        the `DcRigPreset.Modules` array exactly as the manifest serialises it.

        This exists so a PREDICTION can be handed to the renderer in the same
        form as the truth. The goal the model is judged on is "does it sound
        close", which means rendering the predicted rig through the same DI and
        measuring the distance -- and that is only possible if a prediction can
        be written in the manifest's own shape.

        Every key the manifest writes is emitted, including the ones the model
        does not predict (`Bypassed`, `StringParams`, `Branch`, `Sidechain`,
        `ControlAssignments`), at the values the sampler uses. A renderer fed a
        preset missing them would either reject it or silently default it, and
        a silent default is a difference between truth and prediction that has
        nothing to do with the model.

        Trailing `<none>` slots end the chain, and an interior `<none>` is
        dropped: a preset has no way to express a hole, so a chain of
        `[amp, none, delay]` becomes `[amp, delay]`. Only a prediction can
        produce that; the sampler's chains are contiguous.
        """
        out: list[dict] = []
        for cls in list(slots):
            cls = int(cls)
            if cls <= 0:                      # 0 = <none>
                continue
            entry = self.modules[cls - 1]     # class i+1 IS modules[i]; see slot_index
            params = {
                spec.id: self.denormalise(spec, float(knobs[entry.param_offset + j]))
                for j, spec in enumerate(entry.params)
            }
            out.append({
                "ModuleTypeId": entry.id,
                "Params": params,
                "Bypassed": False,
                "StringParams": None,
                "Branch": 0,
                "Sidechain": 0,
                "ControlAssignments": [],
            })
        return out


def load_vocab(path: str | Path) -> Vocab:
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(
            f"{p} not found. Export it from C#:\n"
            "  dotnet test --project DotCharter.Tests/DotCharter.Tests.csproj "
            "--filter-class '*RigLabelVocabularyTests'"
        )
    return Vocab(json.loads(p.read_text(encoding="utf-8")))


DEFAULT_VOCAB_PATH = Path(__file__).with_name("rig_label_vocab.json")
