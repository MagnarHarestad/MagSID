; SOFTHR and NOHR only: still every hard restart soft, beside the NOHR test.
var v_Song_Speed = 3

INSTRUMENTS:
01, $41, $0a, $50, PW 2 +20, SOFTHR
02, $21, $09, $a9, SOFTHR
03, $11, $00, $f7, WAVE $81 $11, NOHR
END:

PAT01:
"C-4", 01
"...", 00
"E-4", 02
"...", 00
"G-4", 01
"---", 00
"C-5", 03
"...", 00
END:

PAT02:
"C-3", 02
"...", 00
"C-3", 03
"G-3", 01
END:

ORDER_CH1:
1, 2
END:

ORDER_CH2:
2, 1
END:

ORDER_CH3:
1
END:
