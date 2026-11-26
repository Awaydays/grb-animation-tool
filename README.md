# GRB Animation Tool

A Windows program for viewing, exporting and replacing the animation clips of Tom Clancy's Ghost Recon Breakpoint.

Version 1.1.0. The release download is a self-contained Windows program (no .NET install needed): unzip it anywhere and start
`GrbAnimTool.exe`. Its source code is published beside it as a zip of the same commit.

## What it needs

- 64-bit Windows 10 or 11;
- Ghost Recon Breakpoint installed on this PC (today's PC version). The tool reads your own game files, read-only, and uses the game's
  own Oodle library from your game folder.

## Quick start

1. Start `GrbAnimTool.exe`, press **Choose game folder...** and pick the folder that holds `GRB.exe`.
2. **Library:** pick a clip to see it play. To export:
   - Clip .glb: a game animation with the game's skeleton. Start from this to change an existing animation.
   - Skeleton .glb: the bare rig with no animation. Start from this to animate from scratch.
3. **Import:** put your .glb on a clip. Your animation must:
   - use the game skeleton (start from a Clip or Skeleton .glb)
   - be at 60 fps (set this in Blender BEFORE importing the .glb)
   - match the clip's length, or use Fit

   Scaling an animation to fit makes it play faster or slower.
4. **Edit > Replace:** puts another game animation, or your .glb, on this clip. The clip keeps its own events, so emotes still end and
   give back control. Works best for emotes, idles and poses. Moving, aiming and weapon animations may not line up with the game's own
   timing.
   - **Fit**, when the lengths differ. Cut: uses part of the animation (only when it's longer than the clip). The suggested start is
     where the loop closes best. Speed: stretches or squeezes it to fit. It will play faster or slower. Hold: plays it at normal speed,
     then keeps the last pose until the clip's own length is over. You get control back only then.
   - **Seamless loop:** blends the end back into the start so a looping animation doesn't jump. Use it for idles, not for emotes or
     one-off moves.
   - **Swap** is only for two clips with the same length and tracks. For anything else, use Replace.
   - Trim or Speed refused: this clip has timing data the tool can't safely change. Replace and Import still work.
5. **Package:** Build makes a folder with your changed files (named 1_-_...) and a folder with the files they replace. Place the files
   like any other animation mod. To undo, delete the 1_-_ files and put the originals back.

**Modded files.** When the animation datafile is unpacked beside your game's `DataPC_patch_01.forge` (in its `Extracted` folder), the
tool also lists the record files there. A file that replaces the game's own version (it's named 1_-_...) is marked Modded. Both are
listed and both can be edited. **Modded only** shows every file that isn't the game's own: Modded (a separate 1_-_ file) and Replaced
(changed inside the game's files).

Every screen's **?** shows its own lines of the above; **Help > Quick start** shows the Import and Replace ones.

## The GRB Clip Logger

The **Log** screen shows which clips the game started, from a log of the GRB Clip Logger, so you can find the clip you just saw in game.
The logger is an optional, separate download from the same releases: a Cheat Engine table (its own `README.txt` says how to use it; its
files are in this repository's `clip-logger` folder). The tool doesn't need it, nor Cheat Engine.

## What the tool never does

- It never writes into your game folder, the folders of the forges it reads, an unpacked datafile's folder or a package of its own:
  every output goes into a folder you pick, and such a folder is refused by whatever path it is given.
- It never ships or copies any of the game's files, or the game's Oodle library: it loads that library from your game folder only
  when its SHA-256 is one the tool knows. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The tool keeps its own settings, caches, error log and package list in `%APPDATA%\GrbAnimTool\`.

## For developers

### Building

    dotnet build GrbAnimTool.slnx -c Release
    dotnet test GrbAnimTool.slnx -c Release --filter "Category!=Slow"

Building needs Windows and the .NET 10 SDK; a build you make yourself runs on the .NET 10 Desktop runtime.

### Layout

- `src/GrbAnimTool.Core`: the core library. It reads the install read-only: the forges, the animation datafile and every clip in it,
  the shared curve table, the body skeleton with the fake-gun add-on, and poses a clip on the skeleton at any time.
  Its `Graph` folder reads the game's animation graph documents (LZO1X streams in the same datafile) for which nodes name each clip,
  whether one holds the clip's length (no trim or speed then) and whether a motion field uses it (no mirror or root-motion scale then).
  Only the places where a clip's ID stands (and a motion field's segments, which name its clip list entry by index) count as holding the
  clip's length; a node that reaches a clip node by a link (a state's variable table, a blend's child, a pick, a switch) isn't read and
  is taken as holding none. A clip the read didn't look for, or a graph that didn't read whole, allows none of these edits; nor does a
  clip that GR_Graph's clip list names but no graph node the tool reads does, since its use can't be checked. A blend child's nominal,
  which for a move-speed blend is the clip's root speed and for a heading blend follows its heading, isn't read: on a clip under a blend's
  child, speed, root-motion scale, mirror, swaps and import stay allowed with a warning that the blend may then pick it wrongly. A clip
  counts as under a blend's child when the child is its clip node's parent or stands up to two levels above that parent (a pick or a
  switch between), by the order of the objects; a blend further up isn't seen.
  Its `Library` folder holds the screens' logic that needs no window: clip search and filters, the play head, a clip's facts, the
  mover's path, its events and foot plants, the trim's handles, the swap sources, which edits a clip can take and the checks on an
  edit's result, the command line and the remembered settings. Its `Package` folder plans and builds a package (the record files and
  the game's own records of the same clips, for uninstalling) from the clips kept. Its `Export` folder writes the skeleton, alone or with a clip, as a
  glTF binary file (`.glb`) with a sidecar for what glTF can't hold, and reads such files back for the export's own control. Its
  `Writing` folder writes curves in the game's forms and rebuilds a clip's record around new curves; its `Edits` and `Retiming` folders
  hold the edits (mirror through the body's middle plane in the clip's own frame, where x is side to side, root-motion scale with a
  foot lock, swap, trim and speed) and their census, each giving a rebuilt
  record or a refusal that says why (a length change, so trim and speed too, is refused on a clip whose byte array before the curves
  holds lists of times, which the tool doesn't adjust yet); its `Records` folder writes record files, the form players install a
  replaced clip in, only ever into a folder you pick outside the game folder, all of them or none. Its `Import` folder reads a
  `.glb` made on the game's skeleton and writes it into the clip it replaces; its `Checks` folder holds the checks every
  new motion goes through (feet in the floor, feet off the floor, joint limits with the knees and elbows held to their hinge's plane,
  twists about a bone's own axis and along the neck and the spine, length, bone match,
  speed by body region, pops, the mover's turn, limbs as segments, the legs against each other among them, read-back). Its `Logs`
  folder reads the clip logs of the optional GRB Clip Logger (above) as they grow, and finds the newest in a folder.
- `src/GrbAnimTool.App`: `GrbAnimTool.exe`, the program itself (WPF). Its window has five screens: Library, Edit, Import, Package and
  Log.
- `clip-logger`: the GRB Clip Logger, an optional table for Cheat Engine that logs which clips the game starts, released as a separate
  download (see [The GRB Clip Logger](#the-grb-clip-logger)).
- `src/GrbAnimTool.Cli`: `grbanim-cli`, a small command-line harness for tests and automation (`check`, `list`, `soak`, `hash-table`,
  `graph`, `index`, `export`, `export-skeleton`, `pose`, `write`, `rebuild-check`, `import`, `replace`, `compare`, `census-edits`, `edit`). Players never
  need it. `list --search` matches as the search box does (typos forgiven) and lists the best matches first.
- `tests/GrbAnimTool.Tests`: the tests. Those that need game data read `GRBANIM_FORGE` (a vanilla `DataPC_patch_01.forge`) and
  `GRBANIM_GAME` (the game folder) and are skipped without them. The slow census of all four clip forges and the export's round trip of
  every clip are in category `Slow`. `GRBANIM_KNOWNGOOD` may name a folder of record files known to work in game: each replacement clip
  there must come out of the core the same, name and bytes. `GRBANIM_BLENDER` may name `blender.exe`, a third-party program used here
  only as one common glTF exporter to test against: the test then runs it headless (factory settings, its user and temporary folders in
  the test's own folder) to re-export two of the tool's exports with its default settings, and each must import back to the game's own
  record; without it that test is reported as skipped. `GRBANIM_CLIPLOGS` may name real clip logs (files, or folders of them,
  separated by `;`), read only: every Greetings start in them must be `mca_emote_Hello`, and their counts and the IDs that aren't the
  install's clips are written to the tests' output (where else in the forges each is found, in category `Slow`); without it those
  tests are reported as skipped. Every other log the tests read is made up, in the test's own folder.
- `tests/GrbAnimTool.App.Tests`: the window's theme as it is shown: the five screens laid out (no window shown) in the light and the
  dark theme, every text on them at least 4.5:1 against what is drawn under it, greyed-out and selected rows included, and the lists'
  rows kept compact. Also the window's responsiveness: every list realises only the rows in sight (under 200) on
  each screen, whichever is shown first and at either size, with 9,970 clips (made up for the test, and the game's own on the game's
  files); the Edit screen opens at once while
  the edits each clip allows are still worked out off the window's thread; and an error nobody caught is shown and logged while the
  window goes on. The Log screen follows a log as it is written (line by line and in parts of lines, a newer log, a log cut short or
  replaced), loads a log of 300,000 lines in the background with only the rows in sight realised, and a click on a line chooses its clip
  in the Library. With `GRBANIM_TIMING_LOG` naming a log file and `GRBANIM_GAME` the game folder, a timing probe drives the real
  screens on the game's files and writes how long each step takes there (without them it is reported as skipped).

### Automated checks

For automated checks the program also takes `--game <folder>`, `--forge <patchForge>`, `--clip <name or hex ID>`, `--time <s>`,
`--mover on|off`, `--screenshot <png>` (render the window to the file, then exit; exit code 0, or non-zero with the error on stderr;
a picture whose 3D view didn't draw, with no floor or no skeleton, isn't saved and fails the run)
and, any number of times, `--shot "<png>|<clip>|<time>|<on|off>"` for several pictures from one run; `--export-skeleton` and
`--export-clip <name or hex ID>` with `--export-out <folder>` make the Library screen's exports without the save dialog, before any
picture; `--import-glb <file> --import-clip <name or hex ID>` runs the Import screen's import (with `--import-out <folder>` its record
file exported there, with `--import-keep` added to the package), and the pictures then show the Import screen at each shot's time.
`--import-out` refuses a folder that already holds files of the same names, writing nothing; add `--overwrite` to replace them, as the
screen's **Replace files already there** does. An import or an edit that changes nothing fails `--import-out`, `--import-keep` and
`--edit-keep`, as the screen's greyed buttons do.
`--edit <name or hex ID> --edit-kind mirror|scale|speed|trim|swap` runs the Edit screen's edit (`scale` is root-motion scale; `--factor <f>` for scale and speed,
`--from <tick> --to <tick>` for trim, `--source <name or hex ID>` for swap; with `--edit-keep` the result goes into the package), and the
pictures then show the Edit screen. `--package-out <folder>` (with `--package-name <name>`) builds the package on the Package screen
without the folder dialog, after any import or edit kept (it waits for the clip index, as the screen does). A `--shot` may end in
`|library`, `|edit`, `|import` or `|package` to show that screen, so one run takes pictures of several (`|package-waiting` shows the
Package screen as it stands while Build waits for the clip index, the index held back for that picture alone, the shot's time part
standing for the index's progress from 0 to 1; `|edit-waiting` the Edit screen as it stands while the edits each clip allows are
worked out, and `|import-waiting` the Import screen while the clip index is read, the same way); a picture of the Edit screen with no
`--edit` in the run shows the shot's clip, or the run's `--clip`, chosen there as a click would (with neither, the screen and its
list as they stand, as a picture of the Import screen with no `--import-glb` does); on the Library it may go on with `|<search>|<who>|<what>`, the list's search text and
its category filters for that picture. A 9th part, after those three (left empty for the other screens), is `dark`, `light` or `system`:
the theme from that picture on, e.g. `--shot "edit.png||||edit||||dark"`. A 10th, `follows` or `own`, sets on the Edit and Import
screens whether the original follows the edit's progress or plays in its own time, from that picture on (never remembered), e.g.
`--shot "edit.png||0.8||edit||||dark|own"`; the shot's time is on the edited clip's timeline, the original at the same time from its
start, as a scrub puts it. A run that takes pictures waits for the clip index first; it keeps the index's cache only in the
folder `--index-cache <folder>` names (without it the index is built for the run and kept nowhere); in the same way such a run keeps
the package list between runs only in the folder `--package-store <folder>` names (without it, nowhere), and remembers the Package
screen's vanilla forge backup only in the settings file `--settings <file>` names (without it, nowhere). In a window too short for a
screen, its panes scroll: every control stays reachable and each list shows at least five rows. `--size <width>x<height>` sets
the window's size for the pictures (never below its least size, 860x520). A run with a picture of the Edit screen waits for every
clip's edit marks first (kept, like the index, only in the folder `--index-cache` names), and its time limit allows for working them
out with no cache (at least a minute, more on a machine with few cores); a run that pictures no Edit screen (or only exports, imports,
edits or builds) doesn't work them out at all. With the environment variable `GRBANIM_TIMING=1` such a run also prints how
long each screen took to show (its first frame and its first input handled), the edit marks and a clip chosen on the Edit screen. An
error nobody caught ends such a run with the error on stderr; one a screen caught itself (and the tool didn't foresee: anything but a
file that can't be read or written, data that isn't what it should be, or one of the tool's own refusals), clips the tool failed
on when the edit marks were worked out (whether worked out in this run or read back from the cache), and the machine running short of
memory while it worked them out, are written to stderr as they come, and the run then ends with a non-zero exit code, its pictures
taken or not.
`--theme dark|light|system` sets the
run's theme (an automated run doesn't read the remembered one; without it, it follows Windows). `--log-file <file.csv>` opens a log
on the Log screen, and `--log-folder <folder>` follows the newest log in a folder instead (an automated run follows nothing of itself:
not the Documents folder, and the folder a `--settings` file names is only put in place, unfollowed; without either option it shows no
log; a log named that can't be read, or a folder named that isn't there, can't be read or holds no log, fails the run); a `--shot`
ending in `|log` pictures the Log screen after the log's first read, its who part `Player` showing the player's starts only, e.g.
`--shot "log.png||||log||Player"` (its search and what parts are refused there). While taking pictures, exporting,
importing, editing or building this way the window stays off-screen and never takes the focus; as a capture there can't see the 3D
view, the Library, Edit and Import screens' pictures draw the skeletons flat through the same camera, and each picture's check is made
on the 3D view of the screen it shows (the Package and Log screens have none).

## Licence

Copyright (C) 2026 Awaydays. GPL-3.0-or-later: see [LICENSE](LICENSE). The program has an additional permission under section 7
of that licence for loading the Oodle library of your own game install: see [LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md).
The licence and the additional permission apply to every file of this project.
It comes with no warranty, to the extent the law allows: see sections 15 and 16 of the licence.
