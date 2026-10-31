; Test program from the CPU8 instruction sheet. It ends in the loop at 0x00B;
; stopping at the HLT at 0x009 means the RAM test failed.
        LDI R0, 5         ; 0x000  counter
        LDI R1, 1         ; 0x001
loop:   SUB R0, R1        ; 0x002  R0 <- R0 - 1, sets Z and C
        JNZ loop          ; 0x003  repeat until R0 = 0
        LDI R2, 0xAA      ; 0x004  test value
        ST [0x20], R2     ; 0x005  RAM[0x20] <- R2
        LD R3, [0x20]     ; 0x006  R3 <- RAM[0x20]
        CMP R2, R3        ; 0x007  Z = 1 when they are equal
        JZ ok             ; 0x008
        HLT               ; 0x009  test failed
ok:     NOP               ; 0x00A  end of the test
done:   JMP done          ; 0x00B
