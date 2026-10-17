# Invisible desktop after process termination — 17 October 2026

The user reported that all desktop icons and desktop mouse interaction disappeared
after closing DesktopOrganizer. The process was already absent. The last application
log contained OutOfMemoryException, followed by a successful inventory read; there
was no graceful shutdown log. The termination mechanism is not established.

Read-only inspection found 65 Shell items, 52 still in collapsed parking, no mouse
capture, no ListView region, and our layered/color-key ListView lease still active.
Backups of journals, Fence JSON and current positions were saved under ignored `.tools/`.

Recovery restored the journal's original ListView properties after HWND/PID validation
and returned only 52 offscreen items to their recorded return coordinates. This was
insufficient: wallpaper was visible but all icons remained invisible. Hit-testing went
to Progman rather than ListView/DefView. The layered DefView had no readable layered
attributes. A documented IShellView.Refresh plus Shell SetRedraw(true) also did not
restore this native presentation. That experimental refresh path was not retained.

With explicit user permission and confirmation that file operations had finished,
Explorer was restarted once as emergency recovery, not as an application workaround.
All 65 identities and coordinates matched the pre-restart snapshot, without resetting
the layout. Icons were visible in the real screenshot. The user confirmed that both
icons and ordinary left/right desktop clicks worked again.

DesktopOrganizer was left stopped. Its autostart registration and persisted groups
were not modified. Do not claim this incident is prevented by the post-drop latency
optimization or that unit tests verify recovery of a damaged Explorer backing surface.

The diagnostic tools retain `--desktop-state` for read-only layer/region/hit-target
inspection and `--recover-desktop <profile-directory>` for journal-validated properties
and offscreen-return recovery. The latter refuses a running DesktopOrganizer or live
Fence surfaces and does not fix an already broken parent backing surface. Recovery
must be checked visually and with actual mouse input, not properties alone.
