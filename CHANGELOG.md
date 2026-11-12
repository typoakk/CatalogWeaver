# Changes

## 0.2.0

- Recover background discovery after database failures and report individual loop health.
- Journal owned image publications, preserve unavailable metadata fields, and read cards consistently.
- Bound preview work and run native image operations in disposable processes with deadlines and resource limits.
- Validate adapter semantics and literal JSON property paths; protect configuration updates with ETags.
- Add cursor pagination, individual refresh controls, shared value validation, and classified retry errors.
- Version queue/state migrations, provide reviewed export-reservation release, and batch file cleanup.
- Add JSON Schemas, generated OpenAPI, background recovery regression tests, private backup/restore tools, and version-tag image delivery.

Existing API clients updating configurations must send the ETag from GET in If-Match.
List endpoints retain JSON arrays; continuation is in X-Next-Cursor.
Stop old workers and back up service volumes before migrating. Target application schemas are never migrated.
