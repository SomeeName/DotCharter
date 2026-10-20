---
paths:
  - "JuceSharp/src/JuceSharp.Native/include/**"
  - "JuceSharp/src/JuceSharp.Native/src/dotcharter_audio.cpp"
  - "JuceSharp/src/JuceSharp.Native/src/AbiArgs.h"
  - "JuceSharp/src/JuceSharp/**"
  - "JuceSharp/src/JuceSharp.Dsp/**"
  - "JuceSharp/src/JuceSharp.StemSeparation/**"
  - "DotCharter.Services/Audio/**"
  - "DotCharter.Services/Infrastructure/Process/**"
  - "DotCharter.Services/Infrastructure/Storage/WindowsPosixRename.cs"
  - "DotCharter.UI/Platform/**"
---

# Managed ↔ native interop

The C ABI is `include/dotcharter_audio.h` (`DCA_ABI_VERSION`), implemented in
the one ABI translation unit `src/dotcharter_audio.cpp`, and mirrored by
`JuceSharp/src/JuceSharp/Interop/NativeMethods.cs` (`ExpectedAbiVersion`).
History and per-version rules: `JuceSharp/docs/agents/native-engine-abi.md`.

**Every change to an export, a struct or a callback checks all of these on
BOTH sides, explicitly — not by eye:**

- ABI compatibility: new behaviour goes in a NEW entry point (`…2`, `…3`),
  never a changed signature; bump `DCA_ABI_VERSION` and `ExpectedAbiVersion`
  together, rebuild the DLL before any managed code runs
- calling convention (`__cdecl` / `CallingConvention.Cdecl`)
- struct layout: field order, `StructLayout(Sequential)`, alignment/packing,
  explicit sizes; add a size/offset test when a struct crosses the boundary
- integer widths (`int32_t` ↔ `int`, `int64_t` ↔ `long`, `size_t` ↔ `nuint`),
  enums with an explicit underlying type on both sides
- bool representation — C `int`/`bool` is not .NET `bool` by default; marshal
  explicitly
- strings: UTF-8 (`LPUTF8Str`) in; out-strings via caller buffer + length with
  truncation that never splits a code point (`Utf8Truncate.h`)
- array/buffer lengths: an explicit count parameter with a fixed width
- pointer nullability: native rejects null handles with a status, never
  dereferences (`NullHandleRejectionTests`)
- callbacks: the native side never calls managed code from the audio
  callback; the managed delegate is rooted for as long as native may call it
  (field or `GCHandle`), and unregistered before it is released
- ownership and allocation/free pairing: memory is freed by the side that
  allocated it, through that side's allocator
- thread affinity: which thread may call each export, and which thread each
  callback arrives on
- error convention: `DCA_OK` / `DCA_ERR_*` status codes mapped to wrapper
  exceptions (`DCA_ERR_CANCELLED` → `OperationCanceledException`)
- native handle lifetime: wrap in `NativeHandleOwner`; every call pins the
  engine handle; destruction is ordered and idempotent

Evidence for an ABI change: a JuceSharp test that the export resolves from
the built DLL and that the mirrored constants match (`AbiV36Tests` is the
pattern). OS P/Invokes elsewhere in the app (`DotCharter.UI/Platform`,
process/storage helpers) follow the same checklist for their Win32 signatures.

Do not simplify, reorder, "modernise" (e.g. `DllImport` → `LibraryImport`) or
reformat interop declarations, `unsafe` blocks or ABI structs unless that is
the explicit task. Treat source-generated or tool-generated bindings as
generated code: change the generator or its input, not the output.
