# Goldens

What c6510 produced. These files exist because the assembler and the assembly player no longer live
in this repository — see the root README — so there is nothing left to assemble a fresh comparison
against. Freezing its output keeps the verification; only the ability to re-derive it is gone.

Every `.bin` is the complete `$1000` memory image, player and song data, with the two-byte `.prg`
load address stripped. Produced on **2026-09-27** at commit `6221867`, when the differential tests
still ran and were green, by:

```
c6510.exe -p include/ -d v_export_loc=$1000 -s out.prg $1000 + export_<song>.s
```

`bellaciao_zp80.bin` is the song at $1000 with `addr.music.zp` defined as $80, the player's zero page moved. Built the
same way with $20, that route gives `bellaciao.bin` exactly.

`bellaciao_2000.bin` and `bellaciao_4400.bin` are the same song assembled at those origins, which is
what pins relocation. `slide_only.bin` and `slide_and_vib.bin` come from the two fixture songs beside
this folder: no real song uses SLIDE, so without them the slide paths and the shared
frequency-modulation block with both effects on would go uncompared.

`lightforce.bin` was reassembled on **2026-10-05**, after Lightforce's Bass lost its filter program
(Kick's identical one already sets the filter up). Same command, with the player, include files and
`c6510.exe` taken from commit `6221867` and `Globalmacros.s` read from the Censor SVN tree. The
procedure was checked first by reproducing the old `lightforce.bin` byte for byte from the old song;
the new one differs from it in a single byte, Bass's `flt_start` entry, `$01` to `$00`.

`lightforce.bin` and `magnificent.bin` were reassembled again on **2026-10-08**, after Lead1 changed
in both songs (Lightforce's gained a wavetable, a note program and NOHR; Magnificent's a new ADSR).
Same player and procedure; the other nine came out byte for byte as they were, which is the check.

`GateOff\` holds the same eleven images for the player whose hard restart keeps the waveform and
clears only the gate (`lda voice_last_waveform,x / and #$fe` in place of `lda #$08`) - the setting
`MagSid.Core.HardRestart.KeepsWaveform` selects, and which folder the tests compare against. Made on
**2026-10-07** with the same commands, the player from commit `6221867` edited in a scratch copy only
for that one instruction. The procedure was first checked by rebuilding all eleven originals byte for
byte; every GateOff image is exactly two bytes longer.

`slide_only.bin` and `slide_and_vib.bin` were reassembled on **2026-10-08**, when slide moved from an
instrument setting to pattern rows (`"1xx"` up, `"2xx"` down, a `$D3` command and its rate). The
player and song reader from commit `6221867` were ported to that in a scratch copy, and the
fixtures rewritten to slide rows. The nine images without a slide came out byte for byte as before.

All eleven were reassembled on **2026-10-13**, when a note off stopped stopping the voice: it now
parks `voice_wt_pos` on `wt_hold`, the wavetable pool's closing `$00`, so pulse, note program,
vibrato and slide run on through the release (`lda #wt_hold-wt_data` for `lda #0`, same size). Each
image differs from the one before in that single immediate byte at `$1011`.

All eleven, and the eleven in `GateOff\`, were reassembled on **2026-10-23**, when the player shrank
by 4 bytes, 8 with vibrato: `Voice_Tick`'s fetch indexes the row tables with Y instead of borrowing X,
`sty voice_arp_pos,x` replaces `tya`/`sta`, ALR (`asr #imm` in c6510, `$4B`) replaces `and #imm`/`lsr`
where the vibrato is armed, and a redundant `ldy voice_inst,x` goes where it turns round. Same
procedure; the eleven old images were first rebuilt byte for byte. Old and new were then run for 5000
frames each in the 6502 emulator and wrote the same SID registers, values and order, every frame.

`GateOff\` had not followed the 2026-10-08 and 2026-10-13 reassemblies - it matched the player neither
before nor after that change, which went unnoticed because the tests read it only with
`KeepsWaveform` on. It was rebuilt from the same player with its one-instruction edit, then checked
against the emitter with `KeepsWaveform` switched on: all 25 image tests passed.

`funk_bellaciao.bin` and `funk_slide.bin` (and their `GateOff\` twins) were added on **2026-10-24**
with funk speed: `var v_Song_Funk = N` beside `v_Song_Speed` makes the ticks alternate between the two
speeds. `ONbeat.PlaySong` reloads from `lda Song_Shuffle / eor #v_Song_Speed^v_Song_Funk / sta
Song_Shuffle / eor #v_Song_Funk / sta Song_Speed` in place of `move8 #v_Song_Speed, Song_Speed` - six
bytes more - and `Song_Shuffle` is one zero-page byte after `filter_delta`, cleared by music.init.
`Tables.s` emits `v_Song_Funk` from its include-time scan, as it does `v_slide`. The fixtures beside
this folder are BellaCiao and slide_only.s with a funk speed added. Same procedure; all eleven images
without funk speed, and the eleven in `GateOff\`, came out byte for byte as before.

All the images with slide or vibrato, seven here and seven in `GateOff\`, were reassembled on
**2026-10-30**, when those effects shrank by 30 bytes with slides, 32 with vibrato and 40 with both.
`Voice_SetFreq` joined the frequency write, so it writes the note plus its offset, and moved into
`Voice_Automation`; a note program's step now only keeps the note, with carry set to go on through the
offset, instead of writing it and overwriting it in the same frame. The note program's two exits became
one, with `.byte $2c` (`bit abs`) stepping over the `inc`. Slide's offset is added by `bpl`/`dec` and
`bcc`/`inc`, vibrato's delay is `dec`/`bpl`/`inc`, and the tests for a rate or a depth of 0 went - they add
nothing, so they only saved cycles. The trigger clears the offset before the `jsr` and reads `vib_ds` once,
and `$D3` falls into `.playrow`. Same procedure, with the old 26 images first rebuilt byte for byte; the 12
images without either effect came out byte for byte as before. Old and new were then run for 30000 frames
each, for every song in `SongData` and the fixtures, and the SID registers held the same values after every
frame. The writes differ on purpose: the note a step used to write first is gone, and the frequency goes low
byte first, the two within a few cycles of each other as they always were.

All 26 were reassembled later the same day, 7 bytes smaller without slide or vibrato and 1 with: `Voice_SetFreq`
falls out of the note program, whose `.byte $2c` now serves every build, and the program's loop moved ahead of
`Voice_Automation` with its entry test turned into a branch to the `rts` that ends `Voice_SetFreq`. A loop
target of 0 is refused by the song tables, so the loop always branches. Old 26 rebuilt first, then 30000 frames
of old and new: the same writes and the same SID state.

The images with vibrato, here and in `GateOff\`, were reassembled again on **2026-10-30**, when the vibrato
tables stopped carrying instruments that have none. Instruments with vibrato now come first in the player's
numbering (`inst_index` in `Tables.s`, `InstrumentTables.PlayerOrder` here), which only the per-instrument
tables and the stream's `$Cx` follow; the pools and the note dictionary keep the song's order, so nothing else
moves. With two or more instruments left over, `vib_ds` and `vib_depth` stop after the ones that have it and
`.freqmod` starts `ldy voice_inst,x / cpy #v_vib_count / bcs Voice_FmWrite`, assembled only then. One left over
keeps an entry of `$00` instead: two table bytes against the compare's four. `cpy Immediate C0` joined
`opcodes.txt`; these images carry it, which is c6510's word for it. Images without vibrato came out byte for
byte as before, and old and new held the same SID state after every one of 30000 frames, on every song.

All 26 were reassembled again on **2026-10-30**, 40 bytes smaller, 38 with slides and no vibrato - tricks from
GoatTracker and SID Duzz It. X runs 0, 7, 14 and the per-channel arrays sit in zero page at that stride, seven
interleaved across three rows of seven, with the single variables in what the last row leaves: one index reaches
a voice and its SID registers, so `SID_channel_offset` and `ZP_ch` are gone, Y is free for the instrument, and
`voice_orderpos` moved to zero page, seeded by two `move8`. Channel loops step with `txa` / `axs #7` (`CB 07`),
and `txa Implied 8A` and `axs Immediate CB` joined `opcodes.txt` after c6510 assembled them so. `voice_note` went:
the note stays in A from `.playrow`, and `Voice_Tick` falls into `Voice_PlayCurrent`, with `Voice_NoteOff` after it.
The pool, note and pattern-pointer tables lost entry 0, never read, by reading through `label-1`. Zero page: 48
bytes, 69 with vibrato, 63 with slides, 80 with both. The Musicshower was not carried over: it reads `voice_note`.
Old 26 rebuilt first; old and new then held the same SID state for 30000 frames on every song in `SongData` and
the fixtures. `GateOff\` was checked against the emitter with `KeepsWaveform` switched on: all its image tests passed.

`player.patch` beside this file is every change the scratch player carries - pattern slides, SOFTHR,
the release, funk speed and the smaller slide and vibrato - against commit `6221867`'s `Code/musicplayer.s` and `Startup/Tables.s` (apply with
`patch -p0 --binary`; the sources are CRLF). Rebuilding any golden means applying it first.

`opcodes.txt` is the emitter's opcode table, one `mnemonic mode hex` line each, frozen after
`OpcodeTableTests` had checked all 106 of them against c6510 instruction by instruction. `asr Immediate
4B` (ALR) joined it on 2026-10-23, after c6510 assembled `asr #$12` as `4B 12`. `sec Implied 38` and
`bit Absolute 2C` joined it on 2026-10-30, after c6510 assembled `sec` and `bit $1234` as `38` and `2C 34 12`.
`rti`, `sei`, `cli`, `sbc Immediate` and `sbc ZeroPage` joined it on 2026-10-31 for the Musicshower, after c6510
assembled `rti`, `sei`, `cli`, `sbc #$12` and `sbc $12` as `40`, `78`, `58`, `E9 12` and `E5 12`.

## When one of these has to change

Only when the player itself changes, and then a golden is not something to regenerate from the C# —
that would just record whatever the emitter now does and assert it against itself. Assemble the new
player with c6510 from the Censor SVN tree and replace the file with its output. If that is not
possible, say so in the commit rather than refreshing the goldens from the code under test.
