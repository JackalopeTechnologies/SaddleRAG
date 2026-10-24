# Language mismatch evidence

`ChunkRepository.GetLanguageMixAsync` groups missing `DocChunk.CodeLanguage` values under `unfenced`. Those values describe absent metadata. The language diagnostic previously compared declared languages against the fraction of all chunks, so a library containing only unfenced text was marked `LanguageMismatch`. Mostly unfenced prose with a small amount of correctly tagged code could receive the same false diagnosis.

The detector now excludes unfenced, blank, and zero-count observations from the language evidence. With no tagged evidence it emits no language mismatch. With tagged evidence it retains the existing 30% matching-language threshold, calculated within the tagged portion of the corpus. A genuine predominance of another tagged language still triggers the diagnostic. Other suspect checks retain their behavior.

The comparison recognizes the explicit C# spellings `C#`, `csharp`, and `cs`, case-insensitively, and combines their observed fractions. The profile contract documents `C#`; [Prism's supported-language documentation](https://prismjs.com/#supported-languages) lists `csharp` and `cs` for C#. Other language names retain their own identities; no general language taxonomy or model-based detection is introduced.

Health still exposes the original language-mix data, including `unfenced`. Finalization persists the corrected suspect decision. Existing saved suspect markers are not rewritten automatically, and no live library or profile was modified for this repair.

Metadata extraction remains separate work: current `CategoryAwareChunker.CreateChunk` leaves `CodeLanguage` unset. Preserving source code-language markup and assigning chunk metadata are not implemented by this diagnostic correction. Missing metadata therefore remains visible as unknown evidence rather than being asserted to be the declared language.

Regression coverage distinguishes all-unfenced and empty observations, mostly unfenced prose with correct or wrong tagged code, the threshold within observed code, C# aliases, and persistence of the resulting suspect decision. Tests use substituted repositories and no model inference.
