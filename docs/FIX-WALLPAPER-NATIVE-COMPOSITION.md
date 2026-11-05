# Wallpaper Engine / native Explorer composition

Local rollback before this change: `pre-wallpaper-composition-fix-20261017` (`94f254a`).
User data and actual positions were backed up under ignored `.tools/`.

## Cause and scope

The old presentation changed the Explorer-owned SysListView32 into a layered child
with LWA_COLORKEY (`0x030201`), disabled LVS_EX_TRANSPARENTBKGND and temporarily
disabled double buffering for marquee. On this Windows/Wallpaper Engine desktop,
SHELLDLL_DefView itself is layered and the live wallpaper is a separate sibling
WorkerW/DX11 view. The extra color-key backing was incompatible with this parent
composition: previous A/B runs produced a black wallpaper sample with the key-only
layer; subsequent damage updates could leave rectangular black areas.

The concrete problematic change was in ExplorerIconLayer.ApplyFenceProperties, not
the user's wallpaper settings or FenceAppearance colors. The exact private Windows
compositor mechanism and the earlier process termination/OOM are not established.

## Rendering after the fix

Bottom to top: existing wallpaper → own layered Fence body → native transparent
Explorer ListView → own layered header/border. The two owned HWNDs per Fence stay
stable. No icon copies, body cutouts, parent alpha changes or new overlays.

ListView uses CLR_NONE, LVS_EX_TRANSPARENTBKGND and LVS_EX_DOUBLEBUFFER. This keeps
the documented parent background painting via WM_PRINTCLIENT and native alpha
marquee. On the tested desktop those are already Explorer's original properties,
so startup/observation/exit perform no property rewrite or composition repaint.
The application no longer adds WS_EX_LAYERED or a color key to Explorer's ListView,
and selection no longer changes its double buffering/backing surface.

The background mouse forwarding fallback is removed from the live notification
service. Input goes straight to Explorer; existing raw input/WinEvent notifications
only observe gestures. There is no production low-level mouse hook or synthetic
background DOWN/UP delivery. Header/grips still receive native Fence input.

Old presentation journals can still restore the old owned color key after matching
HWND/PID checks. Restoration preserves unrelated styles and skips unchanged native
properties; another layered renderer is not overwritten. Fence/icon persistence,
parking, animation clock, autostart and tray behavior are unchanged.

## Verification

- Debug/Release: 0 warnings/errors, 360 tests in each configuration.
- Native properties: no added layered/key backing, transparent/double-buffered styles,
  idempotent restoration, foreign renderer protection and legacy journal compatibility.
- Real A/B screenshot: strongly tinted 70% body visible beneath native ListView;
  output matched the old full-key appearance, without needing its color key.
- Real icon screenshot: actual icons/text above the translucent body, no label cutouts.
- Explorer stability smoke: 0% dark pixels before surfaces, after surfaces/new desktop
  item/ordinary move/exit; deletion/compaction/rename and parking isolation passed.
- Icons smoke: real group assignment, movement, resize, collapse/expand, restart,
  drop-out and graceful exit passed; all 65 initial user positions restored.
- Input hit smoke: empty/content areas hit SysListView32, header/grips hit owned HWND;
  8 native ListView raises preserved stacking without extra repaint.
- Rendering stress: 1/5/12 icons at 0.15/0.25/0.4 s, 66 hide/show presentations,
  60 steps each of populated/empty drag and resize, restoration and restart passed.
  Stable HWNDs, unchanged Explorer region, all 65 initial positions restored.
- Native composition lease: original/idle/selection/exit ListView states identical;
  selection produced zero extra redraws or Fence bitmap presentations.

These are real Shell/Win32 API checks, not physical mouse/OLE acceptance. The updated
stability smoke also compares parent layered attributes and samples a full-width
wallpaper strip to detect partial black damage. User verification is recorded below.

The user subsequently confirmed the physical checklist presented in chat: native
marquee, ordinary icon drag, repeated Fence collapse/expand and tray Exit all worked;
wallpaper stayed normal, and icons plus left/right desktop clicks remained usable
after Exit. Multi-icon OLE drag was not part of that particular confirmation.

## Manual checklist

1. Draw a large native marquee, including beside/through Fence content.
2. Move ordinary single/multiple icons; move a group into/out of a Fence.
3. Drag/resize Fence quickly; collapse/expand manually and on hover.
4. Create a temporary desktop file and remove only that test file.
5. Exit through tray: wallpaper/icons and normal left/right clicks remain usable.
6. Restart: assignments and appearance restored. Observe over a longer session.

An hours-long run, another GPU/Windows configuration and the earlier OOM termination
are not verified by these short checks. Explorer was not restarted for this fix.

References: [ListView styles](https://learn.microsoft.com/en-us/windows/win32/controls/extended-list-view-styles),
[Layered windows and hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features).
