# Schema audit files

The SQL and database files in this directory are historical snapshots, not the current schema implementation. Their v9 replay predates the current production migrations and must not be used to validate or upgrade a running database.

`bash tools/audit-migration/migrate.sh` runs the production `SchemaUpgradeTests` against disposable SQLite databases. Direct database-path arguments are refused. These tests also run in the normal .NET suite and `tools/test-harnesses.ps1`.
