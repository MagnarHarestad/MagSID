; Test fixture, not a tune: Fxy rows and no other effect - the speed code on its own. Channels 1 and
; 3 change speed on the same tick, so channel 1's has to win, and one F row sits inside a loop.
var v_Song_Speed = 4

INSTRUMENTS:
01, $41, $00, $f7, WAVE $09 $41 $40, ARP ROOT, PW 6 +0
02, $21, $00, $aa, WAVE $09 $21 $20, ARP ROOT, PW 2 +6 CONT
END:

PAT01:
"C-4", 01
"...", 00
"F64", 00
"...", 00
"E-4", 02
LS
"G-4", 01
"F30", 00
"...", 00
LE03
"FF2", 00
"C-5", 01
"---", 00
"...", 00
END:

PAT02:
"C-3", 02
"...", 00
"F21", 00
"...", 00
"G-3", 02
"...", 00
"...", 00
"...", 00
"...", 00
"...", 00
"...", 00
END:

ORDER_CH1:
1
END:

ORDER_CH2:
2
END:

ORDER_CH3:
2
END:
