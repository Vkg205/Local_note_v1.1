# LocalNote V0.8.5 — Stability finalization

## Fixed

1. Settings read-modify-write race between MainWindow and MainViewModel.
2. Duplicate navigation loads during search/result page navigation.
3. Page paper style not persisted across page switch/restart.
4. Trash restore previously revived descendants that had been independently deleted.

## Schema

Schema version is now `5`. New columns:

- `pages.paper_style`
- `notebooks.deleted_by`
- `sections.deleted_by`
- `pages.deleted_by`

Existing vaults are migrated automatically at open.
