MIGHTY RADIO
============

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
  "Edit list" opens the feed list in Notepad so you can add your own; press "Reload" after saving.
* Favourites tab: everything you have starred. Use the buttons to play, remove, or reorder.
* Record: while a station is playing, press Record. Press it again (or Stop) to finish.
  Recordings go to: Music\MightyRadio  (MP3 streams are saved as .mp3, others as .ogg)

Where things are saved
----------------------
%APPDATA%\MightyRadio\favourites.json        your favourites
%APPDATA%\MightyRadio\settings.json          volume, last station, window size
%APPDATA%\MightyRadio\stations-scanner.json  your editable scanner list
%APPDATA%\MightyRadio\error.log              only written if something goes wrong
%USERPROFILE%\Music\MightyRadio\             recordings

Network use: Mighty Radio only contacts the radio-browser.info station directory and the stations
you choose to play. No telemetry. Playback engine: VLC (LibVLC), included in the libvlc folder.
