# Rights metadata implemented; publication approval remains Phase 7 design
Publication design requires source/owner/license/usage/platform/attribution/expiry evidence and version-specific approval. Missing or unknown authorization does not qualify for automatic publication. A future review service must record who approves what version and why.

The Phase 2 manual-import slice now records source, requester, owner, caller-declared Unknown/Authorized/Denied status, license evidence, permitted uses/platforms, attribution and optional UTC expiry in immutable per-import records. Responses label them Unreviewed. Unknown/Denied/expired records are permitted for local import/inspection; Authorized requires evidence and usage/platform fields. Deduplicating bytes shares only the asset: another source's rights are never substituted. There is no independent reviewer, grant verification, revision approval or publication implementation yet.

Folder discovery creates only candidates and requires versioned operator source/rights submission before import. A changed file gets a new candidate; imports bind to the submitted metadata fingerprint, so an older rights assertion is not silently transferred to a replacement file. This is a technical source-binding check, not a legal rights review.

Stream preferences are per import, even when content inventory is shared, and source identity is verified before saving a choice. Selection/reinspection preserves each original rights assertion and does not authorize extraction or publication. Operator identity is caller-asserted under current shared-key access; role-based review remains future work.

Publishing must re-check rights and approvals immediately before external action; expired or revoked grants block publication. Script/render changes invalidate dependent approvals where required. Preserve audit evidence. Do not remove attribution to disguise ownership or implement Content ID evasion. Transformative commentary is not assumed legal permission. Phase 1 imports/publishes no content and makes no copyright-free claim.

