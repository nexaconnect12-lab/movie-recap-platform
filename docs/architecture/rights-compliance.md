# Rights metadata implemented; publication approval remains Phase 7 design
Publication design requires source/owner/license/usage/platform/attribution/expiry evidence and version-specific approval. Missing or unknown authorization does not qualify for automatic publication. A future review service must record who approves what version and why.

The Phase 2 manual-import slice now records source, requester, owner, caller-declared Unknown/Authorized/Denied status, license evidence, permitted uses/platforms, attribution and optional UTC expiry in immutable per-import records. Responses label them Unreviewed. Unknown/Denied/expired records are permitted for local import/inspection; Authorized requires evidence and usage/platform fields. Deduplicating bytes shares only the asset: another source's rights are never substituted. There is no independent reviewer, grant verification, revision approval or publication implementation yet.

Folder discovery creates only candidates and requires versioned operator source/rights submission before import. A changed file gets a new candidate; imports bind to the submitted metadata fingerprint, so an older rights assertion is not silently transferred to a replacement file. This is a technical source-binding check, not a legal rights review.

Stream preferences are per import, even when content inventory is shared, and source identity is verified before saving a choice. Selection/reinspection preserves each original rights assertion and does not authorize extraction or publication. Operator identity is caller-asserted under current shared-key access; role-based review remains future work.

Publishing must re-check rights and approvals immediately before external action; expired or revoked grants block publication. Script/render changes invalidate dependent approvals where required. Preserve audit evidence. Do not remove attribution to disguise ownership or implement Content ID evasion. Transformative commentary is not assumed legal permission. Phase 1 imports/publishes no content and makes no copyright-free claim.


Selected-audio WAV artifacts remain bound to the original import/asset/source and selection. Extraction preserves import-specific source/rights metadata and leaves rights Unreviewed. Authenticated local artifact download is available for analysis; no publication authorization or review/revocation service is implied. See ADR 0009 and ../api/audio-extraction.md.

Scene-frame PNGs inherit the referenced analysis/import/source binding and preserve import-specific rights assertions. Sampling/downloading local inspection artifacts leaves rights Unreviewed and grants no publication permission. No cropping, ownership-mark removal or platform evasion is implemented; only fixed-size fitting/padding is performed. See ADR 0010.
