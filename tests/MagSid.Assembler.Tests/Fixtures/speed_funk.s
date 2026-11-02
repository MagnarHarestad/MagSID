; Test fixture, not a tune: Fxy rows in a song that starts with funk speed and has vibrato and slides,
; so the first reload comes from the seeded bytes and the F rows take over from them.
var v_Song_Speed = 5
var v_Song_Funk = 3

INSTRUMENTS:
01, $41, $00, $f7, WAVE $09 $41 $40, ARP ROOT, PW 6 +0, VIB $32 $10
02, $21, $00, $aa, WAVE $09 $21 $20, ARP ROOT, PW 2 +6 CONT
END:

PAT01:
"C-4", 01
"...", 00
"...", 00
"E-4", 02
"108", 00
"F75", 00
"G-4", 01
"...", 00
"...", 00
"F30", 00
"C-5", 02
"...", 00
END:

ORDER_CH1:
1
END:

ORDER_CH2:
1
END:

ORDER_CH3:
1
END:
