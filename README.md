# GRB Anim Tool

A Windows program for viewing, exporting and replacing the animation clips of Tom Clancy's Ghost Recon Breakpoint.

Version 1.0.0. The release download is a self-contained Windows program (no .NET install needed): unzip it anywhere and start
`GrbAnimTool.exe`. Its source code is published beside it as a zip of the same commit.

What it needs:

- 64-bit Windows 10 or 11;

## Running the viewer

Start `GrbAnimTool.exe` (a build of your own is at `src/GrbAnimTool.App/bin/Release/net10.0-windows/`). The first time, press **Choose game folder...**
and pick your Ghost Recon Breakpoint folder, the one that holds `GRB.exe`. The tool checks the game's Oodle library and its forges, opens
the animation datafile in the background (a second or so) and remembers the folder for next time
(`%APPDATA%\GrbAnimTool\settings.json`, which also holds the theme, the Package screen's vanilla forge backup, the previews'
"Original follows the edit's progress" and the folder the Log screen follows; beside it, `index\`
holds the caches, the clip index's and the Edit screen's marks of which edits each clip allows, and `package\` the package list kept
between runs, both below).

**Settings > Theme** (top right) sets the window **Light**, **Dark** or **Follow Windows**. The 3D view stays dark in both.
Under Windows' high contrast the window takes Windows' contrast colours.

The Library screen shows:

- a summary of the install: how many clips, the folder name the clips' record files are installed under, and how many clips are
  Vanilla (the game's own), Replaced or Added;
- every clip in a list you can search (words of the name, or the hex ID; see below), filter (status, family, length, who, what) and
  sort by any column. Its columns are the clip's name in the game's files, its readable name, Who and What (see the clip index below),
  then its index, length, ticks, tracks and status. The Edit and Import screens show the install's clips in the same list, with the
  same columns, search and filters.
- the selected clip on a bare skeleton over a floor grid: drag to orbit, pan and zoom, reset the camera, or let it follow the character.
  With **Mover** on the clip travels and turns as it does in the game and its path shows on the floor; off, it plays in place;
- a timeline: play and pause (Space), step a tick (Left and Right arrows; the keys work once you have clicked the viewport or the timeline,
  not while typing in a box or moving through the list), scrub, loop and speed. Over it a strip marks the clip's events at their times
  (orange; hover one for its class, type and time) and the spans its plant tracks hold each foot planted (the left foot's bar over the
  right's). In the viewport a planted foot's joints get a cyan ring, and a foot joint more than 2 cm below the floor a red one;
- the clip's facts: ID, index, length, ticks, its rotation, position and other tracks, whether it has a mover, its status, and from the
  clip index its readable name, who and what with each category's source, and the graph documents that use it. A clip picked is read
  in the background: its name and length show at once, its track counts and the skeleton a moment later.
  A clip that can't be played (its record doesn't read, its length is out of range or shorter than one tick, or its curves don't read
  to the record's end) shows its facts and why.

The search box takes words of a clip's name in any order, each whole or just its start or a part of it, in any case: `hello emote`
and `emo hel` both find `mca_emote_Hello`, and every word typed must match. Underscores, dashes, a capital inside a name (`IsExhausted`)
and a change between letters and digits split a name into words. Words typed together match together, next to each other in the name:
`V1` finds the names holding V1 (not a V0 name ending in `_01`), `STDInj` those holding `STDInj` or `Std_Inj`. A single letter or digit
matches only a whole word or the start of one. A word of four letters or more that no clip holds as a whole word is also matched with
a small typo forgiven: one letter wrong, missing, extra or two swapped (two such for a word of eight letters or more), so `deth head back`
and `trun left` find what they mean, while `head`, a word as typed, never brings in `heal` or `dead`. When nothing matches at all, every
word is tried with typos. The best matches come first: whole words, then starts of words, then parts of words, then typos (a word
of under eight letters that no clip holds whole and that only runs across a name's words, as `deth` in `grenade_throw`, comes after
its typos; a longer one, as `verylowhealth`, is a name's words typed together and comes before them), then shorter names. A click
on a column header sorts by that column instead (a third click goes back to best match first); the filters always apply.
Text starting with `0x`, or of eight or more hex digits, finds a clip by its hex ID, whole or in part. Accents and full-width letters
read as plain letters, and a long paste is searched by its first 32 words. After each search, filter or sort the list shows the
selected clip when it is still in the list, and its first row otherwise. The Length filter takes the shortest and the longest length in
seconds (either may stay empty); a box holding something that isn't a number gets a red border and is left out. The Import and Edit
screens' lists are the same list: they search, filter and sort the same way.

### The clip index

When an install opens, the tool builds its clip index in the background, with a progress bar in the window; the list works meanwhile
and fills in when it is ready. For each clip it says who plays it (Player, Teammates for the AI teammates, Enemy, NPC for the other
non-player characters, civilians and allies, Animal, Vehicle, Cinematic) and what it is (locomotion, idle, weapon handling, emote,
death, hit reaction, injury and downed, cover, vehicle, interaction, traversal, swimming, melee and takedown, carry and drag, face and
dialogue, pose, menu and lobby); a clip can have several of each. **Menu and lobby** marks the clips of the menus, the lobby and the
squad screen, beside what else the clip is: from the game's graph, the clips no layer played in the world names, only the layer of the
character the menus show (`Avatar-LayerGraph`), so the menus play them if the game plays them at all (37 on today's files: the lobby
poses such as `Lobby_Front_AR_02`, the character screen's idles, draws and stows, two emotes and three whole launcher reloads); and
from the tool's list the squad's end-screen victory poses (4). The graph doesn't tell the lobby, the squad screen, the character screen
or a photo pose apart: that layer names its clips without machines of their own for each screen. The categories come
from three sources, best first: the game's animation graph (which state machines and layers use the clip), a list shipped with the tool,
and the abbreviations in the clip's name. For each kind the best source that places the clip decides; a clip none places is
**Uncategorised**, never guessed. The clip info shows each category with its source, `from the game's graph`, `from the tool's list` or
`from the name` (hover one for the reason), and the graph documents that use the clip. A Who of the game's graph also names the part
of the graph that decided it, so an odd-looking one can be judged at once: the layer, in plain words with its root's name (the
player's layer `MCH-LayerGraph`, the menus' character's layer `Avatar-LayerGraph`, the AI teammates' layer `SPE_TM8-LayerGraph`, the
generic NPC layer `GEN_NPC-LayerGraph`, with its Military, Civilian or Sam Fisher machine where one narrowed it), for example `Player,
from the game's graph: the menus' character's layer (Avatar-LayerGraph)` for a lobby pose, or `Player, from the game's graph: the
player's layer (MCH-LayerGraph); the menus' character's layer (Avatar-LayerGraph)` for an emote the wheel and the menus both play (the
layers played in the world first). A layer that reaches the clip only through machines several layers share says `by a shared
machine`, and kinds with the same part share a line: `probably Enemy and NPC, from the game's graph: the generic NPC layer
(GEN_NPC-LayerGraph), by a shared machine`. Readable names (`Holstered walk, standing`) come
from the tool's list only. The Library, Import and Edit lists show them in the Readable name, Who and What columns, beside the name; their **Who** and
**What** filters go together and with the others (Who Enemy and What Death: the enemies' deaths); and the search also looks in the
readable names, the categories and what the names' abbreviations stand for (the short expansion or the list's own search words:
`soldiers` for `mil`, `draw` for `get`, `put away` for `stow`, `left shoulder` for `LHS`, `north-east` for `NE`; never the longer note
on how the game uses it), so `greting` finds the greeting clips, `enemy death` the enemies' deaths and `holstered walk` the holstered
walk. The list's own search words of several words are a phrase: its last word counts alone or beside any other word (`shoulder`,
`shoulder aim`, `crawl idle` for the vent crawl's idle), the words before it only together with it (`left shoulder`, or typed together,
`leftshoulder`, `walk northeast`). So `left` finds the moves to the left, not every clip with the weapon on the left shoulder (the
weapon's shoulder is no direction), and `right shoulder` brings in no left-shoulder clip whose name says right (a side word typed only
so far, `shoulder ri` on the way to `shoulder rifle`, keeps both shoulders' clips). The compass is a case of its own, as each of its
words is a direction: a single compass word, alone or beside another word, finds the straight and the diagonal moves that hold it,
`east` and `walk east` those to the right (`E`, `NE`, `SE`), `north` and `walk north` those ahead (`NE`, `NW`; the names' straight
`N` and `S` aren't settled, so besides them only the clips whose own words say north). Two compass words find their diagonal only:
`north east`, `north-east` or `northeast` the moves ahead and to the right, `south east` no `NE` clip. Where a grab machine of the graph uses a clip whose
name says carry (taking a body out of a vehicle, aiming while carrying), the name adds carry and drag beside the graph's melee and
takedown, and vehicle for a carry out of a vehicle. In those texts two neighbouring words typed together match when they make eight
letters or more (`holsteredwalk`), and so does an English word's plain ending (`walking`, `walked` or `walks` for walk; a dropped e
only for a word of four letters or more, so `thing` isn't read as `the`). A word's weight differs by where it is found: the clip's name
and its readable name count most, an abbreviation's expansion a little less, a category least, so a clip whose own name holds the word
comes before one only its category names; a word found in more than one of these adds a little, a category most, so among the clips
whose names say `death` the deaths come first. The graph's part is kept in `%APPDATA%\GrbAnimTool\index\`, one file per patch forge,
and built again when the install's files or the way it reads the graph change (the rules are applied anew on every open).

#### Who plays a clip, and so who a replacement changes

A replaced clip changes for every character that plays it. The game's NPC machine gives the AI teammates a layer of their own
(`SPE_TM8-LayerGraph`) with its own copies of the NPCs' death, hit, cover, weapon, traversal and locomotion machines, so the graph
tells them apart from the other NPCs: a clip a document of the teammates' layer names is **Teammates**, from the game's graph (their
death and hit machines name 238 clips; their locomotion layer 41, all 30 rifleman light hits among them), and a clip of their family
`tm8_` is Teammates by the name. **NPC** is the other non-player characters, civilians and allies: a clip is NPC only where a document
of the generic NPC layer, or a rule for those characters, places it. The other rules: the generic NPC layer's own documents
and its Military machine give Enemy and NPC; the families `pmc_`, `npc_` and `mil_` give Enemy and NPC (by the name alone never
Teammates); civilians are never Enemy; where a machine several layers share (vehicles, grabs, swimming, base jumps, custom faces, lip
sync and others) names a clip, its who is the union of those layers' characters, narrowed by the name's family only where the family
has evidence there (`npc_`, `mil_` and `pmc_`, the non-player characters at large, keep the teammates' share of such a machine); and a
strong rule of the graph wins per kind, a container's weak rule only filling a kind nothing else places.

The clip info's **Played by** line, the Edit and Import screens beside the clip being replaced, and the Package screen's list (under
each clip's name) say each kind of character that plays the clip, the sure ones first and the probable ones in a clause of their own,
for example `Player; probably also Teammates and Enemy`, or `probably Enemy and NPC` when none is sure. **Probably** marks a kind that
only the clip's name gives (its family, or its family's pick among the characters of a machine several layers share), or only such a
shared machine gives (the graph doesn't say which of them plays which of its states); the others come from a document of one layer
alone, or from the tool's list. The clip info's Who lines follow the same rule: a kind the name decided reads `probably ... (from the
name)`, and a kind a document of one layer alone gives stays `from the game's graph`, sure, even where the name's family took another
kind away. One exception: the heavies' (`pmc_hvy_`) and the civilians' (`civ_`) families rule the teammates out (a teammate is never a
hostile heavy nor a civilian), so where the teammates' own copy of a machine names such a clip (19 heavy deaths, 16 civilian dodges),
the teammates play it only probably. Those screens show a notice before anything is built (it never stops anything): when more than one
kind plays a clip, `More than one kind of character plays this clip: ... Replacing it changes it for all of them.`; when one kind plays
it only probably, that the tool isn't sure and others may play it too; for a clip whose who is Uncategorised, that the tool can't tell
who else a replacement changes; and while the clip index isn't ready, or when it couldn't be built (with the reason), that who plays
the clip isn't known. Nothing is written before who plays each clip is shown: while the clip index is being read, the Package screen's
Build is greyed with the index's progress beside it (`Waiting for the clip index: ...`) and opens once the notice says who plays each
clip, and the Import screen's **Export record file** is greyed the same way, the progress under it, and opens once **Played by** and
the notice say who plays the clip. When the index couldn't be built, Build and
the export go on (they never refuse because of who plays a clip), the notice saying why. "Probably" stands for: from the clip's name,
from a machine several kinds of character share, or where the name's family says the graph's user is unlikely; the screens' **Played
by** says so on hover. The Who filters and columns have
Teammates, and the search finds them by the word (`teammates`).

## Exporting a clip

To make a clip of your own: export a clip and the skeleton as `.glb`, edit or animate it in any tool that reads and writes glTF on
the same skeleton, then import the `.glb` (below). How you make the animation is up to you; the tool converts and replaces clips.

On the Library screen, **Skeleton .glb...** saves the skeleton (the body and the fake-gun add-on, 75 bones, at rest) as soon as an install
is open, and **Clip .glb...** saves the clip shown. A clip's file holds the skeleton and one animation: every bone the clip has a track
for, sampled at every tick, 60 frames a second, with the mover (the clip's root motion) on the armature itself, so the armature travels and
turns as the character does. Beside it goes `<name>.grbanim.json`: the clip's ID and track list, and everything glTF can't hold (the
helper and IK tracks, the hand weights, the foot plants, the events' times), for importing the clip back later. After writing, the tool
reads the file back and compares it with the clip at every tick; the result shows under the buttons.

The game's axes are x the character's left-right, y forward, z up, in metres; the file turns them for glTF (y up). The skeleton file is
the skeleton's bind pose as the game stores it: the hips at the origin and the legs through the floor (a clip supplies the hips'
height). The keys are at 60 frames a second, so frame n is the game's tick n, from frame 0 to the clip's last tick (shown under Ticks on
the Library screen). glTF holds no frame range of its own, so a tool that keeps one may need it set to the clip's to play exactly the clip.

## Importing a clip

The **Import** screen takes a `.glb` made on the game's skeleton and writes it into the clip it replaces. What it reads: a glTF binary
(`.glb`, not `.gltf` text) whose bones keep the skeleton's names and rest pose under one armature, standing upright with glTF's own axes (y
up; a turn about the vertical is fine), unscaled, with one animation lasting exactly the clip's length (keys between ticks are sampled,
with a warning), its keys as plain floats and translation, rotation and scale rather than matrices, and no compression or quantization
extension. Choose the
file, choose the clip (the list, the Library's own with its search and filters, offers the clips of the file's length while **The
file's length** is ticked, as an import must be exactly its clip's length; a file named after a clip, as the tool's exports are, selects
that clip), and the tool runs
the import and its checks. The game's clip and the import (green) play on one timeline, **Overlay** at first: the game's clip drawn
pale, see-through and a little thinner behind the import, so exactly the bones the import changed stand apart; **Side by side** (the
box over the view) puts the game's clip (blue) on the left and the import on the right, 1.1 m apart. The choice holds while the
program runs. The game's clip plays in its own time as the Edit screen's original does, or at the import's progress with **Game's clip
follows the import's progress** (the same choice as the Edit screen's); an import is exactly its clip's length, so both show the same tick. The import runs as soon as the file and the clip are chosen; **Add to package** (the main button) puts it in the
package list, and until it is added the screen says it isn't in the package yet. The Checks panel lists
every check as pass, warn or fail (failures first, then warnings; the report written with the record file keeps the checks' own order):
the bones against the game's skeleton, the length, feet in the floor and a body floating above it (over
the whole clip, or for a stretch where the game's feet are down), joint limits (an elbow bent backwards; a knee or an elbow bent
sideways, out of its hinge's plane, further than the clip's own by 15 degrees for a knee and 30 for an elbow, failing at 30 and 45; an
ankle rolled, 15 and 30), twists about a
bone's own axis (a head turned round; allowed up to the wider of the clip's own range and a human range per bone, such as 80 degrees
for the head and 90 for a forearm's roll, and per chain: 90 for the neck and head together, 60 for the spine, 120 from the hips to the
head; warn 15 degrees past, fail 45), speed against the clip's own region by region, pops (sudden jumps), the mover's turn (its heading
changing in one tick by more than twice the clip's own largest step plus 10 degrees warns, three times plus 30 fails; turned more than
180 degrees away from the clip's own turn warns), limbs (limbs passing through each other; the legs against the arms or the chest:
nearer than 5 cm warns, under 1 cm fails; the legs against each other: 5 cm nearer than the clip's own warns, under 2 cm fails),
the feet during the clip's foot plants (a toe moved more than 3 cm from the game clip's while a plant holds it warns), read-back (the written record read back), hands on a clip that aims them (a hand moved in a
clip that aims the hands by its own tracks), and a clip a
motion field picks or a blend chooses by its root speed or heading. The checks are an aid that catches common mistakes, not a proof
that the motion plays right in game. **Add to package** holds the result for the Package screen;
**Export record file...** writes it, with its check report (`<index>_-_<name>.checks.txt`, every check and its warnings), into a folder
you pick (never the game's), both or neither; files of the same names already there are replaced only with **Replace files already
there** ticked. An import that changes nothing is neither added nor exported: both buttons are greyed, with the reason under them. That
is the game's own clip unchanged (as an export imported back untouched gives), or a clip whose record is exactly what your install
already holds for it (perhaps your own package's clip, installed in the game from an earlier build; where the tool's table of the game's
clips knows that clip, the reason also says it isn't the game's own clip unchanged): the tool can't tell that from what's installed, so
import it on game files that don't hold it (verify the game files or put your forge backup back first).

A clip shorter than 255 ticks is written with a key on every tick. A longer one can't hold a key a tick (a curve holds at most 255 keys),
so each changed bone is keyed every few ticks (the report's keying line says how many), or, where keys that far apart would miss its
motion, with up to 255 keys placed where the motion needs them; between keys the motion is joined straight. Only the tracks the file
changes are written: a track it leaves as the game had it keeps its bytes, so a long clip exported and imported back untouched is the
game's record byte for byte. The read-back check then says how close the record comes to the file: a WARN or a FAIL there means a
track the file changed moves too fast or too busily to be held in 255 keys, and its line names the tracks written.

Everything that isn't a bone's rotation or position keeps its bytes: the clip's ID, length, track list and events, the hand weights and
offsets, helpers, foot plants. A file whose bones aren't the game's, whose axes aren't turned for glTF, or whose length isn't the clip's
is refused with what to change.

## Editing a clip

The **Edit** screen edits a clip of the game in place: the Library's clip (it takes the one shown there when you open it, or press
**Use the Library's clip**), or one picked in its list, the Library's own list with the same columns, search and filters. Which edits
each clip allows is worked out in the background, never on the window's thread: the first time an install's files are opened this
tries the edits on every clip (about a quarter of a minute on today's files, on all but one of the processor's cores, below the normal
priority so the window and the clip you pick go first), with a bar and its share beside **Show all clips** (`working out the edits,
40%`) and in the list's count; the screen works meanwhile, its list showing every clip, its **Edits** column empty and **Show all
clips** greyed, waiting. The result is kept in the caches' folder (`index\edits-<hash>.json`, one file per patch forge, beside the
clip index's) and read back at once on the next start; it is worked out again when the install's files change (the patch forge, its
animation datafile's table, the base forge or the skeletons) or another build of the tool works edits out (the cache is keyed by the
tool's own build), and when the file is damaged. Marks worked out while something they need couldn't be read (the game's graph, the
swap sources, the skeleton's mirror, which may read the next time), or while the machine ran short of memory (the error then in the
error log), are shown but never kept, so the next start works them out again: the line beside the switch says they aren't whole and why (`the game's animation graph
couldn't be read: <the error>`, also on the edits the graph decides and in the error log), and **Retry** works them out again from the
start (those reads made again, the cache passed over; hovering **Show all clips** then says they were worked out again on request);
while it runs the line and the count say so and the list stays as it is, and a retry that fails keeps the marks had
before. A clip the tool fails on is listed, never left out as allowing no edit: its Edits column stays empty, the line beside the
switch says how many such clips there are, and the error log has the detail. Such marks are kept all the same, the clips not told
among them, as the same files and the same build of the tool fail the same way; read back from the cache, the error log names those
clips again (once a run). Hover **Show all clips** to see how they were had this time. A clip you click just as
they arrive is still taken, and the clip shown (one taken from the Library too) stays in the list, selected, even when it allows no edit. From then on the list
shows only the clips that allow at least one edit (the count says how many it leaves out: 1,070 of 9,938 on today's files), and its
**Edits** column marks the ones each allows, `M` mirror, `R` root-motion scale, `S` speed, `T` trim, `W` swap, a dot for each it
doesn't (`· · S T W`). A clip you pick is tried at every setting when you pick it, in the background (a fraction of a second; the first
one right after the install opens waits for the game's graph to be read, under a second), and its edits show as soon as that is done.
A clip allows speed, root-motion scale or trim when any step of its slider, or any trim tried, works. Tick **Show all clips** to list
every clip again; one that allows no edit shows the reason under each greyed edit. Should the tool fail to work them out at all, the
list says so, lists every clip and offers **Retry**; a clip the tool fails on in a way it doesn't foresee says, under each edit, that
whether it takes it can't be told (the detail on hover), and the other clips aren't held up by it. It offers five edits:

- **Mirror**: the clip mirrored through the body's middle, left for right, its travel and turn the other way, its foot plants swapped
  side for side;
- **Root-motion scale** (0.25 to 2, in steps of 0.05): how far the character travels, its planted feet held where they stood and its
  foot plants' points (which stand in the mover's frame where each plant starts) kept on those feet. Like the speed below, every step
  is tried on the clip chosen, and the slider spans and snaps to the ones that work;
- **Speed** (0.5 to 2, in steps of 0.05): the clip played faster or slower, its events and plants with it. Not every speed can be
  written on every clip: the tool tries each step of the slider on the clip when you choose it, and where some don't work the slider
  spans and snaps to the ones that do, with what works said under it (`This clip's speed can be set from 1.05x to 1.95x.`, and what
  goes wrong at the others; the detail on hover). On today's files, of the 1,563 clips that neither the game's graph nor timing data
  the tool doesn't adjust yet keeps at their speed, 1,386 take every step, 129 some and 48 none;
- **Trim**: the clip cut to a part of it; drag the two handles of the trim strip, the viewer's width, over the events strip. When you
  choose a clip the tool tries a grid of trims (each end kept, or cut by a tenth, a quarter or a half; on a clip of a few ticks, its trims
  of one tick), and where some don't work it says which do under the trim: `Of the trims tried, trims that keep the clip's start can be
  made. The others can't: ...`. The handles stay free, as a trim the grid didn't try may still work. Once the handles stop, the tool
  tries that exact trim; one it can't write says why under the trim, and Apply waits for a trim that works. On today's files 1,492 clips
  allow a trim, 102 of them only some trims (82 only those keeping the start);
- **Swap**: another clip's motion in this clip's place. Only clips of the same length with the identical track list (every track's
  hash and kind) are offered as the source.

An edit's own controls (its slider and value, the swap's source) are usable while the clip allows the edit, and moving one chooses the
edit. An edit the clip can't take is greyed whole, its slider drawn greyed and its value a dash, with the reason written under it in plain words: what would go wrong in the game, or what the
tool can't do yet, for example `Can't mirror: this clip aims the hands at the weapon, and a mirror would leave them reaching to the old
side.` or `Can't change the length yet: this clip has timing data the tool doesn't adjust yet, and it would fall out of step with the
motion.` Hover the reason for the technical detail (the tracks, the graph's place, the kind of stored data). The reasons come from the
animation graph (the game picks the clip by its motion, a motion field, so no mirror or scale; the graph keeps the clip's length, so no
trim or speed; or the graph couldn't be read), the clip itself (it aims the hands at the weapon, so no mirror; it moves no bones, so no
mirror; a part of it holds more keyframes than the tool rewrites yet, so no mirror; it doesn't travel, so no scale), no swap source, or
the refusal of a trial run of the edit. A refusal at **Apply** is worded the same way, its detail on hover. The command line's `graph
--clip` and `edit` print both.
**Apply** runs the edit in the background; the result then plays with the original as a ghost, pale and see-through, beside or behind
it. The original plays in its own time: in real time at 1x, looping at its own length, whatever the result's length (with Loop off it
stops where the result stopped, its last tick when both end together), so a
speed change or a trim shows as a difference in timing (at 1.25x the result ends each loop sooner than the original; a trim loops sooner
while the original plays its whole length), each on its own mover. The timeline is the result's: a pause stops both, a scrub or a step
puts the original at the same time from its start as the result (modulo the original's length when past it), Play runs both on from
there, and the text in the view gives the original's own time and tick. **Original follows the edit's progress** (off unless ticked,
remembered for the next run) instead shows the original at the same point of its move as the result (for trim and speed, the original's
tick that plays at each tick of the result: the edit's time times the speed, or plus the trim's start), to compare poses there, mostly
useful in Overlay. The box over the view sets how the two stand: **Side by side** (the original on the left, the result on
the right, 1.1 m apart, so they overlap only where a clip travels sideways that far with Mover on), where an edit starts for speed,
trim, swap and mirror, whose timing or whole motion differs; **Overlay**
(the original behind the result, a little thinner, so where the two stand in the same place the result is always the one in front),
where root-motion scale starts, to see exactly how far the travel changed. A choice made in the box holds for every edit after it while
the program runs; **Original as a ghost** off shows the result alone, and **Follow character** keeps the camera on the result as it travels
(**Reset camera** with Follow character on centres on the result where it stands then). The 3D view keeps a usable height in the smallest
window, the panes scrolling instead, and the edits scroll on their own so the buttons and the checks stay in sight.
The box shows the layout of the edit chosen as soon as it is chosen. The checks panel lists the result's checks (the failures
first, then the warnings) against the original (feet in the floor, feet off the floor, joint limits, twists, speed, pops, the mover's
turn and limbs; a mirror against the
original mirrored, each bone held to its counterpart), a scaled clip's foot slide, and the warnings from the animation graph
(a blend that reads the clip's speed or heading, a motion field's slot); what the edit did and kept is under it. **Apply and add to
package**, the main button, makes the edit and puts the result in the package list (the same one the Import screen adds to) in one
step; **Apply** alone makes it to look at first, and **Add to package** then adds the result shown. While a result made isn't in the
package, the screen says so under the buttons. A result that changes nothing is never added, greyed with the reason.

## Building a package

The **Package** screen lists what is kept in the Library's own list (the same columns, search and filters), its clips the ones kept,
with the package's own columns after the four every list has: its checks' verdict as a coloured PASS, WARN or FAIL badge (the
warnings on hover), who plays each clip (several kinds of character end in an ellipsis, whole on hover), **Change**, what was changed
in it (as wide as its longest value up to a limit, and never past the list's right edge: a swap from a clip name of up to about 25
characters reads whole at the window's usual size; a longer value, or one in a narrower window, ends in an ellipsis inside the list
and reads whole on hover), and its warnings (whole on hover). At the window's usual size the four common columns,
the checks, who plays each clip and the change are in sight; the warnings and the columns every list has after them are a sideways
scroll away. With **Remove**. The change is worded the same wherever
the tool names one (the Edit screen and the Import screen's 3D views, the Edit screen's lines, the command line): `Mirrored`, `Root-motion scale 0.5x`,
`Speed 1.25x`, `Trim 0.9 s to 7.2 s` (the trim in seconds of the original clip; its exact ticks on hover), `Swap: motion from
mca_emote_Shotgun`, or `Imported: walk.glb`. The
summary over the list names every clip whose checks fail. The list is kept between runs of the tool until it is built, in
`%APPDATA%\GrbAnimTool\package\kept-clips.json` (each kept clip's record, what was changed in it and its checks; written whole each time
the list changes, never half), one list for each game folder and animation datafile you have kept clips on. A successful **Build** takes
its clips out of the list and out of that file at once (a clip kept while it ran stays), so the next run starts with an empty list;
until the tool closes, a clip is kept again (which starts a new list) or another install is opened, the screen shows what was built
and where, and that nothing is left to build. A build that ends after another game folder was opened takes its clips out of the list
kept for the game folder it was built from, and says so. A build that fails or is refused leaves the list as it was, on the screen and
in the file. A list, or a kept
clip, this version can't read (written by a later version, or damaged: a kept record that isn't the one its SHA-256 names, a
property given twice) is left out and said,
the rest coming back; before the list is saved again the file as it was is kept beside it as
`kept-clips.unreadable-<date>_<time>.json` (never written over; the five newest are kept, an older one that can't be deleted then
staying until a later save). When the tool opens the same
game folder again the list comes back, and the screen says so, even after the patch forge was repacked (your package installed, a game
patch, another mod): a clip comes back while the install still has its clip of that ID, index and name, whatever that slot holds now. A
clip that doesn't fit is not shown but stays kept, and so does every other game folder's list: opening game files never changes the
kept file, and the screen names what stays kept for other game files, and each clip that doesn't fit and why; **Forget these** drops
the clips that don't fit (the list shown and other game folders' lists stay). A kept clip whose record doesn't pass the tool's
rebuild check on these game files isn't put back either: it is listed with the clips that don't fit, with why, and stays kept until
**Forget these** drops it. A kept file that can't be read at all (cut short, not a
list, a later format) starts an empty list, the screen saying so, and is kept aside as above before it is replaced. While the kept file
is there but can't be opened or read (another program holding it), nothing is saved over it: keeping a clip, a build's taking its
clips out, and **Forget these** each say the list couldn't be saved, and every list in the file stays as it was. The file list stays empty while the package holds no clip.

Give the package a name and a save folder of your own (the game's folders are refused); the screen lists the exact files a
build writes into one folder of the package's name inside it:

- each clip's record file `<index>_-_<name>.Animation`;
- the folder `Original clips`, holding the game's own record of each clip the package replaces whose original is found (below), under
  the same file names: copied over the package's files in the unpacked datafile, they uninstall the package (a clip without its original
  is named on the screen; with no original found at all, there is no such folder);
- the build's marker file `.grbanimtool-package` (the package's name and the build time). Every writer of the tool refuses a folder carrying
  it, and anything below it.

No install note or README is written: what players read is yours to write. **Build** writes everything all or none, then lists what was
written. The original clips come from your own game files only, never from the tool, and each is checked against the SHA-256 of the
game's own record built into the tool: the install's record of the slot when it is the game's own; where your install holds a modded
clip in a replaced slot, the record of that clip in a **vanilla backup of `DataPC_patch_01.forge`** you name on the Package screen
(**Vanilla forge backup**, with **Browse...** and **Clear**: a copy of the forge from before you modded it, such as the backup an
unpacking tool makes; the tool remembers it in its settings file), and then in the
game's base forge `DataPC.forge`, each read as the tool reads every forge. The Package screen says per clip where its original is taken
from (`taken from your vanilla forge backup: your game folder holds a modded clip there`), and while it reads them (once per install and
file, in the background) Build waits, greyed. When none of them holds the game's own record of a replaced clip, the package is built
without that clip's original, and the screen says so before Build, naming the clip: `no original included for <clip>: it can't be
uninstalled with this package`. Build is never refused for this. On today's game files the base forge holds the animation datafile, but
none of its clip records hashes as the game's own, so for a slot your install has modded only a vanilla forge backup gives the
original. An earlier build is replaced only when you tick **Replace an earlier build of this package**, and only when its
folder carries this tool's marker file for a package of the same name and holds nothing but a package's files (its record files, its
`Original clips` folder of record files and its marker file; a `README.txt` or `install-note.txt` in it counts as the package's and
is replaced with the rest, so keep your own texts for players elsewhere); any other folder of that name
is refused. A kept clip that is the game's own clip unchanged replaces nothing and is refused with its name
(remove it from the package). A kept clip whose record is exactly what your install already holds for it is refused too (said as not
the game's own clip unchanged only where the tool's table of the game's clips knows that clip), as it changes nothing there: your
install may hold it from an earlier build of this package installed in the game, and the tool can't tell it from what's installed, so
build from game files that don't hold it (verify the game files or put your forge backup back first), or keep the earlier package
folder as it is. A kept edit that changes nothing (the clip as your install already holds it) is
refused too (remove it from the package). A save folder that is, or lies inside, a package of this tool (also through a link or a
junction) is refused too (pick a folder outside your packages). A link or a junction
standing where the package goes is never replaced: Build deletes and moves nothing through it (remove the link, or give the package
another name); the Package screen says so before Build. A save folder holding a character Windows refuses in a folder name
(`" * ? < > |`) is refused with that reason. The Package screen also names folders a build that didn't finish left beside the package,
and leaves them for you to delete.

When you build again after installing your own package, a clip of it you haven't changed since is exactly what your install now holds:
the tool can't tell it from what's installed, so it is refused (it would change nothing there). Build from game files that don't hold it
(verify the game files or put your forge backup back first), or keep the earlier package folder as it is. Clips you have changed since
are no longer the game's own in your install, so their originals are looked for in your vanilla forge backup and the base forge, as
above.

Before Build, the Package screen shows each clip's checks and warnings and who plays it, and names each clip more than one kind of
character plays.

### Installing a package by hand

Players need the tool they use to unpack and repack the game's datafiles.

1. Close the game and keep a backup copy of `DataPC_patch_01.forge` from the game folder.
2. With that tool, unpack `DataPC_patch_01.forge`, then the animation datafile inside it. That makes a folder named
   `46_-_MIS_Y2E4_Katya_Maksimov.data` on today's game files, or `24_-_MIS_Y2E4_Katya_Maksimov.data` on the 2023 ones (the number
   is the datafile's place in the forge; GRB Anim Tool names the files from the forge of the install it opened).
3. Copy the package's `.Animation` files (not its `Original clips` folder) over the files of the same names in that folder (check
   that each name is there first: a file of another name sits beside the game's clip and replaces nothing).
4. Pack the animation datafile and then the forge again with the same tool.

To uninstall the package, unpack again, copy the files of its `Original clips` folder over the package's files, and pack again. That
puts back the game's own clips, which also removes any other mod in those slots. Copying the backup of `DataPC_patch_01.forge` back,
or letting the game's launcher verify the game files, removes every other mod installed in that forge too, not only animation packages.

The female player character plays the same clips, by the same IDs, as the male one. A replaced clip changes for every character that
plays it: many NPC clips are also played by the AI teammates (their own death and hit machines name most of the NPCs' deaths and some of
their heavy hits, their locomotion layer all the riflemen's light hits, their weapon layer many of the player's reloads); the Package
screen shows who plays each clip. When two packages replace the same clip, the one copied in last wins.

## The Log screen and the GRB Clip Logger

The **Log** screen shows which clips the game started, from a log of the GRB Clip Logger, so you can find the clip you just saw in
game. The logger is an optional, separate download from the same releases: a Cheat Engine table, checked with Cheat Engine 7.6 (its
own `README.txt` says how to switch it on; its files are in this repository's `clip-logger` folder). GRB Anim Tool doesn't need it,
nor Cheat Engine: every other screen works without them, and the Log screen only reads the logs the logger writes.

The logger writes one CSV file per session (`clips_DX11_<date>_<time>.csv` or `clips_Vulkan_<date>_<time>.csv`) into
`GRB - Animation logging` in your Documents folder, as Windows reports it (so a Documents folder moved to another drive or into
OneDrive is followed).

- **Follow the newest log** (on by default) shows the newest log in that folder as the game writes it, says which file it follows, and
  switches to a newer one when the logger starts it (each time its entry is ticked). The newest is the latest by the date and time in
  its name, unless the files' last writes say the other way round by more than an hour (the PC's clock changed between two sessions:
  the hour repeated when daylight saving ends, or another time zone). The file is read shared, while the logger goes on writing it; a
  line is taken only once its end is written, only what was added is read, and a file replaced or cut short is read again from its
  start. A folder that isn't there is said plainly: the tool makes nothing in it and writes nothing there (the logger makes it the
  first time its entry is ticked). **Change folder...** follows another folder, remembered in `settings.json`. Nothing is read before
  the Log screen is first shown, and nothing while another screen is: what the log gained meanwhile comes in as soon as the Log screen
  is shown again.
- **Open log...** shows any log, kept read as it grows (a file over 64 MB, more than a log can be, isn't read). Starting the tool with
  `--log-file` or `--log-folder` (see Automated checks) makes an automated run, which opens no remembered game folder: choose it to name
  the log's clips.

Each line of the list is one clip start: the time, the clip's name in your install's files (looked up by the ID the log gives, the ID
the Library and Edit screens show), its readable name and who plays it from the clip index, the character (`player`, or `other` when
the logger's Characters setting is All characters), what started it (`clip`, `layer`, `motion` or `other`: a place in the game's code the logger doesn't name, the place shown on
hover) and the ID. An ID that isn't
one of your install's clips shows as `unknown clip` with its ID: the tool never guesses a name. (In the logs tried, such IDs were all
other characters': mostly civilians' and animals' animations the game keeps in other datafiles than the animation datafile the tool
reads, which it doesn't show or replace.) A line that doesn't read as a start shows as `unreadable line`, why on hover. **Show** lists every
character's starts or the player's only. The log's notes (its start, its settings, the player's character found, marks, the summary at
its end) are in the strip under the list, and a log that reached the logger's 32 MB limit says so. A click on a line, or Enter, chooses
its clip in the Library and shows the Library, so the clip plays, exports and edits as any clip chosen there; a line of an unknown ID
opens nothing. While a line is pressed the list holds still (lines the game logs meanwhile wait below it), so the line opened is the one
clicked. A log of 32 MB (about 300,000 lines) loads in the background with its progress, and the list lays out only the rows in
sight.

## Where the tool keeps its files

The tool keeps its own files in `%APPDATA%\GrbAnimTool\`:

- `settings.json`: the game folder and the other choices it remembers;
- `errors.log`, with `errors.log.old`: the detail of errors;
- `index\`: the caches, the clip index's and the Edit screen's marks of which edits each clip allows;
- `package\kept-clips.json`: the package list kept between runs, and beside it at most five
  `kept-clips.unreadable-<date>_<time>.json` (a list that couldn't be read whole) and at most five
  `kept-clips.elsewhere-<date>_<time>.json` (a list another running copy of the tool changed), each kept before a save replaces it.

Everything else it writes (exports, record files with their check reports, packages) goes into a folder you pick.

## What the tool never does

The tool uses the game's own Oodle library from your game folder, and loads it only when its SHA-256 is one the tool knows.
It never ships or copies it. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The tool only reads your install. Every output (an export, a record file, a package, a report, a picture, a log, its settings) is
refused inside the game folder and inside the folder of each forge it reads, by whatever path it is given: a link or junction, a
`subst` drive, a short name, a share of this computer or a `\\?\` spelling leading there is refused too. A datafile unpacked from a
forge is refused as well: write to an empty folder of your own and copy the files into the unpack yourself. Two rules find it:

- by name: an output whose folder, or any folder above it, is named `<number>_-_<name>.data` (any case), or whose folders lead, through
  a link, a junction or a `subst` drive, to a path with such a folder anywhere on it. A folder inside an unpack reached through a share
  of this computer that isn't an administrative one (`\\thispc\name`) isn't caught by this rule, nor by the next, which counts only the
  output folder itself (a share of the unpack itself is caught by the next);
- by content: an output folder (for a file, the folder it goes into) that already holds 1,000 or more of the game's own clip files
  named `<index>_-_<name>.Animation` (the animation datafile's unpack holds 9,938). A file counts when the record it stands for (rebuilt
  from the file and the name in its file name) is, by its SHA-256, the game's own record of the clip ID the file holds, from the table of
  the game's clips built into the tool: this needs no install open, so every writer applies it, the command-line harness's logs and hash
  table included, and its index doesn't matter, so the files of game files of another date count too, where a clip's bytes are today's
  (not checked for the 2023 files, whose clips may differ in their bytes). While an install is open, a file
  also counts when it is byte for byte that install's record of its index and name (the name's case ignored), which also catches names
  spelt in another case and clips newer than the table. A folder carrying this tool's marker file `.grbanimtool-package` (a package),
  and a package's `Original clips` folder right inside one, are never refused by this
  rule, and only the output folder itself is counted, not the folders above it: a new
  folder inside a folder of record files is allowed, and so is a folder of your own unchanged clips, however they were written or
  imported, while there are fewer than 1,000 of them in one folder. The rule needs the unpack's own files, by their own names; a marker file
  copied into an unpack, or a folder inside a renamed unpack, isn't caught by it.

Every output is refused inside a package of this tool as well (a folder carrying its
marker file, or any folder below one, a package's `Original clips` among them), by its spelling and by where it really leads through a link, a junction or a `subst` drive: whatever is written there would be
shared with that package, so pick a folder outside your packages.

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
  download (see [The Log screen and the GRB Clip Logger](#the-log-screen-and-the-grb-clip-logger)).
- `src/GrbAnimTool.Cli`: `grbanim-cli`, a small command-line harness for tests and automation (`check`, `list`, `soak`, `hash-table`,
  `graph`, `index`, `export`, `export-skeleton`, `pose`, `write`, `rebuild-check`, `import`, `compare`, `census-edits`, `edit`). Players never
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
