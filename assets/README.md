# Original AudioSwitcher notification sounds

confirm.wav is an original soft struck-glass/wood note.
error.wav is a lower, muted note in the same sound family.

The clips are created by make-sounds.py using reproducible modal synthesis:
rounded attacks, gently inharmonic partials, a subtle impact texture and natural
exponential decay. They use no MIDI instruments, third-party samples or existing
Windows/Messenger notification recordings.

Run `python assets/make-sounds.py` to reproduce them. Python and the script are
not needed at runtime or for an ordinary build; the WAV files are embedded.

The app trims near-silent edges and attenuates the clips in memory. Profile 2
starts the second note 180 ms after the first, overlapping its fading tail. The second note is resampled two semitones higher; the single profile-1 cue stays unchanged.

