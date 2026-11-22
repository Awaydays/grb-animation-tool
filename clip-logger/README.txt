GRB Clip Logger
===============

An optional helper for Ghost Recon Breakpoint. It writes a log of which animation
clips start playing on your character, with the time, so you can find the clip you
just saw in game. The log gives each clip by its ID; GRB Anim Tool's Log screen
looks the ID up in your own game files to show its name.

It is a Cheat Engine table, checked with Cheat Engine 7.6. Nothing else is
needed, and nothing of the game's files is changed. It is a separate, optional
download: GRB Anim Tool works without it and without Cheat Engine.


Switching it on
---------------
1. Start the game (DX11 or Vulkan) and load into the world.
2. Start Cheat Engine and open "GRB Clip Logger.CT" (or double-click the file).
   The table can be kept anywhere.
3. Click the computer icon in Cheat Engine and choose the game's process:
   GRB.exe (DX11) or GRB_vulkan.exe (Vulkan).
4. Tick the entry for your build: "Clip logger - DX11 (GRB.exe)" or
   "Clip logger - Vulkan (GRB_vulkan.exe)".

Cheat Engine's Lua window (Memory View -> Tools -> Lua Engine) shows where the
log goes and when your character has been found. Finding your character takes a
moment after loading into the world; until then nothing is logged in the default
mode.

On Vulkan that search can take much longer (it runs in the background, and the
game keeps running). A tip, not a requirement: in Cheat Engine's settings
(Edit -> Settings -> Scan Settings), tick "Don't scan memory that is protected
with the Write Combine option" to make it quick.

Switching it off
----------------
Untick the entry. The game's code is put back as it was, and the log file is
closed with a summary at its end. Each time you tick the entry a new log file is
started.


Settings
--------
While the entry is ticked, its settings show below it. Double-click a value to
change it; a change takes effect at once and is noted in the log. They go back to
the defaults each time the entry is ticked.

  Characters                Player only (default), or All characters.
  Repeat window (ms)        1500 by default. When the same clip starts again on
                            the same character within this time (a looping clip,
                            for example), it is not logged again. 0 logs every start.
  Motion matching           Walking, running and sprinting are played by the
                            game's motion matching, which keeps jumping between
                            frames of a set of clips. Default: a jump is logged only
                            when it lands in a different clip than the last one.
                            "Log every pick" logs every jump; "Do not log picks"
                            leaves locomotion out. The repeat window does not
                            hold back motion-matching jumps.
  Dropped (buffer full)     How many starts were lost because the log could not
                            keep up (should stay 0).


The log
-------
Folder: "GRB - Animation logging" in your Documents folder, for example
C:\Users\<your name>\Documents\GRB - Animation logging. If your Documents folder
has been moved (to another drive, or into OneDrive), the log goes there: the
logger asks Windows where Documents is. The folder is made the first time you
tick the entry. The log is written nowhere else: if the folder cannot be made
or written to, the entry does not switch on, says why in one line, and logs
nothing. Cheat Engine's Lua window prints the log's full path when logging
starts. One file per session, named by build, date and time, for
example clips_DX11_2026-11-18_213005.csv. It is written to disk several times a
second, so little is lost if the game or Cheat Engine closes. A file stops growing
at 32 MB (starts after that are counted in its summary).

Lines starting with # are notes (settings, your character found, the summary).
The others are CSV with these columns:

  time          your local date and time, with milliseconds
  seconds       seconds since the log started
  character     player, or other
  entity        the character's address in memory this session (tells characters
                apart within one log; it changes between sessions)
  entity_id     an ID the game keeps with the character, where it has one
  clip_id       the clip's ID, as in the game's files (for example
                0x000001A625068E05)
  source        what started it: clip (an animation state), layer (an animation
                layer), motion (a motion-matching jump), or other followed by
                where in the game's code the start came from (for example
                other GRB.exe+41C2D0)

The game's animation code gets no clip names from it (every clip reports the same
placeholder name), so the log has IDs only.


Notes
-----
- The table is for the current PC version of the game. If a game update changes
  the code it looks for, the entry refuses to switch on and says why; nothing is
  changed then.
- One build's entry works only with that build's process.
- Use it in single player. It only reads what the game is doing; it changes no
  game behaviour.


Licence
-------
Copyright (C) 2026 Awaydays.

GRB Clip Logger is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License in LICENSE for more
details.

The table's scripts are also given as plain text: clip_logger_AA_DX11.txt and
clip_logger_AA_Vulkan.txt hold the same scripts as the table's two entries.
