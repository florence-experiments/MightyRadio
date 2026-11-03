MIGHTY RADIO 1.1
================

How to start
------------
1. Keep this whole "MightyRadio" folder together (do not move MightyRadio.exe out of it).
2. Double-click MightyRadio.exe.
3. The first time, Windows may show "Windows protected your PC" (SmartScreen).
   Click "More info", then "Run anyway". This only happens because the app isn't signed.
   Tip: right-click MightyRadio.exe > Show more options > Send to > Desktop (create shortcut).

Using it
--------
* Radio stations tab: search by station name, country (United Kingdom by default) and genre.
  Double-click a station (or select it and press Play) to listen.
* Scanner tab: air traffic, marine, space, public safety, ham radio, weather and shortwave feeds.
  Items marked "opens in browser" open a web page instead of playing in the app.
  "Edit list" opens the feed list so you can add your own; press "Reload" after saving.
* Favourites tab: everything you have starred. Use the buttons to play, remove, or reorder.
* History tab: song and programme titles you have heard, with station and time. Copy or clear them.
* Record: while a station is playing, press Record. Press it again (or Stop) to finish.
  The first time, a notice asks you to confirm you understand the rules (see below).
  Recordings go to the MightyRadio folder in your Music library, as an exact copy of the stream.
* Schedule tab: record a station at a set time, once, daily or on weekdays. Mighty Radio must be
  open (minimised or in the notification area is fine) and the PC awake.
* Sleep timer: the "Sleep" drop-down next to Record. The sound fades out, then stops.
* Media keys: Play/Pause, Stop, and Next/Previous (steps through your favourites).
* Settings tab: theme (Brass & Amber, Phosphor, Dark), notification-area options, recording notice.

Where things are saved
----------------------
%APPDATA%\MightyRadio\favourites.json          your favourites
%APPDATA%\MightyRadio\settings.json            volume, theme, last station, window size
%APPDATA%\MightyRadio\history.json             now-playing history
%APPDATA%\MightyRadio\schedules.json           scheduled recordings
%APPDATA%\MightyRadio\recording-consent.json   when you accepted the recording notice
%APPDATA%\MightyRadio\stations-scanner.json    your editable scanner list
%APPDATA%\MightyRadio\error.log                only written if something goes wrong
Music library\MightyRadio\                     recordings

Disclaimer
----------
You are responsible for making sure that what you listen to, record and share is lawful where you
are (copyright; in the UK also the Wireless Telegraphy Act 2006 for non-broadcast radio traffic).
Mighty Radio is provided "as is", without warranty of any kind, under the MIT licence (LICENSE.txt),
and the author accepts no liability for how it is used.

Network use: Mighty Radio only contacts the radio-browser.info station directory and the stations
you choose to play. No telemetry. Playback engine: VLC (LibVLC, LGPL 2.1 - see licenses\LGPL-2.1.txt
and THIRD-PARTY-NOTICES.md), included in the libvlc folder.
Project page: https://github.com/florence-experiments/MightyRadio
