# Product requirements and scope
Goal: authorized movies become source-grounded recap scripts, localized narration/subtitles, rendered videos, review and official-platform publication. Burmese is primary; English secondary. The supplied master prompt is the source specification.

| Requirements | Delivery phase |
|---|---|
| FR-001 import; FR-002 preprocessing | 2 |
| FR-003 movie understanding; FR-004 recap | 3 |
| FR-005 localization; FR-006 voice; FR-007 subtitles | 4 |
| FR-008 rendering | 5 |
| FR-009 rights tracking and approval | 2 data foundations, 7 publication gates |
| FR-010 official publishing; FR-011 Quartz scheduler | 7 |
| FR-012 administration dashboard | 6 |
| Shared job persistence, config, hosts, docs, tests | 1 |

Phase 1 acceptance: solution builds; domain/application boundaries; migrations apply to PostgreSQL; API can create/query/control jobs; worker claims and completes a foundation check; audit is persistent; duplicate claims/submissions are handled; retry/lease recovery is tested; local and Windows Service guides exist. Later features must not be represented as completed.

Complete-platform acceptance remains the prompt's authorized import → metadata/scenes → grounded recap → verified Burmese speech → synchronized subtitles → valid render → review → scheduling → permitted official publishing, with recovery, progress and accurate docs. Authorization is never inferred from editing style. Preserve original files; prohibit ownership concealment/evasion. Provider credentials and paid services are optional for local foundation development.

